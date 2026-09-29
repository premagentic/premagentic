using Premagentic.Core;
using Premagentic.Core.Embeddings;
using Premagentic.Core.Ingestion;
using Premagentic.Core.Ingestion.Readers;
using Premagentic.Core.Security;
using Premagentic.Core.Sources;
using Premagentic.Core.Storage;

namespace Premagentic.Tests;

/// <summary>
/// A document the index cannot store (a NUL, or half of a character) is judged
/// at the read step, as one unreadable file, and never reaches the write, where
/// the database's refusal would end the whole run.
/// </summary>
public class PipelineStorableTextTests
{
    private sealed class Returning(ReadDocument read) : IDocumentReader
    {
        public string Name => "returning";
        public IReadOnlyList<string> Extensions { get; } = [".ret"];
        public Task<ReadDocument> ReadAsync(Stream content, string path, CancellationToken ct) => Task.FromResult(read);
    }

    private static Task<SourceRead> ReadAsync(ReadDocument read) =>
        DocumentSourceReading.ReadAsync(
            new SourceContent("notes/file.ret", _ => Task.FromResult<Stream>(new MemoryStream([1])),
                (r, hash) => new SourceDocument("notes/file.ret", r.Title, r.Text, hash, DocumentAccess.Everyone)),
            new ReaderRegistry(new Returning(read)));

    public static TheoryData<string, ReadDocument> Unstorable => new()
    {
        { "a NUL in the text", new ReadDocument("before\0after", null, null) },
        { "a lone high surrogate in the text", new ReadDocument("before\uD800after", null, null) },
        { "a lone low surrogate in the title", new ReadDocument("Text.", "Title \uDC00", null) },
        { "a NUL in a frontmatter value", new ReadDocument("Text.", null, new Dictionary<string, string> { ["owner"] = "ops\0" }) },
        { "a NUL in a frontmatter key", new ReadDocument("Text.", null, new Dictionary<string, string> { ["own\0er"] = "ops" }) },
        {
            "a NUL in a nested parsed frontmatter value",
            new ReadDocument("Text.", null, null)
            {
                ParsedFrontmatter = new FrontmatterResult(new Dictionary<string, string>(), "Text.")
                {
                    Values = new Dictionary<string, object> { ["tags"] = new List<object> { "fine", "bad\0" } },
                },
            }
        },
    };

    [Theory]
    [MemberData(nameof(Unstorable))]
    public async Task A_document_the_index_cannot_store_is_unreadable_with_that_reason(string what, ReadDocument read)
    {
        var result = await ReadAsync(read);

        Assert.True(result.Failure is not null, $"{what} was not refused.");
        Assert.Null(result.Document);
        Assert.Equal("holds a NUL byte or half of a character, which the index cannot store", result.Failure!.Reason);
    }

    [Fact]
    public async Task A_whole_character_outside_the_basic_plane_is_stored()
    {
        // A pair of halves is one character, and UTF-8 writes it.
        var result = await ReadAsync(new ReadDocument("A crate of \U0001F34E apples.", "Stock \U0001F34E", null));

        Assert.NotNull(result.Document);
    }
}

/// <summary>The same, through a real ingest. Requires a running Docker daemon.</summary>
public sealed class PipelineStorableTextIngestTests(DatastoreTestDatabase server) : IClassFixture<DatastoreTestDatabase>
{
    [Fact]
    public async Task A_file_holding_a_NUL_is_one_unreadable_file_and_the_file_after_it_is_written()
    {
        var folder = Directory.CreateTempSubdirectory("prem-nul-").FullName;
        File.WriteAllText(Path.Combine(folder, "a-dock.md"), "# Dock\n\nThe dock opens\0 at seven.\n");
        File.WriteAllText(Path.Combine(folder, "b-gate.md"), "# Gate\n\nThe gate is locked at six.\n");

        await using var db = new PremagenticDatabase(await server.CreateDatabaseAsync());
        await db.InitializeAsync();
        var tenant = await db.EnsureTenantAsync("t", "T");
        var summary = await new IngestPipeline(db, new HashEmbeddingProvider())
            .RunAsync(tenant, new FileSystemSource(folder, DocumentAccess.Everyone, "yard"));

        Assert.Equal(1, summary.Ingested);
        var failure = Assert.Single(summary.FailureList);
        Assert.Equal(("yard/a-dock.md", "holds a NUL byte or half of a character, which the index cannot store"), (failure.Path, failure.Reason));
        await using var cmd = db.DataSource.CreateCommand("SELECT count(*) FROM prem_index.document WHERE path = 'yard/b-gate.md'");
        Assert.Equal(1L, (long)(await cmd.ExecuteScalarAsync())!);
    }
}

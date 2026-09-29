using System.Text;
using Premagentic.Core;
using Premagentic.Core.Embeddings;
using Premagentic.Core.Ingestion;
using Premagentic.Core.Security;
using Premagentic.Core.Sources;
using Premagentic.Core.Storage;

namespace Premagentic.Tests;

/// <summary>
/// One paragraph far over the chunk cap is cut into pieces the index can
/// store, and no word or character is lost or cut in two. No database.
/// </summary>
public sealed class OversizedParagraphTests
{
    private static readonly UTF8Encoding Strict = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>Distinct words, one after another, so every word is its own term in a search vector.</summary>
    internal static string DistinctWords(int count, string between) =>
        string.Join(between, Enumerable.Range(0, count).Select(i => $"w{i:x6}"));

    private static string[] Words(IEnumerable<string> texts) =>
        texts.SelectMany(t => t.Split([' ', '\n'], StringSplitOptions.RemoveEmptyEntries)).ToArray();

    [Theory]
    [InlineData(" ")]
    [InlineData("\n")]
    public void A_paragraph_of_megabytes_is_cut_at_the_cap_between_words(string between)
    {
        var paragraph = DistinctWords(400_000, between);

        var chunks = MarkdownChunker.Chunk($"# Log\n\nBefore.\n\n{paragraph}\n\nAfter.\n");

        Assert.True(chunks.Count > 1000, $"{chunks.Count} chunks");
        Assert.All(chunks, c => Assert.True(c.Content.Length <= MarkdownChunker.MaxParagraphChars, $"a chunk of {c.Content.Length} characters"));
        Assert.Equal(Words([$"Before.\n{paragraph}\nAfter."]), Words(chunks.Select(c => c.Content)));
    }

    [Theory]
    [InlineData(" ")]
    [InlineData("\n")]
    public void A_paragraph_of_words_of_uneven_length_is_cut_only_between_words(string between)
    {
        // Words of 2 to 15 characters, so a cut made at the cap itself falls
        // inside a word nearly every time; words of one length plus a space
        // can divide the cap exactly and hide such a cut.
        var paragraph = string.Join(between, Enumerable.Range(0, 300_000).Select(i => $"{new string('v', i % 14)}w{i % 10}"));

        var chunks = MarkdownChunker.Chunk(paragraph);

        Assert.True(chunks.Count > 1000, $"{chunks.Count} chunks");
        var at = 0;
        foreach (var chunk in chunks)
        {
            var start = paragraph.IndexOf(chunk.Content, at, StringComparison.Ordinal);
            Assert.True(start >= 0, $"a chunk is not the next part of the paragraph after character {at}");
            var end = start + chunk.Content.Length;
            Assert.True(start == 0 || char.IsWhiteSpace(paragraph[start - 1]), $"a chunk starts inside a word, at character {start}");
            Assert.True(end == paragraph.Length || char.IsWhiteSpace(paragraph[end]), $"a chunk ends inside a word, at character {end}");
            at = end;
        }
        // Nothing lost or doubled: the pieces, with one separator between
        // each, are the paragraph again.
        Assert.Equal(paragraph, string.Join(between, chunks.Select(c => c.Content)));
    }

    [Fact]
    public void A_paragraph_with_no_space_is_cut_at_the_cap_and_never_inside_a_character()
    {
        // One letter first, so every cut at the cap falls between the two
        // halves of a character unless the cut is moved back.
        var paragraph = "a" + string.Concat(Enumerable.Repeat("\U0001F34E", 20_000));

        var chunks = MarkdownChunker.Chunk(paragraph);

        Assert.True(chunks.Count > 10, $"{chunks.Count} chunks");
        Assert.All(chunks, c => Assert.True(c.Content.Length <= MarkdownChunker.MaxChunkChars, $"a chunk of {c.Content.Length} characters"));
        foreach (var chunk in chunks) Strict.GetByteCount(chunk.Content);
        Assert.Equal(paragraph, string.Concat(chunks.Select(c => c.Content)));
    }

    [Fact]
    public void A_table_up_to_twice_the_cap_stays_whole()
    {
        var table = string.Join("\n", Enumerable.Range(0, 100).Select(i => $"| row {i:d3} | {new string('v', 20)} |"));
        Assert.InRange(table.Length, MarkdownChunker.MaxChunkChars + 1, MarkdownChunker.MaxParagraphChars);

        var chunks = MarkdownChunker.Chunk($"# Stock\n\n{table}\n");

        Assert.Equal(table, Assert.Single(chunks).Content);
    }
}

/// <summary>
/// A document too large for the index, through a real ingest: the default
/// chunker cuts it so it is stored, and a document the index still refuses is
/// one recorded failure, while the rest of the source is ingested and
/// deletions are still reconciled. Requires a running Docker daemon.
/// </summary>
public sealed class OversizedTextIngestTests(DatastoreTestDatabase server) : IClassFixture<DatastoreTestDatabase>
{
    // 170,000 distinct words of seven characters: over 1 MB of distinct
    // terms, more than one search vector can hold.
    private const int Words = 170_000;

    /// <summary>A chunker that keeps a document whole, as a plug-in chunker may.</summary>
    private sealed class WholeText : IChunker
    {
        public string Name => "whole-text";

        public IReadOnlyList<DocumentChunk> Chunk(SourceDocument document) => [new DocumentChunk(0, "", document.Text)];
    }

    private async Task<(PremagenticDatabase Db, Guid Tenant)> DatabaseAsync()
    {
        var db = new PremagenticDatabase(await server.CreateDatabaseAsync());
        await db.InitializeAsync();
        return (db, await db.EnsureTenantAsync("t", "T"));
    }

    private static async Task<long> CountAsync(PremagenticDatabase db, string sql)
    {
        await using var cmd = db.DataSource.CreateCommand(sql);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    [Fact]
    public async Task A_file_with_one_paragraph_of_a_megabyte_of_words_is_cut_and_stored()
    {
        var folder = Directory.CreateTempSubdirectory("prem-oversized-").FullName;
        File.WriteAllText(Path.Combine(folder, "log.md"), $"# Log\n\n{OversizedParagraphTests.DistinctWords(Words, " ")}\n");

        var (db, tenant) = await DatabaseAsync();
        await using (db)
        {
            var summary = await new IngestPipeline(db, new HashEmbeddingProvider())
                .RunAsync(tenant, new FileSystemSource(folder, DocumentAccess.Everyone));

            Assert.Empty(summary.FailureList);
            Assert.Equal(1, summary.Ingested);
            Assert.True(await CountAsync(db, "SELECT count(*) FROM prem_index.chunk") > 100);
            Assert.True(await CountAsync(db, "SELECT max(length(content))::bigint FROM prem_index.chunk") <= MarkdownChunker.MaxParagraphChars);
        }
    }

    [Fact]
    public async Task A_document_the_index_refuses_is_one_failure_and_the_run_goes_on()
    {
        var folder = Directory.CreateTempSubdirectory("prem-oversized-").FullName;
        File.WriteAllText(Path.Combine(folder, "a-big.md"), "# Big\n\nSmall for now.\n");
        File.WriteAllText(Path.Combine(folder, "c-kept.md"), "# Kept\n\nUnchanged.\n");
        File.WriteAllText(Path.Combine(folder, "d-gone.md"), "# Gone\n\nDeleted before the second run.\n");

        var (db, tenant) = await DatabaseAsync();
        await using (db)
        {
            var pipeline = new IngestPipeline(db, new HashEmbeddingProvider(), chunkers: new ChunkerRegistry(new WholeText()));
            var first = await pipeline.RunAsync(tenant, new FileSystemSource(folder, DocumentAccess.Everyone), "whole-text");
            Assert.Equal(3, first.Ingested);

            File.WriteAllText(Path.Combine(folder, "a-big.md"), $"# Big\n\n{OversizedParagraphTests.DistinctWords(Words, " ")}\n");
            File.WriteAllText(Path.Combine(folder, "b-new.md"), "# New\n\nAdded after the big file changed.\n");
            File.Delete(Path.Combine(folder, "d-gone.md"));

            var second = await pipeline.RunAsync(tenant, new FileSystemSource(folder, DocumentAccess.Everyone), "whole-text");

            var failure = Assert.Single(second.FailureList);
            Assert.Equal("a-big.md", failure.Path);
            Assert.StartsWith("the index could not store it (54000", failure.Reason);
            Assert.Equal(1, second.Ingested);
            Assert.Equal(1, second.Unchanged);
            Assert.Equal(1, second.OrphansRemoved);
            Assert.False(second.ReconciliationSkipped);

            // The refused document keeps its earlier entry, the new one is
            // written, and the deleted one is gone.
            Assert.Equal(1L, await CountAsync(db, "SELECT count(*) FROM prem_index.chunk c JOIN prem_index.document d ON d.id = c.document_id WHERE d.path = 'a-big.md' AND c.content LIKE '%Small for now.%'"));
            Assert.Equal(1L, await CountAsync(db, "SELECT count(*) FROM prem_index.document WHERE path = 'b-new.md'"));
            Assert.Equal(0L, await CountAsync(db, "SELECT count(*) FROM prem_index.document WHERE path = 'd-gone.md'"));
        }
    }
}

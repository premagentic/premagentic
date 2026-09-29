using Premagentic.Core;
using Premagentic.Core.Admin;
using Premagentic.Core.Embeddings;
using Premagentic.Core.Ingestion;
using Premagentic.Core.Sources.Registry;
using Premagentic.Core.Storage;

namespace Premagentic.Tests;

/// <summary>
/// Cuts one chunk per line that has text, so its chunks cannot be mistaken for
/// the Markdown chunker's, which cuts one per heading.
/// </summary>
internal sealed class LineChunker(string name = LineChunker.DefaultName) : IChunker
{
    public const string DefaultName = "by-line";

    public string Name => name;

    public IReadOnlyList<DocumentChunk> Chunk(SourceDocument document) =>
        document.Text.Split('\n').Select(line => line.Trim()).Where(line => line.Length > 0)
            .Select((line, seq) => new DocumentChunk(seq, "", line)).ToArray();
}

/// <summary>The chunker registry, on its own. No database.</summary>
public sealed class ChunkerRegistryTests
{
    [Fact]
    public void The_registry_holds_markdown_first_matches_names_ignoring_case_and_refuses_bad_or_twin_names()
    {
        var registry = new ChunkerRegistry(new LineChunker("Contracts"), new LineChunker());

        Assert.Equal(["markdown", "by-line", "Contracts"], registry.Names);
        Assert.Same(MarkdownChunker.Instance, registry.Find("MARKDOWN"));
        Assert.Equal("Contracts", registry.Canonical("contracts"));
        Assert.Null(registry.Find("runbooks"));
        Assert.Contains("'runbooks'", Assert.Throws<UnknownChunkerException>(() => registry.Resolve("runbooks")).Message);
        Assert.Throws<ArgumentException>(() => registry.Canonical("runbooks"));

        Assert.Equal(["markdown"], ChunkerRegistry.BuiltIn.Names);
        Assert.Throws<ArgumentException>(() => new ChunkerRegistry(new LineChunker("Markdown")));
        Assert.Throws<ArgumentException>(() => new ChunkerRegistry(new LineChunker("a"), new LineChunker("A")));
        foreach (var bad in new[] { "", "-lead", "has space", "slash/name", new string('x', 65) })
            Assert.Throws<ArgumentException>(() => new ChunkerRegistry(new LineChunker(bad)));
    }
}

/// <summary>
/// The chunker seam end to end: a source names its chunker, a run cuts with
/// it, a name the process does not have is refused when the source is added
/// and fails the run closed, and a change of chunker stores every document of
/// the source again. Every file here is invented. Requires a running Docker
/// daemon.
/// </summary>
public sealed class ChunkerTests(DatastoreTestDatabase server) : IClassFixture<DatastoreTestDatabase>
{
    private static readonly AdminActor Tester = new("cli", "test-account");

    private static readonly (string, string)[] Desk =
    [
        ("opening.md", "# Opening\n\n## Doors\nThe side door opens at eight.\nThe front door opens at nine.\n"),
        ("closing.md", "# Closing\n\n## Lights\nThe last person out turns off the lights.\n"),
    ];

    private sealed class Env(PremagenticDatabase db, Guid tenant) : IAsyncDisposable
    {
        public PremagenticDatabase Db => db;
        public Guid Tenant => tenant;
        public IngestRuns Runs { get; } = new(db, tenant);

        public SourceRegistry Registry(ChunkerRegistry? chunkers) => new(db, tenant, chunkers);

        public IngestPipeline Pipeline(ChunkerRegistry? chunkers) => new(db, new HashEmbeddingProvider(), chunkers: chunkers);

        public Task<RecordedIngestResult> RunAsync(RegisteredSource source, ChunkerRegistry? chunkers) =>
            Runs.RunAsync(Pipeline(chunkers), source.ToFileSystemSource(), source.Id, chunker: source.Chunker);

        /// <summary>Per stored document, by path: its chunker, updated_at, and its chunks' ids and contents in order.</summary>
        public async Task<Dictionary<string, Stored>> IndexAsync()
        {
            await using var cmd = db.DataSource.CreateCommand("""
                SELECT d.path, d.chunker, d.updated_at, c.id, c.content
                FROM prem_index.document d JOIN prem_index.chunk c ON c.document_id = d.id
                ORDER BY d.path, c.seq
                """);
            var stored = new Dictionary<string, Stored>(StringComparer.Ordinal);
            await using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
            {
                var path = r.GetString(0);
                if (!stored.TryGetValue(path, out var doc))
                    stored[path] = doc = new Stored(r.GetString(1), r.GetFieldValue<DateTimeOffset>(2), [], []);
                doc.ChunkIds.Add(r.GetGuid(3));
                doc.Contents.Add(r.GetString(4));
            }
            return stored;
        }

        public ValueTask DisposeAsync() => db.DisposeAsync();
    }

    private sealed record Stored(string Chunker, DateTimeOffset UpdatedAt, List<Guid> ChunkIds, List<string> Contents);

    private async Task<Env> EmptyAsync()
    {
        var db = new PremagenticDatabase(await server.CreateDatabaseAsync());
        await db.InitializeAsync();
        return new Env(db, await db.EnsureTenantAsync("t", "T"));
    }

    [Fact]
    public async Task A_source_that_names_a_registered_chunker_is_cut_by_it_and_the_default_is_markdown()
    {
        await using var e = await EmptyAsync();
        var chunkers = new ChunkerRegistry(new LineChunker());
        var registry = e.Registry(chunkers);

        var byLine = await registry.AddAsync("lines", SourcesTests.Folder(Desk), "lines", false, false, Tester, "BY-LINE");
        var plain = await registry.AddAsync("plain", SourcesTests.Folder(Desk), "plain", false, false, Tester);
        Assert.Equal(("by-line", "markdown"), (byLine.Chunker, plain.Chunker));

        var lineRun = await e.RunAsync(byLine, chunkers);
        var plainRun = await e.RunAsync(plain, chunkers);

        var index = await e.IndexAsync();
        var opening = "# Opening\n\n## Doors\nThe side door opens at eight.\nThe front door opens at nine.\n";
        Assert.Equal(
            ["# Opening", "## Doors", "The side door opens at eight.", "The front door opens at nine."],
            index["lines/opening.md"].Contents);
        Assert.Equal(MarkdownChunker.Chunk(opening).Select(c => c.Content), index["plain/opening.md"].Contents);
        Assert.Equal(["by-line", "by-line", "markdown", "markdown"], index.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => kv.Value.Chunker));
        Assert.Equal(
            ("by-line", "markdown"),
            ((await e.Runs.GetAsync(lineRun.RunId))!.Chunker, (await e.Runs.GetAsync(plainRun.RunId))!.Chunker));

        // A pipeline given no registry has the built-in chunkers.
        var summary = await new IngestPipeline(e.Db, new HashEmbeddingProvider()).RunAsync(e.Tenant, plain.ToFileSystemSource());
        Assert.Equal((0, 2), (summary.Ingested, summary.Unchanged));
    }

    [Fact]
    public async Task A_chunker_the_process_does_not_have_is_refused_when_a_source_is_added_or_changed()
    {
        await using var e = await EmptyAsync();
        var registry = e.Registry(null);
        var folder = SourcesTests.Folder(Desk);

        var refused = await Assert.ThrowsAsync<ArgumentException>(() =>
            registry.AddAsync("lines", folder, "lines", false, false, Tester, LineChunker.DefaultName));
        Assert.Contains("'by-line'", refused.Message);
        Assert.Empty(await registry.ListAsync());

        await registry.AddAsync("desk", folder, "desk", false, false, Tester);
        await Assert.ThrowsAsync<ArgumentException>(() =>
            registry.UpdateAsync("desk", okfBundle: true, undeclaredIsMachine: null, Tester, LineChunker.DefaultName));
        var desk = (await registry.FindAsync("desk"))!;
        Assert.Equal(("markdown", false), (desk.Chunker, desk.OkfBundle));
        Assert.Single(await new ChangeRecord(e.Db, e.Tenant).ListAsync());
    }

    [Fact]
    public async Task A_run_under_a_chunker_the_process_does_not_have_fails_closed_and_is_recorded_with_the_name()
    {
        await using var e = await EmptyAsync();
        var withLines = new ChunkerRegistry(new LineChunker());
        var source = await e.Registry(withLines).AddAsync("lines", SourcesTests.Folder(Desk), "lines", false, false, Tester, LineChunker.DefaultName);
        await e.RunAsync(source, withLines);
        var before = await e.IndexAsync();

        // The same source, run by a process that has only the built-in chunkers.
        var refused = await Assert.ThrowsAsync<UnknownChunkerException>(() => e.RunAsync(source, null));

        Assert.Equal(LineChunker.DefaultName, refused.ChunkerName);
        var run = (await e.Runs.LastAsync(source.Id))!;
        Assert.Equal((IngestRunOutcome.Failed, LineChunker.DefaultName), (run.Outcome, run.Chunker));
        Assert.Contains("'by-line'", run.Error);
        Assert.Null(run.Summary);
        // Nothing was cut again, stored or removed.
        var after = await e.IndexAsync();
        Assert.Equal(before.Keys, after.Keys);
        foreach (var (path, doc) in before)
        {
            Assert.Equal((doc.Chunker, doc.UpdatedAt), (after[path].Chunker, after[path].UpdatedAt));
            Assert.Equal(doc.ChunkIds, after[path].ChunkIds);
        }

        // A source never run before gets no documents at all.
        var fresh = await e.Registry(withLines).AddAsync("fresh", SourcesTests.Folder(Desk), "fresh", false, false, Tester, LineChunker.DefaultName);
        await Assert.ThrowsAsync<UnknownChunkerException>(() => e.RunAsync(fresh, null));
        Assert.DoesNotContain((await e.IndexAsync()).Keys, path => path.StartsWith("fresh/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_change_of_chunker_stores_every_document_of_the_source_again_and_moves_its_updated_at()
    {
        await using var e = await EmptyAsync();
        var chunkers = new ChunkerRegistry(new LineChunker());
        var registry = e.Registry(chunkers);
        var desk = await registry.AddAsync("desk", SourcesTests.Folder(Desk), "desk", false, false, Tester);
        var other = await registry.AddAsync("other", SourcesTests.Folder(Desk), "other", false, false, Tester);
        await e.RunAsync(desk, chunkers);
        await e.RunAsync(other, chunkers);
        var first = await e.IndexAsync();

        var (_, switched, changed) = await registry.UpdateAsync("desk", null, null, Tester, LineChunker.DefaultName);
        Assert.True(changed);
        var run = await e.RunAsync(switched, chunkers);

        Assert.Equal((2, 0), (run.Summary.Ingested, run.Summary.Unchanged));
        var second = await e.IndexAsync();
        foreach (var path in new[] { "desk/opening.md", "desk/closing.md" })
        {
            Assert.Equal(LineChunker.DefaultName, second[path].Chunker);
            Assert.True(second[path].UpdatedAt > first[path].UpdatedAt, $"{path} kept its updated_at");
            Assert.Empty(second[path].ChunkIds.Intersect(first[path].ChunkIds));
        }
        Assert.Equal("The last person out turns off the lights.", second["desk/closing.md"].Contents[^1]);
        // Another source's documents are not touched.
        Assert.Equal(first["other/opening.md"].UpdatedAt, second["other/opening.md"].UpdatedAt);

        // Once cut by the new chunker, the next run finds nothing to do.
        var again = await e.RunAsync(switched, chunkers);
        Assert.Equal((0, 2), (again.Summary.Ingested, again.Summary.Unchanged));

        // And switching back cuts them again.
        var (_, back, _) = await registry.UpdateAsync("desk", null, null, Tester, "markdown");
        Assert.Equal(2, (await e.RunAsync(back, chunkers)).Summary.Ingested);
        Assert.Equal("markdown", (await e.IndexAsync())["desk/opening.md"].Chunker);
    }
}

using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using Premagentic.Core;
using Premagentic.Core.Embeddings;
using Premagentic.Core.Ingestion;
using Premagentic.Core.Retrieval;
using Premagentic.Core.Security;
using Premagentic.Core.Sources;
using Premagentic.Core.Storage;
using Testcontainers.PostgreSql;

namespace Premagentic.Tests;

/// <summary>
/// The authorization gate, end to end against a real, stock PostgreSQL.
/// <para>
/// Every case here asserts both directions: that the authorized caller DOES
/// reach the document and that the unauthorized one does not. A test that only
/// checked the denial would pass just as happily against a build that returned
/// nothing to anybody, which is the failure mode that looks like security.
/// </para>
/// Requires a running Docker daemon.
/// </summary>
public sealed class AccessGateTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres =
        new PostgreSqlBuilder(DatastoreTestDatabase.Image).Build();

    private PremagenticDatabase _db = null!;
    private Guid _tenantId;
    private HybridSearch _search = null!;
    private SectionFetcher _sections = null!;

    private const string HandbookPath = "corp/handbook.md";
    private const string SalaryPath = "corp/salary-bands.md";
    private const string UnresolvedPath = "corp/board-minutes.md";

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        _db = new PremagenticDatabase(_postgres.GetConnectionString());
        await _db.InitializeAsync();
        _tenantId = await _db.EnsureTenantAsync("test", "Test Tenant");

        var embedder = new HashEmbeddingProvider();
        await new IngestPipeline(_db, embedder).RunAsync(_tenantId, new InMemorySource("corp", [
            // Everyone may read the handbook.
            Doc(HandbookPath, "Employee handbook", """
                # Employee handbook

                ## Expense claims
                Submit a zeppelin expense claim within thirty days of the trip.
                """, DocumentAccess.Everyone),

            // Only HR may read the salary bands.
            Doc(SalaryPath, "Salary bands", """
                # Salary bands

                ## Band four
                Band four zeppelin engineers are paid between one and two kettles.
                """, DocumentAccess.For("group:hr")),

            // A document whose permissions the connector could not read.
            Doc(UnresolvedPath, "Board minutes", """
                # Board minutes

                ## Acquisition
                The zeppelin acquisition closes in the third quarter.
                """, DocumentAccess.NoOne),
        ]));

        _search = new HybridSearch(_db, embedder);
        _sections = new SectionFetcher(_db);
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        await _postgres.DisposeAsync();
    }

    private async Task<string[]> SearchPaths(AccessScope scope, string query = "zeppelin") =>
        (await _search.SearchAsync(_tenantId, query, new SearchOptions(scope, TopK: 10)))
        .Hits.Select(h => h.Path).Distinct().ToArray();

    [Fact]
    public async Task Public_document_reaches_a_caller_with_no_principals()
    {
        Assert.Contains(HandbookPath, await SearchPaths(AccessScope.PublicOnly));
    }

    [Fact]
    public async Task Restricted_document_is_invisible_without_its_principal()
    {
        var paths = await SearchPaths(AccessScope.ForPrincipals("t", "group:engineering"));

        // The control: this caller can see something, so an empty result would
        // not silently satisfy the assertion below.
        Assert.Contains(HandbookPath, paths);
        Assert.DoesNotContain(SalaryPath, paths);
    }

    [Fact]
    public async Task Restricted_document_reaches_the_holder_of_its_principal()
    {
        Assert.Contains(SalaryPath, await SearchPaths(AccessScope.ForPrincipals("t", "group:hr")));
    }

    [Fact]
    public async Task One_matching_principal_out_of_several_is_enough()
    {
        var paths = await SearchPaths(
            AccessScope.ForPrincipals("t", "group:engineering", "group:hr", "user:42"));
        Assert.Contains(SalaryPath, paths);
    }

    [Fact]
    public async Task A_document_the_connector_could_not_resolve_reaches_nobody()
    {
        Assert.DoesNotContain(UnresolvedPath, await SearchPaths(AccessScope.PublicOnly));
        Assert.DoesNotContain(UnresolvedPath, await SearchPaths(AccessScope.ForPrincipals("t", "group:hr")));

        // It is still in the index, so the gap is visible to an operator rather
        // than being silently dropped at ingest.
        Assert.Contains(UnresolvedPath, await SearchPaths(AccessScope.UnrestrictedAudited("test")));
    }

    [Fact]
    public async Task Unrestricted_scope_sees_everything_and_is_labeled_on_the_event()
    {
        var paths = await SearchPaths(AccessScope.UnrestrictedAudited("operator-review"));
        Assert.Contains(HandbookPath, paths);
        Assert.Contains(SalaryPath, paths);

        await using var cmd = _db.DataSource.CreateCommand(
            "SELECT access_label FROM prem_config.retrieval_event ORDER BY created_at DESC LIMIT 1");
        Assert.Equal("unrestricted:operator-review", (string?)await cmd.ExecuteScalarAsync());
    }

    [Fact]
    public async Task Section_fetch_obeys_the_same_gate_as_search()
    {
        var allowed = await _sections.GetAsync(
            _tenantId, AccessScope.ForPrincipals("t", "group:hr"),
            SalaryPath, heading: null, includeHistorical: false);
        Assert.NotNull(allowed);
        Assert.NotEmpty(allowed!.Chunks);

        // Unauthorized: reported as absent, not as forbidden, so the tool
        // cannot be used to enumerate what exists.
        var denied = await _sections.GetAsync(
            _tenantId, AccessScope.ForPrincipals("t", "group:engineering"),
            SalaryPath, heading: null, includeHistorical: false);
        Assert.Null(denied);
    }

    [Fact]
    public async Task A_permission_change_with_no_content_edit_still_lands()
    {
        var pipeline = new IngestPipeline(_db, new HashEmbeddingProvider());
        const string path = "perm/memo.md";
        const string text = """
            # Memo

            ## Subject
            The kettle rollout is delayed.
            """;

        var first = await pipeline.RunAsync(_tenantId, new InMemorySource("perm", [
            Doc(path, "Memo", text, DocumentAccess.For("group:hr"))]));
        Assert.Equal(1, first.Ingested);

        // Same bytes, different audience. A hash-only comparison would skip
        // this and keep serving the old audience.
        var second = await pipeline.RunAsync(_tenantId, new InMemorySource("perm", [
            Doc(path, "Memo", text, DocumentAccess.For("group:finance"))]));
        Assert.Equal(1, second.Ingested);
        Assert.Equal(0, second.Unchanged);

        Assert.DoesNotContain(path, await SearchPaths(AccessScope.ForPrincipals("t", "group:hr"), "kettle rollout"));
        Assert.Contains(path, await SearchPaths(AccessScope.ForPrincipals("t", "group:finance"), "kettle rollout"));
    }

    [Fact]
    public async Task An_unchanged_rerun_skips_and_removes_nothing()
    {
        var pipeline = new IngestPipeline(_db, new HashEmbeddingProvider());
        var source = new InMemorySource("idem", [
            Doc("idem/note.md", "Note", "# Note\n\n## Body\nA gizmo note.\n", DocumentAccess.Everyone)]);

        Assert.Equal(1, (await pipeline.RunAsync(_tenantId, source)).Ingested);
        var again = await pipeline.RunAsync(_tenantId, source);
        Assert.Equal(0, again.Ingested);
        Assert.Equal(1, again.Unchanged);
        Assert.Equal(0, again.OrphansRemoved);
    }

    [Fact]
    public async Task A_connector_yielding_outside_its_prefix_is_refused()
    {
        var pipeline = new IngestPipeline(_db, new HashEmbeddingProvider());
        var rogue = new InMemorySource("scoped", [
            Doc("elsewhere/leak.md", "Leak", "# Leak\n\n## Body\nContent.\n", DocumentAccess.Everyone)]);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => pipeline.RunAsync(_tenantId, rogue));
        Assert.Contains("outside its declared prefix", ex.Message);
    }

    private static SourceDocument Doc(string path, string title, string text, DocumentAccess access) =>
        new(path, title, text,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))),
            access);

    /// <summary>One connector over a fixed set of documents, each with its own access value.</summary>
    private sealed class InMemorySource(string pathPrefix, IReadOnlyList<SourceDocument> docs) : IDocumentSource
    {
        public string Name => "test-in-memory";
        public string PathPrefix { get; } = pathPrefix;

        public async IAsyncEnumerable<SourceRead> EnumerateAsync(
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            foreach (var doc in docs)
            {
                ct.ThrowIfCancellationRequested();
                yield return SourceRead.Ok(doc);
            }
            await Task.CompletedTask;
        }
    }

    /// <summary>
    /// A connector that yields exactly what it is given, including failures and
    /// nothing at all, so reconciliation behavior can be tested directly.
    /// </summary>
    private sealed class ScriptedSource(string pathPrefix, IReadOnlyList<SourceRead> reads) : IDocumentSource
    {
        public string Name => "test-scripted";
        public string PathPrefix { get; } = pathPrefix;

        public async IAsyncEnumerable<SourceRead> EnumerateAsync(
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            foreach (var r in reads)
            {
                ct.ThrowIfCancellationRequested();
                yield return r;
            }
            await Task.CompletedTask;
        }
    }

    // Reconciliation safety. The first two reproduce bugs found by a peer session
    // on 2026-09-21 and confirmed at the console before they were fixed: an empty
    // source wiped the index, and one unreadable file aborted the whole run.

    [Fact]
    public async Task A_source_that_yields_nothing_does_NOT_wipe_the_index()
    {
        var pipeline = new IngestPipeline(_db, new HashEmbeddingProvider());
        var doc = Doc("vanish/matter.md", "Matter",
            "# Matter\n## Scope\nA zirconium retainer.\n", DocumentAccess.Everyone);

        Assert.Equal(1, (await pipeline.RunAsync(_tenantId,
            new ScriptedSource("vanish", [SourceRead.Ok(doc)]))).Ingested);

        // The unmounted-volume shape: the root exists and there is nothing in it.
        var empty = await pipeline.RunAsync(_tenantId, new ScriptedSource("vanish", []));
        Assert.True(empty.ReconciliationSkipped);
        Assert.Equal(0, empty.OrphansRemoved);

        // The control that lets this test fail: the document is still retrievable.
        Assert.Contains("vanish/matter.md", await SearchPaths(AccessScope.PublicOnly, "zirconium retainer"));
    }

    [Fact]
    public async Task An_operator_can_still_empty_a_source_on_purpose()
    {
        var pipeline = new IngestPipeline(_db, new HashEmbeddingProvider());
        var doc = Doc("onpurpose/gone.md", "Gone",
            "# Gone\n## Scope\nA kettle note.\n", DocumentAccess.Everyone);
        await pipeline.RunAsync(_tenantId, new ScriptedSource("onpurpose", [SourceRead.Ok(doc)]));

        var cleared = await pipeline.RunAsync(
            _tenantId, new ScriptedSource("onpurpose", []), log: null, allowEmptySource: true);
        Assert.False(cleared.ReconciliationSkipped);
        Assert.Equal(1, cleared.OrphansRemoved);
        Assert.DoesNotContain("onpurpose/gone.md", await SearchPaths(AccessScope.PublicOnly, "kettle note"));
    }

    [Fact]
    public async Task An_unreadable_path_keeps_its_index_entry_and_is_counted()
    {
        var pipeline = new IngestPipeline(_db, new HashEmbeddingProvider());
        var readable = Doc("prot/open.md", "Open",
            "# Open\n## Scope\nA gizmo policy.\n", DocumentAccess.Everyone);
        var locked = Doc("prot/locked.md", "Locked",
            "# Locked\n## Scope\nA gizmo secret.\n", DocumentAccess.Everyone);

        Assert.Equal(2, (await pipeline.RunAsync(_tenantId, new ScriptedSource("prot",
            [SourceRead.Ok(readable), SourceRead.Ok(locked)]))).Ingested);

        // Next run: the volume is mounted but one file is encrypted or denied.
        var partial = await pipeline.RunAsync(_tenantId, new ScriptedSource("prot", [
            SourceRead.Ok(readable),
            SourceRead.Failed("prot/locked.md", "UnauthorizedAccessException: access denied"),
        ]));

        Assert.Equal(1, partial.Unreadable);
        Assert.Equal("prot/locked.md", Assert.Single(partial.FailureList).Path);
        Assert.False(partial.ReconciliationSkipped);
        Assert.Equal(0, partial.OrphansRemoved);

        var paths = await SearchPaths(AccessScope.PublicOnly, "gizmo");
        Assert.Contains("prot/open.md", paths);
        Assert.Contains("prot/locked.md", paths);
    }

    [Fact]
    public async Task One_unreadable_path_does_not_stop_the_rest_of_the_run()
    {
        var pipeline = new IngestPipeline(_db, new HashEmbeddingProvider());
        var summary = await pipeline.RunAsync(_tenantId, new ScriptedSource("mixed", [
            SourceRead.Failed("mixed/first-denied.md", "UnauthorizedAccessException"),
            SourceRead.Ok(Doc("mixed/second.md", "Second",
                "# Second\n## Scope\nA widget rule.\n", DocumentAccess.Everyone)),
            SourceRead.Failed("mixed/third-denied.md", "IOException: locked"),
        ]));

        Assert.Equal(3, summary.Scanned);
        Assert.Equal(1, summary.Ingested);
        Assert.Equal(2, summary.Unreadable);
        Assert.Contains("mixed/second.md", await SearchPaths(AccessScope.PublicOnly, "widget rule"));
    }

    [Fact]
    public async Task A_genuinely_deleted_path_is_still_reconciled_away()
    {
        var pipeline = new IngestPipeline(_db, new HashEmbeddingProvider());
        var keep = Doc("del/keep.md", "Keep",
            "# Keep\n## Scope\nA sprocket kept.\n", DocumentAccess.Everyone);
        var drop = Doc("del/drop.md", "Drop",
            "# Drop\n## Scope\nA sprocket dropped.\n", DocumentAccess.Everyone);
        await pipeline.RunAsync(_tenantId, new ScriptedSource("del",
            [SourceRead.Ok(keep), SourceRead.Ok(drop)]));

        // drop.md is ABSENT now, not unreadable, so it should still be removed.
        var after = await pipeline.RunAsync(_tenantId, new ScriptedSource("del", [SourceRead.Ok(keep)]));
        Assert.Equal(1, after.OrphansRemoved);

        var paths = await SearchPaths(AccessScope.PublicOnly, "sprocket");
        Assert.Contains("del/keep.md", paths);
        Assert.DoesNotContain("del/drop.md", paths);
    }
}

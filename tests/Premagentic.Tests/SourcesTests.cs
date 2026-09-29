using Premagentic.Core;
using Premagentic.Core.Admin;
using Premagentic.Core.Embeddings;
using Premagentic.Core.Ingestion;
using Premagentic.Core.Ingestion.Readers;
using Premagentic.Core.Okf;
using Premagentic.Core.Retrieval;
using Premagentic.Core.Security;
using Premagentic.Core.Sources;
using Premagentic.Core.Sources.Registry;
using Premagentic.Core.Storage;

namespace Premagentic.Tests;

/// <summary>
/// The sources registry, the record of every ingest run, what a run says about
/// the files it did not read, and the undeclared-authorship setting end to end.
/// Every file here is invented. Requires a running Docker daemon.
/// </summary>
public sealed class SourcesTests(DatastoreTestDatabase server) : IClassFixture<DatastoreTestDatabase>
{
    internal static readonly string BundleRoot = Path.Combine(AppContext.BaseDirectory, "Fixtures", "okf-bundle");

    private static readonly DateTimeOffset June = new(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly AdminActor Tester = new("cli", "test-account");

    /// <summary>The bundle's concepts that do not say who wrote them.</summary>
    internal static readonly string[] Undeclared =
        ["glossary.md", "misc/scratch.md", "misc/untyped.md", "misc/visitor-notes.md", "supplies/delivery-window.md"];

    private sealed class Env(PremagenticDatabase db, Guid tenant) : IAsyncDisposable
    {
        public PremagenticDatabase Db => db;
        public Guid Tenant => tenant;
        public SourceRegistry Registry { get; } = new(db, tenant);
        public IngestRuns Runs { get; } = new(db, tenant);
        public ChangeRecord Record { get; } = new(db, tenant);
        public IngestPipeline Pipeline { get; } = new(db, new HashEmbeddingProvider());
        public HybridSearch Search { get; } = new(db, new HashEmbeddingProvider());

        public async Task<SearchHit?> HitAsync(string query, string path, CallerKind caller) =>
            (await Search.SearchAsync(Tenant, query, new SearchOptions(
                AccessScope.PublicOnly, TopK: 10, Trust: TrustPolicy.Resolve(caller, null), AsOf: June))).Hits
            .FirstOrDefault(h => h.Path == path);

        public async Task<long> CountAsync(string sql)
        {
            await using var cmd = Db.DataSource.CreateCommand(sql);
            return (long)(await cmd.ExecuteScalarAsync())!;
        }

        public ValueTask DisposeAsync() => db.DisposeAsync();
    }

    private async Task<Env> EmptyAsync()
    {
        var db = new PremagenticDatabase(await server.CreateDatabaseAsync());
        await db.InitializeAsync();
        return new Env(db, await db.EnsureTenantAsync("t", "T"));
    }

    /// <summary>A new folder holding the given files.</summary>
    internal static string Folder(params (string Path, string Text)[] files)
    {
        var root = Directory.CreateTempSubdirectory("premagentic-sources-").FullName;
        Write(root, files);
        return root;
    }

    /// <summary>A copy of the fixture bundle, plus the given files.</summary>
    internal static string BundleCopy(params (string Path, string Text)[] extra)
    {
        var root = Directory.CreateTempSubdirectory("premagentic-bundle-").FullName;
        foreach (var file in Directory.EnumerateFiles(BundleRoot, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(root, Path.GetRelativePath(BundleRoot, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
        Write(root, extra);
        return root;
    }

    private static void Write(string root, (string Path, string Text)[] files)
    {
        foreach (var (path, text) in files)
        {
            var full = Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, text);
        }
    }

    private static readonly (string, string)[] MixedFolder =
    [
        ("minutes.txt", "# Minutes\n\n## Decisions\nThe loading dock opens at seven on weekdays.\n"),
        ("floor-plan.pdf", "%PDF-1.7 invented bytes"),
        ("scans/receipt.PDF", "%PDF-1.4 invented bytes"),
        ("budget.xlsx", "PK invented bytes"),
        ("letter.docx", "PK invented bytes"),
        ("README", "a file with no extension"),
        (".git/config", "[core]"),
        ("drafts/.~lock.letter.docx#", "lock file"),
    ];

    [Fact]
    public async Task The_connector_yields_a_skip_for_every_file_it_does_not_read_and_nothing_for_hidden_ones()
    {
        var source = new FileSystemSource(Folder(MixedFolder), DocumentAccess.Everyone, "office");

        var reads = new List<SourceRead>();
        await foreach (var read in source.ReadThroughAsync(ReaderRegistry.BuiltIn)) reads.Add(read);

        Assert.Equal(["office/minutes.txt"], reads.Where(r => r.Document is not null).Select(r => r.Document!.Path));
        Assert.DoesNotContain(reads, r => r.Failure is not null);
        Assert.Equal(
            [
                ("office/README", "(none)"),
                ("office/budget.xlsx", ".xlsx"),
                ("office/floor-plan.pdf", ".pdf"),
                ("office/letter.docx", ".docx"),
                ("office/scans/receipt.PDF", ".pdf"),
            ],
            reads.Where(r => r.Skip is not null).Select(r => (r.Skip!.Path, r.Skip.Extension)).OrderBy(t => t.Path, StringComparer.Ordinal));
    }

    [Fact]
    public async Task A_mixed_folder_reports_its_skipped_files_by_extension_and_still_indexes_the_readable_one()
    {
        await using var e = await EmptyAsync();

        var summary = await e.Pipeline.RunAsync(e.Tenant, new FileSystemSource(Folder(MixedFolder), DocumentAccess.Everyone));

        Assert.Equal((1, 1, 0), (summary.Scanned, summary.Ingested, summary.Unreadable));
        Assert.Equal(5, summary.Skipped);
        Assert.Equal(
            new Dictionary<string, int> { ["(none)"] = 1, [".docx"] = 1, [".pdf"] = 2, [".xlsx"] = 1 },
            summary.SkippedFormats);
        Assert.Equal(1L, await e.CountAsync("SELECT count(*) FROM prem_index.document"));
        Assert.NotNull(await e.HitAsync("loading dock opens at seven", "minutes.txt", CallerKind.Person));
    }

    [Fact]
    public async Task A_run_record_matches_the_summary()
    {
        await using var e = await EmptyAsync();
        var folder = BundleCopy(("attachments/price-sheet.pdf", "%PDF invented"), ("attachments/bench.JPG", "invented"));
        var registered = await e.Registry.AddAsync("greenhouse", folder, "greenhouse", okfBundle: true, undeclaredIsMachine: false, Tester);

        var result = await e.Runs.RunAsync(e.Pipeline, registered.ToFileSystemSource(), registered.Id);
        var record = await e.Runs.GetAsync(result.RunId);

        Assert.NotNull(record);
        Assert.Equal(IngestRunOutcome.Completed, record.Outcome);
        Assert.NotNull(record.FinishedAt);
        Assert.True(record.FinishedAt >= record.StartedAt);
        Assert.Null(record.Error);
        Assert.Equal(
            (registered.Id, "filesystem", registered.Folder, "greenhouse", true, false),
            (record.SourceId!.Value, record.Connector, record.Folder, record.PathPrefix, record.OkfBundle, record.UndeclaredIsMachine));

        var s = result.Summary;
        var r = record.Summary!;
        Assert.Equal(
            (s.Scanned, s.Ingested, s.Unchanged, s.ChunksEmbedded, s.OrphansRemoved, s.DeniedToEveryone, s.Unreadable,
             s.Skipped, s.UndeclaredAuthorship, s.ReconciliationSkipped),
            (r.Scanned, r.Ingested, r.Unchanged, r.ChunksEmbedded, r.OrphansRemoved, r.DeniedToEveryone, r.Unreadable,
             r.Skipped, r.UndeclaredAuthorship, r.ReconciliationSkipped));
        Assert.Equal(s.SkippedFormats.OrderBy(kv => kv.Key), r.SkippedFormats.OrderBy(kv => kv.Key));
        Assert.Equal(s.FailureList, r.FailureList);
        Assert.Equal((result.BundleReport!.OkfVersion, (bool?)result.BundleReport.VersionKnown), (record.OkfVersion, record.OkfVersionKnown));
        Assert.Equal(result.BundleReport.Issues, record.ConformanceIssues);

        // Real numbers, not two zeros that agree. No folder rule covers the
        // source, so every document is indexed and readable by nobody, and said so.
        Assert.Equal((16, 16, 2, 5, 16), (r.Scanned, r.Ingested, r.Skipped, r.UndeclaredAuthorship, r.DeniedToEveryone));
        Assert.Equal(new Dictionary<string, int> { [".jpg"] = 1, [".pdf"] = 1 }, r.SkippedFormats);
        Assert.Equal("0.2", record.OkfVersion);
        Assert.Contains(record.ConformanceIssues, i => i.Path == "greenhouse/misc/broken.md" && i.Problem == OkfConformanceProblem.UnparseableFrontmatter);
        Assert.Equal(record.Id, (await e.Runs.LastAsync(registered.Id))!.Id);
    }

    [Fact]
    public async Task Every_value_of_a_run_survives_being_recorded()
    {
        await using var e = await EmptyAsync();
        var source = new FileSystemSource(Path.Combine(Path.GetTempPath(), "premagentic-anywhere"), DocumentAccess.Everyone, "lab")
        {
            OkfBundle = true,
            UndeclaredAuthorshipIsMachine = true,
        };
        // Every number different, so no two columns can be swapped unseen.
        var summary = new IngestSummary(
            Scanned: 41, Ingested: 3, Unchanged: 29, ChunksEmbedded: 17, OrphansRemoved: 2, DeniedToEveryone: 5,
            Unreadable: 1, ReconciliationSkipped: false,
            Failures: [new SourceFailure("lab/locked.md", "IOException: in use")],
            SkippedByExtension: new Dictionary<string, int> { [".pdf"] = 4, [".docx"] = 6 },
            UndeclaredAuthorship: 13);
        var report = new OkfBundleReport("0.1",
        [
            new OkfConformanceIssue("lab/a.md", OkfConformanceProblem.MissingType),
            new OkfConformanceIssue("lab/b.md", OkfConformanceProblem.MalformedField, "verified, tags"),
        ]);

        var id = await e.Runs.StartAsync(source, sourceId: null);
        Assert.Equal(IngestRunOutcome.Running, (await e.Runs.GetAsync(id))!.Outcome);
        await e.Runs.CompleteAsync(id, summary, report);
        var run = (await e.Runs.GetAsync(id))!;
        var r = run.Summary!;

        Assert.Equal(
            (41, 3, 29, 17, 2, 5, 1, 13, false),
            (r.Scanned, r.Ingested, r.Unchanged, r.ChunksEmbedded, r.OrphansRemoved, r.DeniedToEveryone, r.Unreadable,
             r.UndeclaredAuthorship, r.ReconciliationSkipped));
        Assert.Equal(summary.FailureList, r.FailureList);
        Assert.Equal(summary.SkippedFormats.OrderBy(kv => kv.Key), r.SkippedFormats.OrderBy(kv => kv.Key));
        Assert.Equal(10L, await e.CountAsync($"SELECT skipped::bigint FROM prem_config.ingest_run WHERE id = '{id}'"));
        Assert.Equal(("0.1", (bool?)true), (run.OkfVersion, run.OkfVersionKnown));
        Assert.Equal(report.Issues, run.ConformanceIssues);
        Assert.Equal((IngestRunOutcome.Completed, "lab", true, true), (run.Outcome, run.PathPrefix, run.OkfBundle, run.UndeclaredIsMachine));

        // A finished run is never finished again.
        await e.Runs.FailAsync(id, "late");
        Assert.Equal(IngestRunOutcome.Completed, (await e.Runs.GetAsync(id))!.Outcome);

        var refused = await e.Runs.StartAsync(source, sourceId: null);
        await e.Runs.CompleteAsync(refused, summary with { ReconciliationSkipped = true }, report: null);
        var refusedRun = (await e.Runs.GetAsync(refused))!;
        Assert.Equal(IngestRunOutcome.ReconciliationRefused, refusedRun.Outcome);
        Assert.Equal(((string?)null, (bool?)null), (refusedRun.OkfVersion, refusedRun.OkfVersionKnown));
        Assert.Empty(refusedRun.ConformanceIssues);
    }

    [Fact]
    public async Task A_run_that_fails_is_recorded_as_failed()
    {
        await using var e = await EmptyAsync();
        var missing = Path.Combine(Path.GetTempPath(), "premagentic-missing-" + Guid.NewGuid().ToString("N"));

        await Assert.ThrowsAsync<DirectoryNotFoundException>(() =>
            e.Runs.RunAsync(e.Pipeline, new FileSystemSource(missing, DocumentAccess.Everyone), sourceId: null));

        var run = Assert.Single(await e.Runs.ListAsync());
        Assert.Equal(IngestRunOutcome.Failed, run.Outcome);
        Assert.NotNull(run.FinishedAt);
        Assert.Null(run.Summary);
        Assert.Null(run.SourceId);
        Assert.Contains("DirectoryNotFoundException", run.Error);
    }

    [Fact]
    public async Task A_run_that_sees_only_skipped_files_reconciles_and_one_that_sees_nothing_still_refuses()
    {
        await using var e = await EmptyAsync();
        var reachable = Folder(("notes.md", "# Notes\n\n## Body\nThe hoses are drained in October.\n"));
        var vanished = Folder(("notes.md", "# Notes\n\n## Body\nThe vents close at dusk.\n"));
        var withSkips = new FileSystemSource(reachable, DocumentAccess.Everyone, "reachable");
        var withNothing = new FileSystemSource(vanished, DocumentAccess.Everyone, "vanished");
        await e.Pipeline.RunAsync(e.Tenant, withSkips);
        await e.Pipeline.RunAsync(e.Tenant, withNothing);

        // One folder now holds only a file this version does not read; the other holds nothing at all.
        File.Delete(Path.Combine(reachable, "notes.md"));
        File.WriteAllText(Path.Combine(reachable, "plan.pdf"), "%PDF invented");
        File.Delete(Path.Combine(vanished, "notes.md"));

        var seen = await e.Pipeline.RunAsync(e.Tenant, withSkips);
        Assert.Equal((false, 1, 0, 1), (seen.ReconciliationSkipped, seen.OrphansRemoved, seen.Scanned, seen.Skipped));
        Assert.Equal(0L, await e.CountAsync("SELECT count(*) FROM prem_index.document WHERE path LIKE 'reachable/%'"));

        var nothing = await e.Pipeline.RunAsync(e.Tenant, withNothing);
        Assert.Equal((true, 0), (nothing.ReconciliationSkipped, nothing.OrphansRemoved));
        Assert.Equal(1L, await e.CountAsync("SELECT count(*) FROM prem_index.document WHERE path LIKE 'vanished/%'"));
    }

    [Fact]
    public async Task A_file_with_no_text_to_index_is_counted_as_skipped_and_empty()
    {
        await using var e = await EmptyAsync();
        var folder = Folder(
            ("blank.md", ""),
            ("spaces.txt", "   \n\n  \n"),
            ("only-frontmatter.md", "---\ntitle: Nothing below\n---\n"),
            ("rota.md", "# Rota\n\n## Weekend\nTwo people open the shop on Saturdays.\n"));

        var summary = await e.Pipeline.RunAsync(e.Tenant, new FileSystemSource(folder, DocumentAccess.Everyone));

        Assert.Equal((4, 1, 3), (summary.Scanned, summary.Ingested, summary.Skipped));
        Assert.Equal(new Dictionary<string, int> { [IngestSummary.EmptyKey] = 3 }, summary.SkippedFormats);
        Assert.Equal(1L, await e.CountAsync("SELECT count(*) FROM prem_index.document"));
    }

    [Fact]
    public async Task A_run_that_refused_to_reconcile_is_recorded_as_such()
    {
        await using var e = await EmptyAsync();
        var folder = Folder(("notes.md", "# Notes\n\n## Body\nThe compost is turned on Fridays.\n"));
        var source = new FileSystemSource(folder, DocumentAccess.Everyone, "notes");

        var first = await e.Runs.RunAsync(e.Pipeline, source, sourceId: null);
        File.Delete(Path.Combine(folder, "notes.md"));
        var second = await e.Runs.RunAsync(e.Pipeline, source, sourceId: null);

        Assert.Equal(IngestRunOutcome.Completed, (await e.Runs.GetAsync(first.RunId))!.Outcome);
        var refused = await e.Runs.GetAsync(second.RunId);
        Assert.Equal(IngestRunOutcome.ReconciliationRefused, refused!.Outcome);
        Assert.True(refused.Summary!.ReconciliationSkipped);
        Assert.Equal(1L, await e.CountAsync("SELECT count(*) FROM prem_index.document"));
    }

    [Fact]
    public async Task Switching_undeclared_authorship_restores_the_values_with_no_content_change_and_only_in_a_bundle()
    {
        await using var e = await EmptyAsync();
        const string query = "raised table that holds trays of plants off the floor";
        const string path = "glossary.md";
        FileSystemSource Source(bool bundle, bool undeclared) =>
            new(BundleRoot, DocumentAccess.Everyone) { OkfBundle = bundle, UndeclaredAuthorshipIsMachine = undeclared };

        var off = await e.Pipeline.RunAsync(e.Tenant, Source(bundle: true, undeclared: false));
        Assert.Equal(Undeclared.Length, off.UndeclaredAuthorship);
        Assert.Equal(OkfAuthorship.Unknown, (await e.HitAsync(query, path, CallerKind.Agent))!.Authorship);

        // Only the concepts whose stored authorship changes are written again.
        var on = await e.Pipeline.RunAsync(e.Tenant, Source(bundle: true, undeclared: true));
        Assert.Equal((Undeclared.Length, Undeclared.Length), (on.Ingested, on.UndeclaredAuthorship));
        Assert.Null(await e.HitAsync(query, path, CallerKind.Agent));
        var flagged = await e.HitAsync(query, path, CallerKind.Person);
        Assert.Equal((OkfAuthorship.Machine, OkfTrustTier.Unverified), (flagged!.Authorship, flagged.TrustTier));

        var backOff = await e.Pipeline.RunAsync(e.Tenant, Source(bundle: true, undeclared: false));
        Assert.Equal(Undeclared.Length, backOff.Ingested);
        Assert.NotNull(await e.HitAsync(query, path, CallerKind.Agent));

        // Outside bundle mode the setting has nothing to act on.
        var plain = await e.Pipeline.RunAsync(e.Tenant, Source(bundle: false, undeclared: true));
        Assert.Equal(0, plain.UndeclaredAuthorship);
        Assert.Equal(OkfAuthorship.Unknown, (await e.HitAsync(query, path, CallerKind.Agent))!.Authorship);
    }

    [Fact]
    public async Task A_source_is_added_changed_and_removed_and_each_change_is_recorded_once()
    {
        await using var e = await EmptyAsync();
        var folder = Folder(("a.md", "# A\n\n## Body\nText.\n"));

        var added = await e.Registry.AddAsync("Handbook", folder, "/handbook/", okfBundle: false, undeclaredIsMachine: false, Tester);
        Assert.Equal(("handbook", Path.GetFullPath(folder)), (added.PathPrefix, added.Folder));
        Assert.Equal(added, await e.Registry.FindAsync("handbook"));

        var (before, after, changed) = await e.Registry.UpdateAsync("handbook", okfBundle: true, undeclaredIsMachine: null, Tester);
        Assert.True(changed);
        Assert.Equal((false, true, false), (before.OkfBundle, after.OkfBundle, after.UndeclaredIsMachine));
        Assert.False((await e.Registry.UpdateAsync("HANDBOOK", okfBundle: true, undeclaredIsMachine: false, Tester)).Changed);

        Assert.NotNull(await e.Registry.RemoveAsync("handbook", Tester));
        Assert.Null(await e.Registry.RemoveAsync("handbook", Tester));
        Assert.Empty(await e.Registry.ListAsync());

        var events = await e.Record.ListAsync();
        Assert.Equal([SourceRegistry.RemoveKind, SourceRegistry.SetKind, SourceRegistry.AddKind], events.Select(ev => ev.Kind));
        Assert.All(events, ev => Assert.Equal(("Handbook", "test-account"), (ev.Target, ev.Actor.Account)));
        var (remove, set, add) = (events[0], events[1], events[2]);
        Assert.Null(add.OldValue);
        Assert.False(add.NewValue!.Value.GetProperty("okf_bundle").GetBoolean());
        Assert.Equal((false, true), (set.OldValue!.Value.GetProperty("okf_bundle").GetBoolean(), set.NewValue!.Value.GetProperty("okf_bundle").GetBoolean()));
        Assert.True(remove.OldValue!.Value.GetProperty("okf_bundle").GetBoolean());
        Assert.Null(remove.NewValue);
    }

    [Fact]
    public async Task Overlapping_prefixes_and_taken_names_are_refused()
    {
        await using var e = await EmptyAsync();
        var folder = Folder(("a.md", "# A\n\n## Body\nText.\n"));
        Task<RegisteredSource> Add(string name, string prefix) =>
            e.Registry.AddAsync(name, folder, prefix, okfBundle: false, undeclaredIsMachine: false, Tester);

        await Add("hr", "hr");
        await Add("hr-archive", "hr-archive");

        await Assert.ThrowsAsync<InvalidOperationException>(() => Add("pay", "hr/pay"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Add("staff", "hr"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Add("everything", ""));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Add("HR", "other"));
        await Assert.ThrowsAsync<ArgumentException>(() => Add("has space", "elsewhere"));
        await Assert.ThrowsAsync<ArgumentException>(() => e.Registry.AddAsync(
            "gone", Path.Combine(folder, "no-such-folder"), "gone", false, false, Tester));

        Assert.Equal(["hr", "hr-archive"], (await e.Registry.ListAsync()).Select(s => s.Name));
        Assert.Equal(2, (await e.Record.ListAsync()).Count);

        // Once the owner of the prefix is gone, the prefix inside it is free.
        await e.Registry.RemoveAsync("hr", Tester);
        Assert.Equal("hr/pay", (await Add("pay", "hr/pay")).PathPrefix);
    }

    [Fact]
    public async Task Removing_a_source_leaves_its_documents_and_its_runs()
    {
        await using var e = await EmptyAsync();
        var folder = Folder(("a.md", "# A\n\n## Body\nText.\n"), ("b.md", "# B\n\n## Body\nMore text.\n"));
        var source = await e.Registry.AddAsync("desk", folder, "desk", okfBundle: false, undeclaredIsMachine: false, Tester);
        var run = await e.Runs.RunAsync(e.Pipeline, source.ToFileSystemSource(), source.Id);

        await e.Registry.RemoveAsync("desk", Tester);

        Assert.Equal(2L, await e.Registry.IndexedDocumentCountAsync(source));
        Assert.Equal(2L, await e.CountAsync("SELECT count(*) FROM prem_index.document WHERE path LIKE 'desk/%'"));
        Assert.Equal(run.RunId, (await e.Runs.LastAsync(source.Id))!.Id);
    }
}

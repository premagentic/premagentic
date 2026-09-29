using Premagentic.Core.Admin;
using Premagentic.Core.Embeddings;
using Premagentic.Core.Ingestion;
using Premagentic.Core.Okf;
using Premagentic.Core.Security;
using Premagentic.Core.Sources;
using Premagentic.Core.Sources.Registry;
using Premagentic.Core.Storage;

namespace Premagentic.Tests;

/// <summary>
/// The review queue's read model: which documents are in it, where each file
/// is, and the filters. Every file here is invented. Requires a running Docker
/// daemon.
/// </summary>
public sealed class ReviewQueueTests(DatastoreTestDatabase server) : IClassFixture<DatastoreTestDatabase>
{
    private static readonly AdminActor Tester = new("cli", "test-account");

    /// <summary>The fixture bundle's concepts below human-reviewed: machine-written, or verified by nobody.</summary>
    private static readonly string[] BundleInQueue =
    [
        "care/humidity.md", "care/pest-scouting.md", "glossary.md", "misc/broken.md", "misc/garbled-trust.md",
        "misc/scratch.md", "misc/untyped.md", "misc/visitor-notes.md", "procedures/old-closing.md",
        "supplies/delivery-window.md", "supplies/fertilizer-prices.md", "supplies/seed-order.md",
    ];

    private static readonly (string, string)[] Office =
    [
        ("minutes.md", "# Minutes\n\n## Decisions\nThe loading dock opens at seven.\n"),
        ("notes.md", "---\ntitle: Notes\n---\n\n# Notes\n\n## Parking\nVisitors park by the gate.\n"),
        ("agent-summary.md",
            "---\ntitle: Summary\ngenerated: { by: summary_agent/1.0, at: 2026-06-01T08:00:00Z }\n---\n\n# Summary\n\n## Week\nDeliveries ran on time.\n"),
        ("reviewed-summary.md",
            "---\ntitle: Reviewed summary\ngenerated: { by: summary_agent/1.0, at: 2026-06-01T08:00:00Z }\n" +
            "verified:\n  - { by: human:riley, at: 2026-06-02T09:00:00Z }\n---\n\n# Reviewed summary\n\n## Week\nThe dock stayed open late on Friday.\n"),
    ];

    [Fact]
    public async Task The_queue_holds_machine_written_documents_below_human_reviewed_and_unverified_concepts_and_says_where_each_file_is()
    {
        var db = new PremagenticDatabase(await server.CreateDatabaseAsync());
        await using var _ = db;
        await db.InitializeAsync();
        var tenant = await db.EnsureTenantAsync("t", "T");
        var registry = new SourceRegistry(db, tenant);
        var runs = new IngestRuns(db, tenant);
        var pipeline = new IngestPipeline(db, new HashEmbeddingProvider());

        var bundleFolder = SourcesTests.BundleCopy();
        var greenhouse = await registry.AddAsync("greenhouse", bundleFolder, "greenhouse", okfBundle: true, undeclaredIsMachine: false, Tester);
        var office = await registry.AddAsync("office", SourcesTests.Folder(Office), "office", okfBundle: false, undeclaredIsMachine: false, Tester);
        await runs.RunAsync(pipeline, greenhouse.ToFileSystemSource(), greenhouse.Id);
        await runs.RunAsync(pipeline, office.ToFileSystemSource(), office.Id);
        // A folder given by hand, twice: the queue places it by its last run.
        var looseOld = SourcesTests.Folder(("draft.md", Office[2].Item2));
        var looseNew = SourcesTests.Folder(("draft.md", Office[2].Item2));
        await runs.RunAsync(pipeline, new FileSystemSource(looseOld, DocumentAccess.FolderRules, "loose"), sourceId: null);
        await runs.RunAsync(pipeline, new FileSystemSource(looseNew, DocumentAccess.FolderRules, "loose"), sourceId: null);

        var queue = new ReviewQueue(db, tenant);
        var places = await queue.PlacesAsync();
        var all = await queue.ListAsync(places, null, null, 100, 0);

        Assert.Equal(
            [.. BundleInQueue.Select(p => "greenhouse/" + p), "loose/draft.md", "office/agent-summary.md"],
            all.Select(i => i.Path));
        Assert.Equal(all.Count, await queue.CountAsync());

        var humidity = all.Single(i => i.Path == "greenhouse/care/humidity.md");
        Assert.Equal(
            (OkfTrustTier.MachineConfirmed, OkfAuthorship.Machine, "greenhouse", Path.Combine(bundleFolder, "care", "humidity.md")),
            (humidity.TrustTier, humidity.Authorship, humidity.Source, humidity.File));
        var oldClosing = all.Single(i => i.Path == "greenhouse/procedures/old-closing.md");
        Assert.Equal((OkfTrustTier.Unverified, OkfAuthorship.Human), (oldClosing.TrustTier, oldClosing.Authorship));
        var draft = all.Single(i => i.Path == "loose/draft.md");
        Assert.Equal(((string?)null, Path.Combine(looseNew, "draft.md")), (draft.Source, draft.File));
        Assert.Equal(("office", Path.Combine(office.Folder, "agent-summary.md")),
            (all.Single(i => i.Path == "office/agent-summary.md").Source, all.Single(i => i.Path == "office/agent-summary.md").File));

        // The filters, and paging.
        Assert.Equal(["greenhouse/care/humidity.md"], (await queue.ListAsync(places, null, OkfTrustTier.MachineConfirmed, 100, 0)).Select(i => i.Path));
        Assert.Equal(["office/agent-summary.md"], (await queue.ListAsync(places, "office/", null, 100, 0)).Select(i => i.Path));
        Assert.Equal(1L, await queue.CountAsync("office/", OkfTrustTier.Unverified));
        Assert.Equal(0L, await queue.CountAsync("office/", OkfTrustTier.MachineConfirmed));
        Assert.Equal(all.Skip(3).Take(3).Select(i => i.Path), (await queue.ListAsync(places, null, null, 3, 3)).Select(i => i.Path));

        // A document with no place: its prefix is read by no source and no recorded run.
        Assert.Null(ReviewQueue.PlaceOf("elsewhere/a.md", places));
    }
}

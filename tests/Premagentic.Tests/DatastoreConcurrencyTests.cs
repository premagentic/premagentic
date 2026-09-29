using System.Collections.Concurrent;
using Premagentic.Core;
using Premagentic.Core.Ingestion;
using Premagentic.Core.Retrieval;
using Premagentic.Core.Security;
using Premagentic.Core.Storage;

namespace Premagentic.Tests;

/// <summary>
/// Searches running while an ingest adds, changes and removes documents, with
/// the index sweeping as often as it can. Nothing may throw, and no search may
/// return a passage its caller could not read.
/// </summary>
public sealed class DatastoreConcurrencyTests(DatastoreTestDatabase server) : IClassFixture<DatastoreTestDatabase>
{
    [Fact]
    public async Task Concurrent_searches_during_an_ingest_return_no_error_and_no_unpermitted_row()
    {
        await using var db = new PremagenticDatabase(await server.CreateDatabaseAsync());
        await db.InitializeAsync();
        var tenantId = await db.EnsureTenantAsync("t", "T");
        var embedder = new SeededEmbeddingProvider();

        // Public and HR documents share every word, so both legs have reasons to
        // surface the HR ones to a caller who must never see them.
        static SourceDocument Doc(int round, int i) => DatastoreSource.Doc(
            $"c/{(i % 2 == 0 ? "open" : "hr")}-{i:D3}.md",
            $"## Section {i}\nrotation gizmo policy passage {i} revision {round}",
            i % 2 == 0 ? DocumentAccess.Everyone : DocumentAccess.For("group:hr"));

        var pipeline = new IngestPipeline(db, embedder);
        await pipeline.RunAsync(tenantId, new DatastoreSource("c", Enumerable.Range(0, 60).Select(i => Doc(0, i)).ToArray()));

        var search = new HybridSearch(db, embedder);
        await search.WarmUpAsync();
        search.Vectors.SweepInterval = TimeSpan.Zero;

        var errors = new ConcurrentQueue<Exception>();
        var leaks = new ConcurrentQueue<string>();
        var answered = 0;
        using var stop = new CancellationTokenSource();

        var searchers = Enumerable.Range(0, 5).Select(w => Task.Run(async () =>
        {
            var n = 0;
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    var result = await search.SearchAsync(tenantId, $"rotation gizmo policy passage {n++ % 60}",
                        new SearchOptions(AccessScope.ForPrincipals("t", "group:engineering"), TopK: 10));
                    foreach (var hit in result.Hits.Where(h => !h.Path.StartsWith("c/open-", StringComparison.Ordinal)))
                        leaks.Enqueue(hit.Path);
                    if (result.Hits.Count > 0) Interlocked.Increment(ref answered);
                }
                catch (Exception ex) { errors.Enqueue(ex); }
            }
        })).ToArray();

        // Rounds of change: every document rewritten (new chunk ids), and a
        // shifting half of them deleted and brought back.
        for (var round = 1; round <= 6; round++)
        {
            var docs = Enumerable.Range(0, 60).Where(i => round % 2 == 0 || i < 30).Select(i => Doc(round, i)).ToArray();
            await pipeline.RunAsync(tenantId, new DatastoreSource("c", docs));
        }
        await stop.CancelAsync();
        await Task.WhenAll(searchers);

        Assert.Empty(errors);
        Assert.Empty(leaks);
        // The control: the searchers were really getting answers throughout.
        Assert.True(answered > 20, $"only {answered} searches returned anything");
    }
}

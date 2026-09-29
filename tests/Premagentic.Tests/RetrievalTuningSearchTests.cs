using System.Text.Json;
using Premagentic.Core;
using Premagentic.Core.Embeddings;
using Premagentic.Core.Identity;
using Premagentic.Core.Ingestion;
using Premagentic.Core.Retrieval;
using Premagentic.Core.Security;
using Premagentic.Core.Storage;

namespace Premagentic.Tests;

/// <summary>
/// The tuning a deployment stores, read by the search for every query: a set
/// value changes the fused scores and the order, a missing key keeps its
/// default, and a stored value that cannot be used keeps its default and is
/// reported once. Requires a running Docker daemon.
/// </summary>
public sealed class RetrievalTuningSearchTests(DatastoreTestDatabase server) : IClassFixture<DatastoreTestDatabase>
{
    // One chunk each. Only the first holds both query words, so the AND pass
    // finds one chunk and the OR pass fills the rest as fallback matches.
    // "over" is a stop word: the text search drops it and the vector leg keeps
    // it, so the tally memo, which says little else, is the nearest vector and
    // no text match at all.
    private const string Query = "lantern ferry over";
    private const string Both = "runbooks/both.md";
    private const string Tally = "memos/tally.md";

    private static readonly (string Path, string Text, string Class)[] Corpus =
    [
        (Both, "## Crossing\nthe lantern on the ferry is lit at dusk", "runbook"),
        ("memos/lantern.md", "## Supplies\na spare lantern wick is in the shed", "memo"),
        ("memos/ferry.md", "## Timetable\nthe ferry leaves on the hour", "memo"),
        ("memos/lanterns.md", "## Care\nclean each lantern glass and trim each lantern wick", "memo"),
        ("runbooks/harbor.md", "## Harbor\nthe harbor gate closes at nine", "runbook"),
        (Tally, "## Tally\nover, over, over and done", "memo"),
    ];

    private sealed record Env(PremagenticDatabase Db, Guid Tenant, HybridSearch Search, SettingsStore Settings, List<RetrievalSettingProblem> Reported)
        : IAsyncDisposable
    {
        public async Task<IReadOnlyList<SearchHit>> HitsAsync() =>
            (await Search.SearchAsync(Tenant, Query, new SearchOptions(AccessScope.PublicOnly, TopK: 6))).Hits;

        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }

    private async Task<Env> NewAsync()
    {
        var db = new PremagenticDatabase(await server.CreateDatabaseAsync());
        await db.InitializeAsync();
        var tenant = await db.EnsureTenantAsync("t", "T");
        var embedder = new HashEmbeddingProvider();
        var summary = await new IngestPipeline(db, embedder).RunAsync(tenant, new DatastoreSource("",
            [.. Corpus.Select(d => DatastoreSource.Doc(d.Path, d.Text, DocumentAccess.Everyone) with { DocClass = d.Class })]));
        Assert.Equal(Corpus.Length, summary.Ingested);

        var reported = new List<RetrievalSettingProblem>();
        var search = new HybridSearch(db, embedder, storedTuning: new RetrievalSettingsLoader(db, reported.Add));
        return new Env(db, tenant, search, new SettingsStore(db, tenant), reported);
    }

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement;

    private static string ClassOf(SearchHit hit) => Corpus.Single(d => d.Path == hit.Path).Class;

    /// <summary>The fused score Reciprocal Rank Fusion gives a hit from its own ranks.</summary>
    private static double Fused(SearchHit hit, int k, double fallback, Func<string, double>? weight = null) =>
        (weight?.Invoke(ClassOf(hit)) ?? 1.0)
        * ((hit.LexicalRank is { } l ? (hit.LexicalFallback ? fallback : 1.0) / (k + l) : 0)
           + (hit.VectorRank is { } v ? 1.0 / (k + v) : 0));

    private static void AssertScoredWith(IReadOnlyList<SearchHit> hits, int k, double fallback, Func<string, double>? weight = null)
    {
        Assert.NotEmpty(hits);
        foreach (var hit in hits)
            Assert.True(Math.Abs(hit.FusedScore - Fused(hit, k, fallback, weight)) < 1e-12,
                $"{hit.Path}: {hit.FusedScore} is not the score for k={k}, fallback={fallback}");
        Assert.Equal(hits.OrderByDescending(h => h.FusedScore).Select(h => h.Path), hits.Select(h => h.Path));
    }

    [Fact]
    public async Task A_stored_rrf_k_and_fallback_weight_are_what_the_next_search_scores_with()
    {
        await using var e = await NewAsync();

        // Nothing stored: the code defaults.
        var before = await e.HitsAsync();
        Assert.Contains(before, h => h.LexicalFallback);
        Assert.Contains(before, h => h.LexicalRank is not null && !h.LexicalFallback);
        AssertScoredWith(before, RetrievalTuning.Default.RrfK, RetrievalTuning.Default.FallbackRrfWeight);

        await e.Settings.SetAsync(RetrievalSettings.RrfK, Json("2"));
        await e.Settings.SetAsync(RetrievalSettings.FallbackRrfWeight, Json("0.05"));
        var after = await e.HitsAsync();
        AssertScoredWith(after, 2, 0.05);
        Assert.All(after, h => Assert.False(Math.Abs(h.FusedScore - Fused(h, 60, 0.5)) < 1e-12, h.Path));

        // Removing them brings the defaults back on the next search.
        await e.Settings.RemoveAsync(RetrievalSettings.RrfK);
        await e.Settings.RemoveAsync(RetrievalSettings.FallbackRrfWeight);
        AssertScoredWith(await e.HitsAsync(), RetrievalTuning.Default.RrfK, RetrievalTuning.Default.FallbackRrfWeight);
        Assert.Empty(e.Reported);
    }

    [Fact]
    public async Task A_stored_fallback_weight_of_zero_moves_the_fallback_matches_below_a_nearer_vector_match()
    {
        await using var e = await NewAsync();

        // The tally memo has no text match and is nearer in the vector ranks
        // than every fallback match; at the default weight each fallback match's
        // text rank still lifts it above the memo.
        var before = await e.HitsAsync();
        var fallbacks = before.Where(h => h.LexicalFallback).ToArray();
        var tally = before.Single(h => h.Path == Tally);
        Assert.NotEmpty(fallbacks);
        Assert.Null(tally.LexicalRank);
        Assert.All(fallbacks, f => Assert.True(tally.VectorRank < f.VectorRank, $"{f.Path} is nearer than the tally memo."));
        Assert.All(fallbacks, f => Assert.True(f.FusedScore > tally.FusedScore, $"{f.Path} is not above the tally memo."));

        await e.Settings.SetAsync(RetrievalSettings.FallbackRrfWeight, Json("0"));
        var after = await e.HitsAsync();

        // A fallback match now counts only its vector rank, so the memo passes
        // every one of them, and the order is not the order before.
        AssertScoredWith(after, RetrievalTuning.Default.RrfK, 0);
        var tallyAt = after.Select(h => h.Path).ToList().IndexOf(Tally);
        Assert.All(after.Where(h => h.LexicalFallback), f =>
            Assert.True(after.Select(h => h.Path).ToList().IndexOf(f.Path) > tallyAt, $"{f.Path} is still above the tally memo."));
        Assert.NotEqual(before.Select(h => h.Path), after.Select(h => h.Path));
        Assert.Equal(Both, after[0].Path);
    }

    [Fact]
    public async Task A_stored_authority_table_reorders_the_results_and_the_default_is_flat()
    {
        await using var e = await NewAsync();
        var before = await e.HitsAsync();
        AssertScoredWith(before, 60, 0.5);
        Assert.Equal(Both, before[0].Path);

        await e.Settings.SetAsync(RetrievalSettings.Authority, Json("""{"by_class": {"MEMO": 3}}"""));
        var after = await e.HitsAsync();
        AssertScoredWith(after, 60, 0.5, c => c == "memo" ? 3 : 1);
        Assert.Equal("memo", ClassOf(after[0]));

        await e.Settings.SetAsync(RetrievalSettings.Authority, Json("""{"default": 0.5, "by_class": {"runbook": 1}}"""));
        AssertScoredWith(await e.HitsAsync(), 60, 0.5, c => c == "runbook" ? 1 : 0.5);
    }

    [Fact]
    public async Task An_unusable_stored_value_leaves_its_default_in_force_and_is_reported_once()
    {
        await using var e = await NewAsync();
        await StoreRawAsync(e, RetrievalSettings.RrfK, "0");
        await StoreRawAsync(e, RetrievalSettings.Authority, """{"by_class": {"memo": -3}}""");
        await StoreRawAsync(e, RetrievalSettings.FallbackRrfWeight, "0.05");

        for (var i = 0; i < 3; i++)
            AssertScoredWith(await e.HitsAsync(), RetrievalTuning.Default.RrfK, 0.05);

        Assert.Equal(new[] { RetrievalSettings.RrfK, RetrievalSettings.Authority }, e.Reported.Select(p => p.Key));
        Assert.Contains(RetrievalSettings.Allowed(RetrievalSettings.RrfK), e.Reported[0].Problem);
    }

    [Fact]
    public async Task Fixed_tuning_and_a_loader_for_the_stored_tuning_are_not_both_accepted()
    {
        await using var e = await NewAsync();
        var loader = new RetrievalSettingsLoader(e.Db);
        Assert.Throws<ArgumentException>(() => new HybridSearch(e.Db, new HashEmbeddingProvider(), tuning: RetrievalTuning.Default, storedTuning: loader));
        Assert.Throws<ArgumentException>(() => new HybridSearch(e.Db, new HashEmbeddingProvider(), authority: AuthorityWeights.Flat, storedTuning: loader));

        // Fixed tuning with no loader is taken as given, whatever is stored.
        await e.Settings.SetAsync(RetrievalSettings.RrfK, Json("2"));
        var fixedSearch = new HybridSearch(e.Db, new HashEmbeddingProvider(), tuning: new RetrievalTuning(RrfK: 7));
        AssertScoredWith((await fixedSearch.SearchAsync(e.Tenant, Query, new SearchOptions(AccessScope.PublicOnly, TopK: 6))).Hits, 7, 0.5);
    }

    [Fact]
    public async Task A_search_result_carries_the_reading_it_ranked_under()
    {
        await using var e = await NewAsync();
        var options = new SearchOptions(AccessScope.PublicOnly, TopK: 6);
        await e.Settings.SetAsync(RetrievalSettings.NoAnswerDistanceFloor, Json("0.3"));
        await e.Settings.SetAsync(RetrievalSettings.RrfK, Json("2"));
        await e.Settings.SetAsync(RetrievalSettings.Authority, Json("""{"by_class": {"memo": 3}}"""));
        await StoreRawAsync(e, RetrievalSettings.FallbackRrfWeight, "7");

        // With a loader: the stored reading, the unusable value's problem included.
        var stored = (await e.Search.SearchAsync(e.Tenant, Query, options)).Settings;
        Assert.Equal(new RetrievalTuning(NoAnswerDistanceFloor: 0.3, RrfK: 2), stored.Tuning);
        Assert.Equal(3.0, stored.Authority.For("MEMO"));
        Assert.Equal(1.0, stored.Authority.For("runbook"));
        Assert.Equal(new[] { RetrievalSettings.FallbackRrfWeight }, stored.Problems.Select(p => p.Key));

        // Without one: the fixed tuning the search was given, whatever is stored.
        var fixedTuning = new RetrievalTuning(NoAnswerDistanceFloor: 0.9, RrfK: 7);
        var fixedAuthority = new AuthorityWeights(new Dictionary<string, double> { ["runbook"] = 2 });
        var given = (await new HybridSearch(e.Db, new HashEmbeddingProvider(), fixedAuthority, fixedTuning)
            .SearchAsync(e.Tenant, Query, options)).Settings;
        Assert.Same(fixedTuning, given.Tuning);
        Assert.Same(fixedAuthority, given.Authority);
        Assert.Empty(given.Problems);

        // And with neither, the code defaults.
        var defaults = (await new HybridSearch(e.Db, new HashEmbeddingProvider()).SearchAsync(e.Tenant, Query, options)).Settings;
        Assert.Same(RetrievalTuning.Default, defaults.Tuning);
        Assert.Same(AuthorityWeights.Flat, defaults.Authority);
        Assert.Empty(defaults.Problems);
    }

    private static async Task StoreRawAsync(Env e, string key, string json)
    {
        await using var cmd = e.Db.DataSource.CreateCommand(
            "INSERT INTO prem_config.setting(tenant_id, key, value) VALUES(@tenant, @key, @value::jsonb)");
        cmd.Parameters.AddWithValue("tenant", e.Tenant);
        cmd.Parameters.AddWithValue("key", key);
        cmd.Parameters.AddWithValue("value", json);
        await cmd.ExecuteNonQueryAsync();
    }
}

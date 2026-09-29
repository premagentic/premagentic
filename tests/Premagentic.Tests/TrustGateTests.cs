using Premagentic.Core;
using Premagentic.Core.Embeddings;
using Premagentic.Core.Ingestion;
using Premagentic.Core.Okf;
using Premagentic.Core.Retrieval;
using Premagentic.Core.Retrieval.Gates;
using Premagentic.Core.Security;
using Premagentic.Core.Sources;
using Premagentic.Core.Storage;

namespace Premagentic.Tests;

/// <summary>
/// The trust gate and the freshness gate, end to end on a real PostgreSQL,
/// over the invented bundle in Fixtures/okf-bundle.
/// <para>
/// Every case asserts both directions: the caller the policy admits DOES get
/// the passage, and the one it holds back does not. A test that only checked
/// the absence would pass as happily against a build that returned nothing.
/// </para>
/// Requires a running Docker daemon.
/// </summary>
public sealed class TrustGateTests(DatastoreTestDatabase server) : IClassFixture<DatastoreTestDatabase>
{
    private static readonly string BundleRoot = Path.Combine(AppContext.BaseDirectory, "Fixtures", "okf-bundle");

    // Fertilizer prices went stale at the start of March; the seed order has years left.
    private static readonly DateTimeOffset June = new(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset FertilizerStale = new(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);

    private static readonly TrustPolicy Agent = TrustPolicy.Resolve(CallerKind.Agent, null);
    private static readonly TrustPolicy Person = TrustPolicy.Resolve(CallerKind.Person, null);

    private sealed class Env(PremagenticDatabase db, Guid tenant, IngestSummary first) : IAsyncDisposable
    {
        private static readonly HashEmbeddingProvider Embedder = new();

        public PremagenticDatabase Db { get; } = db;
        public Guid Tenant { get; } = tenant;
        public IngestSummary First { get; } = first;
        public HybridSearch Search { get; } = new(db, Embedder);
        public SectionFetcher Sections { get; } = new(db);

        public Task<IngestSummary> IngestAsync(bool bundle) =>
            new IngestPipeline(Db, Embedder).RunAsync(Tenant, Source(bundle));

        public async Task<IReadOnlyList<SearchHit>> HitsAsync(
            string query, TrustPolicy? policy, DateTimeOffset asOf, bool historical = false) =>
            (await Search.SearchAsync(Tenant, query,
                new SearchOptions(AccessScope.PublicOnly, TopK: 10, IncludeHistorical: historical, Trust: policy, AsOf: asOf))).Hits;

        public async Task<SearchHit?> HitAsync(string query, string path, TrustPolicy? policy, DateTimeOffset asOf) =>
            (await HitsAsync(query, policy, asOf)).FirstOrDefault(h => h.Path == path);

        public async Task<T> ScalarAsync<T>(string sql, string path)
        {
            await using var cmd = Db.DataSource.CreateCommand(sql);
            cmd.Parameters.AddWithValue("path", path);
            return (T)(await cmd.ExecuteScalarAsync())!;
        }

        public static FileSystemSource Source(bool bundle) =>
            new(BundleRoot, DocumentAccess.Everyone) { OkfBundle = bundle };

        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }

    private async Task<Env> IngestedAsync(bool bundle = true)
    {
        var db = new PremagenticDatabase(await server.CreateDatabaseAsync());
        await db.InitializeAsync();
        var tenant = await db.EnsureTenantAsync("t", "T");
        var summary = await new IngestPipeline(db, new HashEmbeddingProvider()).RunAsync(tenant, Env.Source(bundle));
        return new Env(db, tenant, summary);
    }

    [Fact]
    public async Task A_deprecated_concept_is_absent_by_default()
    {
        await using var e = await IngestedAsync();
        const string query = "roof vents five every evening";

        Assert.DoesNotContain(await e.HitsAsync(query, Person, June), h => h.Path == "procedures/old-closing.md");
        Assert.Contains(await e.HitsAsync(query, Person, June, historical: true), h => h.Path == "procedures/old-closing.md");
    }

    [Fact]
    public async Task A_stale_concept_is_flagged_for_a_person_and_absent_for_an_agent_from_the_instant_on()
    {
        await using var e = await IngestedAsync();
        const string query = "supplier winter price twenty liter drum";
        const string path = "supplies/fertilizer-prices.md";
        var justBefore = FertilizerStale.AddTicks(-10); // one microsecond, the database's resolution

        var agentBefore = await e.HitAsync(query, path, Agent, justBefore);
        Assert.NotNull(agentBefore);
        Assert.False(agentBefore.Stale);
        Assert.False((await e.HitAsync(query, path, Person, justBefore))!.Stale);

        Assert.Null(await e.HitAsync(query, path, Agent, FertilizerStale));
        Assert.True((await e.HitAsync(query, path, Person, FertilizerStale))!.Stale);

        // Human-written, so only freshness can hold it back.
        Assert.Equal(OkfAuthorship.Human, agentBefore.Authorship);
    }

    [Fact]
    public async Task A_concept_with_no_stale_after_is_never_stale()
    {
        await using var e = await IngestedAsync();

        var seed = await e.HitAsync("tomato pepper seed january herb february", "supplies/seed-order.md", Agent, June);
        Assert.NotNull(seed);
        Assert.False(seed.Stale);

        var water = await e.HitAsync("seedling benches before nine door", "care/watering.md", Agent, DateTimeOffset.MaxValue.AddYears(-1));
        Assert.NotNull(water);
        Assert.False(water.Stale);
    }

    [Fact]
    public async Task Agent_written_unverified_content_is_absent_for_an_agent_and_flagged_for_a_person()
    {
        await using var e = await IngestedAsync();
        const string query = "yellow cards every Monday count caught";
        const string path = "care/pest-scouting.md";

        Assert.Null(await e.HitAsync(query, path, Agent, June));

        var forPerson = await e.HitAsync(query, path, Person, June);
        Assert.NotNull(forPerson);
        Assert.Equal(OkfTrustTier.Unverified, forPerson.TrustTier);
        Assert.Equal(OkfAuthorship.Machine, forPerson.Authorship);
        Assert.Equal("care/pest-scouting", forPerson.ConceptId);
    }

    [Fact]
    public async Task Machine_confirmed_content_needs_a_policy_that_accepts_machine_confirmation()
    {
        await using var e = await IngestedAsync();
        const string query = "relative humidity seventy eighty percent";
        const string path = "care/humidity.md";

        Assert.Null(await e.HitAsync(query, path, Agent, June));
        var confirmed = await e.HitAsync(query, path, Agent with { MinimumMachineTier = OkfTrustTier.MachineConfirmed }, June);
        Assert.NotNull(confirmed);
        Assert.Equal(OkfTrustTier.MachineConfirmed, confirmed.TrustTier);
    }

    [Theory]
    [InlineData("sow two seeds per cell thin stronger", "care/propagation.md")]
    [InlineData("latch both doors fans automatic", "procedures/closing.md")]
    public async Task Agent_written_content_with_a_human_sign_off_is_present_for_both(string query, string path)
    {
        await using var e = await IngestedAsync();

        foreach (var policy in new[] { Agent, Person })
        {
            var hit = await e.HitAsync(query, path, policy, June);
            Assert.NotNull(hit);
            Assert.Equal(OkfTrustTier.HumanReviewed, hit.TrustTier);
            Assert.Equal(OkfAuthorship.Machine, hit.Authorship);
        }
    }

    [Fact]
    public async Task An_unparseable_concept_in_a_bundle_is_held_back_and_the_same_file_outside_one_is_not()
    {
        const string query = "frontmatter above not valid YAML body still indexed";
        const string path = "misc/broken.md";

        await using (var bundle = await IngestedAsync(bundle: true))
        {
            Assert.Null(await bundle.HitAsync(query, path, Agent, June));
            var forPerson = await bundle.HitAsync(query, path, Person, June);
            Assert.NotNull(forPerson);
            Assert.Equal(OkfAuthorship.Machine, forPerson.Authorship);
            Assert.Equal(OkfTrustTier.Unverified, forPerson.TrustTier);
            Assert.Equal((short)FrontmatterState.Unparseable,
                await bundle.ScalarAsync<short>("SELECT frontmatter_state FROM prem_index.document WHERE path = @path", path));
        }

        await using var plain = await IngestedAsync(bundle: false);
        var ordinary = await plain.HitAsync(query, path, Agent, June);
        Assert.NotNull(ordinary);
        Assert.Equal(OkfAuthorship.Unknown, ordinary.Authorship);
        Assert.Null(ordinary.ConceptId);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task An_ordinary_file_with_no_frontmatter_is_never_held_back(bool bundle)
    {
        await using var e = await IngestedAsync(bundle);

        var hit = await e.HitAsync("second shade cloth south wall", "misc/scratch.md", TrustPolicy.Strict, June);
        Assert.NotNull(hit);
        Assert.Equal(OkfAuthorship.Unknown, hit.Authorship);
        Assert.False(hit.Stale);
    }

    [Fact]
    public async Task A_search_that_names_no_policy_gets_the_strict_one()
    {
        await using var e = await IngestedAsync();

        Assert.Null(await e.HitAsync("yellow cards every Monday count caught", "care/pest-scouting.md", policy: null, June));
        Assert.Null(await e.HitAsync("supplier winter price twenty liter drum", "supplies/fertilizer-prices.md", policy: null, June));
        // And it is not simply empty.
        Assert.NotNull(await e.HitAsync("sow two seeds per cell thin stronger", "care/propagation.md", policy: null, June));
    }

    [Fact]
    public async Task A_date_nobody_can_read_is_stored_as_minus_infinity_and_is_always_stale()
    {
        await using var e = await IngestedAsync();
        const string path = "misc/garbled-trust.md";

        Assert.True(await e.ScalarAsync<bool>(
            "SELECT stale_after = '-infinity'::timestamptz FROM prem_index.document WHERE path = @path", path));

        var forPerson = await e.HitAsync("trust field present none shape format", path, Person, new DateTimeOffset(1900, 1, 1, 0, 0, 0, TimeSpan.Zero));
        Assert.NotNull(forPerson);
        Assert.True(forPerson.Stale);
    }

    [Fact]
    public async Task Changing_the_policy_changes_the_very_next_search_with_no_reingest()
    {
        await using var e = await IngestedAsync();
        const string query = "yellow cards every Monday count caught";
        const string path = "care/pest-scouting.md";
        const string updatedAt = "SELECT updated_at FROM prem_index.document WHERE path = @path";
        var before = await e.ScalarAsync<DateTime>(updatedAt, path);

        var loose = Agent with { MinimumMachineTier = OkfTrustTier.Unverified };
        Assert.Null(await e.HitAsync(query, path, Agent, June));
        Assert.NotNull(await e.HitAsync(query, path, loose, June));
        Assert.Null(await e.HitAsync(query, path, Agent, June));

        var stale = Agent with { IncludeStale = true };
        const string pricesQuery = "supplier winter price twenty liter drum";
        Assert.Null(await e.HitAsync(pricesQuery, "supplies/fertilizer-prices.md", Agent, June));
        Assert.NotNull(await e.HitAsync(pricesQuery, "supplies/fertilizer-prices.md", stale, June));
        Assert.Null(await e.HitAsync(pricesQuery, "supplies/fertilizer-prices.md", Agent, June));

        Assert.Equal(before, await e.ScalarAsync<DateTime>(updatedAt, path));
    }

    [Fact]
    public async Task Switching_bundle_mode_restores_the_values_with_no_content_change()
    {
        await using var e = await IngestedAsync(bundle: false);
        const string path = "misc/broken.md";
        const string row = "SELECT concat_ws('|', authorship, trust_tier, frontmatter_state, coalesce(okf_concept_id, '-'), content_hash) FROM prem_index.document WHERE path = @path";
        var plain = await e.ScalarAsync<string>(row, path);
        var hash = plain.Split('|')[^1];
        Assert.Equal($"0|0|0|-|{hash}", plain);

        var toBundle = await e.IngestAsync(bundle: true);
        Assert.True(toBundle.Ingested > 0);
        Assert.Equal($"2|0|2|misc/broken|{hash}", await e.ScalarAsync<string>(row, path));

        // Nothing changed this time, so nothing is re-stored. The comparison
        // holds for every stored value, -infinity and timestamps included.
        var again = await e.IngestAsync(bundle: true);
        Assert.Equal(0, again.Ingested);
        Assert.Equal(again.Scanned, again.Unchanged);

        await e.IngestAsync(bundle: false);
        Assert.Equal(plain, await e.ScalarAsync<string>(row, path));
    }

    [Fact]
    public async Task A_section_fetch_obeys_the_same_gates()
    {
        await using var e = await IngestedAsync();
        SearchOptions As(TrustPolicy? policy) => new(AccessScope.PublicOnly, Trust: policy, AsOf: June);

        Assert.Null(await e.Sections.GetAsync(e.Tenant, As(Agent), "care/pest-scouting.md", null));
        var forPerson = await e.Sections.GetAsync(e.Tenant, As(Person), "care/pest-scouting.md", null);
        Assert.NotNull(forPerson);
        Assert.NotEmpty(forPerson.Chunks);
        Assert.Equal(OkfAuthorship.Machine, forPerson.Authorship);
        Assert.Equal(OkfTrustTier.Unverified, forPerson.TrustTier);
        Assert.Equal("care/pest-scouting", forPerson.ConceptId);

        Assert.Null(await e.Sections.GetAsync(e.Tenant, As(Agent), "supplies/fertilizer-prices.md", null));
        var stale = await e.Sections.GetAsync(e.Tenant, As(Person), "supplies/fertilizer-prices.md", null);
        Assert.NotNull(stale);
        Assert.True(stale.Stale);
        Assert.NotEmpty(stale.Chunks);

        // The signature without options fetches under the strict policy.
        Assert.Null(await e.Sections.GetAsync(e.Tenant, AccessScope.PublicOnly, "care/pest-scouting.md", null, includeHistorical: false));
        Assert.NotNull(await e.Sections.GetAsync(e.Tenant, AccessScope.PublicOnly, "care/propagation.md", null, includeHistorical: false));
    }

    [Fact]
    public async Task The_vector_leg_never_scores_a_passage_the_gates_hold_back()
    {
        await using var e = await IngestedAsync();
        var embedder = new HashEmbeddingProvider();
        var query = (await embedder.EmbedAsync(["Replace the yellow cards every Monday and count what they caught."]))[0];

        async Task<HashSet<string>> PathsAsync(TrustPolicy policy)
        {
            // Called directly, so the scope is resolved here the way a search resolves it.
            var scope = await PermittedSetReader.ResolveAsync(e.Db, e.Tenant, AccessScope.PublicOnly);
            var near = await e.Search.Vectors.SearchAsync(
                e.Tenant, embedder.Name, query,
                new GateContext(new SearchOptions(scope, Trust: policy, AsOf: June)), limit: 100);
            await using var cmd = e.Db.DataSource.CreateCommand(
                "SELECT d.path FROM prem_index.chunk c JOIN prem_index.document d ON d.id = c.document_id WHERE c.id = ANY(@ids)");
            cmd.Parameters.AddWithValue("ids", near.Select(n => n.ChunkId).ToArray());
            var paths = new HashSet<string>();
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync()) paths.Add(reader.GetString(0));
            return paths;
        }

        var forAgent = await PathsAsync(Agent);
        var forPerson = await PathsAsync(Person);
        Assert.Contains("care/pest-scouting.md", forPerson);
        Assert.Contains("supplies/fertilizer-prices.md", forPerson);
        Assert.DoesNotContain("care/pest-scouting.md", forAgent);
        Assert.DoesNotContain("supplies/fertilizer-prices.md", forAgent);
        Assert.Contains("care/propagation.md", forAgent);
    }
}

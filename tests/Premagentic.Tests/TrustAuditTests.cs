using Premagentic.Core;
using Premagentic.Core.Embeddings;
using Premagentic.Core.Ingestion;
using Premagentic.Core.Okf;
using Premagentic.Core.Retrieval;
using Premagentic.Core.Security;
using Premagentic.Core.Sources;
using Premagentic.Core.Storage;

namespace Premagentic.Tests;

/// <summary>
/// Every audit row records the trust policy its answer was served under,
/// beside its passages, so a bad answer can be traced to the setting that let
/// it through. Requires a running Docker daemon.
/// </summary>
public sealed class TrustAuditTests(DatastoreTestDatabase server) : IClassFixture<DatastoreTestDatabase>
{
    private static readonly string BundleRoot = Path.Combine(AppContext.BaseDirectory, "Fixtures", "okf-bundle");
    private static readonly DateTimeOffset June = new(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset July = new(2026, 7, 1, 12, 30, 0, TimeSpan.Zero);

    private sealed record AuditedPolicy(
        short? MinimumTier, bool? IncludeStale, DateTimeOffset? At, string Passages,
        string Kind, string Query, string? Heading, string AccessLabel);

    private static async Task<AuditedPolicy> LastAsync(PremagenticDatabase db)
    {
        await using var cmd = db.DataSource.CreateCommand("""
            SELECT trust_minimum_tier, include_stale, policy_at, passages::text, kind, query, requested_heading, access_label
            FROM prem_config.retrieval_event ORDER BY created_at DESC LIMIT 1
            """);
        await using var reader = await cmd.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return new AuditedPolicy(
            reader.IsDBNull(0) ? null : reader.GetInt16(0),
            reader.IsDBNull(1) ? null : reader.GetBoolean(1),
            reader.IsDBNull(2) ? null : reader.GetFieldValue<DateTimeOffset>(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.GetString(7));
    }

    private static async Task<(PremagenticDatabase Db, Guid Tenant)> IngestedAsync(DatastoreTestDatabase server)
    {
        var db = new PremagenticDatabase(await server.CreateDatabaseAsync());
        await db.InitializeAsync();
        var tenant = await db.EnsureTenantAsync("t", "T");
        await new IngestPipeline(db, new HashEmbeddingProvider()).RunAsync(
            tenant, new FileSystemSource(BundleRoot, DocumentAccess.Everyone) { OkfBundle = true });
        return (db, tenant);
    }

    [Fact]
    public async Task The_audit_row_carries_the_policy_the_answer_was_served_under()
    {
        await using var db = new PremagenticDatabase(await server.CreateDatabaseAsync());
        await db.InitializeAsync();
        var tenant = await db.EnsureTenantAsync("t", "T");
        await new IngestPipeline(db, new HashEmbeddingProvider()).RunAsync(
            tenant, new FileSystemSource(BundleRoot, DocumentAccess.Everyone) { OkfBundle = true });
        var search = new HybridSearch(db, new HashEmbeddingProvider());
        const string query = "yellow cards every Monday count caught";

        Task Search(TrustPolicy? policy, DateTimeOffset? asOf) =>
            search.SearchAsync(tenant, query, new SearchOptions(AccessScope.PublicOnly, TopK: 10, Trust: policy, AsOf: asOf));

        await Search(TrustPolicy.Resolve(CallerKind.Agent, null), June);
        var agent = await LastAsync(db);
        Assert.Equal(((short?)2, (bool?)false, (DateTimeOffset?)June), (agent.MinimumTier, agent.IncludeStale, agent.At));
        Assert.DoesNotContain("care/pest-scouting.md", agent.Passages);

        await Search(TrustPolicy.Resolve(CallerKind.Person, null), June);
        var person = await LastAsync(db);
        Assert.Equal(((short?)0, (bool?)true, (DateTimeOffset?)June), (person.MinimumTier, person.IncludeStale, person.At));
        // The passage the looser policy let through sits beside the policy that let it through.
        Assert.Contains("care/pest-scouting.md", person.Passages);

        await Search(new TrustPolicy(OkfTrustTier.MachineConfirmed, IncludeStale: true), July);
        var confirmed = await LastAsync(db);
        Assert.Equal(((short?)1, (bool?)true, (DateTimeOffset?)July), (confirmed.MinimumTier, confirmed.IncludeStale, confirmed.At));

        // No policy and no instant: the strict policy, judged at the clock.
        var before = DateTimeOffset.UtcNow.AddSeconds(-1);
        await Search(null, null);
        var after = DateTimeOffset.UtcNow.AddSeconds(1);
        var strict = await LastAsync(db);
        Assert.Equal(((short?)2, (bool?)false), (strict.MinimumTier, strict.IncludeStale));
        Assert.InRange(strict.At!.Value, before, after);
        Assert.Equal(("search", query, (string?)null), (strict.Kind, strict.Query, strict.Heading));
    }

    [Fact]
    public async Task A_section_fetch_is_recorded_like_a_search_with_what_it_served_and_the_policy()
    {
        var (db, tenant) = await IngestedAsync(server);
        await using var _ = db;
        const string path = "care/pest-scouting.md";
        var sections = new SectionFetcher(db);
        var person = new SearchOptions(AccessScope.PublicOnly, Trust: TrustPolicy.Resolve(CallerKind.Person, null), AsOf: June);

        var section = await sections.GetAsync(tenant, person, path, "Route");

        Assert.NotNull(section);
        Assert.NotEmpty(section.Chunks);
        var fetched = await LastAsync(db);
        Assert.Equal(("section", path, "Route", "public-only"), (fetched.Kind, fetched.Query, fetched.Heading, fetched.AccessLabel));
        Assert.Equal(((short?)0, (bool?)true, (DateTimeOffset?)June), (fetched.MinimumTier, fetched.IncludeStale, fetched.At));

        await using var hash = db.DataSource.CreateCommand("SELECT content_hash FROM prem_index.document WHERE path = @path");
        hash.Parameters.AddWithValue("path", path);
        var contentHash = (string)(await hash.ExecuteScalarAsync())!;
        using var passages = System.Text.Json.JsonDocument.Parse(fetched.Passages);
        Assert.Equal(
            section.Chunks.Select(c => (path, c.HeadingPath, contentHash)),
            passages.RootElement.EnumerateArray().Select(p => (
                p.GetProperty("path").GetString()!, p.GetProperty("heading_path").GetString()!, p.GetProperty("content_hash").GetString()!)));
    }

    [Theory]
    [InlineData("care/pest-scouting.md")]   // there, and held back from an agent by the trust gate
    [InlineData("care/no-such-concept.md")] // not in the index at all
    public async Task A_section_fetch_that_serves_nothing_is_recorded_with_no_passages(string path)
    {
        var (db, tenant) = await IngestedAsync(server);
        await using var _ = db;
        var agent = new SearchOptions(AccessScope.PublicOnly, Trust: TrustPolicy.Resolve(CallerKind.Agent, null), AsOf: June);

        Assert.Null(await new SectionFetcher(db).GetAsync(tenant, agent, path, heading: null));

        var fetched = await LastAsync(db);
        Assert.Equal(("section", path, (string?)null, "[]"), (fetched.Kind, fetched.Query, fetched.Heading, fetched.Passages));
        Assert.Equal(((short?)2, (bool?)false, (DateTimeOffset?)June), (fetched.MinimumTier, fetched.IncludeStale, fetched.At));
    }
}

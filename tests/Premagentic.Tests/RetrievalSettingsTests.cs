using System.Diagnostics;
using System.Text.Json;
using Premagentic.Core;
using Premagentic.Core.Identity;
using Premagentic.Core.Retrieval;
using Premagentic.Core.Storage;

namespace Premagentic.Tests;

/// <summary>
/// The four retrieval settings: what each accepts, and what a stored value
/// that cannot be used turns into. Pure; no database.
/// </summary>
public sealed class RetrievalSettingsTests
{
    private const string Floor = RetrievalSettings.NoAnswerDistanceFloor;
    private const string K = RetrievalSettings.RrfK;
    private const string Fallback = RetrievalSettings.FallbackRrfWeight;
    private const string Authority = RetrievalSettings.Authority;

    private static JsonElement Json(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private static void AssertRefused(string key, string json)
    {
        Assert.False(RetrievalSettings.TryParse(key, Json(json), out var problem));
        Assert.NotNull(problem);
        Assert.StartsWith(key, problem);
        AssertOneSentence(problem);
    }

    private static void AssertOneSentence(string problem)
    {
        Assert.EndsWith(".", problem);
        Assert.DoesNotContain(". ", problem);
    }

    [Fact]
    public void The_keys_are_the_four_the_contract_names()
    {
        Assert.Equal(
            new[] { "retrieval.no_answer_distance_floor", "retrieval.rrf_k", "retrieval.fallback_rrf_weight", "retrieval.authority_weights" },
            RetrievalSettings.Keys);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-0")]
    [InlineData("-0.1")]
    [InlineData("2")]
    [InlineData("2.5")]
    [InlineData("1e400")]
    [InlineData("\"0.5\"")]
    [InlineData("null")]
    [InlineData("true")]
    [InlineData("[0.5]")]
    [InlineData("{\"value\": 0.5}")]
    public void The_no_answer_floor_refuses_anything_but_a_number_above_0_and_below_2(string json)
    {
        AssertRefused(Floor, json);
        RetrievalSettings.TryParse(Floor, Json(json), out var problem);
        Assert.Contains(RetrievalSettings.Allowed(Floor), problem);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("1001")]
    [InlineData("2147483648")]
    [InlineData("60.5")]
    [InlineData("60.0")]
    [InlineData("\"60\"")]
    [InlineData("null")]
    [InlineData("[60]")]
    public void Rrf_k_refuses_anything_but_a_whole_number_from_1_to_1000(string json)
    {
        AssertRefused(K, json);
        RetrievalSettings.TryParse(K, Json(json), out var problem);
        Assert.Contains(RetrievalSettings.Allowed(K), problem);
    }

    [Theory]
    [InlineData("-0.01")]
    [InlineData("1.01")]
    [InlineData("2")]
    [InlineData("1e400")]
    [InlineData("\"0.5\"")]
    [InlineData("null")]
    [InlineData("false")]
    public void The_fallback_weight_refuses_anything_but_a_number_from_0_to_1(string json)
    {
        AssertRefused(Fallback, json);
        RetrievalSettings.TryParse(Fallback, Json(json), out var problem);
        Assert.Contains(RetrievalSettings.Allowed(Fallback), problem);
    }

    [Theory]
    [InlineData("1.2")]
    [InlineData("[]")]
    [InlineData("\"runbook\"")]
    [InlineData("null")]
    [InlineData("{\"default\": 0}")]
    [InlineData("{\"default\": -1}")]
    [InlineData("{\"default\": \"1\"}")]
    [InlineData("{\"default\": 1e400}")]
    [InlineData("{\"default\": 1, \"default\": 1}")]
    [InlineData("{\"by_class\": [\"runbook\"]}")]
    [InlineData("{\"by_class\": {\"runbook\": 0}}")]
    [InlineData("{\"by_class\": {\"runbook\": -1.2}}")]
    [InlineData("{\"by_class\": {\"runbook\": \"1.2\"}}")]
    [InlineData("{\"by_class\": {\"runbook\": null}}")]
    [InlineData("{\"by_class\": {\"\": 1.2}}")]
    [InlineData("{\"by_class\": {\"  \": 1.2}}")]
    [InlineData("{\"by_class\": {\"Runbook\": 1.2, \"runbook\": 0.8}}")]
    [InlineData("{\"byclass\": {\"runbook\": 1.2}}")]
    [InlineData("{\"Default\": 1.0}")]
    public void Authority_weights_refuse_anything_but_positive_weights_in_the_two_members(string json)
    {
        AssertRefused(Authority, json);
    }

    [Theory]
    [InlineData(Floor, "0.0001")]
    [InlineData(Floor, "0.55")]
    [InlineData(Floor, "1.9999")]
    [InlineData(K, "1")]
    [InlineData(K, "60")]
    [InlineData(K, "1000")]
    [InlineData(Fallback, "0")]
    [InlineData(Fallback, "0.5")]
    [InlineData(Fallback, "1")]
    [InlineData(Authority, "{}")]
    [InlineData(Authority, "{\"default\": 0.9}")]
    [InlineData(Authority, "{\"by_class\": {}}")]
    [InlineData(Authority, "{\"default\": 1.0, \"by_class\": {\"runbook\": 1.2, \"chat-log\": 0.5}}")]
    public void The_edges_of_each_range_are_allowed(string key, string json)
    {
        Assert.True(RetrievalSettings.TryParse(key, Json(json), out var problem));
        Assert.Null(problem);
    }

    [Fact]
    public void Each_default_is_allowed_by_its_own_key()
    {
        foreach (var key in RetrievalSettings.Keys)
            Assert.True(RetrievalSettings.TryParse(key, RetrievalSettings.DefaultValue(key), out _), key);
    }

    [Fact]
    public void An_unknown_key_is_refused_with_the_keys_named()
    {
        Assert.False(RetrievalSettings.TryParse("retrieval.rrf", Json("60"), out var problem));
        AssertOneSentence(problem!);
        Assert.All(RetrievalSettings.Keys, k => Assert.Contains(k, problem));
        Assert.False(RetrievalSettings.TryParse("trust.agents_minimum_tier", Json("\"unverified\""), out _));
        Assert.Throws<ArgumentException>(() => RetrievalSettings.Allowed("retrieval.rrf"));
    }

    [Fact]
    public void A_long_stored_value_is_clipped_in_the_problem()
    {
        var pasted = new string('x', 5_000);
        Assert.False(RetrievalSettings.TryParse(K, Json($"\"{pasted}\""), out var problem));
        Assert.True(problem!.Length < 200, problem);
    }

    [Fact]
    public void Nothing_stored_is_the_code_default_with_no_problems()
    {
        var reading = RetrievalSettings.FromStored(new Dictionary<string, JsonElement>());

        Assert.Equal(RetrievalTuning.Default, reading.Tuning);
        Assert.Same(AuthorityWeights.Flat, reading.Authority);
        Assert.Empty(reading.Problems);
    }

    [Fact]
    public void Every_stored_value_is_applied()
    {
        var reading = RetrievalSettings.FromStored(new Dictionary<string, JsonElement>
        {
            [Floor] = Json("0.7"),
            [K] = Json("10"),
            [Fallback] = Json("0.25"),
            [Authority] = Json("{\"default\": 0.9, \"by_class\": {\"runbook\": 1.2}}"),
        });

        Assert.Equal(new RetrievalTuning(NoAnswerDistanceFloor: 0.7, RrfK: 10, FallbackRrfWeight: 0.25), reading.Tuning);
        Assert.Equal(1.2, reading.Authority.For("RUNBOOK"));
        Assert.Equal(0.9, reading.Authority.For("minutes"));
        Assert.Equal(0.9, reading.Authority.For(null));
        Assert.Empty(reading.Problems);
    }

    [Fact]
    public void A_missing_authority_member_keeps_its_flat_value()
    {
        var onlyClasses = RetrievalSettings.FromStored(new Dictionary<string, JsonElement>
        {
            [Authority] = Json("{\"by_class\": {\"runbook\": 1.2}}"),
        });
        Assert.Equal(1.0, onlyClasses.Authority.For("minutes"));
        Assert.Equal(1.2, onlyClasses.Authority.For("runbook"));

        var onlyDefault = RetrievalSettings.FromStored(new Dictionary<string, JsonElement>
        {
            [Authority] = Json("{\"default\": 0.8}"),
        });
        Assert.Equal(0.8, onlyDefault.Authority.For("runbook"));
    }

    [Fact]
    public void An_invalid_stored_value_keeps_its_default_and_is_reported_while_the_others_apply()
    {
        var reading = RetrievalSettings.FromStored(new Dictionary<string, JsonElement>
        {
            [Floor] = Json("0.7"),
            [K] = Json("0"),
            [Fallback] = Json("0.25"),
            [Authority] = Json("{\"by_class\": {\"runbook\": -1}}"),
        });

        Assert.Equal(0.7, reading.Tuning.NoAnswerDistanceFloor);
        Assert.Equal(RetrievalTuning.Default.RrfK, reading.Tuning.RrfK);
        Assert.Equal(0.25, reading.Tuning.FallbackRrfWeight);
        Assert.Same(AuthorityWeights.Flat, reading.Authority);

        Assert.Equal(new[] { K, Authority }, reading.Problems.Select(p => p.Key));
        Assert.All(reading.Problems, p => AssertOneSentence(p.Problem));
        Assert.Contains(RetrievalSettings.Allowed(K), reading.Problems[0].Problem);
    }

    [Fact]
    public void Keys_that_are_not_retrieval_settings_are_ignored()
    {
        var reading = RetrievalSettings.FromStored(new Dictionary<string, JsonElement>
        {
            ["trust.agents_minimum_tier"] = Json("\"unverified\""),
            ["retrieval.rrf"] = Json("5"),
        });

        Assert.Equal(RetrievalTuning.Default, reading.Tuning);
        Assert.Empty(reading.Problems);
    }
}

/// <summary>
/// The retrieval settings read from the database. Requires a running Docker daemon.
/// </summary>
public sealed class RetrievalSettingsStoreTests(DatastoreTestDatabase server) : IClassFixture<DatastoreTestDatabase>
{
    private async Task<(PremagenticDatabase Db, Guid Tenant)> NewAsync()
    {
        var db = new PremagenticDatabase(await server.CreateDatabaseAsync());
        await db.InitializeAsync();
        return (db, await db.EnsureTenantAsync("t", "T"));
    }

    private static async Task StoreRawAsync(PremagenticDatabase db, Guid tenant, string key, string json)
    {
        await using var cmd = db.DataSource.CreateCommand(
            "INSERT INTO prem_config.setting(tenant_id, key, value) VALUES(@tenant, @key, @value::jsonb)");
        cmd.Parameters.AddWithValue("tenant", tenant);
        cmd.Parameters.AddWithValue("key", key);
        cmd.Parameters.AddWithValue("value", json);
        await cmd.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task Load_reads_what_is_stored_for_its_own_tenant_and_a_missing_key_keeps_its_default()
    {
        var (db, tenant) = await NewAsync();
        await using var _ = db;
        var other = await db.EnsureTenantAsync("o", "O");

        Assert.Equal(RetrievalTuning.Default, (await RetrievalSettings.LoadAsync(new SettingsStore(db, tenant))).Tuning);

        await StoreRawAsync(db, tenant, RetrievalSettings.RrfK, "12");
        await StoreRawAsync(db, tenant, RetrievalSettings.Authority, "{\"by_class\": {\"runbook\": 1.5}}");
        await StoreRawAsync(db, other, RetrievalSettings.FallbackRrfWeight, "0.1");

        var reading = await RetrievalSettings.LoadAsync(new SettingsStore(db, tenant));
        Assert.Equal(RetrievalTuning.Default with { RrfK = 12 }, reading.Tuning);
        Assert.Equal(1.5, reading.Authority.For("Runbook"));
        Assert.Empty(reading.Problems);

        var others = await RetrievalSettings.LoadAsync(new SettingsStore(db, other));
        Assert.Equal(RetrievalTuning.Default with { FallbackRrfWeight = 0.1 }, others.Tuning);
        Assert.Equal(1.0, others.Authority.For("runbook"));
    }

    [Fact]
    public async Task A_stored_value_out_of_range_is_reported_and_the_default_applies()
    {
        var (db, tenant) = await NewAsync();
        await using var _ = db;
        await StoreRawAsync(db, tenant, RetrievalSettings.NoAnswerDistanceFloor, "5");
        await StoreRawAsync(db, tenant, RetrievalSettings.FallbackRrfWeight, "\"half\"");

        var reading = await RetrievalSettings.LoadAsync(new SettingsStore(db, tenant));

        Assert.Equal(RetrievalTuning.Default, reading.Tuning);
        Assert.Equal(
            new[] { RetrievalSettings.NoAnswerDistanceFloor, RetrievalSettings.FallbackRrfWeight },
            reading.Problems.Select(p => p.Key));
    }

    [Fact]
    public async Task A_setting_changed_inside_a_callers_transaction_lands_only_when_the_caller_commits()
    {
        var (db, tenant) = await NewAsync();
        await using var _ = db;
        var outside = new SettingsStore(db, tenant);
        await outside.SetAsync(RetrievalSettings.FallbackRrfWeight, JsonDocument.Parse("0.25").RootElement);

        foreach (var commit in new[] { false, true })
        {
            await using var conn = await db.DataSource.OpenConnectionAsync();
            await using var tx = await conn.BeginTransactionAsync();
            var inside = new SettingsStore(db, tenant, tx);
            await inside.SetAsync(RetrievalSettings.RrfK, JsonDocument.Parse("12").RootElement);
            Assert.True(await inside.RemoveAsync(RetrievalSettings.FallbackRrfWeight));

            // The caller's transaction reads its own change, and nobody else does yet.
            Assert.Equal(new RetrievalTuning(RrfK: 12), (await RetrievalSettings.LoadAsync(inside)).Tuning);
            Assert.Equal(new RetrievalTuning(FallbackRrfWeight: 0.25), (await RetrievalSettings.LoadAsync(outside)).Tuning);

            // A trust key is refused inside a transaction as outside one, before anything is sent.
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                inside.SetAsync("trust.stale", JsonDocument.Parse("\"shown-to-everyone\"").RootElement));

            if (commit) await tx.CommitAsync(); else await tx.RollbackAsync();

            Assert.Equal(
                commit ? new RetrievalTuning(RrfK: 12) : new RetrievalTuning(FallbackRrfWeight: 0.25),
                (await RetrievalSettings.LoadAsync(outside)).Tuning);
        }
        Assert.Null(await outside.GetAsync("trust.stale"));
    }

    [Fact]
    public async Task Load_reads_all_four_keys_in_one_round_trip()
    {
        var (db, tenant) = await NewAsync();
        await using var _ = db;
        foreach (var key in RetrievalSettings.Keys)
            await StoreRawAsync(db, tenant, key, RetrievalSettings.DefaultValue(key).GetRawText());
        var settings = new SettingsStore(db, tenant);
        await RetrievalSettings.LoadAsync(settings); // the pool opens its connection outside the count

        // Npgsql starts one activity per command. Counting only children of an
        // activity this test starts keeps other tests' commands out of the count.
        using var parent = new Activity("retrieval-settings-load").Start();
        var commands = 0;
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == "Npgsql",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStarted = a =>
            {
                if (a.ParentSpanId == parent.SpanId) Interlocked.Increment(ref commands);
            },
        };
        ActivitySource.AddActivityListener(listener);

        var reading = await RetrievalSettings.LoadAsync(settings);

        Assert.Empty(reading.Problems);
        Assert.Equal(1, commands);
    }
}

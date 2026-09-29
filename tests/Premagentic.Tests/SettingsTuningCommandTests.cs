using System.Text.Json;
using Premagentic.Cli.Admin;
using Premagentic.Core.Evaluation;
using Premagentic.Core.Okf;
using Premagentic.Core.Retrieval;
using Premagentic.Core.Storage;

namespace Premagentic.Tests;

/// <summary>
/// <c>prem settings</c> for the retrieval tuning and the golden set path: each
/// value checked by <see cref="RetrievalSettings"/> before it is stored, stored
/// with its entry in the change record, and removable, so the default applies
/// again. Requires a running Docker daemon.
/// </summary>
[Collection(ConsoleCollection.Name)]
public sealed class SettingsTuningCommandTests(DatastoreTestDatabase server) : IClassFixture<DatastoreTestDatabase>
{
    private const string RrfK = RetrievalSettings.RrfK;
    private const string Golden = TuningSettingsStore.GoldenSetPath;

    private async Task<(PremagenticDatabase Db, Guid Tenant)> EmptyAsync()
    {
        var db = new PremagenticDatabase(await server.CreateDatabaseAsync());
        await db.InitializeAsync();
        return (db, await db.EnsureTenantAsync("t", "T"));
    }

    private static Task<(int Exit, string Out, string Err)> Settings(PremagenticDatabase db, Guid tenant, params string[] args) =>
        ConsoleCapture.RunAsync(() => SettingsCommands.RunAsync(["settings", .. args], db, tenant));

    private static async Task<JsonElement?> StoredAsync(PremagenticDatabase db, string key)
    {
        await using var cmd = db.DataSource.CreateCommand("SELECT value::text FROM prem_config.setting WHERE key = @key");
        cmd.Parameters.AddWithValue("key", key);
        return await cmd.ExecuteScalarAsync() is string json ? JsonDocument.Parse(json).RootElement.Clone() : null;
    }

    private static async Task<List<(string Kind, string Target, string? Old, string? New)>> RecordAsync(PremagenticDatabase db)
    {
        await using var cmd = db.DataSource.CreateCommand(
            "SELECT kind, target, old_value::text, new_value::text FROM prem_config.admin_event ORDER BY id");
        var rows = new List<(string, string, string?, string?)>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            rows.Add((reader.GetString(0), reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3)));
        return rows;
    }

    private static async Task StoreRawAsync(PremagenticDatabase db, Guid tenant, string key, string json)
    {
        await using var cmd = db.DataSource.CreateCommand(
            "INSERT INTO prem_config.setting(tenant_id, key, value) VALUES(@t, @key, @value::jsonb)");
        cmd.Parameters.AddWithValue("t", tenant);
        cmd.Parameters.AddWithValue("key", key);
        cmd.Parameters.AddWithValue("value", json);
        await cmd.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task A_retrieval_value_is_stored_with_its_change_record_and_setting_it_again_records_nothing()
    {
        var (db, tenant) = await EmptyAsync();
        await using var _ = db;

        var set = await Settings(db, tenant, "set", RrfK, "40");
        Assert.Equal(0, set.Exit);
        Assert.Contains($"{RrfK} is now 40 (was the default, 60). It applies to the next search.", set.Out);
        Assert.Equal(40, (await StoredAsync(db, RrfK))!.Value.GetInt32());
        Assert.Equal([(TuningSettingsStore.SetKind, RrfK, (string?)null, (string?)"40")], await RecordAsync(db));

        var again = await Settings(db, tenant, "set", RrfK, "40");
        Assert.Equal(0, again.Exit);
        Assert.Contains("Nothing changed and nothing was recorded.", again.Out);
        Assert.Single(await RecordAsync(db));

        var weights = await Settings(db, tenant, "set", RetrievalSettings.Authority, """{"default": 1.0, "by_class": {"runbook": 1.2}}""");
        Assert.Equal(0, weights.Exit);
        Assert.Equal(1.2, (await StoredAsync(db, RetrievalSettings.Authority))!.Value.GetProperty("by_class").GetProperty("runbook").GetDouble());

        var floor = await Settings(db, tenant, "set", RetrievalSettings.NoAnswerDistanceFloor, "0.6");
        Assert.Contains("It applies to the next golden-set evaluation; a search is not filtered by it.", floor.Out);

        var history = (await Settings(db, tenant, "history")).Out;
        Assert.Contains($"(none) -> 40  by cli ({Core.Admin.AdminActor.Cli().Account})", history);
    }

    [Theory]
    [InlineData(RetrievalSettings.NoAnswerDistanceFloor, "2", "a number greater than 0 and less than 2")]
    [InlineData(RetrievalSettings.RrfK, "0", "a whole number from 1 to 1000")]
    [InlineData(RetrievalSettings.RrfK, "sixty", "a whole number from 1 to 1000, not 'sixty'")]
    [InlineData(RetrievalSettings.FallbackRrfWeight, "1.5", "a number from 0 to 1")]
    [InlineData(RetrievalSettings.Authority, """{"default": 0}""", "a default weight greater than 0")]
    public async Task A_retrieval_value_the_contract_refuses_is_named_and_nothing_is_stored_or_recorded(string key, string value, string said)
    {
        var (db, tenant) = await EmptyAsync();
        await using var _ = db;

        var run = await Settings(db, tenant, "set", key, value);

        Assert.Equal(1, run.Exit);
        Assert.Contains(said, run.Err);
        Assert.Null(await StoredAsync(db, key));
        Assert.Empty(await RecordAsync(db));
    }

    [Fact]
    public async Task The_list_and_get_say_where_each_tuning_value_came_from()
    {
        var (db, tenant) = await EmptyAsync();
        await using var _ = db;
        await Settings(db, tenant, "set", RrfK, "40");
        await StoreRawAsync(db, tenant, RetrievalSettings.FallbackRrfWeight, "\"high\"");

        var list = (await Settings(db, tenant, "list")).Out.Split(Environment.NewLine);
        Assert.Contains(list, l => l.StartsWith(RrfK) && l.Contains("40") && l.TrimEnd().EndsWith("set"));
        Assert.Contains(list, l => l.StartsWith(RetrievalSettings.NoAnswerDistanceFloor) && l.Contains("0.55") && l.TrimEnd().EndsWith("default"));
        Assert.Contains(list, l => l.StartsWith(RetrievalSettings.FallbackRrfWeight) && l.Contains("0.5")
            && l.Contains("UNUSABLE: retrieval.fallback_rrf_weight takes a number from 0 to 1, not the text \"high\". The default applies."));
        Assert.Contains(list, l => l.StartsWith(Golden) && l.Contains("not set"));
        // The trust settings are listed as before.
        Assert.Contains(list, l => l.StartsWith(TrustSettingsStore.AgentsMinimumTier) && l.TrimEnd().EndsWith("default"));

        var get = await Settings(db, tenant, "get", RrfK);
        Assert.Equal(0, get.Exit);
        Assert.Contains($"{RrfK} = 40 (set)", get.Out);
        Assert.Contains("Default: 60. It takes a whole number from 1 to 1000.", get.Out);
    }

    [Fact]
    public async Task Unset_puts_the_default_back_and_is_recorded_as_setting_unset_and_a_trust_key_is_refused()
    {
        var (db, tenant) = await EmptyAsync();
        await using var _ = db;
        await Settings(db, tenant, "set", RrfK, "40");
        await Settings(db, tenant, "set", TrustSettingsStore.AgentsMinimumTier, "machine-confirmed");

        var unset = await Settings(db, tenant, "unset", RrfK);
        Assert.Equal(0, unset.Exit);
        Assert.Contains($"{RrfK} is back to its default, 60 (was 40).", unset.Out);
        Assert.Null(await StoredAsync(db, RrfK));
        Assert.Equal((TuningSettingsStore.UnsetKind, RrfK, (string?)"40", (string?)null), (await RecordAsync(db))[^1]);

        var records = (await RecordAsync(db)).Count;
        var again = await Settings(db, tenant, "unset", RrfK);
        Assert.Equal(0, again.Exit);
        Assert.Contains("Nothing changed and nothing was recorded.", again.Out);
        Assert.Equal(records, (await RecordAsync(db)).Count);

        var trust = await Settings(db, tenant, "unset", TrustSettingsStore.AgentsMinimumTier);
        Assert.Equal(1, trust.Exit);
        Assert.Contains("is a trust setting and cannot be unset", trust.Err);
        Assert.Equal("machine-confirmed", (await StoredAsync(db, TrustSettingsStore.AgentsMinimumTier))!.Value.GetString());

        var unknown = await Settings(db, tenant, "unset", "retrieval.top_k");
        Assert.Equal(1, unknown.Exit);
        Assert.Contains($"The settings are: {TrustSettingsStore.AgentsMinimumTier}", unknown.Err);
        Assert.Contains(Golden, unknown.Err);
        Assert.Equal(records, (await RecordAsync(db)).Count);
    }

    [Fact]
    public async Task The_golden_set_path_is_stored_only_when_absolute_and_a_missing_file_is_warned_about()
    {
        var (db, tenant) = await EmptyAsync();
        await using var _ = db;
        var file = Path.Combine(Path.GetTempPath(), $"prem-golden-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(file, "[]");
        try
        {
            foreach (var relative in new[] { "golden.json", Path.Combine("eval", "golden.json") })
            {
                var refused = await Settings(db, tenant, "set", Golden, relative);
                Assert.Equal(1, refused.Exit);
                Assert.Contains("takes an absolute path", refused.Err);
            }
            Assert.Null(await StoredAsync(db, Golden));
            Assert.Empty(await RecordAsync(db));

            var set = await Settings(db, tenant, "set", Golden, file);
            Assert.Equal(0, set.Exit);
            Assert.DoesNotContain("WARNING", set.Out);
            Assert.Equal(file, (await StoredAsync(db, Golden))!.Value.GetString());
            Assert.Contains($"{Golden} = {file} (set)", (await Settings(db, tenant, "get", Golden)).Out);
            Assert.Equal(file, (await new TuningSettingsStore(db, tenant).ReadGoldenSetPathAsync()).Path);

            var missing = Path.Combine(Path.GetTempPath(), $"prem-golden-missing-{Guid.NewGuid():N}.json");
            var warned = await Settings(db, tenant, "set", Golden, missing);
            Assert.Equal(0, warned.Exit);
            Assert.Contains($"WARNING: this machine has no file at {missing}.", warned.Out);
            Assert.Equal(missing, (await StoredAsync(db, Golden))!.Value.GetString());

            var unset = await Settings(db, tenant, "unset", Golden);
            Assert.Contains($"{Golden} is no longer set (was {missing}).", unset.Out);
            Assert.Null(await StoredAsync(db, Golden));
            Assert.Equal(
                [TuningSettingsStore.SetKind, TuningSettingsStore.SetKind, TuningSettingsStore.UnsetKind],
                (await RecordAsync(db)).Select(r => r.Kind));
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public async Task A_stored_golden_set_path_that_is_relative_or_not_text_reads_as_a_problem_not_a_path()
    {
        var (db, tenant) = await EmptyAsync();
        await using var _ = db;
        var store = new TuningSettingsStore(db, tenant);

        Assert.Equal(new GoldenSetPathReading(null, null), await store.ReadGoldenSetPathAsync());

        await StoreRawAsync(db, tenant, Golden, "\"golden.json\"");
        var relative = await store.ReadGoldenSetPathAsync();
        Assert.Null(relative.Path);
        Assert.Contains("takes an absolute path", relative.Problem);

        await using (var cmd = db.DataSource.CreateCommand("UPDATE prem_config.setting SET value = '42'::jsonb WHERE key = @key"))
        {
            cmd.Parameters.AddWithValue("key", Golden);
            await cmd.ExecuteNonQueryAsync();
        }
        var number = await store.ReadGoldenSetPathAsync();
        Assert.Null(number.Path);
        Assert.Contains("not text", number.Problem);
        Assert.Contains("UNUSABLE", (await Settings(db, tenant, "get", Golden)).Out);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            store.SetAsync(Golden, JsonSerializer.SerializeToElement(42), Core.Admin.AdminActor.Cli()));
    }
}

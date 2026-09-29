using System.Text.Json;
using Premagentic.Cli.Admin;
using Premagentic.Core.Extensions;
using Premagentic.Core.Identity;
using Premagentic.Core.Okf;
using Premagentic.Core.Storage;

namespace Premagentic.Tests;

/// <summary>
/// <c>prem settings list</c>, <c>get</c>, <c>set</c> and <c>unset</c> over
/// every key <see cref="SettingsCatalog"/> defines, and the key a sample
/// extension adds through the real host: each key is listed, read, set with a
/// value it accepts and put back, with one row in the change record per
/// change. A key added to the catalog without a sample value here fails the
/// theory by name. Requires a running Docker daemon.
/// </summary>
[Collection(ConsoleCollection.Name)]
public sealed class SettingsCatalogCommandTests(DatastoreTestDatabase server) : IClassFixture<DatastoreTestDatabase>
{
    /// <summary>A value each key accepts, as it is typed at the command line.</summary>
    private static readonly Dictionary<string, string> Samples = new(StringComparer.Ordinal)
    {
        ["retrieval.no_answer_distance_floor"] = "0.6",
        ["retrieval.rrf_k"] = "40",
        ["retrieval.fallback_rrf_weight"] = "0.4",
        ["retrieval.authority_weights"] = """{"default": 1.0, "by_class": {"runbook": 1.2}}""",
        ["evaluation.golden_set_path"] = Path.Combine(Path.GetTempPath(), "prem-catalog-golden.json"),
        ["mcp.tool_descriptions"] = """{"search_knowledge": "Search the handbook."}""",
        ["mcp.instructions"] = "Cite the file and heading of every passage you use.",
        ["extensions.folder"] = Path.GetTempPath(),
        ["extensions.allowed"] = $$"""[{"name": "sentence-chunker", "sha256": "{{new string('a', 64)}}"}]""",
        ["agents.self_service_max"] = "3",
        ["connect.snippets"] = """{"other": "Ask the desk at {address} with {token}"}""",
        // Stored false where nothing was stored is a change; true would need the
        // public address first, which the two-key test covers.
        ["mcp.max_request_bytes"] = "1048576",
        ["mcp.oauth.enabled"] = "false",
        ["mcp.oauth.public_url"] = "https://prem.example.internal:8443",
        ["mcp.oauth.dynamic_registration"] = "false",
        ["mcp.oauth.dynamic_redirect_uris"] = """["https://claude.ai/api/mcp/auth_callback"]""",
        ["mcp.oauth.max_pending_clients"] = "50",
        ["mcp.oauth.pending_clients_per_address"] = "5",
        ["mcp.oauth.registrations_per_hour"] = "20",
        ["mcp.oauth.access_token_minutes"] = "30",
        ["mcp.oauth.refresh_token_days"] = "14",
        ["mcp.oauth.grant_days"] = "60",
        ["mcp.oauth.code_seconds"] = "120",
        [GreetingSample.SettingKey] = "Good day",
    };

    /// <summary>The greeting sample, loaded through the host, so its setting is one of the keys.</summary>
    private static readonly ExtensionHost Extensions = GreetingSample.LoadedOnce();

    public static TheoryData<string> Keys()
    {
        var data = new TheoryData<string>();
        foreach (var d in Extensions.Settings.All) data.Add(d.Key);
        return data;
    }

    private async Task<(PremagenticDatabase Db, Guid Tenant)> EmptyAsync()
    {
        var db = new PremagenticDatabase(await server.CreateDatabaseAsync());
        await db.InitializeAsync();
        return (db, await db.EnsureTenantAsync("t", "T"));
    }

    private static Task<(int Exit, string Out, string Err)> Settings(PremagenticDatabase db, Guid tenant, params string[] args) =>
        ConsoleCapture.RunAsync(() => SettingsCommands.RunAsync(["settings", .. args], db, tenant, Extensions));

    private static async Task<long> RecordCountAsync(PremagenticDatabase db)
    {
        await using var cmd = db.DataSource.CreateCommand("SELECT count(*) FROM prem_config.admin_event");
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    private static async Task<JsonElement?> StoredAsync(PremagenticDatabase db, string key)
    {
        await using var cmd = db.DataSource.CreateCommand("SELECT value::text FROM prem_config.setting WHERE key = @key");
        cmd.Parameters.AddWithValue("key", key);
        return await cmd.ExecuteScalarAsync() is string json ? JsonDocument.Parse(json).RootElement.Clone() : null;
    }

    /// <summary>A trust key's sample is a value other than its default, so setting it is a change.</summary>
    private static string SampleFor(SettingDefinition d) => d.IsTrust
        ? TrustSettingsStore.AllowedValues(d.Key).First(v => v != TrustSettingsStore.DefaultValue(d.Key))
        : Samples.TryGetValue(d.Key, out var sample)
            ? sample
            : throw new Xunit.Sdk.XunitException(
                $"{d.Key} is in the settings catalog and has no sample value in this test. Add one, so the four verbs are proven on it.");

    [Theory]
    [MemberData(nameof(Keys))]
    public async Task Every_verb_works_on_every_key(string key)
    {
        var d = Extensions.Settings.Find(key)!;
        var sample = SampleFor(d);
        var (db, tenant) = await EmptyAsync();
        await using var _ = db;

        var listed = await Settings(db, tenant, "list");
        Assert.Equal(0, listed.Exit);
        Assert.Contains(listed.Out.Split(Environment.NewLine), l => l.StartsWith(key + " ", StringComparison.Ordinal));

        var before = await Settings(db, tenant, "get", key);
        Assert.Equal(0, before.Exit);
        Assert.Contains($"{key} = ", before.Out);
        Assert.Contains($"It takes {d.Accepts}.", before.Out);

        var set = await Settings(db, tenant, "set", key, sample);
        Assert.Equal("", set.Err);
        Assert.Equal(0, set.Exit);
        Assert.Contains($"{key} is now ", set.Out);
        Assert.NotNull(await StoredAsync(db, key));
        Assert.Equal(1, await RecordCountAsync(db));

        var after = await Settings(db, tenant, "get", key);
        Assert.Equal(0, after.Exit);
        Assert.Contains("(set)", after.Out);
        Assert.Contains((await Settings(db, tenant, "list")).Out.Split(Environment.NewLine),
            l => l.StartsWith(key + " ", StringComparison.Ordinal)
                 && l.TrimEnd().Replace($" (added by {GreetingSample.Name})", "").EndsWith("set", StringComparison.Ordinal));

        var unset = await Settings(db, tenant, "unset", key);
        if (d.IsTrust)
        {
            Assert.Equal(1, unset.Exit);
            Assert.Contains("is a trust setting and cannot be unset", unset.Err);
            Assert.NotNull(await StoredAsync(db, key));
            Assert.Equal(1, await RecordCountAsync(db));
            return;
        }
        Assert.Equal("", unset.Err);
        Assert.Equal(0, unset.Exit);
        Assert.Null(await StoredAsync(db, key));
        Assert.Equal(2, await RecordCountAsync(db));
        Assert.DoesNotContain("(set)", (await Settings(db, tenant, "get", key)).Out);
    }

    [Theory]
    [InlineData("""["a list"]""", "takes an object of assistant kinds to templates")]
    [InlineData("""{"telegraph": "x {token}"}""", "has no assistant kind 'telegraph'")]
    [InlineData("""{"other": ""}""", "takes a text of 1 to 4,000 characters for 'other'")]
    [InlineData("""{"other": "no place for it"}""", "does not say where the token goes")]
    public async Task A_snippet_template_the_connect_page_could_not_use_is_refused(string value, string said)
    {
        var (db, tenant) = await EmptyAsync();
        await using var _ = db;

        var refused = await Settings(db, tenant, "set", SettingsCatalog.ConnectSnippets, value);

        Assert.Equal(1, refused.Exit);
        Assert.Contains(said, refused.Err);
        Assert.Null(await StoredAsync(db, SettingsCatalog.ConnectSnippets));
        Assert.Equal(0, await RecordCountAsync(db));
    }

    [Fact]
    public async Task A_value_a_key_does_not_accept_is_refused_with_what_it_takes_and_nothing_is_stored()
    {
        var (db, tenant) = await EmptyAsync();
        await using var _ = db;

        var refused = await Settings(db, tenant, "set", AgentSettings.SelfServiceMax, "11");

        Assert.Equal(1, refused.Exit);
        Assert.Contains($"takes {AgentSettings.SelfServiceAccepts}", refused.Err);
        Assert.Null(await StoredAsync(db, AgentSettings.SelfServiceMax));
        Assert.Equal(0, await RecordCountAsync(db));
    }
}

using System.Text.RegularExpressions;
using Premagentic.Core;
using Premagentic.Core.Admin;
using Premagentic.Core.Embeddings;
using Premagentic.Core.Identity;
using Premagentic.Core.Ingestion;
using Premagentic.Core.Okf;
using Premagentic.Core.Retrieval;
using Premagentic.Core.Security;
using Premagentic.Core.Sources;
using Premagentic.Core.Storage;
using Npgsql;

namespace Premagentic.Tests;

/// <summary>
/// The trust settings, read from the database on every search, and the change
/// record every change lands in. End to end on a real PostgreSQL over the
/// invented bundle in Fixtures/okf-bundle. Every case asserts both directions.
/// Requires a running Docker daemon.
/// </summary>
public sealed class SettingsTests(DatastoreTestDatabase server) : IClassFixture<DatastoreTestDatabase>
{
    private static readonly string BundleRoot = Path.Combine(AppContext.BaseDirectory, "Fixtures", "okf-bundle");

    // Fertilizer prices went stale at the start of March.
    private static readonly DateTimeOffset June = new(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);

    private static readonly AdminActor Tester = new("cli", "test-account");

    private const string Agents = TrustSettingsStore.AgentsMinimumTier;
    private const string People = TrustSettingsStore.PeopleMinimumTier;
    private const string Stale = TrustSettingsStore.Stale;

    // Agent-written and unverified: held back from agents, shown to people.
    private static readonly (string Query, string Path) Pest = ("yellow cards every Monday count caught", "care/pest-scouting.md");

    // Written by a person, and stale in June.
    private static readonly (string Query, string Path) Fertilizer = ("supplier winter price twenty liter drum", "supplies/fertilizer-prices.md");

    private sealed class Env(PremagenticDatabase db, Guid tenant) : IAsyncDisposable
    {
        public PremagenticDatabase Db => db;
        public Guid Tenant => tenant;
        public TrustSettingsStore Settings { get; } = new(db, tenant);
        public ChangeRecord Record { get; } = new(db, tenant);
        public HybridSearch Search { get; } = new(db, new HashEmbeddingProvider());

        /// <summary>Searches the way a host does: the policy resolved from the settings as they are at that moment.</summary>
        public async Task<bool> SeesAsync(CallerKind caller, (string Query, string Path) passage, string? agentMinimum = null)
        {
            var policy = await Settings.ResolveAsync(caller, agentMinimum);
            var hits = (await Search.SearchAsync(Tenant, passage.Query,
                new SearchOptions(AccessScope.PublicOnly, TopK: 10, Trust: policy, AsOf: June))).Hits;
            return hits.Any(h => h.Path == passage.Path);
        }

        public async Task<object?> ScalarAsync(string sql)
        {
            await using var cmd = Db.DataSource.CreateCommand(sql);
            return await cmd.ExecuteScalarAsync();
        }

        public async Task ExecuteAsync(string sql)
        {
            await using var cmd = Db.DataSource.CreateCommand(sql);
            await cmd.ExecuteNonQueryAsync();
        }

        public async Task StoreRawAsync(string key, string json)
        {
            await using var cmd = Db.DataSource.CreateCommand(
                "INSERT INTO prem_config.setting(tenant_id, key, value) VALUES(@tenant, @key, @value::jsonb)");
            cmd.Parameters.AddWithValue("tenant", Tenant);
            cmd.Parameters.AddWithValue("key", key);
            cmd.Parameters.AddWithValue("value", json);
            await cmd.ExecuteNonQueryAsync();
        }

        public ValueTask DisposeAsync() => db.DisposeAsync();
    }

    private async Task<Env> EmptyAsync()
    {
        var db = new PremagenticDatabase(await server.CreateDatabaseAsync());
        await db.InitializeAsync();
        return new Env(db, await db.EnsureTenantAsync("t", "T"));
    }

    private async Task<Env> IngestedAsync()
    {
        var e = await EmptyAsync();
        await new IngestPipeline(e.Db, new HashEmbeddingProvider()).RunAsync(
            e.Tenant, new FileSystemSource(BundleRoot, DocumentAccess.Everyone) { OkfBundle = true });
        return e;
    }

    public static TheoryData<string, CallerKind, string, bool, string, bool, string, bool> Changes => new()
    {
        // key, caller, passage, seen at first, first value, seen after it, second value, seen after it
        { Agents, CallerKind.Agent, "pest", false, "unverified", true, "human-reviewed", false },
        { People, CallerKind.Person, "pest", true, "human-reviewed", false, "unverified", true },
        { Stale, CallerKind.Agent, "fertilizer", false, "shown-to-everyone", true, "shown-to-people-only", false },
        { Stale, CallerKind.Person, "fertilizer", true, "hidden-from-everyone", false, "shown-to-people-only", true },
    };

    [Theory]
    [MemberData(nameof(Changes))]
    public async Task A_setting_change_applies_to_the_next_search_in_both_directions_with_no_reingest(
        string key, CallerKind caller, string passageName, bool atFirst, string first, bool afterFirst, string second, bool afterSecond)
    {
        await using var e = await IngestedAsync();
        var passage = passageName == "pest" ? Pest : Fertilizer;
        var indexedBefore = await e.ScalarAsync("SELECT string_agg(path || updated_at::text, ',' ORDER BY path) FROM prem_index.document");

        Assert.Equal(atFirst, await e.SeesAsync(caller, passage));
        await e.Settings.SetAsync(key, first, Tester);
        Assert.Equal(afterFirst, await e.SeesAsync(caller, passage));
        await e.Settings.SetAsync(key, second, Tester);
        Assert.Equal(afterSecond, await e.SeesAsync(caller, passage));

        // Nothing in the index was written: the policy is applied when the question is asked.
        Assert.Equal(indexedBefore, await e.ScalarAsync("SELECT string_agg(path || updated_at::text, ',' ORDER BY path) FROM prem_index.document"));
    }

    [Fact]
    public async Task A_missing_key_is_its_default()
    {
        await using var e = await EmptyAsync();

        Assert.Equal(TrustSettings.Default, await e.Settings.LoadAsync());
        Assert.All(await e.Settings.ReadAllAsync(), r =>
        {
            Assert.Equal(TrustSettingSource.Default, r.Source);
            Assert.Equal(r.DefaultValue, r.Value);
        });
    }

    [Theory]
    [InlineData("\"nonsense\"")]
    [InlineData("2")]
    [InlineData("null")]
    [InlineData("{\"tier\": \"unverified\"}")]
    public async Task An_unreadable_stored_value_reads_as_the_strictest(string json)
    {
        await using var e = await IngestedAsync();
        // The control: by default a person sees both passages.
        Assert.True(await e.SeesAsync(CallerKind.Person, Pest));
        Assert.True(await e.SeesAsync(CallerKind.Person, Fertilizer));

        foreach (var key in TrustSettingsStore.Keys) await e.StoreRawAsync(key, json);

        Assert.Equal(
            new TrustSettings(OkfTrustTier.HumanReviewed, OkfTrustTier.HumanReviewed, StaleVisibility.HiddenFromEveryone),
            await e.Settings.LoadAsync());
        Assert.False(await e.SeesAsync(CallerKind.Person, Pest));
        Assert.False(await e.SeesAsync(CallerKind.Person, Fertilizer));
        Assert.All(await e.Settings.ReadAllAsync(), r => Assert.Equal(TrustSettingSource.Unreadable, r.Source));
    }

    [Fact]
    public async Task A_looser_agents_setting_never_loosens_an_agent_whose_own_minimum_is_stricter()
    {
        await using var e = await IngestedAsync();
        var identity = new IdentityStore(e.Db, e.Tenant);
        var owner = await identity.CreateUserAsync("owner", "Owner", Role.Member);
        var strict = await identity.CreateAgentAsync("strict-reader", owner.Id, AgentMode.Service, 60, "human-reviewed", ModelLocation.Local);
        var plain = await identity.CreateAgentAsync("plain-reader", owner.Id, AgentMode.Service, 60, null, ModelLocation.Local);

        await e.Settings.SetAsync(Agents, "unverified", Tester);

        // The control: the deployment setting did loosen an agent with no minimum of its own.
        Assert.True(await e.SeesAsync(CallerKind.Agent, Pest, (await identity.FindAgentAsync(plain.Id))!.MinimumTrustTier));
        Assert.False(await e.SeesAsync(CallerKind.Agent, Pest, (await identity.FindAgentAsync(strict.Id))!.MinimumTrustTier));
    }

    [Fact]
    public async Task Each_change_adds_exactly_one_row_to_the_change_record_and_a_repeat_adds_none()
    {
        await using var e = await EmptyAsync();
        var elsewhere = new AdminActor("cli", "another-account");

        Assert.True((await e.Settings.SetAsync(Agents, "unverified", Tester)).Changed);
        Assert.False((await e.Settings.SetAsync(Agents, "Unverified", Tester)).Changed);
        Assert.True((await e.Settings.SetAsync(Stale, "shown to everyone", Tester)).Changed);
        Assert.True((await e.Settings.SetAsync(Agents, "human-reviewed", elsewhere)).Changed);

        var events = await e.Record.ListAsync();
        Assert.Equal(
            [
                ("setting.set", Agents, "\"unverified\"", "\"human-reviewed\"", "another-account"),
                ("setting.set", Stale, "(none)", "\"shown-to-everyone\"", "test-account"),
                ("setting.set", Agents, "(none)", "\"unverified\"", "test-account"),
            ],
            events.Select(ev => (ev.Kind, ev.Target, ev.OldValue?.GetRawText() ?? "(none)", ev.NewValue?.GetRawText() ?? "(none)", ev.Actor.Account!)));
        Assert.All(events, ev => Assert.Equal("cli", ev.Actor.Surface));
        Assert.Equal(events.Select(ev => ev.Id).OrderDescending(), events.Select(ev => ev.Id));
    }

    [Theory]
    [InlineData("UPDATE prem_config.admin_event SET new_value = '\"unverified\"'")]
    [InlineData("DELETE FROM prem_config.admin_event")]
    [InlineData("TRUNCATE prem_config.admin_event")]
    public async Task The_change_record_refuses_every_edit(string sql)
    {
        await using var e = await EmptyAsync();
        await e.Settings.SetAsync(Agents, "machine-confirmed", Tester);

        var ex = await Assert.ThrowsAsync<PostgresException>(() => e.ExecuteAsync(sql));
        Assert.Contains("append only", ex.MessageText);

        var only = Assert.Single(await e.Record.ListAsync());
        Assert.Equal("\"machine-confirmed\"", only.NewValue?.GetRawText());
    }

    [Fact]
    public void No_code_updates_or_deletes_the_change_record()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Premagentic.slnx"))) root = root.Parent;
        Assert.NotNull(root);

        var edit = new Regex(@"(UPDATE|DELETE\s+FROM|TRUNCATE)\s+(TABLE\s+)?prem_config\.admin_event", RegexOptions.IgnoreCase);
        var writers = Directory.EnumerateFiles(Path.Combine(root.FullName, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Split(Path.DirectorySeparatorChar).Any(p => p is "bin" or "obj"))
            .Where(f => File.ReadAllText(f).Contains("prem_config.admin_event", StringComparison.Ordinal))
            .ToArray();

        // The control: the scan does reach the file that writes the record.
        Assert.Contains(writers, f => f.EndsWith("ChangeRecord.cs", StringComparison.Ordinal));
        Assert.All(writers, f => Assert.DoesNotMatch(edit, File.ReadAllText(f)));
    }

    [Theory]
    [InlineData(Agents, "2", "unverified, machine-confirmed, human-reviewed")]
    [InlineData(Agents, "reviewed", "unverified, machine-confirmed, human-reviewed")]
    [InlineData(Stale, "sometimes", "hidden-from-everyone, shown-to-people-only, shown-to-everyone")]
    [InlineData("trust.default_tier", "unverified", "trust.agents_minimum_tier, trust.people_minimum_tier, trust.stale")]
    public async Task An_unknown_key_or_a_value_that_does_not_parse_is_refused_with_what_is_allowed(string key, string value, string allowed)
    {
        await using var e = await EmptyAsync();

        var ex = await Assert.ThrowsAsync<ArgumentException>(() => e.Settings.SetAsync(key, value, Tester));

        Assert.Contains(allowed, ex.Message);
        Assert.Equal(0L, await e.ScalarAsync("SELECT count(*) FROM prem_config.setting"));
        Assert.Empty(await e.Record.ListAsync());
    }

    [Fact]
    public async Task A_change_whose_record_cannot_be_written_is_not_made()
    {
        await using var e = await EmptyAsync();
        // No such user, so the record's foreign key refuses the row.
        var nobody = new AdminActor("portal", null, Guid.NewGuid());

        await Assert.ThrowsAsync<PostgresException>(() => e.Settings.SetAsync(Agents, "unverified", nobody));

        Assert.Equal(TrustSettingSource.Default, (await e.Settings.ReadAsync(Agents)).Source);
        Assert.Empty(await e.Record.ListAsync());

        // The control: the same change by an actor the record accepts is made.
        await e.Settings.SetAsync(Agents, "unverified", Tester);
        Assert.Equal("unverified", (await e.Settings.ReadAsync(Agents)).Value);
    }
}

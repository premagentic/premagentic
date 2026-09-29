using Premagentic.Core.Identity;
using Premagentic.Core.Security;
using Premagentic.Core.Security.Acl;
using Premagentic.Core.Sources;
using Premagentic.Core.Storage;
using Npgsql;

namespace Premagentic.Tests;

/// <summary>
/// Migrates a fresh database to a chosen version and no further, so a
/// migration that backfills rows can be tested against rows written the way
/// the release before it wrote them. The runner is the product's own, given
/// only the embedded migrations up to the version; migrating on is the same
/// runner given all of them, as an upgrade is.
/// </summary>
internal static class MigrateTo
{
    /// <returns>The migrations up to and including <paramref name="version"/>; at least one, or it throws.</returns>
    public static IReadOnlyList<Migration> Upto(int version)
    {
        var upto = Migration.LoadEmbedded().Where(m => m.Version <= version).ToArray();
        if (upto.Length == 0) throw new ArgumentOutOfRangeException(nameof(version), "No migration is at or below that version.");
        return upto;
    }

    public static Task<IReadOnlyList<AppliedMigration>> VersionAsync(NpgsqlDataSource dataSource, int version) =>
        new MigrationRunner(dataSource, Upto(version)).MigrateAsync();

    /// <summary>The newest version recorded in <c>prem_config</c>.</summary>
    public static async Task<int> RecordedAsync(NpgsqlDataSource dataSource)
    {
        await using var cmd = dataSource.CreateCommand("SELECT max(version) FROM prem_config.schema_migration");
        return (int)(await cmd.ExecuteScalarAsync())!;
    }
}

/// <summary>
/// Backfills tested across the upgrade that runs them.
/// Requires a running Docker daemon.
/// </summary>
public sealed class MigrationBackfillTests(DatastoreTestDatabase server) : IClassFixture<DatastoreTestDatabase>
{
    [Fact]
    public async Task Migrating_to_a_version_stops_there_and_migrating_on_applies_the_rest()
    {
        await using var ds = NpgsqlDataSource.Create(await server.CreateDatabaseAsync());

        await MigrateTo.VersionAsync(ds, 67);
        Assert.Equal(MigrateTo.Upto(67)[^1].Version, await MigrateTo.RecordedAsync(ds));
        Assert.True(await MigrateTo.RecordedAsync(ds) < 68);

        await new MigrationRunner(ds).MigrateAsync();
        Assert.Equal(Migration.LoadEmbedded()[^1].Version, await MigrateTo.RecordedAsync(ds));
    }

    [Fact]
    public async Task Upgrading_past_0068_marks_the_connect_page_agents_self_made_and_the_rest_unknown()
    {
        await using var ds = NpgsqlDataSource.Create(await server.CreateDatabaseAsync());
        await MigrateTo.VersionAsync(ds, 67);

        // Rows as the release before 0068 wrote them: the connect page set
        // assistant_kind, and nothing else did.
        Guid tenant, owner, connected, registered;
        await using (var cmd = ds.CreateCommand("INSERT INTO prem_config.tenant(key, name) VALUES('t', 'T') RETURNING id"))
            tenant = (Guid)(await cmd.ExecuteScalarAsync())!;
        await using (var cmd = ds.CreateCommand(
            "INSERT INTO prem_config.app_user(tenant_id, sign_in_name, display_name, role) VALUES(@t, 'ada', 'Ada', 'member') RETURNING id"))
        {
            cmd.Parameters.AddWithValue("t", tenant);
            owner = (Guid)(await cmd.ExecuteScalarAsync())!;
        }
        connected = await InsertAgentAsync(ds, tenant, owner, "ada-desktop", "coding tool");
        registered = await InsertAgentAsync(ds, tenant, owner, "ada-helper", assistantKind: null);

        await new MigrationRunner(ds).MigrateAsync();

        Assert.Equal("self:" + owner.ToString("D"), await CreatedByAsync(ds, connected));
        Assert.Equal("unknown", await CreatedByAsync(ds, registered));
    }

    [Fact]
    public async Task Upgrading_to_0080_ends_a_self_token_whose_owner_was_disabled_before_it()
    {
        var (status, controls) = await ReEnableAcrossTheUpgradeAsync(ownerDisabled: true, agentDisabled: false);
        Assert.Equal(CallerStatus.TokenSuperseded, status);
        Assert.All(controls, s => Assert.Equal(CallerStatus.Resolved, s));
    }

    [Fact]
    public async Task Upgrading_to_0080_ends_a_self_token_whose_agent_was_disabled_before_it()
    {
        var (status, controls) = await ReEnableAcrossTheUpgradeAsync(ownerDisabled: false, agentDisabled: true);
        Assert.Equal(CallerStatus.TokenSuperseded, status);
        Assert.All(controls, s => Assert.Equal(CallerStatus.Resolved, s));
    }

    [Fact]
    public async Task A_self_token_issued_before_the_migration_is_current_after_it()
    {
        var (status, controls) = await ReEnableAcrossTheUpgradeAsync(ownerDisabled: false, agentDisabled: false);
        Assert.Equal(CallerStatus.Resolved, status);
        Assert.All(controls, s => Assert.Equal(CallerStatus.Resolved, s));
    }

    /// <summary>
    /// Writes, as the release before 0080 did, a person with a connect-page
    /// agent and its token, and a service agent of the same person with its
    /// token; disables the person or the agent as asked; upgrades; re-enables
    /// both; and says how each token resolves now. The service agent's token
    /// is the control: a disable before the upgrade leaves it as it was.
    /// </summary>
    private async Task<(CallerStatus SelfToken, CallerStatus[] Controls)> ReEnableAcrossTheUpgradeAsync(bool ownerDisabled, bool agentDisabled)
    {
        var connectionString = await server.CreateDatabaseAsync();
        await using var ds = NpgsqlDataSource.Create(connectionString);
        await MigrateTo.VersionAsync(ds, 79);

        Guid tenant, owner;
        await using (var cmd = ds.CreateCommand("INSERT INTO prem_config.tenant(key, name) VALUES('t', 'T') RETURNING id"))
            tenant = (Guid)(await cmd.ExecuteScalarAsync())!;
        await using (var cmd = ds.CreateCommand(
            "INSERT INTO prem_config.app_user(tenant_id, sign_in_name, display_name, role, disabled) VALUES(@t, 'ada', 'Ada', 'member', @off) RETURNING id"))
        {
            cmd.Parameters.AddWithValue("t", tenant);
            cmd.Parameters.AddWithValue("off", ownerDisabled);
            owner = (Guid)(await cmd.ExecuteScalarAsync())!;
        }
        var self = await InsertAgentWithTokenAsync(ds, tenant, owner, "ada-desk", "self:" + owner.ToString("D"), "acts_for_user", agentDisabled);
        var service = await InsertAgentWithTokenAsync(ds, tenant, owner, "ada-bot", "cli:an-admin", "service", disabled: false);

        await new MigrationRunner(ds).MigrateAsync();

        await using var db = new PremagenticDatabase(connectionString);
        var identity = new IdentityStore(db, tenant);
        await identity.SetUserDisabledAsync(owner, false);
        await identity.SetAgentDisabledAsync(self.Agent, false);
        return (
            (await CallerAccess.ResolveAgentTokenAsync(identity, self.Token)).Resolved.Status,
            [(await CallerAccess.ResolveAgentTokenAsync(identity, service.Token)).Resolved.Status]);
    }

    /// <summary>
    /// Before 0141 the switch was a deny entry for the hosted-model agents
    /// group at the top of the rule of a registered source's folder, and
    /// nowhere else. The upgrade makes a legacy hold of exactly those, and of
    /// no other: not a rule with the denial lower down, not a removed rule,
    /// not a folder no live source reads (a folder beneath one, or a removed
    /// source's), not another connector's rule, not a denial of another
    /// tenant's group. The same entry written by hand on a subfolder stays an
    /// ordinary entry of an ordinary rule, which a rule write removes.
    /// </summary>
    [Fact]
    public async Task Upgrading_to_0141_holds_each_registered_folder_whose_live_rule_opens_with_the_switchs_entry()
    {
        var connectionString = await server.CreateDatabaseAsync();
        await using var ds = NpgsqlDataSource.Create(connectionString);
        await MigrateTo.VersionAsync(ds, 140);

        // Tenants as the release before 0141 made them, each with its group.
        await using var db = new PremagenticDatabase(connectionString);
        var tenant = await db.EnsureTenantAsync("t", "T");
        var other = await db.EnsureTenantAsync("u", "U");
        var denial = SourceExposure.Denial(await ReservedGroupAsync(ds, tenant));
        var othersDenial = SourceExposure.Denial(await ReservedGroupAsync(ds, other));
        var everyone = AclEntry.Allow(Principal.Everyone);
        var somebody = AclEntry.Allow(Principal.User(CallerResolver.IdText(Guid.NewGuid())));

        foreach (var name in new[] { "handbook", "hr", "gone", "borrowed" })
            await InsertSourceAsync(ds, tenant, name);
        await InsertSourceAsync(ds, tenant, "old", removed: true);
        await InsertSourceAsync(ds, other, "yard");

        await InsertRuleAsync(ds, tenant, "handbook", AclSet.Of(denial, everyone));
        await InsertRuleAsync(ds, tenant, "handbook/weekend", AclSet.Of(denial, everyone));
        await InsertRuleAsync(ds, tenant, "hr/archive", AclSet.Of(denial, everyone));
        await InsertRuleAsync(ds, tenant, "hr", AclSet.Of(somebody, denial, everyone));
        await InsertRuleAsync(ds, tenant, "gone", AclSet.Of(denial, everyone), removed: true);
        await InsertRuleAsync(ds, tenant, "borrowed", AclSet.Of(othersDenial, everyone));
        await InsertRuleAsync(ds, tenant, "old", AclSet.Of(denial, everyone));
        await InsertRuleAsync(ds, tenant, "handbook", AclSet.Of(denial, everyone), source: "test-datastore");
        await InsertRuleAsync(ds, other, "yard", AclSet.Of(othersDenial, everyone));

        await new MigrationRunner(ds).MigrateAsync();

        Assert.Equal([new HostedHold("filesystem", "handbook")], await HostedHolds.ListAsync(db, tenant));
        Assert.Equal([new HostedHold("filesystem", "yard")], await HostedHolds.ListAsync(db, other));
        Assert.Equal([new HostedHold("filesystem", "handbook")], await HostedHolds.LegacyAsync(db, tenant));

        // The hand-written entry on a subfolder of a folder nobody held: not
        // held, read as a denial by hand, and gone with the next write of its
        // rule.
        var exposure = new SourceExposureStore(db, tenant);
        Assert.Equal(SourceExposureState.DeniedByHand, await exposure.ReadAsync("hr/archive"));
        var rules = new AclStore(db, tenant);
        await rules.SetRuleAsync(new FolderRule("filesystem", "hr/archive", AclSet.Of(everyone)));
        Assert.Equal(["allow everyone"], (await rules.ListRulesAsync())
            .Single(r => r.Rule.Source == "filesystem" && r.Rule.PathPrefix == "hr/archive").Rule.Acl.Entries.Select(e => e.ToString()));
        Assert.Equal(SourceExposureState.MayBeServed, await exposure.ReadAsync("hr/archive"));

        // Releasing the legacy hold takes the switch's entry out of its own
        // rule only.
        await exposure.SetAsync("handbook", mayBeServed: true, new Core.Admin.AdminActor("cli", "tester"));
        Assert.Equal(["allow everyone"], (await rules.ListRulesAsync())
            .Single(r => r.Rule.Source == "filesystem" && r.Rule.PathPrefix == "handbook").Rule.Acl.Entries.Select(e => e.ToString()));
        Assert.Equal(denial, (await rules.ListRulesAsync())
            .Single(r => r.Rule.Source == "filesystem" && r.Rule.PathPrefix == "hr").Rule.Acl.Entries[1]);
    }

    private static async Task<Guid> ReservedGroupAsync(NpgsqlDataSource ds, Guid tenant)
    {
        await using var cmd = ds.CreateCommand(
            "SELECT id FROM prem_config.app_group WHERE tenant_id = @t AND system_key = 'hosted_model_agents'");
        cmd.Parameters.AddWithValue("t", tenant);
        return (Guid)(await cmd.ExecuteScalarAsync())!;
    }

    /// <summary>A registered source as the release before 0141 wrote one, its prefix its name.</summary>
    private static async Task InsertSourceAsync(NpgsqlDataSource ds, Guid tenant, string name, bool removed = false)
    {
        await using var cmd = ds.CreateCommand("""
            INSERT INTO prem_config.source(tenant_id, name, folder, path_prefix, deleted_at)
            VALUES(@t, @name, @folder, @name, CASE WHEN @removed THEN now() END)
            """);
        cmd.Parameters.AddWithValue("t", tenant);
        cmd.Parameters.AddWithValue("name", name);
        cmd.Parameters.AddWithValue("folder", Path.Combine(Path.GetTempPath(), "premagentic-backfill-" + name));
        cmd.Parameters.AddWithValue("removed", removed);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>A folder rule written the way the release before 0141 wrote one: its list, then the rule.</summary>
    private static async Task InsertRuleAsync(
        NpgsqlDataSource ds, Guid tenant, string prefix, AclSet set, bool removed = false, string source = "filesystem")
    {
        await using var cmd = ds.CreateCommand("""
            WITH s AS (
                INSERT INTO prem_config.acl_set(tenant_id, sha256, canonical_text) VALUES(@t, @sha, @text)
                ON CONFLICT (tenant_id, sha256) DO UPDATE SET canonical_text = EXCLUDED.canonical_text
                RETURNING id)
            INSERT INTO prem_config.folder_rule(tenant_id, source, path_prefix, acl_set_id, deleted_at)
            SELECT @t, @source, @prefix, s.id, CASE WHEN @removed THEN now() END FROM s
            """);
        cmd.Parameters.AddWithValue("t", tenant);
        cmd.Parameters.AddWithValue("sha", set.Hash);
        cmd.Parameters.AddWithValue("text", set.CanonicalText);
        cmd.Parameters.AddWithValue("source", source);
        cmd.Parameters.AddWithValue("prefix", prefix);
        cmd.Parameters.AddWithValue("removed", removed);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<(Guid Agent, string Token)> InsertAgentWithTokenAsync(
        NpgsqlDataSource ds, Guid tenant, Guid owner, string name, string createdBy, string mode, bool disabled)
    {
        Guid agent;
        await using (var cmd = ds.CreateCommand("""
            INSERT INTO prem_config.agent(tenant_id, name, owner_user_id, mode, requests_per_minute, model_location, created_by, disabled)
            VALUES(@t, @name, @owner, @mode, 60, 'local', @by, @off) RETURNING id
            """))
        {
            cmd.Parameters.AddWithValue("t", tenant);
            cmd.Parameters.AddWithValue("name", name);
            cmd.Parameters.AddWithValue("owner", owner);
            cmd.Parameters.AddWithValue("mode", mode);
            cmd.Parameters.AddWithValue("by", createdBy);
            cmd.Parameters.AddWithValue("off", disabled);
            agent = (Guid)(await cmd.ExecuteScalarAsync())!;
        }

        var now = DateTimeOffset.UtcNow;
        var issued = AgentTokens.Issue(agent, now, now.AddDays(1));
        await using (var cmd = ds.CreateCommand("""
            INSERT INTO prem_config.agent_token(id, tenant_id, agent_id, secret_sha256, created_at, expires_at)
            VALUES(@id, @t, @agent, @hash, @created, @expires)
            """))
        {
            cmd.Parameters.AddWithValue("id", issued.Record.Id);
            cmd.Parameters.AddWithValue("t", tenant);
            cmd.Parameters.AddWithValue("agent", agent);
            cmd.Parameters.AddWithValue("hash", issued.Record.SecretHash);
            cmd.Parameters.AddWithValue("created", issued.Record.CreatedAt);
            cmd.Parameters.AddWithValue("expires", issued.Record.ExpiresAt);
            await cmd.ExecuteNonQueryAsync();
        }
        return (agent, issued.PlainText);
    }

    private static async Task<Guid> InsertAgentAsync(NpgsqlDataSource ds, Guid tenant, Guid owner, string name, string? assistantKind)
    {
        await using var cmd = ds.CreateCommand("""
            INSERT INTO prem_config.agent(tenant_id, name, owner_user_id, mode, requests_per_minute, model_location, assistant_kind)
            VALUES(@t, @name, @owner, 'acts_for_user', 60, 'local', @kind) RETURNING id
            """);
        cmd.Parameters.AddWithValue("t", tenant);
        cmd.Parameters.AddWithValue("name", name);
        cmd.Parameters.AddWithValue("owner", owner);
        cmd.Parameters.AddWithValue("kind", (object?)assistantKind ?? DBNull.Value);
        return (Guid)(await cmd.ExecuteScalarAsync())!;
    }

    private static async Task<string> CreatedByAsync(NpgsqlDataSource ds, Guid agent)
    {
        await using var cmd = ds.CreateCommand("SELECT created_by FROM prem_config.agent WHERE id = @id");
        cmd.Parameters.AddWithValue("id", agent);
        return (string)(await cmd.ExecuteScalarAsync())!;
    }
}

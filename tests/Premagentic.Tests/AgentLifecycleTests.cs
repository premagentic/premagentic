using System.Text.Json;
using Premagentic.Core.Admin;
using Premagentic.Core.Audit;
using Premagentic.Core.Identity;
using Premagentic.Core.Storage;

namespace Premagentic.Tests;

/// <summary>
/// Reissuing an assistant's key and removing an assistant, through
/// <see cref="AgentLifecycle"/> as every surface calls it: a reissue replaces a
/// live key in one change and one row and never brings a key back; a removal
/// ends an agent for good and keeps every row that names it. Requires a
/// running Docker daemon.
/// </summary>
public sealed class AgentLifecycleTests(DatastoreTestDatabase server) : IClassFixture<DatastoreTestDatabase>
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-09-01T09:00:00Z", System.Globalization.CultureInfo.InvariantCulture);

    private sealed record World(PremagenticDatabase Db, Guid Tenant, TestClock Clock, IdentityStore Store, User Alice, User Bob, Agent Svc)
        : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Db.DisposeAsync();

        public AdminChanges Changes => new(Db, Tenant, Clock);
    }

    private async Task<World> NewAsync()
    {
        var db = new PremagenticDatabase(await server.CreateDatabaseAsync());
        await db.InitializeAsync();
        var tenant = await db.EnsureTenantAsync("t", "T");
        var clock = new TestClock(Start);
        var store = new IdentityStore(db, tenant, clock);
        var alice = await store.CreateUserAsync("alice", "Alice", Role.Member);
        var bob = await store.CreateUserAsync("bob", "Bob", Role.Member);
        var svc = await store.CreateAgentAsync("svc", alice.Id, AgentMode.Service, 60, null, ModelLocation.Local);
        return new World(db, tenant, clock, store, alice, bob, svc);
    }

    private static Task<ReissuedAgentToken> ReissueAsync(World w, string tokenId, TimeSpan? lifetime = null) =>
        w.Changes.RunAsync(AdminActor.Cli(), change => AgentLifecycle.ReissueAsync(change, tokenId, lifetime));

    private static Task<AgentRemoval> RemoveAsync(World w, Guid agentId) =>
        w.Changes.RunAsync(AdminActor.Cli(), change => AgentLifecycle.RemoveAsync(change, agentId));

    private static async Task<long> CountAsync(World w, string sql)
    {
        await using var cmd = w.Db.DataSource.CreateCommand(sql);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    private static async Task<List<(string Kind, string Target, string? Old, string? New)>> RecordAsync(World w)
    {
        await using var cmd = w.Db.DataSource.CreateCommand(
            "SELECT kind, target, old_value::text, new_value::text FROM prem_config.admin_event ORDER BY id");
        var rows = new List<(string, string, string?, string?)>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            rows.Add((reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3)));
        return rows;
    }

    private static Task<ResolvedCaller> ResolveAsync(World w, string token) =>
        new CallerResolver(w.Store, w.Clock).ResolveAsync(CallerIdentity.AgentToken(token));

    [Fact]
    public async Task A_reissue_replaces_a_live_token_with_a_new_one_in_one_change_and_one_row()
    {
        await using var w = await NewAsync();
        var old = await w.Store.IssueTokenAsync(w.Svc.Id, TimeSpan.FromDays(30));
        w.Clock.Now = Start.AddDays(10);

        var reissued = await ReissueAsync(w, old.Record.Id);

        Assert.Equal(CallerStatus.Resolved, (await ResolveAsync(w, reissued.New.PlainText)).Status);
        Assert.Equal(CallerStatus.TokenRevoked, (await ResolveAsync(w, old.PlainText)).Status);
        Assert.Equal(old.Record.Id, reissued.ReplacedTokenId);
        Assert.Equal("svc", reissued.AgentName);
        // The replaced token's own lifetime, counted from now.
        Assert.Equal(Start.AddDays(40), reissued.New.Record.ExpiresAt);

        var row = Assert.Single(await RecordAsync(w));
        Assert.Equal((AgentLifecycle.ReissueKind, "svc"), (row.Kind, row.Target));
        Assert.Equal(old.Record.Id, JsonDocument.Parse(row.Old!).RootElement.GetProperty("token_id").GetString());
        Assert.Equal(reissued.New.Record.Id, JsonDocument.Parse(row.New!).RootElement.GetProperty("token_id").GetString());
        Assert.DoesNotContain(reissued.New.PlainText, row.New!, StringComparison.Ordinal);
        Assert.DoesNotContain(reissued.New.PlainText, reissued.ToString(), StringComparison.Ordinal);

        // A lifetime given overrides the replaced one's.
        var again = await ReissueAsync(w, reissued.New.Record.Id, TimeSpan.FromDays(5));
        Assert.Equal(Start.AddDays(15), again.New.Record.ExpiresAt);
    }

    [Fact]
    public async Task A_reissue_that_is_refused_writes_nothing_and_says_why()
    {
        await using var w = await NewAsync();

        async Task RefusedAsync(string tokenId, string sentence)
        {
            var tokens = await CountAsync(w, "SELECT count(*) FROM prem_config.agent_token");
            var rows = await CountAsync(w, "SELECT count(*) FROM prem_config.admin_event");
            var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => ReissueAsync(w, tokenId));
            Assert.Equal(sentence, refused.Message);
            Assert.Equal(tokens, await CountAsync(w, "SELECT count(*) FROM prem_config.agent_token"));
            Assert.Equal(rows, await CountAsync(w, "SELECT count(*) FROM prem_config.admin_event"));
        }

        await RefusedAsync("0123456789abcdef01234567", AgentLifecycle.NoSuchToken);

        // A disabled agent, and one whose person is disabled.
        var disabledAgent = await w.Store.CreateAgentAsync("off", w.Alice.Id, AgentMode.Service, 60, null, ModelLocation.Local);
        var offToken = await w.Store.IssueTokenAsync(disabledAgent.Id, TimeSpan.FromDays(30));
        await w.Store.SetAgentDisabledAsync(disabledAgent.Id, true);
        await RefusedAsync(offToken.Record.Id, AgentLifecycle.AgentDisabled("off"));

        var bobs = await w.Store.CreateAgentAsync("bobs", w.Bob.Id, AgentMode.Service, 60, null, ModelLocation.Local);
        var bobsToken = await w.Store.IssueTokenAsync(bobs.Id, TimeSpan.FromDays(30));
        await w.Store.SetUserDisabledAsync(w.Bob.Id, true);
        await RefusedAsync(bobsToken.Record.Id, AgentLifecycle.OwnerDisabled("bobs"));

        // A revoked token, and an expired one.
        var revoked = await w.Store.IssueTokenAsync(w.Svc.Id, TimeSpan.FromDays(30));
        await w.Store.RevokeTokenAsync(revoked.Record.Id);
        await RefusedAsync(revoked.Record.Id, AgentLifecycle.TokenRevoked(revoked.Record.Id));

        var shortLived = await w.Store.IssueTokenAsync(w.Svc.Id, TimeSpan.FromDays(1));
        w.Clock.Now = Start.AddDays(2);
        await RefusedAsync(shortLived.Record.Id, AgentLifecycle.TokenExpired(shortLived.Record.Id));

        // A person's own assistant disabled and enabled again: its token ended
        // for good, and a reissue does not bring it back.
        var own = await new SelfServeAgents(w.Db, w.Tenant, null, w.Clock).CreateAsync(
            w.Alice.Id, new SelfServeAgentRequest("alice-laptop", "local", null, "coding tool"), default);
        var ownTokenId = (await w.Store.ListTokensAsync(own.AgentId)).Single().Id;
        await w.Store.SetAgentDisabledAsync(own.AgentId, true);
        await w.Store.SetAgentDisabledAsync(own.AgentId, false);
        await RefusedAsync(ownTokenId, AgentLifecycle.TokenSuperseded(ownTokenId));

        // An assistant made through the authorization flow holds no token of
        // its own; one written there by hand is refused all the same.
        var flow = await w.Store.CreateAgentAsync("oauth-desk", w.Alice.Id, AgentMode.ActsForUser, 60, null,
            ModelLocation.Local, origin: AgentOrigin.OAuth("prem_cli_0123456789abcdef01234567"));
        await using (var insert = w.Db.DataSource.CreateCommand("""
            INSERT INTO prem_config.agent_token(id, tenant_id, agent_id, secret_sha256, created_at, expires_at)
            VALUES ('abcdefabcdefabcdefabcdef', @tenant, @agent, @hash, @created, @expires)
            """))
        {
            insert.Parameters.AddWithValue("tenant", w.Tenant);
            insert.Parameters.AddWithValue("agent", flow.Id);
            insert.Parameters.AddWithValue("hash", new byte[32]);
            insert.Parameters.AddWithValue("created", w.Clock.Now);
            insert.Parameters.AddWithValue("expires", w.Clock.Now.AddDays(30));
            await insert.ExecuteNonQueryAsync();
        }
        await RefusedAsync("abcdefabcdefabcdefabcdef", AgentLifecycle.GrantHeld);

        // A removed agent's token is no token at all.
        var gone = await w.Store.IssueTokenAsync(w.Svc.Id, TimeSpan.FromDays(30));
        await RemoveAsync(w, w.Svc.Id);
        await RefusedAsync(gone.Record.Id, AgentLifecycle.NoSuchToken);
    }

    [Fact]
    public async Task A_reissue_that_cannot_revoke_the_old_token_writes_nothing()
    {
        await using var w = await NewAsync();
        var old = await w.Store.IssueTokenAsync(w.Svc.Id, TimeSpan.FromDays(30));
        // A trigger that skips the revoke of this one token, standing in for a
        // revoke that finds nothing to revoke once the new token is issued.
        await using (var trigger = w.Db.DataSource.CreateCommand($"""
            CREATE FUNCTION prem_config.test_skip_revoke() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                IF NEW.id = '{old.Record.Id}' AND NEW.revoked_at IS NOT NULL THEN RETURN NULL; END IF;
                RETURN NEW;
            END $$;
            CREATE TRIGGER test_skip_revoke BEFORE UPDATE ON prem_config.agent_token
                FOR EACH ROW EXECUTE FUNCTION prem_config.test_skip_revoke();
            """))
            await trigger.ExecuteNonQueryAsync();

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => ReissueAsync(w, old.Record.Id));

        Assert.Equal(AgentLifecycle.TokenRevoked(old.Record.Id), refused.Message);
        Assert.Equal(1, await CountAsync(w, "SELECT count(*) FROM prem_config.agent_token"));
        Assert.Empty(await RecordAsync(w));
        Assert.Equal(CallerStatus.Resolved, (await ResolveAsync(w, old.PlainText)).Status);
    }

    [Fact]
    public async Task A_removal_ends_an_agent_for_good_and_keeps_every_row_that_names_it()
    {
        await using var w = await NewAsync();
        var token = await w.Store.IssueTokenAsync(w.Svc.Id, TimeSpan.FromDays(30));
        await w.Store.SetAgentDisabledAsync(w.Svc.Id, true);
        await using (var ask = w.Db.DataSource.CreateCommand("""
            INSERT INTO prem_config.retrieval_event(
                tenant_id, kind, query, caller_user_id, caller_agent_id, include_historical, passages, elapsed_ms, model_location, created_at)
            VALUES(@tenant, 'search', 'where is the handbook', NULL, @agent, false, '[]'::jsonb, 1, 'local', @at)
            """))
        {
            ask.Parameters.AddWithValue("tenant", w.Tenant);
            ask.Parameters.AddWithValue("agent", w.Svc.Id);
            ask.Parameters.AddWithValue("at", Start);
            await ask.ExecuteNonQueryAsync();
        }
        var generationBefore = await CountAsync(w, $"SELECT credential_generation::bigint FROM prem_config.agent WHERE id = '{w.Svc.Id}'");

        var removal = await RemoveAsync(w, w.Svc.Id);

        Assert.Equal(("svc", true), (removal.Name, removal.WasDisabled));
        Assert.Equal([token.Record.Id], removal.RevokedTokenIds);
        Assert.Equal(CallerStatus.TokenRevoked, (await ResolveAsync(w, token.PlainText)).Status);
        Assert.Equal(generationBefore + 1,
            await CountAsync(w, $"SELECT credential_generation::bigint FROM prem_config.agent WHERE id = '{w.Svc.Id}'"));

        // Gone from every list and every lookup, and nothing brings it back.
        Assert.Null(await w.Store.FindAgentAsync(w.Svc.Id));
        Assert.Null(await w.Store.FindAgentByNameAsync("svc"));
        Assert.DoesNotContain(await w.Store.ListAgentsAsync(), a => a.Id == w.Svc.Id);
        Assert.False(await w.Store.SetAgentDisabledAsync(w.Svc.Id, false));
        await Assert.ThrowsAsync<InvalidOperationException>(() => w.Store.IssueTokenAsync(w.Svc.Id, TimeSpan.FromDays(1)));
        var again = await Assert.ThrowsAsync<InvalidOperationException>(() => RemoveAsync(w, w.Svc.Id));
        Assert.Equal(AgentLifecycle.NoSuchAgent, again.Message);

        // Listed as removed, with its person, when and by whom.
        var listed = Assert.Single(await w.Store.ListRemovedAgentsAsync());
        Assert.Equal((w.Svc.Id, "svc", w.Alice.Id, "alice", Start, AdminActor.Cli().Describe()),
            (listed.Id, listed.Name, listed.OwnerUserId, listed.OwnerSignInName, listed.RemovedAt, listed.RemovedBy));

        // One row, and the history still names it.
        var row = Assert.Single(await RecordAsync(w));
        Assert.Equal((AgentLifecycle.RemoveKind, "svc"), (row.Kind, row.Target));
        Assert.Contains(token.Record.Id, row.New!, StringComparison.Ordinal);
        var asked = Assert.Single(await new AuditTrail(w.Db, w.Tenant).PageAsync(10));
        Assert.Equal(("svc", true), (asked.AgentName, asked.AgentRemoved));
        var used = Assert.Single(await new UsageQueries(w.Db, w.Tenant).ByCallerAsync(
            new UsageWindow(Start.AddDays(-1), Start.AddDays(1)), hostedOnly: false, default), u => u.Kind == "agent");
        Assert.Equal(("svc", true), (used.Name, used.Removed));

        // Its name is free for a new agent, which is another agent.
        var next = await w.Store.CreateAgentAsync("svc", w.Alice.Id, AgentMode.Service, 60, null, ModelLocation.Local);
        Assert.NotEqual(w.Svc.Id, next.Id);
        var nowAsked = Assert.Single(await new AuditTrail(w.Db, w.Tenant).PageAsync(10));
        Assert.True(nowAsked.AgentRemoved);
    }

    [Fact]
    public async Task A_removal_of_a_persons_own_assistant_by_an_administrator_ends_it_for_good()
    {
        await using var w = await NewAsync();
        var agents = new SelfServeAgents(w.Db, w.Tenant, null, w.Clock);
        var own = await agents.CreateAsync(w.Alice.Id, new SelfServeAgentRequest("alice-laptop", "local", null, "coding tool"), default);
        Assert.Equal(1, await agents.ConnectedCountAsync(w.Alice.Id, default));

        await RemoveAsync(w, own.AgentId);

        Assert.Equal(CallerStatus.TokenRevoked, (await ResolveAsync(w, own.Token)).Status);
        Assert.Empty(await agents.ListAsync(w.Alice.Id, default));
        Assert.Equal(0, await agents.ConnectedCountAsync(w.Alice.Id, default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => w.Store.IssueTokenAsync(own.AgentId, TimeSpan.FromDays(1)));
    }
}

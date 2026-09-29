using System.Net;
using System.Text.Json;
using Premagentic.Cli.Admin;
using Premagentic.Core.Admin;
using Premagentic.Core.Evaluation;
using Premagentic.Core.Identity;
using Premagentic.Core.Storage;

namespace Premagentic.Tests;

/// <summary>
/// A disable or a password change ends a person's own assistant credentials
/// for good: a connect-page agent's token issued before it is refused after a
/// re-enable (the owner's decisions of 2026-09-25). Agents an administrator
/// made are left as they were. Requires a running Docker daemon.
/// </summary>
public sealed class CredentialGenerationTests(DatastoreTestDatabase server) : IClassFixture<DatastoreTestDatabase>
{
    internal sealed record World(PremagenticDatabase Db, Guid Tenant, IdentityStore Identity, User Alice, User Carol)
        : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }

    internal static async Task<World> NewAsync(DatastoreTestDatabase server, int bound = 10)
    {
        var db = new PremagenticDatabase(await server.CreateDatabaseAsync());
        await db.InitializeAsync();
        var tenant = await db.EnsureTenantAsync("t", "T");
        var identity = new IdentityStore(db, tenant);
        var alice = await identity.CreateUserAsync("alice", "Alice", Role.Member);
        var carol = await identity.CreateUserAsync("carol", "Carol", Role.Administrator);
        await new TuningSettingsStore(db, tenant).SetAsync(
            AgentSettings.SelfServiceMax, JsonSerializer.SerializeToElement(bound), AdminActor.Cli());
        return new World(db, tenant, identity, alice, carol);
    }

    internal static Task<CreatedAgent> ConnectAsync(World w, string name, TimeProvider? time = null) =>
        new SelfServeAgents(w.Db, w.Tenant, oauth: null, time).CreateAsync(
            w.Alice.Id, new SelfServeAgentRequest(name, "local", null, "coding tool"), default);

    private static async Task<CallerStatus> StatusAsync(World w, string token) =>
        (await CallerAccess.ResolveAgentTokenAsync(w.Identity, token)).Resolved.Status;

    [Fact]
    public async Task A_self_serve_token_ends_when_its_owner_is_disabled_and_stays_ended_after_re_enable()
    {
        await using var w = await NewAsync(server);
        var desk = await ConnectAsync(w, "desk");
        Assert.Equal(CallerStatus.Resolved, await StatusAsync(w, desk.Token));

        await w.Identity.SetUserDisabledAsync(w.Alice.Id, true);
        // The owner is judged before the token's stamps, so the refusal names the real cause.
        Assert.Equal(CallerStatus.OwnerDisabled, await StatusAsync(w, desk.Token));

        await w.Identity.SetUserDisabledAsync(w.Alice.Id, false);
        Assert.Equal(CallerStatus.TokenSuperseded, await StatusAsync(w, desk.Token));
        Assert.True((await w.Identity.FindTokenAsync(TokenId(desk.Token)))!.Superseded);
    }

    [Fact]
    public async Task A_self_serve_token_ends_when_its_agent_is_disabled_and_stays_ended_after_re_enable()
    {
        await using var w = await NewAsync(server);
        var desk = await ConnectAsync(w, "desk");

        Assert.True(await w.Identity.SetAgentDisabledAsync(desk.AgentId, true));
        Assert.Equal(CallerStatus.AgentDisabled, await StatusAsync(w, desk.Token));

        Assert.True(await w.Identity.SetAgentDisabledAsync(desk.AgentId, false));
        Assert.Equal(CallerStatus.TokenSuperseded, await StatusAsync(w, desk.Token));
    }

    [Fact]
    public async Task A_self_serve_token_ends_at_a_password_change()
    {
        await using var w = await NewAsync(server);
        var desk = await ConnectAsync(w, "desk");

        await w.Identity.SetPasswordHashAsync(w.Alice.Id, new PasswordHasher().Hash("alice picks a new long password"));

        Assert.Equal(CallerStatus.TokenSuperseded, await StatusAsync(w, desk.Token));
    }

    [Fact]
    public async Task A_token_issued_after_the_re_enable_works()
    {
        await using var w = await NewAsync(server);
        var desk = await ConnectAsync(w, "desk");
        await w.Identity.SetUserDisabledAsync(w.Alice.Id, true);
        await w.Identity.SetUserDisabledAsync(w.Alice.Id, false);

        // An administrator issues the same agent a new token after the re-enable.
        var fresh = await w.Identity.IssueTokenAsync(desk.AgentId, TimeSpan.FromDays(1));

        Assert.Equal(CallerStatus.TokenSuperseded, await StatusAsync(w, desk.Token));
        Assert.Equal(CallerStatus.Resolved, await StatusAsync(w, fresh.PlainText));
        Assert.False((await w.Identity.FindTokenAsync(fresh.Record.Id))!.Superseded);
    }

    [Fact]
    public async Task Service_and_administrator_made_agent_tokens_survive_a_re_enable_and_a_password_change()
    {
        await using var w = await NewAsync(server);
        var service = await w.Identity.CreateAgentAsync(
            "report-bot", w.Alice.Id, AgentMode.Service, 60, null, ModelLocation.Local, origin: AgentOrigin.Cli("an-admin"));
        var helper = await w.Identity.CreateAgentAsync(
            "helper", w.Alice.Id, AgentMode.ActsForUser, 60, null, ModelLocation.Local, origin: AgentOrigin.Portal(w.Carol.Id));
        var legacy = await w.Identity.CreateAgentAsync(
            "legacy", w.Alice.Id, AgentMode.ActsForUser, 60, null, ModelLocation.Local);
        var tokens = new List<string>();
        foreach (var agent in new[] { service, helper, legacy })
            tokens.Add((await w.Identity.IssueTokenAsync(agent.Id, TimeSpan.FromDays(1))).PlainText);
        var desk = await ConnectAsync(w, "desk");

        await w.Identity.SetUserDisabledAsync(w.Alice.Id, true);
        await w.Identity.SetUserDisabledAsync(w.Alice.Id, false);
        await w.Identity.SetPasswordHashAsync(w.Alice.Id, new PasswordHasher().Hash("alice picks a new long password"));
        foreach (var agent in new[] { service, helper, legacy })
        {
            await w.Identity.SetAgentDisabledAsync(agent.Id, true);
            await w.Identity.SetAgentDisabledAsync(agent.Id, false);
        }

        foreach (var token in tokens)
            Assert.Equal(CallerStatus.Resolved, await StatusAsync(w, token));
        // The control: the connect-page token in the same world did end.
        Assert.Equal(CallerStatus.TokenSuperseded, await StatusAsync(w, desk.Token));
    }

    [Fact]
    public async Task A_sign_in_rehash_leaves_assistant_credentials_working()
    {
        await using var w = await NewAsync(server);
        var hasher = new PasswordHasher();
        var verified = hasher.Hash("alice keeps the same password");
        await w.Identity.SetPasswordHashAsync(w.Alice.Id, verified);
        var desk = await ConnectAsync(w, "desk");

        Assert.True(await w.Identity.RehashPasswordAsync(w.Alice.Id, verified, hasher.Hash("alice keeps the same password")));

        Assert.Equal(CallerStatus.Resolved, await StatusAsync(w, desk.Token));
    }

    [Fact]
    public async Task An_ended_self_serve_agent_frees_its_slot()
    {
        await using var w = await NewAsync(server, bound: 2);
        await ConnectAsync(w, "desk");
        await ConnectAsync(w, "laptop");
        var full = await Assert.ThrowsAsync<InvalidOperationException>(() => ConnectAsync(w, "phone"));
        Assert.Contains("You have 2 connected assistants", full.Message);

        await w.Identity.SetUserDisabledAsync(w.Alice.Id, true);
        await w.Identity.SetUserDisabledAsync(w.Alice.Id, false);

        Assert.Equal(0, await new SelfServeAgents(w.Db, w.Tenant).ConnectedCountAsync(w.Alice.Id, default));
        await ConnectAsync(w, "phone");
        Assert.Equal(1, await new SelfServeAgents(w.Db, w.Tenant).ConnectedCountAsync(w.Alice.Id, default));
    }

    [Fact]
    public async Task Every_own_agent_shows_its_state_and_only_a_live_one_holds_a_slot()
    {
        await using var w = await NewAsync(server);
        var clock = new TestClock(DateTimeOffset.UtcNow);
        await ConnectAsync(w, "old", clock);
        clock.Now += SelfServeAgents.TokenLifetime + TimeSpan.FromDays(1);
        await ConnectAsync(w, "live", clock);
        var gone = await ConnectAsync(w, "gone", clock);
        var ended = await ConnectAsync(w, "ended", clock);
        var agents = new SelfServeAgents(w.Db, w.Tenant, oauth: null, clock);
        await agents.RevokeAsync(w.Alice.Id, gone.AgentId, default);
        await w.Identity.SetAgentDisabledAsync(ended.AgentId, true);
        await w.Identity.SetAgentDisabledAsync(ended.AgentId, false);

        var states = (await agents.ListAsync(w.Alice.Id, default)).ToDictionary(a => a.Name, a => a.State);

        Assert.Equal(OwnAgentState.Expired, states["old"]);
        Assert.Equal(OwnAgentState.Live, states["live"]);
        Assert.Equal(OwnAgentState.Revoked, states["gone"]);
        Assert.Equal(OwnAgentState.Ended, states["ended"]);
        Assert.Equal(states.Count(s => s.Value == OwnAgentState.Live), await agents.ConnectedCountAsync(w.Alice.Id, default));
    }

    [Fact]
    public async Task An_oauth_agent_is_issued_no_token_by_the_store()
    {
        await using var w = await NewAsync(server);
        var agent = await w.Identity.CreateAgentAsync(
            "oauth-0123456789abcdef01234567", w.Alice.Id, AgentMode.ActsForUser, 60, null, ModelLocation.Hosted,
            origin: AgentOrigin.OAuth("prem_cli_0123456789abcdef01234567"));

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => w.Identity.IssueTokenAsync(agent.Id, TimeSpan.FromDays(1)));

        Assert.Equal(IdentityStore.OAuthAgentTokenRefusal, refused.Message);
        Assert.Empty(await w.Identity.ListTokensAsync(agent.Id));
        Assert.Equal(AgentOrigin.OAuthKind, (await w.Identity.FindAgentAsync(agent.Id))!.Origin!.Kind);
    }

    [Fact]
    public async Task An_oauth_agent_is_issued_no_token_from_the_portal()
    {
        await using var portal = await PortalWorld.NewAsync(server);
        var identity = portal.World.Identity;
        await identity.CreateAgentAsync(
            "oauth-0123456789abcdef01234567", portal.World.Alice.Id, AgentMode.ActsForUser, 60, null, ModelLocation.Hosted,
            origin: AgentOrigin.OAuth("prem_cli_0123456789abcdef01234567"));

        using var response = await portal.PostAsync("/portal/agents/oauth-0123456789abcdef01234567/tokens", portal.Admin, [("days", "7")]);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains(IdentityStore.OAuthAgentTokenRefusal, Uri.UnescapeDataString(response.Headers.Location!.OriginalString));
        Assert.Equal(0, await portal.ScalarAsync(
            "SELECT count(*) FROM prem_config.agent_token t JOIN prem_config.agent a ON a.id = t.agent_id WHERE a.created_by LIKE 'oauth:%'"));
    }

    [Fact]
    public void A_superseded_token_is_not_usable_before_its_expiry()
    {
        var now = DateTimeOffset.UtcNow;
        var record = AgentTokens.Issue(Guid.NewGuid(), now, now.AddDays(1)).Record;

        Assert.True(record.IsUsableAt(now));
        Assert.False((record with { Superseded = true }).IsUsableAt(now));
    }

    internal static string TokenId(string token) =>
        AgentTokens.TryParse(token, out var parsed) ? parsed.TokenId : throw new ArgumentException("Not an agent token.");
}

/// <summary>The command line's token verbs and the credential generations. Requires a running Docker daemon.</summary>
[Collection(ConsoleCollection.Name)]
public sealed class CredentialGenerationCliTests(DatastoreTestDatabase server) : IClassFixture<DatastoreTestDatabase>
{
    [Fact]
    public async Task Tokens_issue_refuses_an_oauth_agent()
    {
        await using var w = await CredentialGenerationTests.NewAsync(server);
        await w.Identity.CreateAgentAsync(
            "oauth-0123456789abcdef01234567", w.Alice.Id, AgentMode.ActsForUser, 60, null, ModelLocation.Hosted,
            origin: AgentOrigin.OAuth("prem_cli_0123456789abcdef01234567"));

        var run = await ConsoleCapture.RunAsync(() =>
            AdminCommands.RunAsync(["tokens", "issue", "oauth-0123456789abcdef01234567"], w.Db, w.Tenant));

        Assert.Equal(1, run.Exit);
        Assert.Contains(IdentityStore.OAuthAgentTokenRefusal, run.Err);
        Assert.DoesNotContain("prem_agt_", run.Out);
    }

    [Fact]
    public async Task A_superseded_token_is_listed_as_ended()
    {
        await using var w = await CredentialGenerationTests.NewAsync(server);
        var desk = await CredentialGenerationTests.ConnectAsync(w, "desk");
        await w.Identity.SetPasswordHashAsync(w.Alice.Id, new PasswordHasher().Hash("alice picks a new long password"));

        var run = await ConsoleCapture.RunAsync(() => AdminCommands.RunAsync(["tokens", "list", "desk"], w.Db, w.Tenant));

        Assert.Equal(0, run.Exit);
        var line = Assert.Single(run.Out.Split('\n', StringSplitOptions.RemoveEmptyEntries));
        Assert.StartsWith(CredentialGenerationTests.TokenId(desk.Token), line);
        Assert.Contains(" ended ", line);
    }
}

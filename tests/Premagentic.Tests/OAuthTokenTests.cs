using System.Text.Json;
using Premagentic.Core.Identity;

namespace Premagentic.Tests;

/// <summary>
/// The token and revocation endpoints and the check at <c>/mcp</c>: the code
/// claimed once and only after every check; refresh rotation with reuse ending
/// the grant; client binding; the audience; one answer for every failed
/// grant. Requires a running Docker daemon.
/// </summary>
public sealed class OAuthTokenTests(DatastoreTestDatabase server) : IClassFixture<DatastoreTestDatabase>
{
    private const string Port = "http://127.0.0.1:51004/callback";

    private static async Task<(OAuthWorld World, string Client, string Code, string Verifier)> ApprovedAsync(DatastoreTestDatabase server)
    {
        var w = await OAuthWorld.NewAsync(server);
        var client = await w.RegisterLoopbackAsync();
        var (verifier, challenge) = OAuthWorld.Pkce();
        var url = await w.ApproveAsync(w.Alice, client, Port, challenge);
        return (w, client, OAuthWorld.Query(url, "code"), verifier);
    }

    private static string Body(OAuthEndpointAnswer answer) => JsonSerializer.Serialize(answer.Body);

    [Fact]
    public async Task A_code_exchanged_gives_an_access_token_that_resolves_as_the_agent_and_names_its_grant()
    {
        var (w, client, code, verifier) = await ApprovedAsync(server);
        await using var _ = w;

        var answer = await w.ExchangeAsync(client, code, verifier);

        Assert.Equal(200, answer.StatusCode);
        Assert.Equal("Bearer", answer.Body["token_type"]);
        Assert.Equal(OAuthScopes.Read, answer.Body["scope"]);
        Assert.Equal(3600L, answer.Body["expires_in"]);
        var access = (string)answer.Body["access_token"]!;
        Assert.StartsWith(OAuthPrefixes.AccessToken, access);
        Assert.StartsWith(OAuthPrefixes.RefreshToken, (string)answer.Body["refresh_token"]!);

        var (checkedAccess, _) = await w.Tokens.CheckAccessAsync(access, default);
        var caller = await CallerAccess.ResolveOAuthAsync(w.Identity, checkedAccess!);
        Assert.Equal(CallerStatus.Resolved, caller.Resolved.Status);
        Assert.Equal($"agent:{checkedAccess!.AgentId:D} token:oauth:{checkedAccess.GrantId}:{checkedAccess.TokenId}", caller.Scope.AuditLabel);
        Assert.Equal(w.Alice.Id, caller.Resolved.UserId);
        Assert.Equal(OAuthGrantStatus.Live, (await w.Grants.ListOwnAsync(w.Alice.Id, default)).Single().Status);
    }

    [Fact]
    public async Task Token_request_without_redirect_uri_is_accepted()
    {
        var (w, client, code, verifier) = await ApprovedAsync(server);
        await using var _ = w;
        Assert.Equal(200, (await w.ExchangeAsync(client, code, verifier)).StatusCode);
    }

    [Fact]
    public async Task Token_request_with_the_same_redirect_uri_is_accepted()
    {
        var (w, client, code, verifier) = await ApprovedAsync(server);
        await using var _ = w;
        Assert.Equal(200, (await w.ExchangeAsync(client, code, verifier, ("redirect_uri", Port))).StatusCode);
    }

    [Fact]
    public async Task Token_request_with_another_loopback_port_is_invalid_grant()
    {
        var (w, client, code, verifier) = await ApprovedAsync(server);
        await using var _ = w;

        var answer = await w.ExchangeAsync(client, code, verifier, ("redirect_uri", "http://127.0.0.1:51005/callback"));

        Assert.Equal("""{"error":"invalid_grant"}""", Body(answer));
        // Not consumed: the right request still works.
        Assert.Equal(200, (await w.ExchangeAsync(client, code, verifier)).StatusCode);
    }

    [Fact]
    public async Task A_code_issued_to_another_client_is_invalid_grant_and_is_not_consumed()
    {
        var (w, client, code, verifier) = await ApprovedAsync(server);
        await using var _ = w;
        var other = await w.RegisterLoopbackAsync("Other");

        Assert.Equal("""{"error":"invalid_grant"}""", Body(await w.ExchangeAsync(other, code, verifier)));
        Assert.Equal(200, (await w.ExchangeAsync(client, code, verifier)).StatusCode);
    }

    [Fact]
    public async Task An_invalid_first_attempt_does_not_burn_the_code()
    {
        var (w, client, code, verifier) = await ApprovedAsync(server);
        await using var _ = w;

        var wrong = await w.ExchangeAsync(client, code, OAuthWorld.Pkce().Verifier);

        Assert.Equal(400, wrong.StatusCode);
        Assert.True(wrong.CountsAsFailure);
        Assert.Equal(200, (await w.ExchangeAsync(client, code, verifier)).StatusCode);
    }

    [Fact]
    public async Task A_replayed_code_revokes_the_grant()
    {
        var (w, client, code, verifier) = await ApprovedAsync(server);
        await using var _ = w;
        var first = await w.ExchangeAsync(client, code, verifier);

        var replay = await w.ExchangeAsync(client, code, verifier);

        Assert.Equal("""{"error":"invalid_grant"}""", Body(replay));
        var grant = (await w.Grants.ListOwnAsync(w.Alice.Id, default)).Single();
        Assert.Equal(OAuthStore.CodeReplayed, grant.RevokedReason);
        Assert.Null(await w.CallAsync((string)first.Body["access_token"]!));
    }

    [Fact]
    public async Task An_invalid_replay_revokes_nothing()
    {
        var (w, client, code, verifier) = await ApprovedAsync(server);
        await using var _ = w;
        var first = await w.ExchangeAsync(client, code, verifier);

        var replay = await w.ExchangeAsync(client, code, OAuthWorld.Pkce().Verifier);

        Assert.Equal("""{"error":"invalid_grant"}""", Body(replay));
        Assert.Null((await w.Grants.ListOwnAsync(w.Alice.Id, default)).Single().RevokedAt);
        Assert.Equal(CallerStatus.Resolved, await w.CallAsync((string)first.Body["access_token"]!));
    }

    [Fact]
    public async Task An_expired_code_is_invalid_grant_and_revokes_nothing()
    {
        var (w, client, code, verifier) = await ApprovedAsync(server);
        await using var _ = w;
        w.Clock.Now += TimeSpan.FromSeconds(OAuthSettings.CodeSecondsDefault + 1);

        Assert.Equal("""{"error":"invalid_grant"}""", Body(await w.ExchangeAsync(client, code, verifier)));
        Assert.Null((await w.Grants.ListOwnAsync(w.Alice.Id, default)).Single().RevokedAt);
    }

    [Fact]
    public async Task A_refresh_rotates_and_a_reused_refresh_token_revokes_the_grant()
    {
        await using var w = await OAuthWorld.NewAsync(server);
        var client = await w.RegisterLoopbackAsync();
        var c = await w.ConnectAsync(w.Alice, client);

        var refreshed = await w.RefreshAsync(client, c.Refresh);
        Assert.Equal(200, refreshed.StatusCode);
        Assert.NotEqual(c.Refresh, refreshed.Body["refresh_token"]);
        var grant = (await w.Grants.ListOwnAsync(w.Alice.Id, default)).Single();
        Assert.Equal(1, grant.RefreshCount);
        Assert.NotNull(grant.LastRefreshedAt);

        var reused = await w.RefreshAsync(client, c.Refresh);

        Assert.Equal("""{"error":"invalid_grant"}""", Body(reused));
        Assert.Equal(OAuthStore.RefreshTokenReused, (await w.Grants.ListOwnAsync(w.Alice.Id, default)).Single().RevokedReason);
        Assert.Null(await w.CallAsync((string)refreshed.Body["access_token"]!));
        Assert.Equal(400, (await w.RefreshAsync(client, (string)refreshed.Body["refresh_token"]!)).StatusCode);
    }

    [Fact]
    public async Task A_refresh_token_presented_by_another_client_is_invalid_grant_and_does_not_rotate()
    {
        await using var w = await OAuthWorld.NewAsync(server);
        var client = await w.RegisterLoopbackAsync();
        var other = await w.RegisterLoopbackAsync("Other");
        var c = await w.ConnectAsync(w.Alice, client);

        Assert.Equal("""{"error":"invalid_grant"}""", Body(await w.RefreshAsync(other, c.Refresh)));

        Assert.Null((await w.Grants.ListOwnAsync(w.Alice.Id, default)).Single().RevokedAt);
        Assert.Equal(200, (await w.RefreshAsync(client, c.Refresh)).StatusCode);
    }

    [Fact]
    public async Task Two_concurrent_refreshes_issue_once_and_the_loser_revokes()
    {
        await using var w = await OAuthWorld.NewAsync(server);
        var client = await w.RegisterLoopbackAsync();
        var c = await w.ConnectAsync(w.Alice, client);

        var answers = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => w.RefreshAsync(client, c.Refresh)));

        Assert.Single(answers, a => a.StatusCode == 200);
        Assert.Single(answers, a => a.StatusCode == 400);
        Assert.Equal(OAuthStore.RefreshTokenReused, (await w.Grants.ListOwnAsync(w.Alice.Id, default)).Single().RevokedReason);
    }

    [Fact]
    public async Task A_used_refresh_token_revokes_after_a_sweep()
    {
        await using var w = await OAuthWorld.NewAsync(server);
        var client = await w.RegisterLoopbackAsync();
        var c = await w.ConnectAsync(w.Alice, client);
        var second = (string)(await w.RefreshAsync(client, c.Refresh)).Body["refresh_token"]!;

        // Another refresh sweeps what has expired; the used token has not, so it stays to be recognized.
        w.Clock.Now += TimeSpan.FromHours(2);
        Assert.Equal(200, (await w.RefreshAsync(client, second)).StatusCode);

        Assert.Equal(400, (await w.RefreshAsync(client, c.Refresh)).StatusCode);
        Assert.Equal(OAuthStore.RefreshTokenReused, (await w.Grants.ListOwnAsync(w.Alice.Id, default)).Single().RevokedReason);
    }

    [Fact]
    public async Task Refresh_after_a_public_url_change_is_invalid_grant_and_frees_the_slot()
    {
        await using var w = await OAuthWorld.NewAsync(server, bound: 1);
        var client = await w.RegisterLoopbackAsync();
        var c = await w.ConnectAsync(w.Alice, client);
        // The control: under the same address the refresh works.
        var refreshed = (string)(await w.RefreshAsync(client, c.Refresh)).Body["refresh_token"]!;

        w.Oauth = new OAuthDeployment("https://renamed.test", true, [OAuthWorld.Listed]);

        Assert.Equal("""{"error":"invalid_grant"}""", Body(await w.RefreshAsync(client, refreshed)));
        Assert.Equal(OAuthStore.AddressChanged, (await w.Grants.ListOwnAsync(w.Alice.Id, default)).Single().RevokedReason);
        Assert.Equal(0, await new SelfServeAgents(w.Db, w.Tenant, w.Oauth, w.Clock).ConnectedCountAsync(w.Alice.Id, default));
    }

    [Fact]
    public async Task A_token_for_another_resource_is_refused_at_mcp()
    {
        await using var w = await OAuthWorld.NewAsync(server);
        var client = await w.RegisterLoopbackAsync();
        var c = await w.ConnectAsync(w.Alice, client);

        w.Oauth = new OAuthDeployment("https://renamed.test", true, []);
        var (access, refusal) = await w.Tokens.CheckAccessAsync(c.Access, default);

        Assert.Null(access);
        Assert.Equal("access_other_audience", refusal);
    }

    [Fact]
    public async Task Scope_is_always_read()
    {
        await using var w = await OAuthWorld.NewAsync(server);
        var client = await w.RegisterLoopbackAsync();
        foreach (var scope in new string?[] { null, "read offline_access", "openid read" })
        {
            var (verifier, challenge) = OAuthWorld.Pkce();
            var p = w.Params(client, Port, challenge);
            if (scope is not null) p["scope"] = [scope];
            var url = await w.Consent.ApproveAsync(w.Alice.Id, p, ModelLocation.Hosted, "A Vendor", default);
            var answer = await w.ExchangeAsync(client, OAuthWorld.Query(url, "code"), verifier, ("scope", scope ?? "read"));
            Assert.Equal(OAuthScopes.Read, answer.Body["scope"]);

            var refreshed = await w.RefreshAsync(client, (string)answer.Body["refresh_token"]!, ("scope", "openid"));
            Assert.Equal(200, refreshed.StatusCode);
            Assert.Equal(OAuthScopes.Read, refreshed.Body["scope"]);
        }
        await using var cmd = w.Db.DataSource.CreateCommand("SELECT count(*) FROM prem_config.oauth_grant WHERE scope <> 'read'");
        Assert.Equal(0L, (long)(await cmd.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task Unknown_or_removed_client_is_invalid_client_and_a_secret_is_refused()
    {
        var (w, client, code, verifier) = await ApprovedAsync(server);
        await using var _ = w;

        var unknown = await w.ExchangeAsync("prem_cli_0123456789abcdef01234567", code, verifier);
        var secret = await w.ExchangeAsync(client, code, verifier, ("client_secret", "anything"));
        await w.Clients.RemoveAsync(w.Admin, client, default);
        var removed = await w.ExchangeAsync(client, code, verifier);

        Assert.Equal("invalid_client", unknown.Body["error"]);
        Assert.True(unknown.CountsAsFailure);
        Assert.Equal("invalid_client", secret.Body["error"]);
        Assert.Equal("invalid_client", removed.Body["error"]);
        Assert.False(removed.CountsAsFailure);
    }

    [Fact]
    public async Task Every_failed_grant_answers_the_same()
    {
        var (w, client, code, verifier) = await ApprovedAsync(server);
        await using var _ = w;
        var other = await w.RegisterLoopbackAsync("Other");

        var answers = new[]
        {
            await w.ExchangeAsync(client, "no-such-code-000000000000000000000000000000", verifier),
            await w.ExchangeAsync(client, code, OAuthWorld.Pkce().Verifier),
            await w.ExchangeAsync(other, code, verifier),
            await w.RefreshAsync(client, "prem_ort_0123456789abcdef01234567_" + new string('A', 43)),
            await w.RefreshAsync(client, "not a token"),
        };

        Assert.All(answers, a => Assert.Equal("""{"error":"invalid_grant"}""", Body(a)));
        Assert.All(answers, a => Assert.Equal(400, a.StatusCode));
    }

    [Fact]
    public async Task The_revocation_endpoint_answers_by_its_table()
    {
        await using var w = await OAuthWorld.NewAsync(server);
        var client = await w.RegisterLoopbackAsync();
        var other = await w.RegisterLoopbackAsync("Other");
        var c = await w.ConnectAsync(w.Alice, client);

        Task<OAuthEndpointAnswer> Revoke(string id, string token) =>
            w.Tokens.RevokeAsync(new Dictionary<string, string> { ["client_id"] = id, ["token"] = token }, default);

        Assert.Equal(200, (await Revoke(client, "garbage")).StatusCode);
        Assert.Equal(200, (await Revoke(client, "prem_agt_0123456789abcdef01234567_" + new string('A', 43))).StatusCode);
        Assert.Equal("invalid_client", (await Revoke("prem_cli_0123456789abcdef01234567", c.Refresh)).Body["error"]);
        Assert.Equal("invalid_grant", (await Revoke(other, c.Refresh)).Body["error"]);
        Assert.Equal(CallerStatus.Resolved, await w.CallAsync(c.Access));

        Assert.Equal(200, (await Revoke(client, c.Access)).StatusCode);
        Assert.Null(await w.CallAsync(c.Access));
        var grant = (await w.Grants.ListOwnAsync(w.Alice.Id, default)).Single();
        Assert.Equal(OAuthStore.ClientRevoke, grant.RevokedReason);
        Assert.True((await w.Identity.FindAgentAsync(c.AgentId))!.Disabled);
        // Already revoked: nothing more.
        Assert.Equal(200, (await Revoke(client, c.Refresh)).StatusCode);
    }

    [Fact]
    public async Task A_rotated_out_refresh_token_revokes_nothing_when_revoked()
    {
        await using var w = await OAuthWorld.NewAsync(server);
        var client = await w.RegisterLoopbackAsync();
        var c = await w.ConnectAsync(w.Alice, client);
        var fresh = await w.RefreshAsync(client, c.Refresh);

        var answer = await w.Tokens.RevokeAsync(new Dictionary<string, string> { ["client_id"] = client, ["token"] = c.Refresh }, default);

        Assert.Equal(200, answer.StatusCode);
        Assert.Equal(CallerStatus.Resolved, await w.CallAsync((string)fresh.Body["access_token"]!));
    }
}

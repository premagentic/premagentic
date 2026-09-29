using System.Text.Json;
using Premagentic.Core.Identity;

namespace Premagentic.Tests;

/// <summary>
/// Dynamic registration (RFC 7591) as the flow serves it: loopback redirects
/// by default, https ones only from the administrator's list, private-use
/// ones dropped, anything malformed refusing the whole request; hostile text
/// refused; pending clients capped. Requires a running Docker daemon.
/// </summary>
public sealed class OAuthRegistrationTests(DatastoreTestDatabase server) : IClassFixture<DatastoreTestDatabase>
{
    private static string Body(string name, params string[] redirects) =>
        JsonSerializer.Serialize(new { client_name = name, redirect_uris = redirects });

    private static string[] Stored(OAuthEndpointAnswer answer) =>
        ((IEnumerable<string>)answer.Body["redirect_uris"]!).ToArray();

    [Fact]
    public async Task Dynamic_registration_accepts_a_loopback_redirect()
    {
        await using var w = await OAuthWorld.NewAsync(server);

        var answer = await w.RegisterAsync(Body("Desk Tool", OAuthWorld.Loopback));

        Assert.Equal(201, answer.StatusCode);
        Assert.StartsWith(OAuthPrefixes.ClientId, (string)answer.Body["client_id"]!);
        Assert.Equal(new[] { OAuthWorld.Loopback }, Stored(answer));
        Assert.Equal("none", answer.Body["token_endpoint_auth_method"]);
        Assert.False(answer.Body.ContainsKey("client_secret"));
        Assert.Equal(1, await w.RowsAsync("oauth.client.register"));
    }

    [Fact]
    public async Task Dynamic_registration_refuses_an_https_redirect_by_default()
    {
        await using var w = await OAuthWorld.NewAsync(server);

        var answer = await w.RegisterAsync(Body("Web Tool", "https://attacker.example/cb"));

        Assert.Equal(400, answer.StatusCode);
        Assert.Equal("invalid_redirect_uri", answer.Body["error"]);
        Assert.Equal(OAuthClients.LoopbackOnly, answer.Body["error_description"]);
        Assert.Equal(0, await w.RowsAsync("oauth.client.register"));
    }

    [Fact]
    public async Task Dynamic_registration_accepts_a_listed_uri_and_refuses_its_sibling_path()
    {
        await using var w = await OAuthWorld.NewAsync(server);

        var listed = await w.RegisterAsync(Body("Hosted Tool", OAuthWorld.Listed));
        var sibling = await w.RegisterAsync(Body("Hosted Tool", OAuthWorld.Listed + "2"));

        Assert.Equal(201, listed.StatusCode);
        Assert.Equal(new[] { OAuthWorld.Listed }, Stored(listed));
        Assert.Equal(400, sibling.StatusCode);
        Assert.Equal("invalid_redirect_uri", sibling.Body["error"]);
    }

    [Fact]
    public async Task Cursors_three_uri_body_registers_with_the_loopback_uri_only()
    {
        await using var w = await OAuthWorld.NewAsync(server);
        const string custom = "cursor://anysphere.cursor-mcp/oauth/callback";

        var answer = await w.RegisterAsync(Body("Cursor", custom, "http://localhost:8787/callback", "https://cursor.com/oauth/callback"));

        Assert.Equal(201, answer.StatusCode);
        Assert.Equal(new[] { "http://localhost:8787/callback" }, Stored(answer));
        await using (var cmd = w.Db.DataSource.CreateCommand("SELECT new_value::text FROM prem_config.admin_event WHERE kind = 'oauth.client.register'"))
        {
            var row = (string)(await cmd.ExecuteScalarAsync())!;
            Assert.Contains("cursor://anysphere.cursor-mcp", row);
            Assert.Contains("https://cursor.com", row);
            Assert.DoesNotContain("oauth/callback\"", row.Replace("http://localhost:8787/callback", ""));
        }

        var (_, challenge) = OAuthWorld.Pkce();
        var check = await w.Consent.CheckAsync(w.Params((string)answer.Body["client_id"]!, custom, challenge), default);
        Assert.Equal(AuthorizationCheckOutcome.ShowError, check.Outcome);
    }

    [Fact]
    public async Task A_fragment_uri_beside_a_good_one_refuses_the_whole_body()
    {
        await using var w = await OAuthWorld.NewAsync(server);

        var answer = await w.RegisterAsync(Body("Desk Tool", OAuthWorld.Loopback, "http://127.0.0.1/other#frag"));

        Assert.Equal(400, answer.StatusCode);
        Assert.Equal("invalid_redirect_uri", answer.Body["error"]);
        Assert.Equal(0, await w.RowsAsync("oauth.client.register"));
    }

    [Fact]
    public async Task More_than_ten_submitted_redirects_refuse_the_whole_body()
    {
        await using var w = await OAuthWorld.NewAsync(server);
        var many = Enumerable.Range(1, 11).Select(i => $"http://127.0.0.1:{8000 + i}/cb").ToArray();

        var answer = await w.RegisterAsync(Body("Desk Tool", many));

        Assert.Equal(400, answer.StatusCode);
        Assert.Equal("invalid_redirect_uri", answer.Body["error"]);
    }

    [Theory]
    [InlineData("Evil\u202Eloot")]
    [InlineData("Zero\u200Bwidth")]
    [InlineData("Half\uD800pair")]
    [InlineData("   ")]
    [InlineData("12345678901234567890123456789012345678901234567890123456789012345")]
    public async Task Hostile_metadata_is_refused(string name)
    {
        await using var w = await OAuthWorld.NewAsync(server);

        var answer = await w.RegisterAsync(Body(name, OAuthWorld.Loopback));

        Assert.Equal(400, answer.StatusCode);
        Assert.Equal("invalid_client_metadata", answer.Body["error"]);
    }

    [Theory]
    [InlineData("  Desk Tool  ", "Desk Tool")]
    [InlineData("1234567890123456789012345678901234567890123456789012345678901234", "1234567890123456789012345678901234567890123456789012345678901234")]
    [InlineData("\u0645\u0633\u0627\u0639\u062F", "\u0645\u0633\u0627\u0639\u062F")]
    public async Task A_plain_name_is_accepted_trimmed_and_never_cut(string name, string stored)
    {
        await using var w = await OAuthWorld.NewAsync(server);

        var answer = await w.RegisterAsync(Body(name, OAuthWorld.Loopback));

        Assert.Equal(201, answer.StatusCode);
        Assert.Equal(stored, answer.Body["client_name"]);
    }

    [Fact]
    public async Task Application_type_is_kept_only_as_native_or_web()
    {
        await using var w = await OAuthWorld.NewAsync(server);

        var native = await w.RegisterAsync("""{"client_name":"A","redirect_uris":["http://127.0.0.1/cb"],"application_type":"native"}""");
        var other = await w.RegisterAsync("""{"client_name":"B","redirect_uris":["http://127.0.0.1/cb"],"application_type":"browser"}""");

        Assert.Equal("native", native.Body["application_type"]);
        Assert.False(other.Body.ContainsKey("application_type"));
        var stored = (await w.Clients.ListAsync(default)).Single(c => c.Client.Id == (string)other.Body["client_id"]!);
        Assert.Null(stored.Client.ApplicationType);
    }

    [Fact]
    public async Task The_pending_cap_refuses_and_neither_an_admin_nor_an_approved_client_counts()
    {
        await using var w = await OAuthWorld.NewAsync(server);
        await w.SetAsync(OAuthSettings.MaxPendingClients, 2);
        var first = await w.RegisterLoopbackAsync("One", source: "10.0.0.1");
        await w.RegisterLoopbackAsync("Two", source: "10.0.0.2");

        var full = await w.RegisterAsync(Body("Three", OAuthWorld.Loopback), "10.0.0.3");
        Assert.Equal(503, full.StatusCode);
        Assert.Equal("temporarily_unavailable", full.Body["error"]);
        Assert.NotNull(full.RetryAfter);

        // An administrator's registration is never capped, and does not count.
        await w.Clients.AddAsync(w.Admin, null, "Admin Tool", [OAuthWorld.Loopback], null, null, default);
        Assert.Equal(503, (await w.RegisterAsync(Body("Three", OAuthWorld.Loopback), "10.0.0.3")).StatusCode);

        // Approving one takes it out of the count.
        var (_, challenge) = OAuthWorld.Pkce();
        await w.ApproveAsync(w.Alice, first, "http://127.0.0.1:5000/callback", challenge);
        Assert.Equal(201, (await w.RegisterAsync(Body("Three", OAuthWorld.Loopback), "10.0.0.3")).StatusCode);
    }

    [Fact]
    public async Task One_address_cannot_hold_more_than_N_pending_clients()
    {
        await using var w = await OAuthWorld.NewAsync(server);
        await w.SetAsync(OAuthSettings.PendingClientsPerAddress, 2);
        await w.RegisterLoopbackAsync("One", source: "10.0.0.9");
        await w.RegisterLoopbackAsync("Two", source: "10.0.0.9");

        var third = await w.RegisterAsync(Body("Three", OAuthWorld.Loopback), "10.0.0.9");
        Assert.Equal(429, third.StatusCode);
        Assert.Equal("temporarily_unavailable", third.Body["error"]);
        Assert.Equal(201, (await w.RegisterAsync(Body("Three", OAuthWorld.Loopback), "10.0.0.10")).StatusCode);

        // A day later the two were never approved and no longer count.
        w.Clock.Now += TimeSpan.FromHours(OAuthSettings.PendingClientHours + 1);
        Assert.Equal(201, (await w.RegisterAsync(Body("Three", OAuthWorld.Loopback), "10.0.0.9")).StatusCode);
    }

    [Fact]
    public async Task An_approved_client_survives_25_hours_after_every_grant_is_revoked()
    {
        await using var w = await OAuthWorld.NewAsync(server);
        var client = await w.RegisterLoopbackAsync();
        await w.ConnectAsync(w.Alice, client);
        await w.Grants.RevokeManyAsync(w.Admin, null, default);

        w.Clock.Now += TimeSpan.FromHours(25);
        await w.RegisterLoopbackAsync("Another");

        var (_, challenge) = OAuthWorld.Pkce();
        Assert.Equal(AuthorizationCheckOutcome.Valid,
            (await w.Consent.CheckAsync(w.Params(client, "http://127.0.0.1:5000/callback", challenge), default)).Outcome);
    }

    [Fact]
    public async Task A_never_approved_client_is_swept_after_a_day_and_its_id_reads_as_expired()
    {
        await using var w = await OAuthWorld.NewAsync(server);
        var client = await w.RegisterLoopbackAsync();

        w.Clock.Now += TimeSpan.FromHours(OAuthSettings.PendingClientHours + 1);
        await w.RegisterLoopbackAsync("Another");

        var (_, challenge) = OAuthWorld.Pkce();
        var check = await w.Consent.CheckAsync(w.Params(client, "http://127.0.0.1:5000/callback", challenge), default);
        Assert.Equal(AuthorizationCheckOutcome.ShowError, check.Outcome);
        Assert.Equal(OAuthConsent.RegistrationEnded, check.Message);
    }
}

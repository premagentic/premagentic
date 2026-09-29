using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Premagentic.Api.Callers;
using Microsoft.AspNetCore.Http;

namespace Premagentic.Tests;

/// <summary>
/// People signing in over HTTP: the session, its cookie, its end, the one
/// failure response, the lockout and throttle, HTTPS, and the anti-forgery rule.
/// Requires a running Docker daemon.
/// </summary>
public sealed class ApiSignInTests(DatastoreTestDatabase server) : IClassFixture<DatastoreTestDatabase>
{
    [Fact]
    public async Task No_caller_is_a_401_on_every_route_that_reads_or_changes_anything()
    {
        await using var w = await ApiWorld.NewAsync(server);
        using var client = w.Host.Client();

        foreach (var (method, path, body) in new (HttpMethod, string, object?)[]
        {
            (HttpMethod.Post, "/api/search", new { query = "zeppelin" }),
            (HttpMethod.Post, "/api/section", new { path = ApiWorld.Handbook }),
            (HttpMethod.Get, "/api/session", null),
            (HttpMethod.Delete, "/api/session", null),
            (HttpMethod.Post, "/mcp", new { jsonrpc = "2.0", id = 1, method = "tools/list" }),
            (HttpMethod.Get, "/api/no-such-route", null),
        })
        {
            using var response = await client.SendAsync(Api.Request(method, path, body));
            Assert.True(response.StatusCode == HttpStatusCode.Unauthorized, $"{method} {path} gave {(int)response.StatusCode}");
            Assert.DoesNotContain("zeppelin", await response.Content.ReadAsStringAsync());
        }

        // The control: the anonymous health check answers, and the same search with a caller is a 200.
        using (var health = await client.GetAsync("/health")) Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        var bob = await Api.SessionAsync(client, "bob", ApiWorld.BobPassword);
        Assert.Equal([ApiWorld.Handbook], await Api.PathsAsync(await Api.SearchAsync(client, session: bob)));
    }

    [Fact]
    public async Task Sign_in_issues_a_session_who_am_i_names_it_and_sign_out_ends_it()
    {
        await using var w = await ApiWorld.NewAsync(server);
        using var client = w.Host.Client();

        var session = await Api.SessionAsync(client, "alice", ApiWorld.AlicePassword);

        using (var me = await client.SendAsync(Api.Request(HttpMethod.Get, "/api/session", session: session, antiForgery: false)))
        {
            Assert.Equal(HttpStatusCode.OK, me.StatusCode);
            var body = await me.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("person", body.GetProperty("kind").GetString());
            Assert.Equal("alice", body.GetProperty("signInName").GetString());
            Assert.Equal("session", body.GetProperty("via").GetString());
            Assert.Equal(session.AntiForgeryToken, body.GetProperty("antiForgeryToken").GetString());
        }
        Assert.Equal([ApiWorld.Pay, ApiWorld.Handbook, ApiWorld.Plan], await Api.PathsAsync(await Api.SearchAsync(client, session: session)));

        using (var signOut = await client.SendAsync(Api.Request(HttpMethod.Delete, "/api/session", session: session)))
        {
            Assert.Equal(HttpStatusCode.NoContent, signOut.StatusCode);
            Assert.Contains("expires=", Api.SetCookieLine(signOut), StringComparison.OrdinalIgnoreCase);
        }

        using var after = await Api.SearchAsync(client, session: session);
        Assert.Equal(HttpStatusCode.Unauthorized, after.StatusCode);
    }

    [Fact]
    public async Task Every_sign_in_issues_a_fresh_session_id()
    {
        await using var w = await ApiWorld.NewAsync(server);
        using var client = w.Host.Client();

        var first = await Api.SessionAsync(client, "alice", ApiWorld.AlicePassword);
        var second = await Api.SessionAsync(client, "alice", ApiWorld.AlicePassword);

        Assert.NotEqual(first.Cookie, second.Cookie);
        Assert.NotEqual(first.AntiForgeryToken, second.AntiForgeryToken);
        Assert.Equal(32, Convert.FromBase64String(Pad(first.Cookie)).Length);
        foreach (var s in new[] { first, second })
            Assert.Equal(HttpStatusCode.OK, (await Api.SearchAsync(client, session: s)).StatusCode);

        // Only the hash is stored: the cookie value itself is nowhere in the table.
        await using var cmd = w.Db.DataSource.CreateCommand("SELECT id_sha256 FROM prem_config.user_session");
        await using var reader = await cmd.ExecuteReaderAsync();
        var stored = new List<byte[]>();
        while (await reader.ReadAsync()) stored.Add(reader.GetFieldValue<byte[]>(0));
        Assert.Equal(2, stored.Count);
        Assert.All(stored, h => Assert.Equal(32, h.Length));
        Assert.DoesNotContain(stored, h => h.AsSpan().SequenceEqual(Convert.FromBase64String(Pad(first.Cookie))));
    }

    [Fact]
    public async Task The_session_cookie_is_HttpOnly_Secure_SameSite_Strict_and_host_only()
    {
        await using var w = await ApiWorld.NewAsync(server);
        using var client = w.Host.Client();

        using var response = await Api.SignInAsync(client, "alice", ApiWorld.AlicePassword);
        var line = Api.SetCookieLine(response)!;
        var attributes = line.Split(';').Skip(1).Select(a => a.Trim().ToLowerInvariant()).ToArray();

        Assert.StartsWith("__Host-", line);
        Assert.Contains("httponly", attributes);
        Assert.Contains("secure", attributes);
        Assert.Contains("samesite=strict", attributes);
        Assert.Contains("path=/", attributes);
        Assert.DoesNotContain(attributes, a => a.StartsWith("domain=", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_wrong_password_an_unknown_name_a_disabled_account_and_a_locked_one_get_the_same_response()
    {
        await using var w = await ApiWorld.NewAsync(server);
        using var client = w.Host.Client();

        // Lock Bob: five wrong passwords.
        for (var i = 0; i < 5; i++)
            (await Api.SignInAsync(client, "bob", "not bobs password")).Dispose();

        var responses = new[]
        {
            await Api.SignInAsync(client, "alice", "not alices password"),
            await Api.SignInAsync(client, "nobody-by-this-name", ApiWorld.AlicePassword),
            await Api.SignInAsync(client, "eve", ApiWorld.EvePassword),
            await Api.SignInAsync(client, "bob", ApiWorld.BobPassword),
            await Api.SignInAsync(client, "dan", ""),
        };

        var first = await Describe(responses[0]);
        Assert.Equal(HttpStatusCode.Unauthorized, responses[0].StatusCode);
        foreach (var response in responses)
        {
            Assert.Equal(first, await Describe(response));
            Assert.Null(Api.SetCookieLine(response));
            response.Dispose();
        }

        // The control: the right password for an account in good standing works.
        using var ok = await Api.SignInAsync(client, "alice", ApiWorld.AlicePassword);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
    }

    [Fact]
    public async Task Five_wrong_passwords_lock_the_account_until_the_lock_passes()
    {
        await using var w = await ApiWorld.NewAsync(server);
        using var client = w.Host.Client();

        for (var i = 0; i < 4; i++)
            Assert.Equal(HttpStatusCode.Unauthorized, (await Api.SignInAsync(client, "bob", "wrong")).StatusCode);
        // Four failures do not lock: the right password still works, and clears the count.
        Assert.Equal(HttpStatusCode.OK, (await Api.SignInAsync(client, "bob", ApiWorld.BobPassword)).StatusCode);

        for (var i = 0; i < 5; i++)
            Assert.Equal(HttpStatusCode.Unauthorized, (await Api.SignInAsync(client, "bob", "wrong")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Api.SignInAsync(client, "bob", ApiWorld.BobPassword)).StatusCode);

        w.Clock.Now += TimeSpan.FromMinutes(14);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Api.SignInAsync(client, "bob", ApiWorld.BobPassword)).StatusCode);

        w.Clock.Now += TimeSpan.FromMinutes(2);
        Assert.Equal(HttpStatusCode.OK, (await Api.SignInAsync(client, "bob", ApiWorld.BobPassword)).StatusCode);
    }

    [Fact]
    public async Task Failed_sign_ins_are_throttled_by_address_and_other_addresses_are_not()
    {
        await using var w = await ApiWorld.NewAsync(server, new ApiHostOptions
        {
            Throttle = new SignInThrottleOptions(3, TimeSpan.FromMinutes(10), 100),
        });

        using var client = w.Host.Client();
        async Task<int> SignInFrom(string address, string name, string password)
        {
            var request = Api.Request(HttpMethod.Post, "/api/session", new { signInName = name, password },
                header: (TestRemoteAddress.Header, address));
            using var response = await client.SendAsync(request);
            return (int)response.StatusCode;
        }

        for (var i = 0; i < 3; i++)
            Assert.Equal(StatusCodes.Status401Unauthorized, await SignInFrom("203.0.113.7", "nobody-" + i, "guess"));

        // The fourth attempt from that address is refused before any password is checked, even a right one.
        Assert.Equal(StatusCodes.Status429TooManyRequests, await SignInFrom("203.0.113.7", "alice", ApiWorld.AlicePassword));
        Assert.Equal(StatusCodes.Status200OK, await SignInFrom("203.0.113.8", "alice", ApiWorld.AlicePassword));

        w.Clock.Now += TimeSpan.FromMinutes(11);
        Assert.Equal(StatusCodes.Status200OK, await SignInFrom("203.0.113.7", "alice", ApiWorld.AlicePassword));
    }

    [Fact]
    public async Task Password_sign_in_is_refused_over_plain_http_by_default()
    {
        await using var w = await ApiWorld.NewAsync(server);
        using var http = w.Host.Client(https: false);

        using var refused = await Api.SignInAsync(http, "alice", ApiWorld.AlicePassword);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Null(Api.SetCookieLine(refused));
        Assert.Contains("HTTPS", await refused.Content.ReadAsStringAsync());

        // The control: the same request over HTTPS signs in.
        using var https = w.Host.Client();
        using var ok = await Api.SignInAsync(https, "alice", ApiWorld.AlicePassword);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
    }

    [Fact]
    public async Task The_development_switch_allows_password_sign_in_over_plain_http()
    {
        await using var w = await ApiWorld.NewAsync(server, new ApiHostOptions { AllowHttpSignIn = true });
        using var http = w.Host.Client(https: false);

        using var ok = await Api.SignInAsync(http, "alice", ApiWorld.AlicePassword);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        // The cookie is still marked Secure: the switch loosens sign-in, not the cookie.
        Assert.Contains("secure", Api.SetCookieLine(ok)!.ToLowerInvariant());
    }

    [Fact]
    public async Task A_request_that_changes_state_under_a_session_needs_its_anti_forgery_token()
    {
        await using var w = await ApiWorld.NewAsync(server);
        using var client = w.Host.Client();
        var alice = await Api.SessionAsync(client, "alice", ApiWorld.AlicePassword);
        var bob = await Api.SessionAsync(client, "bob", ApiWorld.BobPassword);

        Assert.Equal(HttpStatusCode.Forbidden, (await Api.SearchAsync(client, session: alice, antiForgery: false)).StatusCode);
        var withBobsToken = Api.Request(HttpMethod.Post, "/api/search", new { query = "zeppelin" }, alice with { AntiForgeryToken = bob.AntiForgeryToken });
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(withBobsToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(Api.Request(HttpMethod.Delete, "/api/session", session: alice, antiForgery: false))).StatusCode);

        // With its own token it goes through, and a safe request needs none.
        Assert.Equal(HttpStatusCode.OK, (await Api.SearchAsync(client, session: alice)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Api.Request(HttpMethod.Get, "/api/session", session: alice, antiForgery: false))).StatusCode);
    }

    [Fact]
    public async Task A_bearer_token_needs_no_anti_forgery_token()
    {
        await using var w = await ApiWorld.NewAsync(server);
        using var client = w.Host.Client();

        using var response = await Api.SearchAsync(client, bearer: w.BotToken);
        Assert.Equal([ApiWorld.AuditLog, ApiWorld.Handbook], await Api.PathsAsync(response));
    }

    [Fact]
    public async Task A_session_dies_the_moment_its_user_is_disabled_and_stays_dead_after_enable()
    {
        await using var w = await ApiWorld.NewAsync(server);
        using var client = w.Host.Client();
        var session = await Api.SessionAsync(client, "alice", ApiWorld.AlicePassword);
        Assert.Equal(HttpStatusCode.OK, (await Api.SearchAsync(client, session: session)).StatusCode);

        await w.Identity.SetUserDisabledAsync(w.Alice.Id, true);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Api.SearchAsync(client, session: session)).StatusCode);

        await w.Identity.SetUserDisabledAsync(w.Alice.Id, false);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Api.SearchAsync(client, session: session)).StatusCode);

        // The control: she can sign in again, with a new session.
        var fresh = await Api.SessionAsync(client, "alice", ApiWorld.AlicePassword);
        Assert.Equal(HttpStatusCode.OK, (await Api.SearchAsync(client, session: fresh)).StatusCode);
    }

    [Fact]
    public async Task A_disabled_flag_written_by_another_process_stops_the_session_on_the_next_request()
    {
        await using var w = await ApiWorld.NewAsync(server);
        using var client = w.Host.Client();
        var session = await Api.SessionAsync(client, "alice", ApiWorld.AlicePassword);

        // Straight to the table, as another writer might, leaving the session row in place.
        await using (var cmd = w.Db.DataSource.CreateCommand("UPDATE prem_config.app_user SET disabled = true WHERE id = @id"))
        {
            cmd.Parameters.AddWithValue("id", w.Alice.Id);
            await cmd.ExecuteNonQueryAsync();
        }

        using var me = await client.SendAsync(Api.Request(HttpMethod.Get, "/api/session", session: session, antiForgery: false));
        Assert.Equal(HttpStatusCode.Unauthorized, me.StatusCode);
    }

    [Fact]
    public async Task A_new_password_ends_every_session()
    {
        await using var w = await ApiWorld.NewAsync(server);
        using var client = w.Host.Client();
        var session = await Api.SessionAsync(client, "alice", ApiWorld.AlicePassword);

        await w.Identity.SetPasswordHashAsync(w.Alice.Id, new Core.Identity.PasswordHasher().Hash("alice picks a new phrase"));

        Assert.Equal(HttpStatusCode.Unauthorized, (await Api.SearchAsync(client, session: session)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Api.SignInAsync(client, "alice", "alice picks a new phrase")).StatusCode);
    }

    [Fact]
    public async Task A_session_ends_after_its_idle_timeout_and_after_its_absolute_lifetime()
    {
        await using var w = await ApiWorld.NewAsync(server);
        using var client = w.Host.Client();

        var idle = await Api.SessionAsync(client, "alice", ApiWorld.AlicePassword);
        w.Clock.Now += TimeSpan.FromMinutes(29);
        Assert.Equal(HttpStatusCode.OK, (await Api.SearchAsync(client, session: idle)).StatusCode);
        w.Clock.Now += TimeSpan.FromMinutes(29);
        Assert.Equal(HttpStatusCode.OK, (await Api.SearchAsync(client, session: idle)).StatusCode);
        w.Clock.Now += TimeSpan.FromMinutes(31);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Api.SearchAsync(client, session: idle)).StatusCode);

        var busy = await Api.SessionAsync(client, "alice", ApiWorld.AlicePassword);
        for (var elapsed = TimeSpan.Zero; elapsed < TimeSpan.FromHours(8) - TimeSpan.FromMinutes(20); elapsed += TimeSpan.FromMinutes(20))
        {
            w.Clock.Now += TimeSpan.FromMinutes(20);
            Assert.Equal(HttpStatusCode.OK, (await Api.SearchAsync(client, session: busy)).StatusCode);
        }
        w.Clock.Now += TimeSpan.FromMinutes(20);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Api.SearchAsync(client, session: busy)).StatusCode);
    }

    [Fact]
    public async Task A_session_cookie_that_is_malformed_or_unknown_holds_nothing()
    {
        await using var w = await ApiWorld.NewAsync(server);
        using var client = w.Host.Client();
        var real = await Api.SessionAsync(client, "alice", ApiWorld.AlicePassword);

        foreach (var cookie in new[] { "x", real.Cookie + "A", Convert.ToBase64String(new byte[32]).TrimEnd('=').Replace('+', '-').Replace('/', '_') })
        {
            var forged = new ApiSession(cookie, real.AntiForgeryToken);
            Assert.Equal(HttpStatusCode.Unauthorized, (await Api.SearchAsync(client, session: forged)).StatusCode);
        }
        Assert.Equal(HttpStatusCode.OK, (await Api.SearchAsync(client, session: real)).StatusCode);
    }

    private static async Task<string> Describe(HttpResponseMessage response) =>
        $"{(int)response.StatusCode} {response.Content.Headers.ContentType} {await response.Content.ReadAsStringAsync()}";

    private static string Pad(string base64Url)
    {
        var s = base64Url.Replace('-', '+').Replace('_', '/');
        return s + new string('=', (4 - s.Length % 4) % 4);
    }
}

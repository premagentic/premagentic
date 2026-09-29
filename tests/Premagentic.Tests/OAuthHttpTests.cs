using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using Premagentic.Api.Callers;
using Premagentic.Core.Admin;
using Premagentic.Core.Identity;
using Premagentic.Portal.Pages;

namespace Premagentic.Tests;

/// <summary>
/// The authorization flow while it is on, over HTTP: the discovery documents,
/// self-registration, the authorization forwarder, the token and revocation
/// endpoints with their throttle, and <c>/mcp</c> taking an access token.
/// Requires a running Docker daemon.
/// </summary>
public sealed class OAuthHttpTests(DatastoreTestDatabase server) : IClassFixture<DatastoreTestDatabase>
{
    private const string ResourceMetadataUrl = OAuthApi.PublicUrl + "/.well-known/oauth-protected-resource/mcp";

    // --- Discovery ---

    [Fact]
    public async Task The_resource_document_is_the_same_at_both_addresses_and_names_the_canonical_resource()
    {
        await using var a = await OAuthApi.NewAsync(server);

        using var inserted = await a.Http.GetAsync(OAuthPaths.ProtectedResourceMetadata);
        using var root = await a.Http.GetAsync(OAuthPaths.ProtectedResourceMetadataRoot);
        var bytes = await inserted.Content.ReadAsByteArrayAsync();

        Assert.Equal(HttpStatusCode.OK, inserted.StatusCode);
        Assert.Equal(bytes, await root.Content.ReadAsByteArrayAsync());
        var document = JsonDocument.Parse(bytes).RootElement;
        Assert.Equal(OAuthApi.Resource, document.GetProperty("resource").GetString());
        Assert.Equal(new[] { OAuthApi.PublicUrl }, Strings(document, "authorization_servers"));
        Assert.Equal(new[] { "read" }, Strings(document, "scopes_supported"));
        Assert.Equal(new[] { "header" }, Strings(document, "bearer_methods_supported"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task The_server_document_lists_self_registration_only_while_it_is_on(bool registration)
    {
        await using var a = await OAuthApi.NewAsync(server, dynamicRegistration: registration);

        using var response = await a.Http.GetAsync(OAuthPaths.AuthorizationServerMetadata);
        var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(OAuthApi.PublicUrl, document.GetProperty("issuer").GetString());
        Assert.Equal(OAuthApi.PublicUrl + "/oauth/authorize", document.GetProperty("authorization_endpoint").GetString());
        Assert.Equal(OAuthApi.PublicUrl + "/oauth/token", document.GetProperty("token_endpoint").GetString());
        Assert.Equal(OAuthApi.PublicUrl + "/oauth/revoke", document.GetProperty("revocation_endpoint").GetString());
        Assert.Equal(registration, document.TryGetProperty("registration_endpoint", out _));
        Assert.Equal(new[] { "S256" }, Strings(document, "code_challenge_methods_supported"));
        Assert.Equal(new[] { "none" }, Strings(document, "token_endpoint_auth_methods_supported"));
        Assert.Equal(new[] { "authorization_code", "refresh_token" }, Strings(document, "grant_types_supported"));
        Assert.Equal(new[] { "code" }, Strings(document, "response_types_supported"));
        Assert.True(document.GetProperty("authorization_response_iss_parameter_supported").GetBoolean());
        Assert.False(document.GetProperty("client_id_metadata_document_supported").GetBoolean());

        // With self-registration off, its path answers as any unmapped path.
        if (!registration)
        {
            using var register = await a.RegisterRawAsync();
            await OAuthHttpOffTests.AssertPlain401Async(register, "POST /oauth/register");
        }
    }

    [Fact]
    public async Task Every_flow_response_is_nosniff_and_no_store()
    {
        await using var a = await OAuthApi.NewAsync(server);
        var client = await a.RegisterAsync();
        var (code, verifier) = await a.ApproveAsync(a.World.Alice, client);

        var responses = new List<(string What, HttpResponseMessage Response)>
        {
            ("resource document", await a.Http.GetAsync(OAuthPaths.ProtectedResourceMetadata)),
            ("resource document, root", await a.Http.GetAsync(OAuthPaths.ProtectedResourceMetadataRoot)),
            ("server document", await a.Http.GetAsync(OAuthPaths.AuthorizationServerMetadata)),
            ("registration", await a.RegisterRawAsync()),
            ("refused registration", await a.PostTextAsync(OAuthPaths.Register, "{}")),
            ("authorize", await a.Http.GetAsync(OAuthPaths.Authorize + "?client_id=x")),
            ("token", await a.ExchangeAsync(client, code, verifier)),
            ("refused token", await a.ExchangeAsync(client, code, verifier)),
            ("revoke", await a.FormAsync(OAuthPaths.Revoke, [("client_id", client), ("token", "nothing")])),
            ("refused revoke", await a.FormAsync(OAuthPaths.Revoke, [("token", "nothing")])),
        };

        foreach (var (what, response) in responses)
        {
            using (response)
            {
                Assert.True(response.Headers.TryGetValues("X-Content-Type-Options", out var sniff) && sniff.Single() == "nosniff", what);
                Assert.True(response.Headers.CacheControl?.NoStore == true, what);
            }
        }
    }

    // --- /mcp ---

    [Fact]
    public async Task While_on_a_401_from_mcp_names_the_resource_metadata_and_the_scope()
    {
        await using var a = await OAuthApi.NewAsync(server);

        using var none = await a.Http.SendAsync(OAuthApi.McpPost(null));
        using var bad = await a.Http.SendAsync(OAuthApi.McpPost("not-a-token"));
        using var forged = await a.Http.SendAsync(OAuthApi.McpPost(OAuthApi.ForgedAccessToken));
        using var elsewhere = await a.Http.SendAsync(Api.Request(HttpMethod.Post, "/api/search", new { query = "zeppelin" }));

        Assert.Equal($"Bearer resource_metadata=\"{ResourceMetadataUrl}\", scope=\"read\"", OAuthHttpOffTests.Challenge(none));
        Assert.Equal($"Bearer error=\"invalid_token\", resource_metadata=\"{ResourceMetadataUrl}\", scope=\"read\"", OAuthHttpOffTests.Challenge(bad));
        Assert.Equal(OAuthHttpOffTests.Challenge(bad), OAuthHttpOffTests.Challenge(forged));
        Assert.Equal(OAuthApi.RefusalBody, await forged.Content.ReadAsStringAsync());
        // Anywhere but /mcp the challenge is as it always was.
        await OAuthHttpOffTests.AssertPlain401Async(elsewhere, "/api/search");
    }

    [Fact]
    public async Task While_on_every_service_of_the_flow_resolves()
    {
        await using var a = await OAuthApi.NewAsync(server);
        var services = a.World.Host.Services;

        Assert.Equal(OAuthApi.PublicUrl, services.GetService<OAuthDeployment>()?.PublicUrl);
        Assert.NotNull(services.GetService<OAuthThrottles>());
        Assert.NotNull(services.GetService<IOAuthHealth>());
        using var scope = services.CreateScope();
        Assert.NotNull(scope.ServiceProvider.GetService<OAuthTokenService>());
        Assert.NotNull(scope.ServiceProvider.GetService<IOAuthClients>());
        Assert.NotNull(scope.ServiceProvider.GetService<IOAuthConsent>());
        Assert.NotNull(scope.ServiceProvider.GetService<IOAuthGrants>());
    }

    [Fact]
    public async Task A_search_through_an_access_token_runs_as_its_agent_with_the_grant_in_the_audit_label()
    {
        await using var a = await OAuthApi.NewAsync(server);
        var client = await a.RegisterAsync();
        var c = await a.ConnectAsync(a.World.Alice, client);

        await using var mcp = await OAuthApi.McpAsync(a.World.Host, c.Access);
        Assert.Equal("premagentic", mcp.ServerInfo.Name);
        var result = await mcp.CallToolAsync("search_knowledge", new Dictionary<string, object?> { ["query"] = "oauth zeppelin", ["topK"] = 10 });
        Assert.False(result.IsError ?? false);
        // Alice's documents, and never one only the report bot may read.
        var text = string.Concat(result.Content.OfType<TextContentBlock>().Select(b => b.Text));
        Assert.Contains(ApiWorld.Plan, text);
        Assert.DoesNotContain(ApiWorld.AuditLog, text);

        await using var cmd = a.World.Db.DataSource.CreateCommand(
            "SELECT caller_agent_id, access_label FROM prem_config.retrieval_event WHERE query = 'oauth zeppelin'");
        await using var reader = await cmd.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(c.AgentId, reader.GetGuid(0));
        Assert.Contains($"token:oauth:{c.GrantId}:", reader.GetString(1));
    }

    [Fact]
    public async Task Each_kind_of_token_works_only_where_it_belongs()
    {
        await using var a = await OAuthApi.NewAsync(server);
        var client = await a.RegisterAsync();
        var c = await a.ConnectAsync(a.World.Alice, client);

        // At /mcp: the access token and an agent token; never a refresh token.
        await using (var access = await OAuthApi.McpAsync(a.World.Host, c.Access)) Assert.Equal(2, (await access.ListToolsAsync()).Count);
        await using (var agent = await OAuthApi.McpAsync(a.World.Host, a.World.AssistantToken)) Assert.Equal(2, (await agent.ListToolsAsync()).Count);
        using (var refresh = await a.Http.SendAsync(OAuthApi.McpPost(c.Refresh))) Assert.Equal(HttpStatusCode.Unauthorized, refresh.StatusCode);

        // At the API: an agent token only.
        using (var search = await Api.SearchAsync(a.Http, bearer: c.Access)) Assert.Equal(HttpStatusCode.Unauthorized, search.StatusCode);
        using (var search = await Api.SearchAsync(a.Http, bearer: c.Refresh)) Assert.Equal(HttpStatusCode.Unauthorized, search.StatusCode);
        using (var search = await Api.SearchAsync(a.Http, bearer: a.World.AssistantToken)) Assert.Equal(HttpStatusCode.OK, search.StatusCode);

        // At the token endpoint, as a refresh token: the refresh token only.
        foreach (var wrong in new[] { c.Access, a.World.AssistantToken })
        {
            using var response = await a.RefreshAsync(client, wrong);
            Assert.Equal("""{"error":"invalid_grant"}""", await response.Content.ReadAsStringAsync());
        }
        using (var right = await a.RefreshAsync(client, c.Refresh)) Assert.Equal(HttpStatusCode.OK, right.StatusCode);
    }

    [Fact]
    public async Task The_support_bundle_carries_the_flow_while_its_flag_is_on()
    {
        await using var a = await OAuthApi.NewAsync(server);
        await a.RegisterAsync("Waiting Tool");
        var client = await a.RegisterAsync();
        await a.ConnectAsync(a.World.Alice, client);
        var carol = await Api.SessionAsync(a.Http, "carol", ApiWorld.CarolPassword);

        using var response = await a.Http.SendAsync(Api.Request(HttpMethod.Get, "/portal/health/support-bundle.json", session: carol));
        var oauth = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("oauth");

        Assert.Equal(OAuthApi.PublicUrl, oauth.GetProperty("public_url").GetString());
        Assert.Equal(2, oauth.GetProperty("clients").GetInt32());
        Assert.Equal(1, oauth.GetProperty("pending_clients").GetInt32());
        Assert.Equal(OAuthSettings.MaxPendingClientsDefault, oauth.GetProperty("max_pending_clients").GetInt32());
        Assert.Equal(1, oauth.GetProperty("live_grants").GetInt32());
    }

    // --- Self-registration ---

    [Fact]
    public async Task A_registration_answer_carries_no_secret_and_says_the_client_authenticates_with_nothing()
    {
        await using var a = await OAuthApi.NewAsync(server);

        using var response = await a.RegisterRawAsync();
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.StartsWith(OAuthPrefixes.ClientId, body.GetProperty("client_id").GetString());
        Assert.Equal("none", body.GetProperty("token_endpoint_auth_method").GetString());
        Assert.False(body.TryGetProperty("client_secret", out _));
    }

    [Fact]
    public async Task A_registration_is_json_of_at_most_16_KB_and_a_refused_one_records_nothing()
    {
        await using var a = await OAuthApi.NewAsync(server);
        var body = JsonSerializer.Serialize(new { client_name = "Desk Tool", redirect_uris = new[] { OAuthApi.Loopback } });

        using var text = await a.PostTextAsync(OAuthPaths.Register, body, "text/plain");
        using var large = await a.PostTextAsync(OAuthPaths.Register,
            JsonSerializer.Serialize(new { client_name = "Desk Tool", redirect_uris = new[] { OAuthApi.Loopback }, padding = new string('x', 17_000) }));

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, text.StatusCode);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, large.StatusCode);
        Assert.Equal(0, await a.CountAsync("SELECT count(*) FROM prem_config.oauth_client"));
        Assert.Equal(0, await a.CountAsync("SELECT count(*) FROM prem_config.admin_event WHERE kind = 'oauth.client.register'"));

        // The control: the same body as JSON registers.
        using var json = await a.PostTextAsync(OAuthPaths.Register, body);
        Assert.Equal(HttpStatusCode.Created, json.StatusCode);
    }

    [Fact]
    public async Task One_address_is_held_to_its_registrations_an_hour_and_the_operator_is_warned_once_a_minute()
    {
        var logs = new List<string>();
        await using var a = await OAuthApi.NewAsync(server, options: new ApiHostOptions { Logs = logs },
            settings: (OAuthSettings.RegistrationsPerHour, "2"));

        await a.RegisterAsync(address: "10.0.0.9");
        await a.RegisterAsync(address: "10.0.0.9");
        using var third = await a.RegisterRawAsync(address: "10.0.0.9");
        using var fourth = await a.RegisterRawAsync(address: "10.0.0.9");

        Assert.Equal(HttpStatusCode.TooManyRequests, third.StatusCode);
        Assert.NotNull(third.Headers.RetryAfter);
        var body = JsonDocument.Parse(await third.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("temporarily_unavailable", body.GetProperty("error").GetString());
        Assert.Equal(OAuthClients.PendingLimit, body.GetProperty("error_description").GetString());
        Assert.Equal(HttpStatusCode.TooManyRequests, fourth.StatusCode);
        string[] warnings;
        lock (logs) warnings = logs.Where(l => l.StartsWith("Warning", StringComparison.Ordinal)
                                                && l.Contains(OAuthSettings.RegistrationsPerHour + " is 2", StringComparison.Ordinal)).ToArray();
        Assert.Single(warnings);

        // Another address is not held, and an hour later neither is this one.
        await a.RegisterAsync(address: "10.0.0.10");
        a.World.Clock.Now += TimeSpan.FromMinutes(61);
        await a.RegisterAsync(address: "10.0.0.9");
    }

    // --- The authorization forwarder ---

    [Fact]
    public async Task Authorize_forwards_every_parameter_escaped_again_with_repeats_kept()
    {
        await using var a = await OAuthApi.NewAsync(server);

        using var response = await a.Http.GetAsync(
            "/oauth/authorize?client_id=prem_cli_1&redirect_uri=http://127.0.0.1:5000/cb&resource=https://prem.test:8443/mcp&state=a%20b&state=c");

        Assert.Equal(HttpStatusCode.SeeOther, response.StatusCode);
        var location = response.Headers.Location!.OriginalString;
        Assert.Equal(
            "/portal/oauth/consent?client_id=prem_cli_1&redirect_uri=http%3A%2F%2F127.0.0.1%3A5000%2Fcb" +
            "&resource=https%3A%2F%2Fprem.test%3A8443%2Fmcp&state=a%20b&state=c",
            location);
        // So the sign-in page takes it as a place to come back to, where the raw
        // request would have been turned away to the portal's front page.
        Assert.Equal(location, SignInPage.SafeReturn(location));
        Assert.Equal("/portal", SignInPage.SafeReturn(
            "/portal/oauth/consent?client_id=prem_cli_1&redirect_uri=http://127.0.0.1:5000/cb"));
    }

    [Fact]
    public async Task An_authorize_request_too_long_to_survive_sign_in_is_a_plain_400()
    {
        await using var a = await OAuthApi.NewAsync(server);

        // Each ':' is three characters escaped once and five escaped twice.
        using var fits = await a.Http.GetAsync("/oauth/authorize?state=" + new string(':', 1_500));
        using var tooLong = await a.Http.GetAsync("/oauth/authorize?state=" + new string(':', 1_700));

        Assert.Equal(HttpStatusCode.SeeOther, fits.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);
        Assert.Equal("text/plain; charset=utf-8", tooLong.Content.Headers.ContentType?.ToString());
        Assert.Equal("This authorization request is too long.", await tooLong.Content.ReadAsStringAsync());
    }

    // --- The token endpoint ---

    [Fact]
    public async Task The_token_endpoint_takes_a_small_form_only_and_counts_none_of_its_refusals_of_shape()
    {
        await using var a = await OAuthApi.NewAsync(server);
        var client = await a.RegisterAsync();
        const string from = "10.0.0.3";

        // A form a client would send, labeled JSON: only its media type can refuse it here.
        using var json = await a.PostTextAsync(OAuthPaths.Token, $"grant_type=refresh_token&client_id={client}&refresh_token=x", address: from);
        using var large = await a.FormAsync(OAuthPaths.Token, [("client_id", client), ("padding", new string('x', 4_000)), ("more", new string('y', 4_300))], from);
        using var many = await a.FormAsync(OAuthPaths.Token, Enumerable.Range(0, 17).Select(i => ($"k{i}", "v")), from);
        using var twice = await a.FormAsync(OAuthPaths.Token, [("grant_type", "refresh_token"), ("client_id", client), ("client_id", client)], from);

        Assert.Equal((HttpStatusCode.BadRequest, "The request is sent as application/x-www-form-urlencoded."),
            (json.StatusCode, await DescriptionAsync(json)));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, large.StatusCode);
        Assert.Equal((HttpStatusCode.BadRequest, "invalid_request"), (many.StatusCode, await ErrorAsync(many)));
        Assert.Equal((HttpStatusCode.BadRequest, "A parameter is given more than once."), (twice.StatusCode, await DescriptionAsync(twice)));

        // Forty more refusals of shape, and the address is still not held.
        for (var i = 0; i < 40; i++)
        {
            using var again = await a.PostTextAsync(OAuthPaths.Token, "{}", address: from);
        }
        Assert.False(a.World.Host.Services.GetRequiredService<OAuthThrottles>().FailuresBlocked(IPAddress.Parse(from), out _));
    }

    [Theory]
    [InlineData("Basic cHJlbV9jbGk6c2VjcmV0", 401, "Basic realm=\"PremAgentic\"")]
    [InlineData("Bearer something", 401, "Bearer")]
    [InlineData("Digest username=\"sneaky\"", 400, "")]
    public async Task An_authorization_header_at_the_token_endpoint_is_refused_in_its_own_scheme(string header, int status, string challenge)
    {
        await using var a = await OAuthApi.NewAsync(server);
        var client = await a.RegisterAsync();

        // A client_secret beside the header: the header's rule answers.
        using var response = await a.FormAsync(OAuthPaths.Token,
            [("grant_type", "refresh_token"), ("client_id", client), ("client_secret", "s"), ("refresh_token", "x")], authorization: header);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(status, (int)response.StatusCode);
        Assert.Equal(challenge, OAuthHttpOffTests.Challenge(response));
        Assert.Equal(status == 401 ? "invalid_client" : "invalid_request", JsonDocument.Parse(body).RootElement.GetProperty("error").GetString());
        Assert.DoesNotContain("sneaky", body);
        Assert.DoesNotContain("Digest", body);
    }

    [Fact]
    public async Task A_client_secret_in_the_form_with_no_header_is_invalid_client()
    {
        await using var a = await OAuthApi.NewAsync(server);
        var client = await a.RegisterAsync();

        using var response = await a.FormAsync(OAuthPaths.Token,
            [("grant_type", "refresh_token"), ("client_id", client), ("client_secret", "s"), ("refresh_token", "x")]);

        Assert.Equal((HttpStatusCode.BadRequest, "invalid_client"), (response.StatusCode, await ErrorAsync(response)));
    }

    // --- The throttle ---

    [Fact]
    public async Task Thirty_unmatched_failures_then_a_valid_refresh_and_a_valid_exchange_both_succeed()
    {
        var logs = new List<string>();
        await using var a = await OAuthApi.NewAsync(server, options: new ApiHostOptions { Logs = logs });
        var client = await a.RegisterAsync();
        const string from = "10.0.0.7";
        var c = await a.ConnectAsync(a.World.Alice, client, from);
        var (code, verifier) = await a.ApproveAsync(a.World.Bob, client);

        for (var i = 0; i < 30; i++)
        {
            using var unknown = await a.ExchangeAsync(client, UnknownCode(), verifier, from);
            Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
        }

        // Judged before the throttle: a success is answered whatever the count.
        using (var refresh = await a.RefreshAsync(client, c.Refresh, from)) Assert.Equal(HttpStatusCode.OK, refresh.StatusCode);
        using (var exchange = await a.ExchangeAsync(client, code, verifier, from)) Assert.Equal(HttpStatusCode.OK, exchange.StatusCode);

        // The next failures wait, and the operator hears of it once a minute.
        using (var held = await a.ExchangeAsync(client, UnknownCode(), verifier, from))
        {
            Assert.Equal(HttpStatusCode.TooManyRequests, held.StatusCode);
            Assert.NotNull(held.Headers.RetryAfter);
            Assert.Equal("temporarily_unavailable", await ErrorAsync(held));
        }
        using (var held = await a.ExchangeAsync(client, UnknownCode(), verifier, from)) Assert.Equal(HttpStatusCode.TooManyRequests, held.StatusCode);
        lock (logs) Assert.Single(logs, l => l.Contains("too many failed requests from " + from, StringComparison.Ordinal));

        // Another address is not held.
        using var other = await a.ExchangeAsync(client, UnknownCode(), verifier, "10.0.0.8");
        Assert.Equal(HttpStatusCode.BadRequest, other.StatusCode);
    }

    [Fact]
    public async Task Refusals_of_values_that_matched_a_row_do_not_count()
    {
        await using var a = await OAuthApi.NewAsync(server);
        var client = await a.RegisterAsync();
        const string from = "10.0.0.7";
        var c = await a.ConnectAsync(a.World.Alice, client, from);
        await new OAuthGrants(a.World.Db, a.World.Tenant, a.Deployment, a.World.Clock)
            .RevokeAsync(new AdminActor("portal", null, a.World.Carol.Id), c.GrantId, default);

        for (var i = 0; i < 31; i++)
        {
            using var dead = await a.RefreshAsync(client, c.Refresh, from);
            Assert.Equal(HttpStatusCode.BadRequest, dead.StatusCode);
        }

        // A success is answered whatever the count, so a failure is what shows
        // the address was never held: it is judged, not answered 429.
        Assert.False(a.World.Host.Services.GetRequiredService<OAuthThrottles>().FailuresBlocked(IPAddress.Parse(from), out _));
        using var failure = await a.ExchangeAsync(client, UnknownCode(), "v", from);
        Assert.Equal(HttpStatusCode.BadRequest, failure.StatusCode);
        var (code, verifier) = await a.ApproveAsync(a.World.Bob, client);
        using var exchange = await a.ExchangeAsync(client, code, verifier, from);
        Assert.Equal(HttpStatusCode.OK, exchange.StatusCode);
    }

    [Fact]
    public async Task A_held_address_that_brings_back_a_used_refresh_token_still_ends_the_grant()
    {
        await using var a = await OAuthApi.NewAsync(server);
        var client = await a.RegisterAsync();
        const string from = "10.0.0.7";
        var c = await a.ConnectAsync(a.World.Alice, client, from);
        using (var rotate = await a.RefreshAsync(client, c.Refresh, from)) Assert.Equal(HttpStatusCode.OK, rotate.StatusCode);
        for (var i = 0; i < 30; i++)
        {
            using var unknown = await a.ExchangeAsync(client, UnknownCode(), "v", from);
        }

        using var reused = await a.RefreshAsync(client, c.Refresh, from);

        Assert.Equal(HttpStatusCode.TooManyRequests, reused.StatusCode);
        Assert.Equal(OAuthStore.RefreshTokenReused, await a.ReasonAsync(c.GrantId));
    }

    // --- Revocation ---

    [Fact]
    public async Task A_client_revoking_its_refresh_token_ends_the_grant_and_an_agent_token_revokes_nothing()
    {
        await using var a = await OAuthApi.NewAsync(server);
        var client = await a.RegisterAsync();
        var c = await a.ConnectAsync(a.World.Alice, client);

        using (var agent = await a.FormAsync(OAuthPaths.Revoke, [("client_id", client), ("token", a.World.AssistantToken)]))
            Assert.Equal(HttpStatusCode.OK, agent.StatusCode);
        using (var search = await Api.SearchAsync(a.Http, bearer: a.World.AssistantToken)) Assert.Equal(HttpStatusCode.OK, search.StatusCode);

        using (var revoke = await a.FormAsync(OAuthPaths.Revoke, [("client_id", client), ("token", c.Refresh), ("token_type_hint", "access_token")]))
            Assert.Equal(HttpStatusCode.OK, revoke.StatusCode);
        Assert.Equal(OAuthStore.ClientRevoke, await a.ReasonAsync(c.GrantId));
        using (var call = await a.Http.SendAsync(OAuthApi.McpPost(c.Access))) Assert.Equal(HttpStatusCode.Unauthorized, call.StatusCode);

        using var noClient = await a.FormAsync(OAuthPaths.Revoke, [("token", c.Refresh)]);
        using var unknownClient = await a.FormAsync(OAuthPaths.Revoke, [("client_id", "prem_cli_000000000000000000000000"), ("token", c.Refresh)]);
        Assert.Equal((HttpStatusCode.BadRequest, "invalid_request"), (noClient.StatusCode, await ErrorAsync(noClient)));
        Assert.Equal((HttpStatusCode.BadRequest, "invalid_client"), (unknownClient.StatusCode, await ErrorAsync(unknownClient)));
    }

    // --- The operator's log ---

    [Fact]
    public async Task No_log_line_holds_a_token_a_code_or_a_verifier()
    {
        var logs = new List<string>();
        await using var a = await OAuthApi.NewAsync(server, options: new ApiHostOptions { Logs = logs });
        var client = await a.RegisterAsync();

        // The framework's request lines, which carry a request's whole query,
        // are off for every provider the API logs to, and a warning of the
        // same category is not: the level, not the category, is what is set.
        var requestLines = a.World.Host.Services.GetRequiredService<ILoggerFactory>()
            .CreateLogger("Microsoft.AspNetCore.Hosting.Diagnostics");
        Assert.False(requestLines.IsEnabled(LogLevel.Information));
        Assert.True(requestLines.IsEnabled(LogLevel.Warning));

        // A real authorize request, as the person's browser sends it, then the
        // consent page it is forwarded to, which sends a signed-out browser on
        // to sign in with the whole address to come back to. The query carries
        // the client's state and challenge.
        var state = OAuthWorld.Base64Url(RandomNumberGenerator.GetBytes(24));
        var (_, challenge) = OAuthWorld.Pkce();
        using (var authorize = await a.Http.GetAsync(
                   $"{OAuthPaths.Authorize}?response_type=code&client_id={client}&redirect_uri={Uri.EscapeDataString(OAuthApi.LoopbackOnAPort)}" +
                   $"&code_challenge={challenge}&code_challenge_method=S256&resource={Uri.EscapeDataString(OAuthApi.Resource)}&state={state}"))
        {
            Assert.Equal(HttpStatusCode.SeeOther, authorize.StatusCode);
            var consent = authorize.Headers.Location!.OriginalString;
            Assert.Contains(state, consent);
            using var signIn = await a.Http.GetAsync(consent);
            Assert.Equal(HttpStatusCode.SeeOther, signIn.StatusCode);
            Assert.Contains(Uri.EscapeDataString(challenge), signIn.Headers.Location!.OriginalString);
        }

        var (code, verifier) = await a.ApproveAsync(a.World.Alice, client);

        using var exchange = await a.ExchangeAsync(client, code, verifier);
        var tokens = JsonDocument.Parse(await exchange.Content.ReadAsStringAsync()).RootElement;
        var (access, refresh) = (tokens.GetProperty("access_token").GetString()!, tokens.GetProperty("refresh_token").GetString()!);
        using (var mcp = await a.Http.SendAsync(OAuthApi.McpPost(access))) { }
        using (var replay = await a.ExchangeAsync(client, code, verifier)) Assert.Equal(HttpStatusCode.BadRequest, replay.StatusCode);
        using (var late = await a.RefreshAsync(client, refresh)) Assert.Equal(HttpStatusCode.BadRequest, late.StatusCode);
        using (var call = await a.Http.SendAsync(OAuthApi.McpPost(access))) Assert.Equal(HttpStatusCode.Unauthorized, call.StatusCode);
        using (var revoke = await a.FormAsync(OAuthPaths.Revoke, [("client_id", client), ("token", refresh)])) { }

        string all;
        lock (logs) all = string.Join("\n", logs);
        // The refusals were logged, and so was the authorize request by its
        // path, so the lines this looks through exist and cover it.
        Assert.Contains("code_replayed", all);
        Assert.Contains("access_grant_ended", all);
        Assert.Contains(OAuthPaths.Authorize, all);
        foreach (var secret in new[]
                 {
                     access, refresh, code, verifier, access[^43..], refresh[^43..],
                     state, challenge, Uri.EscapeDataString(state), Uri.EscapeDataString(challenge),
                 })
            Assert.DoesNotContain(secret, all, StringComparison.Ordinal);
    }

    // --- The certificate at start ---

    [Fact]
    public void The_certificate_warning_speaks_only_when_the_certificate_does_not_name_the_host()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=prem.test", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName("prem.test");
        request.CertificateExtensions.Add(names.Build());
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        var path = Path.Combine(Path.GetTempPath(), $"prem-{Guid.NewGuid():N}.crt");
        File.WriteAllText(path, certificate.ExportCertificatePem());
        try
        {
            Assert.Null(OAuthEndpoints.CertificateWarning(path, new OAuthDeployment("https://prem.test:8443", true, [])));
            var warning = OAuthEndpoints.CertificateWarning(path, new OAuthDeployment("https://other.test", true, []));
            Assert.NotNull(warning);
            Assert.Contains("'other.test'", warning);
            Assert.Null(OAuthEndpoints.CertificateWarning(null, new OAuthDeployment("https://other.test", true, [])));
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string UnknownCode() => OAuthWorld.Base64Url(RandomNumberGenerator.GetBytes(32));

    private static async Task<string?> ErrorAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("error").GetString();

    private static async Task<string?> DescriptionAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.TryGetProperty("error_description", out var d)
            ? d.GetString()
            : null;

    private static string[] Strings(JsonElement document, string name) =>
        document.GetProperty(name).EnumerateArray().Select(e => e.GetString()!).ToArray();
}

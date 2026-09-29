using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Premagentic.Api.Callers;
using Premagentic.Core.Identity;
using Premagentic.Core.Storage;

namespace Premagentic.Tests;

/// <summary>
/// The authorization flow while it is off, as every install starts: none of
/// its paths exists, none of its services resolves, <c>/mcp</c> answers as it
/// always did, and no request reads or writes one of its tables. Requires a
/// running Docker daemon.
/// </summary>
public sealed partial class OAuthHttpOffTests(DatastoreTestDatabase server) : IClassFixture<DatastoreTestDatabase>
{
    internal static readonly string[] FlowPaths =
    [
        OAuthPaths.ProtectedResourceMetadata, OAuthPaths.ProtectedResourceMetadataRoot, OAuthPaths.AuthorizationServerMetadata,
        OAuthPaths.Register, OAuthPaths.Authorize, OAuthPaths.Consent, OAuthPaths.Token, OAuthPaths.Revoke,
        OAuthPaths.Grants, OAuthPaths.Clients,
    ];

    /// <summary>A path never mapped, under the same first segment as <paramref name="path"/>.</summary>
    private static string ControlFor(string path) =>
        path.StartsWith("/portal/", StringComparison.Ordinal) ? "/portal/no-such-page"
        : path.StartsWith("/.well-known/", StringComparison.Ordinal) ? "/.well-known/no-such-document"
        : "/oauth/no-such-route";

    [Fact]
    public async Task While_off_every_path_of_the_flow_answers_as_a_path_that_was_never_mapped()
    {
        await using var w = await ApiWorld.NewAsync(server);
        using var http = w.Host.Client(true, new PassThrough());
        var alice = await Api.SessionAsync(http, "alice", ApiWorld.AlicePassword);

        var callers = new (string Name, Func<HttpMethod, string, HttpRequestMessage> Make)[]
        {
            ("nobody", (m, p) => Api.Request(m, p)),
            ("a bearer that does not resolve", (m, p) => Api.Request(m, p, bearer: "not-a-token")),
            ("a value in the access token's shape", (m, p) => Api.Request(m, p, bearer: OAuthApi.ForgedAccessToken)),
            ("a live agent token", (m, p) => Api.Request(m, p, bearer: w.AssistantToken)),
            ("a person signed in, without the anti-forgery token", (m, p) => Api.Request(m, p, session: alice, antiForgery: false)),
            ("a person signed in, with the anti-forgery token", (m, p) => Api.Request(m, p, session: alice)),
        };

        foreach (var path in FlowPaths)
        foreach (var method in new[] { HttpMethod.Get, HttpMethod.Post })
        foreach (var (name, make) in callers)
        {
            using var control = await http.SendAsync(make(method, ControlFor(path)));
            using var actual = await http.SendAsync(make(method, path));
            var expected = await DescribeAsync(control);
            var got = await DescribeAsync(actual);
            Assert.True(expected == got, $"{method} {path} from {name} answered\n{got}\nwhere the unmapped {ControlFor(path)} answered\n{expected}");
        }
    }

    [Fact]
    public async Task While_off_nobody_gets_anything_from_the_flow_but_the_plain_401()
    {
        await using var w = await ApiWorld.NewAsync(server);
        using var http = w.Host.Client(true, new PassThrough());

        foreach (var path in FlowPaths)
        foreach (var method in new[] { HttpMethod.Get, HttpMethod.Post })
        {
            using var response = await http.SendAsync(Api.Request(method, path));
            await AssertPlain401Async(response, $"{method} {path}");
        }
    }

    [Fact]
    public async Task While_off_mcp_refuses_with_the_plain_challenge_whatever_bearer_comes()
    {
        await using var w = await ApiWorld.NewAsync(server);
        using var http = w.Host.Client(true, new PassThrough());

        foreach (var bearer in new[] { null, "not-a-token", OAuthApi.ForgedAccessToken })
        {
            using var response = await http.SendAsync(OAuthApi.McpPost(bearer));
            await AssertPlain401Async(response, $"/mcp with {(bearer is null ? "no token" : bearer[..8])}");
        }
    }

    [Fact]
    public async Task While_off_no_service_of_the_flow_resolves()
    {
        await using var w = await ApiWorld.NewAsync(server);

        AssertFlowServicesAbsent(w.Host.Services);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Flow_on_but_unusable_starts_off(bool badRedirectList)
    {
        var logs = new List<string>();
        await using var w = await ApiWorld.NewAsync(server, new ApiHostOptions { Logs = logs }, beforeStart: async (db, tenant) =>
        {
            var store = new SettingsStore(db, tenant);
            await store.SetAsync(OAuthSettings.Enabled, OAuthApi.Json("true"));
            if (badRedirectList)
            {
                await store.SetAsync(OAuthSettings.PublicUrl, OAuthApi.Json($"\"{OAuthApi.PublicUrl}\""));
                // Straight to the store, past the check 'prem settings set' runs.
                await store.SetAsync(OAuthSettings.DynamicRedirectUris, OAuthApi.Json("""["http://app.example/cb"]"""));
            }
        });
        using var http = w.Host.Client(true, new PassThrough());

        AssertFlowServicesAbsent(w.Host.Services);
        foreach (var path in FlowPaths)
        foreach (var method in new[] { HttpMethod.Get, HttpMethod.Post })
        {
            using var response = await http.SendAsync(Api.Request(method, path));
            await AssertPlain401Async(response, $"{method} {path}");
        }

        // One warning, naming both remedies, however many requests came after it.
        string[] warnings;
        lock (logs) warnings = logs.Where(l => l.StartsWith("Warning", StringComparison.Ordinal)
                                                && l.Contains(OAuthSettings.Enabled + " is true", StringComparison.Ordinal)).ToArray();
        var warning = Assert.Single(warnings);
        Assert.Contains($"set {OAuthSettings.PublicUrl}", warning);
        Assert.Contains($"set {OAuthSettings.Enabled} to false", warning);
    }

    [Fact]
    public async Task While_off_no_request_reads_or_writes_a_table_of_the_flow()
    {
        // An assistant an administrator stored from its metadata document, as
        // 'prem oauth clients add --metadata-file' does whether the flow is on
        // or off; the session below names it at the flow's paths.
        var stored = OAuthClientDocument.Parse(
            System.Text.Encoding.UTF8.GetBytes(OAuthClientDocumentReadTests.Document()), "https://app.example/client.json");
        var w = await ApiWorld.NewAsync(server, beforeStart: (db, tenant) =>
            new OAuthClients(db, tenant).AddFromDocumentAsync(new Premagentic.Core.Admin.AdminActor("cli", "test-account"), stored, null, null, default));
        var (connection, clock, bot) = (w.ConnectionString, w.Clock, w.BotToken);
        // Closed before the first count, so the counts compare only what the session below does.
        await w.DisposeAsync();
        var before = await OAuthTableCounts.ReadWhenAloneAsync(connection);

        await using (var host = ApiTestHost.Start(connection, clock, new SeededEmbeddingProvider()))
        {
            AssertFlowServicesAbsent(host.Services);
            using var http = host.Client(true, new PassThrough());

            // The stored assistant names itself at the flow's paths, which do not exist while it is off.
            using (var authorize = await http.SendAsync(Api.Request(HttpMethod.Get,
                       $"/oauth/authorize?response_type=code&client_id={Uri.EscapeDataString(stored.ClientId)}&code_challenge_method=S256&state=s1")))
                Assert.Equal(HttpStatusCode.Unauthorized, authorize.StatusCode);
            using (var token = await http.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/oauth/token")
                   {
                       Content = new FormUrlEncodedContent(
                           [new("grant_type", "authorization_code"), new("client_id", stored.ClientId), new("code", "prem_cod_0123")]),
                   }))
                Assert.Equal(HttpStatusCode.Unauthorized, token.StatusCode);

            // A person signs in and searches.
            var alice = await Api.SessionAsync(http, "alice", ApiWorld.AlicePassword);
            using (var search = await Api.SearchAsync(http, session: alice)) Assert.Equal(HttpStatusCode.OK, search.StatusCode);

            // An agent calls over MCP; a value in the access token's shape is refused there.
            await using (var client = await OAuthApi.McpAsync(host, bot)) Assert.Equal(2, (await client.ListToolsAsync()).Count);
            using (var forged = await http.SendAsync(OAuthApi.McpPost(OAuthApi.ForgedAccessToken)))
                Assert.Equal(HttpStatusCode.Unauthorized, forged.StatusCode);

            // A connect on the connect page, which counts the person's bound, then a call with the token it showed.
            using (var connect = await PostFormAsync(http, alice, "/portal/connect",
                       ("name", "alice-desk"), ("kind", "coding-tool"), ("model", "hosted"), ("vendor", "Invented Models")))
            {
                Assert.Equal(HttpStatusCode.OK, connect.StatusCode);
                var token = AgentToken().Match(await connect.Content.ReadAsStringAsync()).Value;
                await using var desk = await OAuthApi.McpAsync(host, token);
                Assert.Equal(2, (await desk.ListToolsAsync()).Count);
            }

            // An administrator takes the support bundle, which says nothing of the flow while its flag is off.
            var carol = await Api.SessionAsync(http, "carol", ApiWorld.CarolPassword);
            using (var bundle = await http.SendAsync(Api.Request(HttpMethod.Get, "/portal/health/support-bundle.json", session: carol)))
            {
                Assert.Equal(HttpStatusCode.OK, bundle.StatusCode);
                using var json = System.Text.Json.JsonDocument.Parse(await bundle.Content.ReadAsStringAsync());
                Assert.True(json.RootElement.TryGetProperty("sizes", out _));
                Assert.False(json.RootElement.TryGetProperty("oauth", out _));
            }

            // And disables a person and an agent, and sets a password.
            foreach (var (path, field) in new[]
                     {
                         ("/portal/users/bob/enabled", ("enabled", "off")),
                         ("/portal/agents/alice-desk/enabled", ("enabled", "off")),
                         ("/portal/users/alice/password", ("password", "alice picks a new long password")),
                     })
            {
                using var change = await PostFormAsync(http, carol, path, field);
                Assert.True(change.StatusCode == HttpStatusCode.Redirect, $"{path} answered {(int)change.StatusCode}");
            }
        }

        var after = await OAuthTableCounts.ReadWhenAloneAsync(connection);
        Assert.Equal(before, after);

        // The session did what it says, read after the count.
        await using var db = new PremagenticDatabase(connection);
        var tenant = await db.EnsureTenantAsync(ApiTestHost.TenantKey, "T");
        var identity = new IdentityStore(db, tenant, clock);
        Assert.True((await identity.FindUserByNameAsync("bob"))!.Disabled);
        Assert.True((await identity.FindAgentByNameAsync("alice-desk"))!.Disabled);
    }

    [Fact]
    public async Task While_on_the_same_count_sees_the_one_call_that_reads_an_access_token()
    {
        var a = await OAuthApi.NewAsync(server);
        var client = await a.RegisterAsync();
        var c = await a.ConnectAsync(a.World.Alice, client);
        var (connection, clock) = (a.World.ConnectionString, a.World.Clock);
        await a.DisposeAsync();
        var before = await OAuthTableCounts.ReadWhenAloneAsync(connection);

        // A host started with the flow on, and one client's calls with the access
        // token: nothing else here reads that table.
        await using (var host = ApiTestHost.Start(connection, clock, new SeededEmbeddingProvider()))
        await using (var mcp = await OAuthApi.McpAsync(host, c.Access))
            Assert.Equal(2, (await mcp.ListToolsAsync()).Count);

        var after = await OAuthTableCounts.ReadWhenAloneAsync(connection);
        Assert.True(after["oauth_access_token"].Reads > before["oauth_access_token"].Reads,
            $"the access token table was read {before["oauth_access_token"].Reads} times before the calls and {after["oauth_access_token"].Reads} after them");
    }

    internal static void AssertFlowServicesAbsent(IServiceProvider services)
    {
        Assert.Null(services.GetService<OAuthDeployment>());
        Assert.Null(services.GetService<OAuthThrottles>());
        Assert.Null(services.GetService<IOAuthHealth>());
        using var scope = services.CreateScope();
        Assert.Null(scope.ServiceProvider.GetService<OAuthTokenService>());
        Assert.Null(scope.ServiceProvider.GetService<OAuthClients>());
        Assert.Null(scope.ServiceProvider.GetService<IOAuthClients>());
        Assert.Null(scope.ServiceProvider.GetService<IOAuthConsent>());
        Assert.Null(scope.ServiceProvider.GetService<IOAuthGrants>());
    }

    internal static async Task AssertPlain401Async(HttpResponseMessage response, string what)
    {
        Assert.True(response.StatusCode == HttpStatusCode.Unauthorized, $"{what} answered {(int)response.StatusCode}");
        Assert.Equal("Bearer", Challenge(response));
        Assert.Equal("application/json; charset=utf-8", response.Content.Headers.ContentType?.ToString());
        Assert.Equal(OAuthApi.RefusalBody, await response.Content.ReadAsStringAsync());
    }

    internal static string Challenge(HttpResponseMessage response) =>
        response.Headers.TryGetValues("WWW-Authenticate", out var values) ? string.Join(", ", values) : "";

    /// <summary>Status, every header but Date, and the body.</summary>
    private static async Task<string> DescribeAsync(HttpResponseMessage response)
    {
        var headers = response.Headers.Concat(response.Content.Headers)
            .Where(h => !h.Key.Equals("Date", StringComparison.OrdinalIgnoreCase))
            .OrderBy(h => h.Key, StringComparer.OrdinalIgnoreCase)
            .Select(h => $"{h.Key}: {string.Join(", ", h.Value)}");
        return $"{(int)response.StatusCode}\n{string.Join("\n", headers)}\n{await response.Content.ReadAsStringAsync()}";
    }

    /// <summary>A form post the way a portal page sends it: the anti-forgery field and the portal's own origin.</summary>
    private static Task<HttpResponseMessage> PostFormAsync(HttpClient http, ApiSession session, string path, params (string Name, string Value)[] fields)
    {
        var request = Api.Request(HttpMethod.Post, path, session: session, antiForgery: false);
        request.Content = new FormUrlEncodedContent(fields.Append(("prem_antiforgery", session.AntiForgeryToken))
            .Select(f => new KeyValuePair<string, string>(f.Item1, f.Item2)));
        request.Headers.Add("Origin", PortalWorld.Origin);
        return http.SendAsync(request);
    }

    [GeneratedRegex("prem_agt_[0-9a-f]{24}_[A-Za-z0-9_-]{43}")]
    private static partial Regex AgentToken();
}

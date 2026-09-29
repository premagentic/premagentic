using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using Npgsql;
using Premagentic.Core.Identity;
using Premagentic.Core.Storage;

namespace Premagentic.Tests;

/// <summary>
/// The API with the authorization flow on, over <see cref="ApiWorld"/>'s people
/// and documents. The flow's settings are stored before the host starts, as an
/// administrator sets them before a restart. Approval goes through the consent
/// store, which is what the portal's consent page calls; everything else goes
/// over HTTP, from an address the test names.
/// </summary>
internal sealed class OAuthApi : IAsyncDisposable
{
    public const string PublicUrl = "https://prem.test:8443";
    public const string Resource = PublicUrl + "/mcp";
    public const string Loopback = "http://127.0.0.1/callback";
    public const string LoopbackOnAPort = "http://127.0.0.1:51004/callback";

    /// <summary>What a 401 body says, on every path, flow on or off.</summary>
    public const string RefusalBody = """{"error":"Sign in, or present an agent token."}""";

    private OAuthApi(ApiWorld world, OAuthDeployment deployment)
    {
        World = world;
        Deployment = deployment;
        Http = world.Host.Client(true, new PassThrough());
    }

    public ApiWorld World { get; }

    /// <summary>The flow as the host runs it, for the stores a test calls directly.</summary>
    public OAuthDeployment Deployment { get; }

    /// <summary>Follows no redirect and keeps no cookie.</summary>
    public HttpClient Http { get; }

    public static async Task<OAuthApi> NewAsync(
        DatastoreTestDatabase server, bool dynamicRegistration = true, ApiHostOptions? options = null,
        params (string Key, string Json)[] settings)
    {
        var world = await ApiWorld.NewAsync(server, options,
            beforeStart: (db, tenant) => TurnOnAsync(db, tenant, dynamicRegistration, settings));
        return new OAuthApi(world, new OAuthDeployment(PublicUrl, dynamicRegistration, []));
    }

    /// <summary>Stores what turns the flow on, and any other of its settings given, as raw JSON.</summary>
    public static async Task TurnOnAsync(PremagenticDatabase db, Guid tenant, bool dynamicRegistration = true,
        params (string Key, string Json)[] settings)
    {
        var store = new SettingsStore(db, tenant);
        await store.SetAsync(OAuthSettings.PublicUrl, Json($"\"{PublicUrl}\""));
        await store.SetAsync(OAuthSettings.Enabled, Json("true"));
        await store.SetAsync(OAuthSettings.DynamicRegistration, Json(dynamicRegistration ? "true" : "false"));
        foreach (var (key, json) in settings) await store.SetAsync(key, Json(json));
    }

    public static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

    public Task<HttpResponseMessage> PostTextAsync(string path, string body, string contentType = "application/json", string address = "10.0.0.1")
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new ByteArrayContent(Encoding.UTF8.GetBytes(body)) };
        request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
        request.Headers.Add(TestRemoteAddress.Header, address);
        return Http.SendAsync(request);
    }

    public Task<HttpResponseMessage> RegisterRawAsync(string name = "Desk Tool", string redirect = Loopback, string address = "10.0.0.1") =>
        PostTextAsync(OAuthPaths.Register, JsonSerializer.Serialize(new { client_name = name, redirect_uris = new[] { redirect } }), address: address);

    /// <summary>Registers a client that answers on a loopback address, as a desktop assistant does, and returns its id.</summary>
    public async Task<string> RegisterAsync(string name = "Desk Tool", string redirect = Loopback, string address = "10.0.0.1")
    {
        using var response = await RegisterRawAsync(name, redirect, address);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.Created, $"registration answered {(int)response.StatusCode}: {body}");
        return JsonDocument.Parse(body).RootElement.GetProperty("client_id").GetString()!;
    }

    /// <summary>A form post to the token or revocation endpoint, from <paramref name="address"/>.</summary>
    public Task<HttpResponseMessage> FormAsync(string path, IEnumerable<(string Name, string Value)> fields, string address = "10.0.0.2",
        string? authorization = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new FormUrlEncodedContent(fields.Select(f => new KeyValuePair<string, string>(f.Name, f.Value))),
        };
        request.Headers.Add(TestRemoteAddress.Header, address);
        if (authorization is not null) request.Headers.TryAddWithoutValidation("Authorization", authorization);
        return Http.SendAsync(request);
    }

    public Task<HttpResponseMessage> ExchangeAsync(string clientId, string code, string verifier, string address = "10.0.0.2") =>
        FormAsync(OAuthPaths.Token,
            [("grant_type", "authorization_code"), ("client_id", clientId), ("code", code), ("code_verifier", verifier)], address);

    public Task<HttpResponseMessage> RefreshAsync(string clientId, string refresh, string address = "10.0.0.2") =>
        FormAsync(OAuthPaths.Token, [("grant_type", "refresh_token"), ("client_id", clientId), ("refresh_token", refresh)], address);

    /// <summary>
    /// A person approves, through the consent store, a second after the last
    /// step (the clock is the test's). Returns the code and its verifier.
    /// </summary>
    public async Task<(string Code, string Verifier)> ApproveAsync(User who, string clientId, string redirect = LoopbackOnAPort)
    {
        var (verifier, challenge) = OAuthWorld.Pkce();
        World.Clock.Now += TimeSpan.FromSeconds(1);
        var parameters = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
        {
            ["response_type"] = ["code"], ["client_id"] = [clientId], ["redirect_uri"] = [redirect],
            ["code_challenge"] = [challenge], ["code_challenge_method"] = ["S256"], ["resource"] = [Resource], ["state"] = ["s1"],
        };
        var url = await new OAuthConsent(World.Db, World.Tenant, Deployment, World.Clock)
            .ApproveAsync(who.Id, parameters, ModelLocation.Hosted, "A Vendor", default);
        return (OAuthWorld.Query(url, "code"), verifier);
    }

    /// <summary>A person connects a client end to end: approve, then exchange the code over HTTP.</summary>
    public async Task<Connection> ConnectAsync(User who, string clientId, string address = "10.0.0.2")
    {
        var (code, verifier) = await ApproveAsync(who, clientId);
        using var response = await ExchangeAsync(clientId, code, verifier, address);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"exchange answered {(int)response.StatusCode}: {text}");
        var body = JsonDocument.Parse(text).RootElement;
        var grant = (await new OAuthGrants(World.Db, World.Tenant, Deployment, World.Clock).ListOwnAsync(who.Id, default))
            .First(g => g.ClientId == clientId);
        return new Connection(body.GetProperty("access_token").GetString()!, body.GetProperty("refresh_token").GetString()!, grant.GrantId, grant.AgentId);
    }

    public async Task<string?> ReasonAsync(string grantId)
    {
        await using var cmd = World.Db.DataSource.CreateCommand("SELECT revoked_reason FROM prem_config.oauth_grant WHERE id = @id");
        cmd.Parameters.AddWithValue("id", grantId);
        return await cmd.ExecuteScalarAsync() as string;
    }

    public async Task<long> CountAsync(string sql)
    {
        await using var cmd = World.Db.DataSource.CreateCommand(sql);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>An MCP client of <paramref name="host"/> that presents <paramref name="token"/>, spoken by the SDK's own client.</summary>
    public static async Task<McpClient> McpAsync(ApiTestHost host, string token)
    {
        var options = new HttpClientTransportOptions
        {
            Endpoint = new Uri("https://localhost/mcp"),
            AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = "Bearer " + token },
        };
        var transport = new HttpClientTransport(options, host.Client(), NullLoggerFactory.Instance, ownsHttpClient: true);
        return await McpClient.CreateAsync(transport, loggerFactory: NullLoggerFactory.Instance);
    }

    /// <summary>A JSON-RPC post to /mcp with the bearer given, or none, for the answers a client never gets past.</summary>
    public static HttpRequestMessage McpPost(string? bearer)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}""", Encoding.UTF8, "application/json"),
        };
        request.Headers.Accept.ParseAdd("application/json, text/event-stream");
        if (bearer is not null) request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + bearer);
        return request;
    }

    /// <summary>A value in the access token's shape that no server issued: the prefix, 24 hex, and 32 zero bytes in base64url.</summary>
    public static readonly string ForgedAccessToken = OAuthPrefixes.AccessToken + "0123456789abcdef01234567_" + new string('A', 43);

    public async ValueTask DisposeAsync()
    {
        Http.Dispose();
        await World.DisposeAsync();
    }
}

/// <summary>
/// What PostgreSQL counted on the flow's five tables, read only when nothing
/// else is connected to the database. A backend reports its counts before it
/// leaves <c>pg_stat_activity</c>, so once every other backend has left, a
/// count read in a fresh statement cannot be stale.
/// </summary>
internal static class OAuthTableCounts
{
    public static readonly string[] Tables = ["oauth_access_token", "oauth_client", "oauth_code", "oauth_grant", "oauth_refresh_token"];

    /// <summary>Reads (sequential and index scans), inserts and updates per table.</summary>
    public sealed record Counts(long Reads, long Inserts, long Updates);

    /// <summary>
    /// Waits, at most 30 seconds, until no other backend is connected to the
    /// database (the caller has closed every data source that touched it), then
    /// reads the counts. A connection that stays open fails the test rather
    /// than giving a count that could be stale.
    /// </summary>
    public static async Task<Dictionary<string, Counts>> ReadWhenAloneAsync(string connectionString)
    {
        var alone = new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false }.ConnectionString;
        await using (var wait = new NpgsqlConnection(alone))
        {
            await wait.OpenAsync();
            var waited = System.Diagnostics.Stopwatch.StartNew();
            while (true)
            {
                await using var others = new NpgsqlCommand(
                    "SELECT count(*) FROM pg_stat_activity WHERE datname = current_database() AND pid <> pg_backend_pid()", wait);
                if ((long)(await others.ExecuteScalarAsync())! == 0) break;
                if (waited.Elapsed > TimeSpan.FromSeconds(30))
                    throw new Xunit.Sdk.XunitException("Another connection to the test database stayed open, so the counts could be stale.");
                await Task.Delay(50);
            }
        }

        await using var read = new NpgsqlConnection(alone);
        await read.OpenAsync();
        await using var cmd = new NpgsqlCommand("""
            SELECT relname, seq_scan + COALESCE(idx_scan, 0), n_tup_ins, n_tup_upd
            FROM pg_stat_user_tables WHERE schemaname = 'prem_config' AND relname LIKE 'oauth\_%'
            ORDER BY relname
            """, read);
        await using var reader = await cmd.ExecuteReaderAsync();
        var counts = new Dictionary<string, Counts>(StringComparer.Ordinal);
        while (await reader.ReadAsync())
            counts[reader.GetString(0)] = new Counts(reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3));
        Assert.Equal(Tables, counts.Keys.Order(StringComparer.Ordinal).ToArray());
        return counts;
    }
}

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Premagentic.Core.Admin;
using Premagentic.Core.Evaluation;
using Premagentic.Core.Identity;
using Premagentic.Core.Storage;

namespace Premagentic.Tests;

/// <summary>
/// The authorization flow's stores over one database, on a clock the test
/// sets: Alice and Bob sign in as people, Carol is an administrator, and the
/// deployment lists one https redirect a self-registered client may use.
/// </summary>
internal sealed class OAuthWorld : IAsyncDisposable
{
    public const string PublicUrl = "https://prem.test:8443";
    public const string Listed = "https://listed.example/cb";
    public const string Loopback = "http://127.0.0.1/callback";

    private OAuthWorld() { }

    public required PremagenticDatabase Db { get; init; }
    public required Guid Tenant { get; init; }
    public required TestClock Clock { get; init; }
    public required IdentityStore Identity { get; init; }
    public required User Alice { get; init; }
    public required User Bob { get; init; }
    public required User Carol { get; init; }
    public OAuthDeployment Oauth { get; set; } = new(PublicUrl, true, [Listed]);

    public OAuthClients Clients => new(Db, Tenant, Oauth, Clock);
    public OAuthConsent Consent => new(Db, Tenant, Oauth, Clock);
    public OAuthTokenService Tokens => new(Db, Tenant, Oauth, Clock);
    public OAuthGrants Grants => new(Db, Tenant, Oauth, Clock);
    public AdminActor Admin => new("portal", null, Carol.Id);

    public static async Task<OAuthWorld> NewAsync(DatastoreTestDatabase server, int bound = 10)
    {
        var db = new PremagenticDatabase(await server.CreateDatabaseAsync());
        await db.InitializeAsync();
        var tenant = await db.EnsureTenantAsync("t", "T");
        var clock = new TestClock(new DateTimeOffset(2026, 9, 25, 12, 0, 0, TimeSpan.Zero));
        var identity = new IdentityStore(db, tenant, clock);
        var world = new OAuthWorld
        {
            Db = db, Tenant = tenant, Clock = clock, Identity = identity,
            Alice = await identity.CreateUserAsync("alice", "Alice", Role.Member),
            Bob = await identity.CreateUserAsync("bob", "Bob", Role.Member),
            Carol = await identity.CreateUserAsync("carol", "Carol", Role.Administrator),
        };
        await world.SetAsync(AgentSettings.SelfServiceMax, bound);
        return world;
    }

    public Task SetAsync(string key, object value) =>
        new TuningSettingsStore(Db, Tenant).SetAsync(key, JsonSerializer.SerializeToElement(value), AdminActor.Cli());

    public async Task<OAuthEndpointAnswer> RegisterAsync(string body, string source = "10.0.0.1") =>
        await Clients.RegisterAsync(JsonDocument.Parse(body).RootElement.Clone(), source, default);

    /// <summary>Registers a client that answers at a loopback address, as a desktop assistant does, and returns its id.</summary>
    public async Task<string> RegisterLoopbackAsync(string name = "Desk Tool", string redirect = Loopback, string source = "10.0.0.1")
    {
        var answer = await RegisterAsync(JsonSerializer.Serialize(new { client_name = name, redirect_uris = new[] { redirect } }), source);
        Assert.True(answer.StatusCode == 201, $"registration answered {answer.StatusCode}: {JsonSerializer.Serialize(answer.Body)}");
        return (string)answer.Body["client_id"]!;
    }

    public static (string Verifier, string Challenge) Pkce()
    {
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        return (verifier, Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))));
    }

    public Dictionary<string, IReadOnlyList<string>> Params(string clientId, string? redirect, string challenge,
        string? resource = null, string? state = "s1", params (string Name, string Value)[] extra)
    {
        var p = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
        {
            ["response_type"] = ["code"],
            ["client_id"] = [clientId],
            ["code_challenge"] = [challenge],
            ["code_challenge_method"] = ["S256"],
            ["resource"] = [resource ?? Oauth.Resource],
        };
        if (redirect is not null) p["redirect_uri"] = [redirect];
        if (state is not null) p["state"] = [state];
        foreach (var (name, value) in extra) p[name] = [value];
        return p;
    }

    /// <summary>
    /// Approves a request, a second after the last step: the clock is set by
    /// the test, and two grants made at the same instant would leave "the
    /// latest" to the order of their random ids.
    /// </summary>
    public Task<string> ApproveAsync(User who, string clientId, string redirect, string challenge,
        ModelLocation location = ModelLocation.Hosted, string? vendor = "A Vendor")
    {
        Clock.Now += TimeSpan.FromSeconds(1);
        return Consent.ApproveAsync(who.Id, Params(clientId, redirect, challenge), location, location == ModelLocation.Hosted ? vendor : null, default);
    }

    public static string Query(string url, string name)
    {
        var query = url[(url.IndexOf('?') + 1)..];
        foreach (var pair in query.Split('&'))
        {
            var eq = pair.IndexOf('=');
            if (Uri.UnescapeDataString(pair[..eq]) == name) return Uri.UnescapeDataString(pair[(eq + 1)..]);
        }
        throw new Xunit.Sdk.XunitException($"'{url}' has no {name}.");
    }

    public Task<OAuthEndpointAnswer> ExchangeAsync(string clientId, string code, string verifier, params (string Name, string Value)[] extra)
    {
        var form = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["grant_type"] = "authorization_code", ["client_id"] = clientId, ["code"] = code, ["code_verifier"] = verifier,
        };
        foreach (var (name, value) in extra) form[name] = value;
        return Tokens.TokenAsync(form, default);
    }

    public Task<OAuthEndpointAnswer> RefreshAsync(string clientId, string refresh, params (string Name, string Value)[] extra)
    {
        var form = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["grant_type"] = "refresh_token", ["client_id"] = clientId, ["refresh_token"] = refresh,
        };
        foreach (var (name, value) in extra) form[name] = value;
        return Tokens.TokenAsync(form, default);
    }

    /// <summary>A person connects a client end to end: approve, then exchange the code. Returns the tokens and the grant.</summary>
    public async Task<Connection> ConnectAsync(User who, string clientId, string redirect = "http://127.0.0.1:51004/callback",
        ModelLocation location = ModelLocation.Hosted)
    {
        var (verifier, challenge) = Pkce();
        var url = await ApproveAsync(who, clientId, redirect, challenge, location);
        var answer = await ExchangeAsync(clientId, Query(url, "code"), verifier);
        Assert.True(answer.StatusCode == 200, $"exchange answered {answer.StatusCode}: {JsonSerializer.Serialize(answer.Body)}");
        var access = (string)answer.Body["access_token"]!;
        var grant = (await Grants.ListOwnAsync(who.Id, default)).First(g => g.ClientId == clientId);
        return new Connection(access, (string)answer.Body["refresh_token"]!, grant.GrantId, grant.AgentId);
    }

    /// <summary>What a call with this access token gets: refused by the flow's checks (null status), or the agent resolution's status.</summary>
    public async Task<CallerStatus?> CallAsync(string access)
    {
        var (checkedAccess, _) = await Tokens.CheckAccessAsync(access, default);
        if (checkedAccess is null) return null;
        return (await CallerAccess.ResolveOAuthAsync(Identity, checkedAccess)).Resolved.Status;
    }

    public async Task<long> RowsAsync(string kind)
    {
        await using var cmd = Db.DataSource.CreateCommand("SELECT count(*) FROM prem_config.admin_event WHERE kind = @kind");
        cmd.Parameters.AddWithValue("kind", kind);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    public async Task<string?> ReasonAsync(string grantId)
    {
        await using var cmd = Db.DataSource.CreateCommand("SELECT revoked_reason FROM prem_config.oauth_grant WHERE id = @id");
        cmd.Parameters.AddWithValue("id", grantId);
        return await cmd.ExecuteScalarAsync() as string;
    }

    public static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public ValueTask DisposeAsync() => Db.DisposeAsync();
}

internal sealed record Connection(string Access, string Refresh, string GrantId, Guid AgentId);

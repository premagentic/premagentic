using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Premagentic.Api;
using Premagentic.Api.Callers;
using Premagentic.Core;
using Premagentic.Core.Embeddings;
using Premagentic.Core.Identity;
using Premagentic.Core.Ingestion;
using Premagentic.Core.Security;
using Premagentic.Core.Security.Acl;
using Premagentic.Core.Sources;
using Premagentic.Core.Storage;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Premagentic.Tests;

/// <summary>What a test changes about the API it starts.</summary>
internal sealed class ApiHostOptions
{
    /// <summary>PREM_SIGN_IN_HEADER, or null for the trusted-header mode off.</summary>
    public string? SignInHeader { get; set; }

    /// <summary>PREM_ALLOW_HTTP_SIGN_IN=1.</summary>
    public bool AllowHttpSignIn { get; set; }

    /// <summary>PREM_PRINCIPAL_HEADER, which the API must refuse to start with.</summary>
    public string? RetiredPrincipalHeader { get; set; }

    public PasswordHasher? Hasher { get; set; }

    public SignInThrottleOptions? Throttle { get; set; }

    /// <summary>A search role for reads, as an installed deployment has; null reads through the database the host was given.</summary>
    public string? SearchRoleConnection { get; set; }

    /// <summary>Collects every line the host logs, as "level category: message"; null collects nothing.</summary>
    public List<string>? Logs { get; set; }

    /// <summary>
    /// Sign-in adapters asked after the two built in, standing in for what an
    /// extension would bring. For proving what the built-ins do and do not hand
    /// on to whatever sits behind them.
    /// </summary>
    public IReadOnlyList<Premagentic.Core.Identity.SignIn.ISignInAdapter>? ExtraSignInAdapters { get; set; }

    /// <summary>
    /// The principal mapper the host reads outside principals through,
    /// standing in for one an extension would bring. Null leaves the host with
    /// the one its extension host composed, which is none unless a test
    /// installs an extension that brings one.
    /// </summary>
    public Premagentic.Core.Identity.IPrincipalMapper? PrincipalMapper { get; set; }

    /// <summary>
    /// The readers the host registers, standing in for what an extension would
    /// bring. Null leaves the host with the built-in ones, which is what an
    /// installation with no extensions has.
    /// </summary>
    public Premagentic.Core.Ingestion.Readers.ReaderRegistry? Readers { get; set; }

    /// <summary>
    /// The extension host this process composed, or null to compose none, which
    /// is a different state from composing one that found nothing.
    /// </summary>
    public Premagentic.Core.Extensions.ExtensionHost? Extensions { get; set; }

    /// <summary>Whether the host sends HSTS, as if it held its own certificate; null leaves what the configuration says.</summary>
    public Premagentic.Api.Hosting.ApiResponseHeaderOptions? Headers { get; set; }

    /// <summary>
    /// Registrations applied last, over the host's own, for a test that stands
    /// something in for what the host would build, such as the authorization
    /// flow's services behind the portal's pages.
    /// </summary>
    public Action<IServiceCollection>? Services { get; set; }

    /// <summary>
    /// Serve on Kestrel over plain HTTP, on a loopback port the system picks,
    /// instead of in process: for a test of a bound the web server itself keeps,
    /// since the in-process test server holds no request body to an endpoint's
    /// size limit. A session then needs <see cref="AllowHttpSignIn"/>.
    /// </summary>
    public bool Kestrel { get; set; }
}

/// <summary>Writes every log line into a list the test reads.</summary>
internal sealed class ListLoggerProvider(List<string> lines) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new Logger(categoryName, lines);

    public void Dispose() { }

    private sealed class Logger(string category, List<string> lines) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (lines) lines.Add($"{logLevel} {category}: {formatter(state, exception)}");
        }
    }
}

/// <summary>
/// The Premagentic API in process, with its real Program, middleware and
/// endpoints, over one test's own database. The clock, the embedder and a few
/// policies are the only things replaced.
/// </summary>
internal sealed class ApiTestHost : IAsyncDisposable
{
    public const string TenantKey = "t";

    // Any type from the API assembly names its entry point. The test project
    // also references the CLI and the MCP server, and all three have a
    // top-level Program, so Program itself would be ambiguous here.
    private readonly WebApplicationFactory<ApiSettings> _factory;

    // The API's hosting checks that a database is configured before any service
    // can be replaced, and ends the process when none is. Every test host
    // replaces the database, so the development switch passes the check and
    // the development database is never connected to. The value is the same for
    // every test, so setting it for the whole process is safe.
    static ApiTestHost() => Environment.SetEnvironmentVariable(PremagenticDatabase.DevelopmentSwitch, "1");

    // Where Kestrel listens, for a host started with ApiHostOptions.Kestrel; null in process.
    private readonly Uri? _kestrel;

    private ApiTestHost(WebApplicationFactory<ApiSettings> factory, Uri? kestrel = null) => (_factory, _kestrel) = (factory, kestrel);

    public TestServer Server => _factory.Server;

    public IServiceProvider Services => _factory.Services;

    public static ApiTestHost Start(string connectionString, TimeProvider clock, IEmbeddingProvider embedder, ApiHostOptions? options = null)
    {
        options ??= new ApiHostOptions();
        var factory = new WebApplicationFactory<ApiSettings>().WithWebHostBuilder(web =>
        {
            // Every setting the API reads is set here, so nothing leaks in from
            // the environment of whoever runs the tests.
            web.UseSetting("PREM_TENANT_KEY", TenantKey);
            web.UseSetting("PREM_TENANT_NAME", "T");
            web.UseSetting("PREM_HEADING_PREFIX", "0");
            web.UseSetting(ApiSettings.SignInHeaderKey, options.SignInHeader ?? "");
            web.UseSetting(ApiSettings.AllowHttpSignInKey, options.AllowHttpSignIn ? "1" : "");
            web.UseSetting("PREM_PRINCIPAL_HEADER", options.RetiredPrincipalHeader ?? "");
            // On Kestrel, a loopback port the system picks, so two hosts, or a host
            // and anything else on the machine, never ask for the same one.
            if (options.Kestrel) web.UseSetting(WebHostDefaults.ServerUrlsKey, "http://127.0.0.1:0");
            web.ConfigureTestServices(services =>
            {
                services.RemoveAll<PremagenticDatabase>();
                services.AddSingleton(_ => new PremagenticDatabase(connectionString));
                services.RemoveAll<IEmbeddingProvider>();
                services.AddSingleton(embedder);
                services.RemoveAll<TimeProvider>();
                services.AddSingleton(clock);
                services.AddSingleton<IStartupFilter, TestRemoteAddress>();
                if (options.Hasher is { } hasher)
                {
                    services.RemoveAll<PasswordHasher>();
                    services.AddSingleton(hasher);
                }
                if (options.Throttle is { } throttle)
                {
                    services.RemoveAll<SignInThrottleOptions>();
                    services.AddSingleton(throttle);
                }
                if (options.SearchRoleConnection is { } searchRole)
                {
                    services.RemoveAll<SearchRole>();
                    services.AddSingleton(_ => new SearchRole(searchRole));
                }
                if (options.Logs is { } logs)
                    services.AddSingleton<ILoggerProvider>(new ListLoggerProvider(logs));
                if (options.Extensions is { } host)
                {
                    services.RemoveAll<Premagentic.Core.Extensions.ExtensionHost>();
                    services.AddSingleton(host);
                }
                if (options.Readers is { } readers)
                {
                    services.RemoveAll<Premagentic.Core.Ingestion.Readers.ReaderRegistry>();
                    services.AddSingleton(readers);
                }
                if (options.Headers is { } headers)
                {
                    services.RemoveAll<Premagentic.Api.Hosting.ApiResponseHeaderOptions>();
                    services.AddSingleton(headers);
                }
                if (options.PrincipalMapper is { } mapper)
                {
                    services.RemoveAll<Premagentic.Core.Identity.PrincipalMapping>();
                    services.AddScoped(sp => new Premagentic.Core.Identity.PrincipalMapping(
                        sp.GetRequiredService<PremagenticDatabase>(), sp.GetRequiredService<Deployment>().TenantId, mapper));
                }
                if (options.ExtraSignInAdapters is { } extraAdapters)
                {
                    services.RemoveAll<SignInAdapters>();
                    services.AddScoped(sp => new SignInAdapters(
                        sp.GetRequiredService<ApiSettings>(), sp.GetRequiredService<SessionStore>(), extraAdapters));
                }
                options.Services?.Invoke(services);
            });
        });
        if (options.Kestrel)
        {
            factory.UseKestrel();
            factory.StartServer();
            var addresses = factory.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>()
                .Features.Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>()!.Addresses;
            return new ApiTestHost(factory, new Uri(addresses.First()));
        }
        var host = new ApiTestHost(factory);
        _ = factory.Server; // start now, so a startup failure surfaces here
        return host;
    }

    /// <summary>
    /// A client that keeps no cookies of its own: every test says exactly what it
    /// sends. On Kestrel it speaks plain HTTP to the port the host listens on,
    /// whatever <paramref name="https"/> says, and waits up to 30 s for a
    /// <c>100 Continue</c> a request asks for.
    /// </summary>
    public HttpClient Client(bool https = true, params DelegatingHandler[] handlers)
    {
        if (_kestrel is { } listening)
        {
            HttpMessageHandler inner = new SocketsHttpHandler
            {
                AllowAutoRedirect = handlers.Length == 0, UseCookies = false, Expect100ContinueTimeout = TimeSpan.FromSeconds(30),
            };
            foreach (var handler in Enumerable.Reverse(handlers))
            {
                handler.InnerHandler = inner;
                inner = handler;
            }
            return new HttpClient(inner) { BaseAddress = listening };
        }
        var baseAddress = new Uri(https ? "https://localhost" : "http://localhost");
        return handlers.Length == 0
            ? _factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = baseAddress, HandleCookies = false })
            : _factory.CreateDefaultClient(baseAddress, handlers);
    }

    public ValueTask DisposeAsync() => _factory.DisposeAsync();
}

/// <summary>
/// Sets the connection's remote address from <see cref="Header"/>, which the
/// test server otherwise leaves empty, so a test can speak from two addresses.
/// Runs before the application's own pipeline. Test hosts only.
/// </summary>
internal sealed class TestRemoteAddress : IStartupFilter
{
    public const string Header = "X-Test-Remote-Address";

    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use(async (context, following) =>
        {
            if (context.Request.Headers[Header].ToString() is { Length: > 0 } address)
                context.Connection.RemoteIpAddress = IPAddress.Parse(address);
            await following(context);
        });
        next(app);
    };
}

/// <summary>
/// One tenant with people, groups, agents, rules and documents, served by an
/// in-process API. The same shape as the gate tests' world, so what each
/// caller may read is already proven below HTTP:
/// <list type="bullet">
/// <item>Alice is staff. Bob is staff and a contractor. Carol owns the service
/// agent and belongs to nothing. Eve is disabled. Dan has no password.</item>
/// <item>Alice's assistant acts for her; the report bot is a service agent
/// holding the auditors group.</item>
/// <item><c>pub</c>: allow everyone. <c>staff</c>: deny contractors, then allow
/// staff. <c>audit</c>: allow auditors. <c>hr</c>: deny the assistant, then
/// allow Alice. <c>none</c>: no rule.</item>
/// </list>
/// </summary>
internal sealed class ApiWorld : IAsyncDisposable
{
    public const string Handbook = "pub/handbook.md";
    public const string Plan = "staff/plan.md";
    public const string AuditLog = "audit/log.md";
    public const string Pay = "hr/pay.md";
    public const string Draft = "none/draft.md";

    public const string AlicePassword = "alice rides the blue tram";
    public const string BobPassword = "bob keeps seven lanterns";
    public const string CarolPassword = "carol counts quiet herons";
    public const string EvePassword = "eve waters the late orchard";

    public static readonly DateTimeOffset Start = new(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);

    private ApiWorld() { }

    public required PremagenticDatabase Db { get; init; }
    public required Guid Tenant { get; init; }
    public required TestClock Clock { get; init; }
    public required IdentityStore Identity { get; init; }
    public required ApiTestHost Host { get; init; }
    public required User Alice { get; init; }
    public required User Bob { get; init; }
    public required User Carol { get; init; }
    public required User Eve { get; init; }
    public required User Dan { get; init; }
    public required Agent Assistant { get; init; }
    public required Agent Bot { get; init; }
    public required string AssistantToken { get; init; }
    public required string BotToken { get; init; }
    public required string ConnectionString { get; init; }

    /// <param name="beforeStart">Runs against the world's database after it is filled and before the API starts, for what the API reads at start.</param>
    public static async Task<ApiWorld> NewAsync(
        DatastoreTestDatabase server, ApiHostOptions? options = null, bool okfBundle = false, int botRate = 60,
        Func<PremagenticDatabase, Guid, Task>? beforeStart = null)
    {
        var connectionString = await server.CreateDatabaseAsync();
        var db = new PremagenticDatabase(connectionString);
        await db.InitializeAsync();
        var tenant = await db.EnsureTenantAsync(ApiTestHost.TenantKey, "T");
        var clock = new TestClock(Start);
        var identity = new IdentityStore(db, tenant, clock);
        var rules = new AclStore(db, tenant);
        var names = new PrincipalNames(identity);
        var hasher = options?.Hasher ?? new PasswordHasher();

        var alice = await identity.CreateUserAsync("alice", "Alice", Role.Member);
        var bob = await identity.CreateUserAsync("bob", "Bob", Role.Member);
        var carol = await identity.CreateUserAsync("carol", "Carol", Role.Administrator);
        var eve = await identity.CreateUserAsync("eve", "Eve", Role.Member);
        var dan = await identity.CreateUserAsync("dan", "Dan", Role.Member);
        await identity.SetPasswordHashAsync(alice.Id, hasher.Hash(AlicePassword));
        await identity.SetPasswordHashAsync(bob.Id, hasher.Hash(BobPassword));
        await identity.SetPasswordHashAsync(carol.Id, hasher.Hash(CarolPassword));
        await identity.SetPasswordHashAsync(eve.Id, hasher.Hash(EvePassword));
        await identity.SetUserDisabledAsync(eve.Id, true);

        var staff = await identity.CreateGroupAsync("Staff");
        var contractors = await identity.CreateGroupAsync("Contractors");
        var auditors = await identity.CreateGroupAsync("Auditors");
        await identity.AddMemberAsync(staff.Id, alice.Id);
        await identity.AddMemberAsync(staff.Id, bob.Id);
        await identity.AddMemberAsync(contractors.Id, bob.Id);
        await identity.AddMemberAsync(staff.Id, eve.Id);

        var assistant = await identity.CreateAgentAsync("assistant", alice.Id, AgentMode.ActsForUser, 60, null, ModelLocation.Local);
        var bot = await identity.CreateAgentAsync("report-bot", carol.Id, AgentMode.Service, botRate, null, ModelLocation.Local);
        await identity.GrantGroupAsync(bot.Id, auditors.Id);
        var assistantToken = (await identity.IssueTokenAsync(assistant.Id, TimeSpan.FromDays(30))).PlainText;
        var botToken = (await identity.IssueTokenAsync(bot.Id, TimeSpan.FromDays(30))).PlainText;

        const string source = "test-datastore";
        await rules.SetRuleAsync(new FolderRule(source, "pub", await names.ToAclSetAsync(["allow everyone"])));
        await rules.SetRuleAsync(new FolderRule(source, "staff", await names.ToAclSetAsync(["deny group:Contractors", "allow group:Staff"])));
        await rules.SetRuleAsync(new FolderRule(source, "audit", await names.ToAclSetAsync(["allow group:Auditors"])));
        await rules.SetRuleAsync(new FolderRule(source, "hr", await names.ToAclSetAsync(["deny agent:assistant", "allow user:alice"])));

        var embedder = new SeededEmbeddingProvider();
        var pipeline = new IngestPipeline(db, embedder);
        var summary = await pipeline.RunAsync(tenant, new DatastoreSource("", [
            DatastoreSource.Doc(Handbook, "## Handbook\nthe zeppelin handbook for everyone", DocumentAccess.FolderRules),
            DatastoreSource.Doc(Plan, "## Plan\nthe zeppelin staff plan", DocumentAccess.FolderRules),
            DatastoreSource.Doc(AuditLog, "## Log\nthe zeppelin audit log", DocumentAccess.FolderRules),
            DatastoreSource.Doc(Pay, "## Pay\nthe zeppelin pay bands", DocumentAccess.FolderRules),
            DatastoreSource.Doc(Draft, "## Draft\nthe zeppelin draft nobody may read", DocumentAccess.FolderRules),
        ]));
        Assert.Equal(5, summary.Ingested);

        if (okfBundle)
        {
            var bundle = new FileSystemSource(Path.Combine(AppContext.BaseDirectory, "Fixtures", "okf-bundle"), DocumentAccess.Everyone, "okf")
            {
                OkfBundle = true,
            };
            await pipeline.RunAsync(tenant, bundle);
        }

        if (beforeStart is not null) await beforeStart(db, tenant);
        var host = ApiTestHost.Start(connectionString, clock, embedder, options);
        return new ApiWorld
        {
            Db = db, Tenant = tenant, Clock = clock, Identity = identity, Host = host, ConnectionString = connectionString,
            Alice = alice, Bob = bob, Carol = carol, Eve = eve, Dan = dan, Assistant = assistant, Bot = bot,
            AssistantToken = assistantToken, BotToken = botToken,
        };
    }

    public async ValueTask DisposeAsync()
    {
        await Host.DisposeAsync();
        await Db.DisposeAsync();
    }
}

/// <summary>A signed-in session as the browser would hold it.</summary>
internal sealed record ApiSession(string Cookie, string AntiForgeryToken);

/// <summary>Requests the way a browser, a proxy or an agent sends them, each credential stated explicitly.</summary>
internal static class Api
{
    public const string Cookie = "__Host-prem-session";
    public const string AntiForgeryHeader = "X-Prem-Antiforgery";

    public static HttpRequestMessage Request(
        HttpMethod method, string path, object? body = null, ApiSession? session = null, bool antiForgery = true,
        string? bearer = null, (string Name, string Value)? header = null)
    {
        var request = new HttpRequestMessage(method, path);
        if (body is not null) request.Content = JsonContent.Create(body);
        if (session is not null)
        {
            request.Headers.Add("Cookie", $"{Cookie}={session.Cookie}");
            if (antiForgery) request.Headers.Add(AntiForgeryHeader, session.AntiForgeryToken);
        }
        if (bearer is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        if (header is { } h) request.Headers.Add(h.Name, h.Value);
        return request;
    }

    public static async Task<HttpResponseMessage> SignInAsync(HttpClient client, string name, string password) =>
        await client.PostAsJsonAsync("/api/session", new { signInName = name, password });

    /// <summary>Signs in and returns the session, asserting it worked.</summary>
    public static async Task<ApiSession> SessionAsync(HttpClient client, string name, string password)
    {
        using var response = await SignInAsync(client, name, password);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return new ApiSession(SessionCookie(response)!, body.GetProperty("antiForgeryToken").GetString()!);
    }

    /// <summary>The session cookie's Set-Cookie line, or null when the response set none.</summary>
    public static string? SetCookieLine(HttpResponseMessage response) =>
        response.Headers.TryGetValues("Set-Cookie", out var lines)
            ? lines.FirstOrDefault(l => l.StartsWith(Cookie + "=", StringComparison.Ordinal))
            : null;

    public static string? SessionCookie(HttpResponseMessage response) =>
        SetCookieLine(response) is { } line ? line[(Cookie.Length + 1)..].Split(';')[0] : null;

    public static async Task<HttpResponseMessage> SearchAsync(
        HttpClient client, string query = "zeppelin", ApiSession? session = null, bool antiForgery = true, string? bearer = null,
        (string Name, string Value)? header = null, int topK = 10) =>
        await client.SendAsync(Request(HttpMethod.Post, "/api/search", new { query, topK }, session, antiForgery, bearer, header));

    /// <summary>The distinct paths a search response returned, sorted, asserting it was a 200.</summary>
    public static async Task<string[]> PathsAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("hits").EnumerateArray().Select(h => h.GetProperty("path").GetString()!)
            .Distinct().Order(StringComparer.Ordinal).ToArray();
    }
}

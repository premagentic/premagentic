using System.Net;
using System.Text;
using Premagentic.Api;
using Premagentic.Api.Callers;
using Premagentic.Api.Hosting;
using Premagentic.Core.Embeddings;
using Premagentic.Core.Identity;
using Premagentic.Core.Storage;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Premagentic.Tests;

/// <summary>
/// The API's hosting, in process, through its real Program and pipeline: the
/// startup refusal and the trusted proxies. Requests go through
/// <see cref="TestServer.SendAsync"/>, which sets the connection's own address
/// before any middleware runs, so the forwarding headers are handled by the
/// framework's middleware exactly as in production, never by hand.
/// </summary>
public sealed class InstallHostingTests(DatastoreTestDatabase server) : IClassFixture<DatastoreTestDatabase>
{
    private const string Password = "a long walk to the lighthouse";
    private const string ProxyA = "10.0.0.5";
    private const string ProxyB = "10.0.0.6";

    private static WebApplicationFactory<ApiSettings> Host(IReadOnlyDictionary<string, string?> settings, string? connectionString = null) =>
        new WebApplicationFactory<ApiSettings>().WithWebHostBuilder(web =>
        {
            // Every setting the hosting and the API read is given here, so nothing
            // from the environment of whoever runs the tests decides the outcome.
            foreach (var key in new[]
                     {
                         "PREM_CONNECTION_STRING", "PREM_CREDENTIALS_FILE", PremagenticDatabase.DevelopmentSwitch, TrustedProxies.Key,
                         "FORWARDEDHEADERS_ENABLED", "ASPNETCORE_FORWARDEDHEADERS_ENABLED", "PREM_PRINCIPAL_HEADER",
                         ApiSettings.SignInHeaderKey, ApiSettings.AllowHttpSignInKey, SearchRoleRequirement.AllowKey,
                     })
                web.UseSetting(key, settings.TryGetValue(key, out var value) ? value : "");
            web.UseSetting("PREM_TENANT_KEY", "t");
            web.UseSetting("PREM_TENANT_NAME", "T");
            if (connectionString is null) return;
            web.ConfigureTestServices(services =>
            {
                services.RemoveAll<PremagenticDatabase>();
                services.AddSingleton(_ => new PremagenticDatabase(connectionString));
                services.RemoveAll<IEmbeddingProvider>();
                services.AddSingleton<IEmbeddingProvider>(new SeededEmbeddingProvider());
                services.RemoveAll<SignInThrottleOptions>();
                services.AddSingleton(new SignInThrottleOptions(2, TimeSpan.FromMinutes(10), 100));
            });
        });

    /// <summary>A database with one person who can sign in, and an API over it with <paramref name="proxies"/> trusted.</summary>
    private async Task<WebApplicationFactory<ApiSettings>> ApiAsync(string? proxies)
    {
        var connectionString = await server.CreateDatabaseAsync();
        await using (var db = new PremagenticDatabase(connectionString))
        {
            await db.InitializeAsync();
            var identity = new IdentityStore(db, await db.EnsureTenantAsync("t", "T"));
            var user = await identity.CreateUserAsync("pat", "Pat", Role.Member);
            await identity.SetPasswordHashAsync(user.Id, new PasswordHasher().Hash(Password));
        }
        var factory = Host(new Dictionary<string, string?>
        {
            ["PREM_CONNECTION_STRING"] = connectionString,
            [TrustedProxies.Key] = proxies,
            [SearchRoleRequirement.AllowKey] = "1",
        }, connectionString);
        _ = factory.Server;
        return factory;
    }

    /// <summary>A failed sign-in as it arrives: from <paramref name="peer"/>, with whatever forwarding headers are given.</summary>
    private static async Task<int> SignInAsync(
        WebApplicationFactory<ApiSettings> api, string peer, string? forwardedFor = null, string? forwardedProto = null,
        bool https = false, string password = "not the password")
    {
        var context = await api.Server.SendAsync(c =>
        {
            c.Request.Method = "POST";
            c.Request.Scheme = https ? "https" : "http";
            c.Request.Host = new HostString("search.example.test");
            c.Request.Path = "/api/session";
            c.Request.ContentType = "application/json";
            var body = Encoding.UTF8.GetBytes($$"""{"signInName":"pat","password":"{{password}}"}""");
            c.Request.Body = new MemoryStream(body);
            c.Request.ContentLength = body.Length;
            // SendAsync otherwise tells the endpoint the request cannot have a body.
            c.Features.Set<Microsoft.AspNetCore.Http.Features.IHttpRequestBodyDetectionFeature>(new HasBody());
            c.Connection.RemoteIpAddress = IPAddress.Parse(peer);
            if (forwardedFor is not null) c.Request.Headers["X-Forwarded-For"] = forwardedFor;
            if (forwardedProto is not null) c.Request.Headers["X-Forwarded-Proto"] = forwardedProto;
        });
        return context.Response.StatusCode;
    }

    private sealed class HasBody : Microsoft.AspNetCore.Http.Features.IHttpRequestBodyDetectionFeature
    {
        public bool CanHaveBody => true;
    }

    private static StartupRefusedException Refusal(Action start)
    {
        var thrown = Record.Exception(start);
        for (var e = thrown; e is not null; e = e.InnerException)
            if (e is StartupRefusedException refused) return refused;
        throw new Xunit.Sdk.XunitException($"expected a startup refusal, got {thrown?.GetType().Name ?? "no exception"}: {thrown?.Message}");
    }

    // ----------------------------------------------------------------- refusal

    [Fact]
    public void With_nothing_configured_the_api_refuses_in_process_with_the_sentence()
    {
        using var api = Host(new Dictionary<string, string?> { [PremagenticDatabase.DevelopmentSwitch] = "0" });

        var refused = Refusal(() => _ = api.Server);

        Assert.StartsWith("No database is configured. Set PREM_CREDENTIALS_FILE", refused.Message);
        Assert.Equal(2, StartupRefusedException.ExitCode);
    }

    [Fact]
    public async Task The_real_api_ends_a_refusal_with_the_sentence_and_exit_2()
    {
        // The API as a process of its own, with nothing configured, so the refusal
        // reaches the top of the process and the entry point's handler ends it.
        var start = new System.Diagnostics.ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "Premagentic.Api.dll"));
        foreach (var key in new[] { "PREM_CONNECTION_STRING", "PREM_CREDENTIALS_FILE", PremagenticDatabase.DevelopmentSwitch })
            start.Environment.Remove(key);

        using var process = System.Diagnostics.Process.Start(start)!;
        var stderr = process.StandardError.ReadToEndAsync();
        var stdout = process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();

        Assert.Equal(StartupRefusedException.ExitCode, process.ExitCode);
        Assert.StartsWith("Premagentic API cannot start: No database is configured.", await stderr);
        Assert.DoesNotContain("Unhandled exception", await stderr + await stdout);
    }

    [Fact]
    public void An_unreadable_credentials_file_is_a_refusal_not_a_fallback()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"premagentic-missing-{Guid.NewGuid():N}.credentials");
        using var api = Host(new Dictionary<string, string?>
        {
            ["PREM_CREDENTIALS_FILE"] = missing,
            [PremagenticDatabase.DevelopmentSwitch] = "1",
        });

        var refused = Refusal(() => _ = api.Server);

        Assert.Contains($"PREM_CREDENTIALS_FILE names {missing}", refused.Message);
        Assert.IsType<FileNotFoundException>(refused.InnerException);
    }

    [Fact]
    public async Task A_deployment_by_connection_string_with_no_search_role_starts_only_when_it_says_so()
    {
        var connectionString = await server.CreateDatabaseAsync();

        using (var refusedApi = Host(new Dictionary<string, string?> { ["PREM_CONNECTION_STRING"] = connectionString }, connectionString))
        {
            var refused = Refusal(() => _ = refusedApi.Server);
            Assert.Contains("No search role is configured", refused.Message);
            Assert.Contains($"{SearchRoleRequirement.AllowKey}=1", refused.Message);
        }

        // The control: the same deployment, saying on purpose that it runs with the SQL gate alone.
        using var allowed = Host(new Dictionary<string, string?>
        {
            ["PREM_CONNECTION_STRING"] = connectionString,
            [SearchRoleRequirement.AllowKey] = "1",
        }, connectionString);
        Assert.NotNull(allowed.Server);
    }

    [Fact]
    public async Task The_development_database_still_starts_with_no_search_role()
    {
        var connectionString = await server.CreateDatabaseAsync();

        using var api = Host(new Dictionary<string, string?> { [PremagenticDatabase.DevelopmentSwitch] = "1" }, connectionString);

        Assert.NotNull(api.Server);
    }

    [Theory]
    [InlineData("conn", null, null, null, true)]
    [InlineData("conn", null, "1", null, true)]
    [InlineData("conn", null, null, "1", false)]
    [InlineData("conn", null, "1", "1", false)]
    [InlineData(null, null, "1", null, false)]
    [InlineData(null, "app.credentials", null, null, false)]
    [InlineData("conn", null, null, "yes", true)]
    public void Only_a_connection_string_deployment_that_did_not_say_so_is_refused(
        string? connection, string? credentials, string? development, string? allow, bool refused)
    {
        var settings = new Dictionary<string, string?>
        {
            ["PREM_CONNECTION_STRING"] = connection,
            ["PREM_CREDENTIALS_FILE"] = credentials,
            [PremagenticDatabase.DevelopmentSwitch] = development,
            [SearchRoleRequirement.AllowKey] = allow,
        };

        Assert.Equal(refused, SearchRoleRequirement.Refusal(k => settings.GetValueOrDefault(k)) is not null);
    }

    [Fact]
    public void The_framework_switch_that_believes_any_forwarder_is_refused()
    {
        using var api = Host(new Dictionary<string, string?>
        {
            ["PREM_CONNECTION_STRING"] = "Host=nowhere",
            ["ASPNETCORE_FORWARDEDHEADERS_ENABLED"] = "true",
        });

        var refused = Refusal(() => _ = api.Server);

        Assert.Contains("ASPNETCORE_FORWARDEDHEADERS_ENABLED believes forwarding headers from any client", refused.Message);
    }

    [Theory]
    [InlineData("0.0.0.0/0", "covers every address")]
    [InlineData("::/0", "covers every address")]
    [InlineData("10.0.0.5, proxy.example.test", "is not an address or a range")]
    [InlineData("10.0.0.300", "is not an address or a range")]
    public void A_proxy_list_that_would_believe_everyone_or_does_not_parse_is_refused(string value, string sentence)
    {
        Assert.Contains(sentence, Assert.Throws<StartupRefusedException>(() => TrustedProxies.Parse(value)).Message);
    }

    [Fact]
    public void Proxies_are_listed_by_address_or_range()
    {
        var networks = TrustedProxies.Parse(" 10.0.0.5 , 10.1.0.0/16,fd00::/8 ,::1");

        Assert.Equal(["10.0.0.5/32", "10.1.0.0/16", "fd00::/8", "::1/128"], networks.Select(n => n.ToString()));
        Assert.Empty(TrustedProxies.Parse(null));
        Assert.Empty(TrustedProxies.Parse(" , "));
    }

    // ---------------------------------------------------------------- proxies

    [Fact]
    public async Task By_default_every_forwarding_header_is_ignored()
    {
        await using var api = await ApiAsync(proxies: null);

        // The scheme: plain HTTP stays plain HTTP whatever the header claims, so
        // password sign-in is refused; over HTTPS the same request is answered.
        Assert.Equal(StatusCodes.Status403Forbidden, await SignInAsync(api, ProxyA, "198.51.100.7", "https"));
        Assert.Equal(StatusCodes.Status401Unauthorized, await SignInAsync(api, ProxyA, "198.51.100.7", https: true));

        // The address: two clients behind one proxy are one address to the throttle.
        Assert.Equal(StatusCodes.Status401Unauthorized, await SignInAsync(api, ProxyA, "198.51.100.8", https: true));
        Assert.Equal(StatusCodes.Status429TooManyRequests, await SignInAsync(api, ProxyA, "198.51.100.9", https: true));
    }

    [Fact]
    public async Task A_listed_proxy_is_believed_about_the_client_address_and_scheme()
    {
        await using var api = await ApiAsync($"{ProxyA}, 10.9.0.0/16");

        // HTTPS ended at the proxy: the client's scheme is honored.
        Assert.Equal(StatusCodes.Status401Unauthorized, await SignInAsync(api, ProxyA, "198.51.100.7", "https"));
        // One client's failures block that client only.
        Assert.Equal(StatusCodes.Status401Unauthorized, await SignInAsync(api, ProxyA, "198.51.100.7", "https"));
        Assert.Equal(StatusCodes.Status429TooManyRequests, await SignInAsync(api, ProxyA, "198.51.100.7", "https"));
        Assert.Equal(StatusCodes.Status401Unauthorized, await SignInAsync(api, ProxyA, "198.51.100.8", "https"));
        // A range is listed too, and the right password still signs in.
        Assert.Equal(StatusCodes.Status200OK, await SignInAsync(api, "10.9.3.4", "198.51.100.20", "https", password: Password));
    }

    [Fact]
    public async Task An_unlisted_sender_is_not_believed_even_when_another_proxy_is_listed()
    {
        await using var api = await ApiAsync(ProxyA);

        // A client talking to the API directly, claiming to be forwarded.
        Assert.Equal(StatusCodes.Status403Forbidden, await SignInAsync(api, "203.0.113.9", "198.51.100.7", "https"));
        // Nor the loopback addresses, which the framework trusts unless told otherwise.
        Assert.Equal(StatusCodes.Status403Forbidden, await SignInAsync(api, "127.0.0.1", "198.51.100.7", "https"));
        Assert.Equal(StatusCodes.Status403Forbidden, await SignInAsync(api, "::1", "198.51.100.7", "https"));
        // And the address it claimed is not the one counted: its own is.
        Assert.Equal(StatusCodes.Status401Unauthorized, await SignInAsync(api, "203.0.113.9", "198.51.100.1", https: true));
        Assert.Equal(StatusCodes.Status401Unauthorized, await SignInAsync(api, "203.0.113.9", "198.51.100.2", https: true));
        Assert.Equal(StatusCodes.Status429TooManyRequests, await SignInAsync(api, "203.0.113.9", "198.51.100.3", https: true));
    }

    [Fact]
    public async Task A_client_cannot_forge_its_way_past_the_proxy()
    {
        await using var api = await ApiAsync($"{ProxyA}, {ProxyB}");

        // The client put its own entries in front; the proxies appended the real
        // one. The walk from the right stops at the first unlisted address, so
        // whatever the client wrote, it is counted as 198.51.100.7.
        Assert.Equal(StatusCodes.Status401Unauthorized, await SignInAsync(api, ProxyA, "1.2.3.4, 198.51.100.7, 10.0.0.6", "https"));
        Assert.Equal(StatusCodes.Status401Unauthorized, await SignInAsync(api, ProxyA, "5.6.7.8, 198.51.100.7, 10.0.0.6", "https"));
        Assert.Equal(StatusCodes.Status429TooManyRequests, await SignInAsync(api, ProxyA, "9.9.9.9, 198.51.100.7, 10.0.0.6", "https"));
        // Behind both proxies, another client is counted as itself, not as the inner proxy.
        Assert.Equal(StatusCodes.Status401Unauthorized, await SignInAsync(api, ProxyA, "198.51.100.8, 10.0.0.6", "https"));
    }

    [Fact]
    public async Task A_proxy_reaching_a_dual_stack_listener_is_recognized_by_its_ipv4_address()
    {
        await using var api = await ApiAsync(ProxyA);

        // Listening on every address, the server sees an IPv4 peer as IPv4 mapped into IPv6.
        Assert.Equal(StatusCodes.Status401Unauthorized, await SignInAsync(api, "::ffff:10.0.0.5", "198.51.100.7", "https"));
    }
}

using System.Net;
using System.Text;
using Premagentic.Api.Hosting;
using Microsoft.Extensions.Configuration;

namespace Premagentic.Tests;

/// <summary>
/// The API's own surfaces send nosniff and no-store on every response,
/// refusals included, and HSTS goes out only when the service holds its own
/// certificate and no proxy is trusted. Requires a running Docker daemon.
/// </summary>
public sealed class ApiResponseHeadersTests(DatastoreTestDatabase server) : IClassFixture<DatastoreTestDatabase>
{
    private static void AssertGuarded(HttpResponseMessage response)
    {
        Assert.Equal(["nosniff"], response.Headers.GetValues("X-Content-Type-Options"));
        Assert.True(response.Headers.CacheControl?.NoStore, $"{response.RequestMessage?.RequestUri} was sent without no-store.");
    }

    [Fact]
    public async Task Every_api_surface_says_no_sniffing_and_no_storing_even_when_it_refuses()
    {
        await using var w = await ApiWorld.NewAsync(server);
        using var client = w.Host.Client();

        using var health = await client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        AssertGuarded(health);

        using var search = await client.PostAsync("/api/search", new StringContent("{\"query\":\"x\"}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Unauthorized, search.StatusCode);
        AssertGuarded(search);

        using var mcp = await client.PostAsync("/mcp", new StringContent("{}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Unauthorized, mcp.StatusCode);
        AssertGuarded(mcp);

        // No HSTS where the service does not hold its own certificate.
        Assert.False(health.Headers.Contains("Strict-Transport-Security"));
    }

    [Fact]
    public async Task Hsts_goes_on_https_responses_only_when_the_service_terminates_tls()
    {
        await using var own = await ApiWorld.NewAsync(server, new ApiHostOptions { Headers = new ApiResponseHeaderOptions(Hsts: true) });

        using (var https = own.Host.Client(https: true))
        using (var response = await https.GetAsync("/health"))
            Assert.Equal([ApiResponseHeaders.HstsValue], response.Headers.GetValues("Strict-Transport-Security"));

        // Not on plain HTTP, where a browser ignores it anyway and a proxy may be speaking for the host.
        using (var http = own.Host.Client(https: false))
        using (var response = await http.GetAsync("/health"))
            Assert.False(response.Headers.Contains("Strict-Transport-Security"));

        // The control: the same request to a service that does not hold its certificate.
        await using var proxied = await ApiWorld.NewAsync(server, new ApiHostOptions { Headers = new ApiResponseHeaderOptions(Hsts: false) });
        using var client = proxied.Host.Client(https: true);
        using var none = await client.GetAsync("/health");
        Assert.False(none.Headers.Contains("Strict-Transport-Security"));
    }

    [Fact]
    public void Hsts_is_chosen_only_for_a_setup_certificate_with_no_trusted_proxy()
    {
        var folder = Directory.CreateTempSubdirectory("prem-headers-").FullName;
        try
        {
            var credentials = Path.Combine(folder, "app.credentials");
            File.WriteAllText(Path.Combine(folder, "kestrel.json"), "{}");

            Assert.True(ApiResponseHeaderOptions.From(Config(("PREM_CREDENTIALS_FILE", credentials))).Hsts);
            Assert.False(ApiResponseHeaderOptions.From(Config(
                ("PREM_CREDENTIALS_FILE", credentials), (TrustedProxies.Key, "10.0.0.5"))).Hsts);
            Assert.False(ApiResponseHeaderOptions.From(Config()).Hsts);

            File.Delete(Path.Combine(folder, "kestrel.json"));
            Assert.False(ApiResponseHeaderOptions.From(Config(("PREM_CREDENTIALS_FILE", credentials))).Hsts);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    private static IConfiguration Config(params (string Key, string Value)[] settings) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value)))
            .Build();
}

using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;

namespace Premagentic.Tests;

/// <summary>
/// An embedding provider that cannot be made or cannot be reached stops the
/// real CLI with one sentence and exit code 2, not the runtime's stack trace.
/// The provider is pointed at a proxy on a local port nothing listens on, so
/// the request fails on this machine and nothing leaves it.
/// Requires a running Docker daemon.
/// </summary>
public sealed class EmbeddingProviderRefusalTests(DatastoreTestDatabase server) : IClassFixture<DatastoreTestDatabase>
{
    [Fact]
    public async Task A_search_whose_provider_has_no_key_or_cannot_be_reached_is_refused_in_one_line()
    {
        var database = await server.CreateDatabaseAsync();
        var closedPort = ClosedLocalPort();

        var (noKey, noKeyText) = await SearchAsync(database, apiKey: null, closedPort);
        Assert.Equal(2, noKey);
        Assert.Contains("prem search stopped before it searched: The embedding provider openai:text-embedding-3-small needs OPENAI_API_KEY", noKeyText);
        AssertNoStackTrace(noKeyText);

        var (unreachable, unreachableText) = await SearchAsync(database, apiKey: "not-a-real-key", closedPort);
        Assert.Equal(2, unreachable);
        Assert.Contains("prem search stopped before it searched: The embedding provider openai:text-embedding-3-small could not be reached", unreachableText);
        Assert.Contains("Nothing was embedded.", unreachableText);
        AssertNoStackTrace(unreachableText);
    }

    private static void AssertNoStackTrace(string output)
    {
        Assert.DoesNotContain("Unhandled exception", output);
        Assert.DoesNotContain("   at ", output);
        Assert.Single(output.Split('\n'), line => line.StartsWith("prem search stopped", StringComparison.Ordinal));
    }

    private static int ClosedLocalPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static async Task<(int ExitCode, string Output)> SearchAsync(string connectionString, string? apiKey, int proxyPort)
    {
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add(CliPath());
        start.ArgumentList.Add("search");
        start.ArgumentList.Add("how long do I have to file an expense claim");
        foreach (var name in new[] { "PREM_CREDENTIALS_FILE", "PREM_SEARCH_CONNECTION_STRING", "OPENAI_API_KEY",
                     "HTTP_PROXY", "http_proxy", "ALL_PROXY", "all_proxy", "NO_PROXY", "no_proxy" })
            start.Environment.Remove(name);
        start.Environment["PREM_CONNECTION_STRING"] = connectionString;
        start.Environment["PREM_EMBEDDING_PROVIDER"] = "openai";
        start.Environment["HTTPS_PROXY"] = start.Environment["https_proxy"] = $"http://127.0.0.1:{proxyPort}";
        if (apiKey is not null) start.Environment["OPENAI_API_KEY"] = apiKey;

        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, await stdout + await stderr);
    }

    // The CLI's own build output, as the test project records it (see its project file).
    private static string CliPath()
    {
        var path = typeof(EmbeddingProviderRefusalTests).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(attribute => attribute.Key == "Premagentic.Cli.Path")
            .Value;
        Assert.True(File.Exists(path), "the CLI was not built where the test project recorded it: " + path);
        return path!;
    }
}

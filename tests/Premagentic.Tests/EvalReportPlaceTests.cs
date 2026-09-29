using System.Diagnostics;
using System.Reflection;
using Premagentic.Core.Embeddings;
using Premagentic.Core.Ingestion;
using Premagentic.Core.Profiles;
using Premagentic.Core.Security;
using Premagentic.Core.Storage;

namespace Premagentic.Tests;

/// <summary>
/// Where <c>prem eval</c> writes its report. A golden set can live in a
/// profile folder, and a file the profile does not name makes the next apply
/// of that profile refuse, so with no path given the report goes to the
/// current folder, and no report is ever written into a folder that holds a
/// profile. Requires a running Docker daemon.
/// </summary>
public sealed class EvalReportPlaceTests(DatastoreTestDatabase server) : IClassFixture<DatastoreTestDatabase>
{
    private const string GoldenJson = """
        [
          { "id": "finds-the-hangar", "question": "zeppelin hangar", "category": "lookup",
            "expectedSourcePaths": ["pub/hangar.md"], "forbiddenSourcePaths": [], "notes": "" }
        ]
        """;

    [Fact]
    public async Task The_report_goes_to_the_current_folder_and_never_into_a_profile_folder()
    {
        var connection = await server.CreateDatabaseAsync();
        await using var db = new PremagenticDatabase(connection);
        await db.InitializeAsync();
        var tenant = await db.EnsureTenantAsync("t", "T");
        await new IngestPipeline(db, new HashEmbeddingProvider()).RunAsync(tenant, new DatastoreSource("", [
            DatastoreSource.Doc("pub/hangar.md", "## Hangar\nthe zeppelin hangar opens at dawn", DocumentAccess.Everyone),
        ]));

        var root = Directory.CreateTempSubdirectory("prem-eval-place-");
        try
        {
            var profile = Directory.CreateDirectory(Path.Combine(root.FullName, "starter-profile"));
            await File.WriteAllTextAsync(Path.Combine(profile.FullName, ProfileReader.ManifestFile), "{}");
            var golden = Path.Combine(profile.FullName, "golden.json");
            await File.WriteAllTextAsync(golden, GoldenJson);
            var elsewhere = Directory.CreateDirectory(Path.Combine(root.FullName, "elsewhere"));

            // No path given: the report is written to the current folder, which
            // is not the golden set's, and the program says where.
            var (code, output, errors) = await EvalAsync(connection, elsewhere.FullName, golden);
            Assert.True(code == 0, $"exit {code}{Environment.NewLine}{output}{errors}");
            var written = Assert.Single(Directory.GetFiles(elsewhere.FullName, "report-*.md"));
            Assert.Contains($"Report written to {written}", output);
            Assert.Equal(["golden.json", ProfileReader.ManifestFile], Directory.GetFiles(profile.FullName).Select(Path.GetFileName).Order(StringComparer.Ordinal));

            // A path into the profile folder is refused, and nothing is written there.
            (code, output, errors) = await EvalAsync(connection, elsewhere.FullName, golden, Path.Combine(profile.FullName, "report.md"));
            Assert.Equal(1, code);
            Assert.Contains("holds a profile", errors);
            Assert.Equal(["golden.json", ProfileReader.ManifestFile], Directory.GetFiles(profile.FullName).Select(Path.GetFileName).Order(StringComparer.Ordinal));

            // So is the default, when the current folder is the profile folder.
            (code, output, errors) = await EvalAsync(connection, profile.FullName, golden);
            Assert.Equal(1, code);
            Assert.Contains("holds a profile", errors);
            Assert.Equal(["golden.json", ProfileReader.ManifestFile], Directory.GetFiles(profile.FullName).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    private static async Task<(int Code, string Output, string Errors)> EvalAsync(
        string connection, string workingDirectory, params string[] arguments)
    {
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = workingDirectory,
        };
        start.ArgumentList.Add(CliPath());
        start.ArgumentList.Add("eval");
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        // Nothing from this process's own settings reaches the CLI.
        foreach (var name in start.Environment.Keys.Where(k => k.StartsWith("PREM_", StringComparison.OrdinalIgnoreCase)).ToArray())
            start.Environment.Remove(name);
        start.Environment["PREM_CONNECTION_STRING"] = connection;
        start.Environment["PREM_TENANT_KEY"] = "t";
        start.Environment["PREM_TENANT_NAME"] = "T";
        start.Environment["PREM_EMBEDDING_PROVIDER"] = "hash";

        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, await stdout, await stderr);
    }

    private static string CliPath()
    {
        var path = typeof(EvalReportPlaceTests).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(attribute => attribute.Key == "Premagentic.Cli.Path")
            .Value;
        Assert.True(File.Exists(path), "the CLI was not built where the test project recorded it: " + path);
        return path!;
    }
}

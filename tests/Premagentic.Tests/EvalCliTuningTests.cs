using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Premagentic.Core.Embeddings;
using Premagentic.Core.Identity;
using Premagentic.Core.Ingestion;
using Premagentic.Core.Retrieval;
using Premagentic.Core.Security;
using Premagentic.Core.Storage;

namespace Premagentic.Tests;

/// <summary>
/// <c>prem eval</c>, run as a person runs it, ranks and judges under the
/// retrieval tuning the deployment stores, and its report names the floor it
/// judged by. Requires a running Docker daemon.
/// </summary>
public sealed class EvalCliTuningTests(DatastoreTestDatabase server) : IClassFixture<DatastoreTestDatabase>
{
    private const string GoldenJson = """
        [
          { "id": "finds-the-hangar", "question": "zeppelin hangar", "category": "lookup",
            "expectedSourcePaths": ["pub/hangar.md"], "forbiddenSourcePaths": [], "notes": "" },
          { "id": "has-no-answer", "question": "xylophone quartz marmalade", "category": "no-answer",
            "expectedSourcePaths": [], "forbiddenSourcePaths": [], "expectNoAnswer": true, "notes": "" }
        ]
        """;

    [Fact]
    public async Task The_cli_eval_judges_by_the_stored_floor_and_its_report_names_it()
    {
        var connection = await server.CreateDatabaseAsync();
        await using var db = new PremagenticDatabase(connection);
        await db.InitializeAsync();
        var tenant = await db.EnsureTenantAsync("t", "T");
        await new IngestPipeline(db, new HashEmbeddingProvider()).RunAsync(tenant, new DatastoreSource("", [
            DatastoreSource.Doc("pub/hangar.md", "## Hangar\nthe zeppelin hangar opens at dawn", DocumentAccess.Everyone),
            DatastoreSource.Doc("pub/crew.md", "## Crew\nthe zeppelin crew eats at noon", DocumentAccess.Everyone),
        ]));
        var golden = Path.Combine(Path.GetTempPath(), $"prem-eval-golden-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(golden, GoldenJson);
        try
        {
            // At the default floor, nothing found only by meaning counts as an answer to the nonsense question.
            var atDefault = await EvalAsync(connection, golden);
            Assert.Contains("No-answer distance floor: 0.55.", atDefault);
            Assert.Contains("## has-no-answer, PASS", atDefault);

            // At a floor near the top of the range, any passage found by meaning counts, so the same question fails.
            await new SettingsStore(db, tenant).SetAsync(RetrievalSettings.NoAnswerDistanceFloor, JsonDocument.Parse("1.99").RootElement);
            var atStored = await EvalAsync(connection, golden);
            Assert.Contains("No-answer distance floor: 1.99.", atStored);
            Assert.Contains("## has-no-answer, FAIL", atStored);
            Assert.Contains("## finds-the-hangar, PASS", atStored);
        }
        finally
        {
            File.Delete(golden);
        }
    }

    /// <summary>Runs <c>prem eval</c> and returns its report, with any decimal comma read as a point.</summary>
    private static async Task<string> EvalAsync(string connection, string golden)
    {
        var report = Path.Combine(Path.GetTempPath(), $"prem-eval-report-{Guid.NewGuid():N}.md");
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in new[] { CliPath(), "eval", golden, report })
            start.ArgumentList.Add(argument);
        // Nothing from this process's own settings reaches the CLI.
        foreach (var name in start.Environment.Keys.Where(k => k.StartsWith("PREM_", StringComparison.OrdinalIgnoreCase)).ToArray())
            start.Environment.Remove(name);
        start.Environment["PREM_CONNECTION_STRING"] = connection;
        start.Environment["PREM_TENANT_KEY"] = "t";
        start.Environment["PREM_TENANT_NAME"] = "T";
        start.Environment["PREM_EMBEDDING_PROVIDER"] = "hash";
        start.Environment["PREM_HEADING_PREFIX"] = "0";

        try
        {
            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            var (output, errors) = (await stdout, await stderr);
            // 0 when every case passes, 1 when one fails; anything else is a crash.
            Assert.True(process.ExitCode is 0 or 1, $"exit {process.ExitCode}{Environment.NewLine}{output}{errors}");
            Assert.True(File.Exists(report), $"no report{Environment.NewLine}{output}{errors}");
            return (await File.ReadAllTextAsync(report)).Replace("0,55", "0.55").Replace("1,99", "1.99");
        }
        finally
        {
            File.Delete(report);
        }
    }

    // The CLI's own build output, recorded by the test project at build time.
    private static string CliPath()
    {
        var path = typeof(EvalCliTuningTests).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(attribute => attribute.Key == "Premagentic.Cli.Path")
            .Value;
        Assert.True(File.Exists(path), "the CLI was not built where the test project recorded it: " + path);
        return path!;
    }
}

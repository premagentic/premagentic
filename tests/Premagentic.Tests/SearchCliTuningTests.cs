using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Text.Json;
using Premagentic.Core.Embeddings;
using Premagentic.Core.Identity;
using Premagentic.Core.Ingestion;
using Premagentic.Core.Retrieval;
using Premagentic.Core.Security;
using Premagentic.Core.Storage;

namespace Premagentic.Tests;

/// <summary>
/// <c>prem search</c>, run as a person runs it, scores with the retrieval
/// tuning the deployment stores and warns once about a stored value it cannot
/// use. Requires a running Docker daemon.
/// </summary>
public sealed partial class SearchCliTuningTests(DatastoreTestDatabase server) : IClassFixture<DatastoreTestDatabase>
{
    [Fact]
    public async Task The_cli_search_scores_with_the_stored_tuning_and_warns_once_about_a_value_it_cannot_use()
    {
        var connection = await server.CreateDatabaseAsync();
        await using var db = new PremagenticDatabase(connection);
        await db.InitializeAsync();
        var tenant = await db.EnsureTenantAsync("t", "T");
        await new IngestPipeline(db, new HashEmbeddingProvider()).RunAsync(tenant, new DatastoreSource("", [
            DatastoreSource.Doc("pub/hangar.md", "## Hangar\nthe zeppelin hangar opens at dawn", DocumentAccess.Everyone),
            DatastoreSource.Doc("pub/crew.md", "## Crew\nthe zeppelin crew eats at noon", DocumentAccess.Everyone),
        ]));
        var settings = new SettingsStore(db, tenant);

        // At the default k of 60 no passage can score above two ranks of 1/61,
        // 0.0328 as the CLI prints it.
        var before = await SearchAsync(connection);
        Assert.Empty(before.Warnings);
        Assert.InRange(before.Top, double.Epsilon, 0.0328);

        // At k = 1 the passage ranked first scores at least 1/2.
        await settings.SetAsync(RetrievalSettings.RrfK, JsonDocument.Parse("1").RootElement);
        Assert.InRange((await SearchAsync(connection)).Top, 0.5, 1.0);

        // A value the key does not allow keeps the default, with one warning.
        await settings.RemoveAsync(RetrievalSettings.RrfK);
        await using (var cmd = db.DataSource.CreateCommand(
            "INSERT INTO prem_config.setting(tenant_id, key, value) VALUES(@tenant, @key, '0'::jsonb)"))
        {
            cmd.Parameters.AddWithValue("tenant", tenant);
            cmd.Parameters.AddWithValue("key", RetrievalSettings.RrfK);
            await cmd.ExecuteNonQueryAsync();
        }
        var unusable = await SearchAsync(connection);
        Assert.Equal(before.Top, unusable.Top);
        Assert.Single(unusable.Warnings);
        Assert.Contains(RetrievalSettings.RrfK, unusable.Warnings[0]);
        Assert.Contains(RetrievalSettings.Allowed(RetrievalSettings.RrfK), unusable.Warnings[0]);
    }

    [Fact]
    public async Task A_query_over_the_limit_ends_the_cli_search_with_the_sentence_and_nothing_recorded()
    {
        var connection = await server.CreateDatabaseAsync();
        await using var db = new PremagenticDatabase(connection);
        await db.InitializeAsync();
        await db.EnsureTenantAsync("t", "T");
        var tooLong = new string('z', QueryLimits.MaxLength + 1);

        var (code, output, errors) = await RunAsync(connection, "search", tooLong, "--unrestricted", "test");

        Assert.True(code == 1, $"exit {code}{Environment.NewLine}{output}{errors}");
        Assert.Contains(QueryLimits.SearchRefusal(tooLong)!, errors);
        Assert.DoesNotContain("   at ", errors);
        Assert.DoesNotContain("hits in", output);
        await using var cmd = db.DataSource.CreateCommand("SELECT count(*) FROM prem_config.retrieval_event");
        Assert.Equal(0L, await cmd.ExecuteScalarAsync());
    }

    [Fact]
    public async Task A_golden_question_over_the_limit_ends_prem_eval_in_one_line_before_it_runs()
    {
        var connection = await server.CreateDatabaseAsync();
        await using var db = new PremagenticDatabase(connection);
        await db.InitializeAsync();
        await db.EnsureTenantAsync("t", "T");
        var folder = Directory.CreateTempSubdirectory("premagentic-eval-").FullName;
        var golden = Path.Combine(folder, "golden-set.json");
        await File.WriteAllTextAsync(golden, JsonSerializer.Serialize(new[]
        {
            new { id = "long", question = new string('z', QueryLimits.MaxLength + 1), category = "operations",
                  expectedSourcePaths = new[] { "pub/hangar.md" }, forbiddenSourcePaths = Array.Empty<string>() },
        }));
        var report = Path.Combine(folder, "report.md");

        var (code, output, errors) = await RunAsync(connection, "eval", golden, report);

        Assert.True(code == 1, $"exit {code}{Environment.NewLine}{output}{errors}");
        Assert.Contains($"The golden set at {golden} has a question over 4,000 characters, 'long', which a search refuses.", errors);
        Assert.DoesNotContain("   at ", errors);
        Assert.False(File.Exists(report));
    }

    private static async Task<(double Top, string[] Warnings)> SearchAsync(string connection)
    {
        var (code, output, errors) = await RunAsync(connection, "search", "zeppelin", "--unrestricted", "test", "--top", "2");
        Assert.True(code == 0, $"exit {code}{Environment.NewLine}{output}{errors}");

        var scores = Score().Matches(output).Select(m => double.Parse(m.Groups[1].Value.Replace(',', '.'), CultureInfo.InvariantCulture)).ToArray();
        Assert.True(scores.Length > 0, output);
        return (scores.Max(), [.. errors.Split('\n').Where(l => l.StartsWith("WARNING:", StringComparison.Ordinal))]);
    }

    private static async Task<(int Code, string Output, string Errors)> RunAsync(string connection, params string[] arguments)
    {
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments.Prepend(CliPath()))
            start.ArgumentList.Add(argument);
        // Nothing from this process's own settings reaches the CLI.
        foreach (var name in start.Environment.Keys.Where(k => k.StartsWith("PREM_", StringComparison.OrdinalIgnoreCase)).ToArray())
            start.Environment.Remove(name);
        start.Environment["PREM_CONNECTION_STRING"] = connection;
        start.Environment["PREM_TENANT_KEY"] = "t";
        start.Environment["PREM_TENANT_NAME"] = "T";
        start.Environment["PREM_EMBEDDING_PROVIDER"] = "hash";
        start.Environment["PREM_HEADING_PREFIX"] = "0";

        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var (output, errors) = (await stdout, await stderr);
        return (process.ExitCode, output, errors);
    }

    // The CLI's own build output, recorded by the test project at build time (see
    // its project file). The copy of prem.dll in this folder cannot run on its
    // own: the assemblies beside it are this project's, not the CLI's.
    private static string CliPath()
    {
        var path = typeof(SearchCliTuningTests).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(attribute => attribute.Key == "Premagentic.Cli.Path")
            .Value;
        Assert.True(File.Exists(path), "the CLI was not built where the test project recorded it: " + path);
        return path!;
    }

    [GeneratedRegex(@"^\[(\d+[.,]\d+)\]", RegexOptions.Multiline)]
    private static partial Regex Score();
}

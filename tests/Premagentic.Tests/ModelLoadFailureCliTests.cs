using System.Diagnostics;
using System.Reflection;
using Premagentic.Core.Storage;

namespace Premagentic.Tests;

/// <summary>
/// The verbs that embed, run as a person runs them, over a model that cannot
/// be loaded: one sentence and exit code 2, never the runtime's stack trace.
/// Requires a running Docker daemon.
/// </summary>
public sealed class ModelLoadFailureCliTests(DatastoreTestDatabase server) : IClassFixture<DatastoreTestDatabase>
{
    [Fact]
    public async Task A_model_that_cannot_be_loaded_stops_the_search_with_one_sentence_and_exit_code_2_and_no_stack_trace()
    {
        var connection = await DatabaseAsync();
        var folder = DamagedModelFolder();
        try
        {
            var run = await RunAsync(connection, folder.FullName, "search", "zeppelin", "--unrestricted", "test");

            AssertRefused(run, "prem search stopped before it searched: ", folder.FullName);
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task A_model_that_cannot_be_loaded_stops_the_ingest_with_one_sentence_and_exit_code_2_and_no_stack_trace()
    {
        var connection = await DatabaseAsync();
        var folder = DamagedModelFolder();
        var documents = Directory.CreateTempSubdirectory("prem-cli-damaged-model-documents-");
        try
        {
            File.WriteAllText(Path.Combine(documents.FullName, "hangar.md"), "# Hangar\n\nThe zeppelin is moored in hangar two.\n");

            var run = await RunAsync(connection, folder.FullName, "ingest", documents.FullName, "--public");

            AssertRefused(run, "prem ingest stopped: ", folder.FullName);
        }
        finally
        {
            folder.Delete(recursive: true);
            documents.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task A_model_that_cannot_be_loaded_stops_the_eval_with_one_sentence_and_exit_code_2_no_stack_trace_and_no_report()
    {
        var connection = await DatabaseAsync();
        var folder = DamagedModelFolder();
        var reports = Directory.CreateTempSubdirectory("prem-cli-damaged-model-reports-");
        try
        {
            var golden = Path.Combine(reports.FullName, "golden-questions.json");
            File.WriteAllText(golden, "[]");
            var report = Path.Combine(reports.FullName, "report.md");

            var run = await RunAsync(connection, folder.FullName, "eval", golden, report);

            AssertRefused(run, "prem eval stopped: ", folder.FullName);
            Assert.False(File.Exists(report), "the refused eval wrote a report");
        }
        finally
        {
            folder.Delete(recursive: true);
            reports.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task A_vocabulary_that_cannot_be_loaded_stops_the_search_with_one_sentence_and_exit_code_2_and_no_stack_trace()
    {
        var connection = await DatabaseAsync();
        var folder = Directory.CreateTempSubdirectory("prem-cli-vocabulary-");
        try
        {
            File.WriteAllBytes(Path.Combine(folder.FullName, "model.onnx"), IdentityOnnxModel.Bytes());
            File.WriteAllText(Path.Combine(folder.FullName, "vocab.txt"), "[PAD]\n[UNK]\n[CLS]\n[SEP]\nhangar\n");

            var run = await RunAsync(connection, folder.FullName, "search", "zeppelin", "--unrestricted", "test");

            AssertRefusedWith(run,
                $"prem search stopped before it searched: The local embedding model in {folder.FullName} could not be loaded, " +
                "because its vocabulary, vocab.txt, could not be: ",
                "The vocabulary is damaged or is not the model's");
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    private async Task<string> DatabaseAsync()
    {
        var connection = await server.CreateDatabaseAsync();
        await using var db = new PremagenticDatabase(connection);
        await db.InitializeAsync();
        await db.EnsureTenantAsync("t", "T");
        return connection;
    }

    // A model folder whose model.onnx is invented bytes, not a model.
    private static DirectoryInfo DamagedModelFolder()
    {
        var folder = Directory.CreateTempSubdirectory("prem-cli-damaged-model-");
        File.WriteAllBytes(Path.Combine(folder.FullName, "model.onnx"), "not a model, invented bytes"u8.ToArray());
        File.WriteAllText(Path.Combine(folder.FullName, "vocab.txt"), "[PAD]\n[UNK]\n[CLS]\n[SEP]\nhangar\n");
        return folder;
    }

    // The model file's refusal, after the verb's opening words.
    private static void AssertRefused((int Exit, string Output, string Errors) run, string opening, string modelFolder) =>
        AssertRefusedWith(run, $"{opening}The local embedding model in {modelFolder} could not be loaded: ",
            "The model file is damaged or is not the model");

    // Exit code 2 and one line on standard error that starts as given and carries the remedy, with no stack trace.
    private static void AssertRefusedWith((int Exit, string Output, string Errors) run, string start, string remedy)
    {
        var (exit, output, errors) = run;
        Assert.Equal(StartupRefusedException.ExitCode, exit);
        Assert.StartsWith(start, errors.TrimStart(), StringComparison.Ordinal);
        Assert.Contains(remedy, errors, StringComparison.Ordinal);
        Assert.DoesNotContain("Unhandled exception", errors + output, StringComparison.Ordinal);
        Assert.DoesNotContain("   at ", errors + output, StringComparison.Ordinal);
        Assert.Single(errors.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    private static async Task<(int Exit, string Output, string Errors)> RunAsync(string connection, string modelFolder, params string[] arguments)
    {
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add(CliPath());
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        // Nothing from this process's own settings reaches the CLI.
        foreach (var name in start.Environment.Keys.Where(k => k.StartsWith("PREM_", StringComparison.OrdinalIgnoreCase)).ToArray())
            start.Environment.Remove(name);
        start.Environment["PREM_CONNECTION_STRING"] = connection;
        start.Environment["PREM_TENANT_KEY"] = "t";
        start.Environment["PREM_TENANT_NAME"] = "T";
        start.Environment["PREM_EMBEDDING_PROVIDER"] = "local";
        start.Environment["PREM_ONNX_MODEL_DIR"] = modelFolder;

        using var process = Process.Start(start)!;
        // Both streams are read to their end before the exit code is looked at:
        // under load a tool's last line arrives after it exits.
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, await stdout, await stderr);
    }

    // The CLI's own build output, recorded by the test project at build time.
    private static string CliPath()
    {
        var path = typeof(ModelLoadFailureCliTests).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(attribute => attribute.Key == "Premagentic.Cli.Path")
            .Value;
        Assert.True(File.Exists(path), "the CLI was not built where the test project recorded it: " + path);
        return path!;
    }
}

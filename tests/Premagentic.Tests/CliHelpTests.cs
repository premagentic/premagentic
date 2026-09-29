using System.Diagnostics;
using System.Reflection;
using System.Text.RegularExpressions;
using Premagentic.Cli;
using Premagentic.Cli.Admin;
using Premagentic.Cli.Setup;

namespace Premagentic.Tests;

/// <summary>
/// What <c>prem</c> says about itself, run as the built program with no
/// database configured: an agent learns the command line from its help, so the
/// help has to answer where nothing is set up yet. And the one usage text per
/// command: the flags it names are the flags its parser reads, and the manual
/// page is the text the program prints.
/// </summary>
public sealed class CliHelpTests
{
    public static TheoryData<string[], int, string> Answers()
    {
        const string main = "PremAgentic CLI: governed retrieval over an organization's own documents.";
        var data = new TheoryData<string[], int, string>
        {
            { [], 0, main },
            { ["--help"], 0, main },
            { ["-h"], 0, main },
            { ["help"], 0, main },
            { ["help", "users"], 0, "prem users add <sign-in-name> [--display \"Name\"] [--role administrator|auditor|member]" },
            { ["help", "--markdown"], 0, "# The prem command" },
            { ["eval", "--help"], 0, "prem eval <golden-questions.json> [report-path]" },
            { ["search", "--help"], 0, "prem search \"<query>\" [--user name | --with-token | --as a,b | --unrestricted reason] [--historical] [--top N]" },
            { ["section", "-h"], 0, "prem section <path> [heading] [--user name | --with-token | --as a,b | --unrestricted reason] [--historical]" },
            { ["ingest", "--help"], 0, "prem ingest <folder> [--public | --principals a,b | --entry \"allow group:Staff\" ...] [--prefix p]" },
            { ["migrate", "--help"], 0, "prem migrate" },
            { ["rebuild-index", "--help"], 0, "prem rebuild-index --confirm" },
            { ["users", "add", "--help"], 0, "prem users add <sign-in-name> [--display \"Name\"] [--role administrator|auditor|member]" },
            // A call a command cannot take gets the same text, on standard error, and exit 1.
            { ["search"], 1, "prem search \"<query>\" [--user name | --with-token | --as a,b | --unrestricted reason] [--historical] [--top N]" },
            { ["search", "hangar", "--topk", "3"], 1, "prem search does not take --topk." },
            { ["users", "rename", "sam"], 1, "prem users has no subcommand 'rename'." },
            { ["oauth", "tokens"], 1, "prem oauth has no subcommand 'tokens'." },
            { ["oauth", "clients", "add", "Desk", "--secret", "x"], 1, "prem oauth does not take --secret." },
            { ["rules", "set", "--everyone"], 1, "prem rules does not take --everyone." },
            { ["frobnicate"], 1, "Unknown command 'frobnicate'. prem --help lists every command." },
            // A call an extension could take waits for the extensions, and with no
            // database none can load, so the built-ins' refusal stands.
            { ["groups", "tally"], 1, "prem groups has no subcommand 'tally'." },
            // Asked for help, a built-in command answers as every other does.
            { ["groups", "tally", "--help"], 0, "prem groups add <name>" },
            { ["help", "frobnicate"], 1, "prem help takes --markdown or one command, and 'frobnicate' is neither." },
            { ["--frobnicate"], 1, "Unknown command '--frobnicate'. prem --help lists every command." },
            // A command the business add-on brings is named, whatever the call.
            { ["audit"], 1, "'prem audit' comes with the PremAgentic business add-on, and no extension loads until a database is configured." },
            { ["audit", "--help"], 1, "'prem audit' comes with the PremAgentic business add-on, and no extension loads until a database is configured." },
            { ["audit", "prune", "--plan"], 1, "'prem audit prune' comes with the PremAgentic business add-on, and no extension loads until a database is configured." },
            { ["help", "audit"], 1, "'prem audit' comes with the PremAgentic business add-on, and no extension loads until a database is configured." },
            // So is directory group mapping, which the add-on brings under the built-in groups.
            { ["groups", "map", "S-1-5-21-7", "Staff"], 1, "'prem groups map' comes with the PremAgentic business add-on, and no extension loads until a database is configured." },
            { ["groups", "unmap", "S-1-5-21-7"], 1, "'prem groups unmap' comes with the PremAgentic business add-on, and no extension loads until a database is configured." },
            { ["groups", "mappings"], 1, "'prem groups mappings' comes with the PremAgentic business add-on, and no extension loads until a database is configured." },
        };
        foreach (var verb in new[] { "users", "groups", "agents", "tokens", "oauth", "rules", "settings", "sources", "extensions", "profile", "reminders" })
        {
            data.Add([verb], 0, $"prem {verb} ");
            data.Add([verb, "--help"], 0, $"prem {verb} ");
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(Answers))]
    public async Task Help_and_a_bad_call_are_answered_before_any_database_is_asked_for(string[] args, int exit, string firstLine)
    {
        var (code, output, errors) = await RunAsync(args);

        Assert.True(code == exit, $"exit {code}, expected {exit}{Environment.NewLine}{output}{errors}");
        var printed = exit == 0 ? output : errors;
        Assert.StartsWith(firstLine, printed.ReplaceLineEndings("\n").Split('\n')[0]);
        // Nothing asked for a database: the refusal a connection would give is absent.
        Assert.DoesNotContain("cannot start", output + errors);
    }

    [Fact]
    public async Task The_version_is_printed_with_the_commit_it_was_built_from_and_no_database()
    {
        var (code, output, errors) = await RunAsync(["--version"]);

        Assert.True(code == 0, $"exit {code}{Environment.NewLine}{output}{errors}");
        Assert.Equal($"prem {Help.Version()}", output.Trim());
        // The informational version carries the commit after a plus sign, as the bug template asks.
        Assert.Matches(@"^prem \d+\.\d+\.\d+\S*\+[0-9a-f]{7,}$", output.Trim());
    }

    [Fact]
    public async Task The_main_usage_is_every_commands_text_and_each_command_prints_its_own()
    {
        var (_, all, _) = await RunAsync(["--help"]);
        all = all.ReplaceLineEndings("\n");

        foreach (var (verb, _, usage) in Help.Commands)
        {
            var own = usage.TrimEnd().ReplaceLineEndings("\n");
            Assert.Contains(own, all);
            if (verb is "setup" or "remove") continue;
            var (_, printed, _) = await RunAsync([verb, "--help"]);
            Assert.Equal(own, printed.ReplaceLineEndings("\n").TrimEnd());
        }
        Assert.Contains("PREM_TENANT_NAME", all);
        Assert.Contains("every command but setup, remove, --help and", all);
    }

    /// <summary>
    /// The files that read a command's flags, and the usage texts of the
    /// commands they read them for. Any other file under the CLI that holds a
    /// flag literal is a parser this list does not know, and fails the test.
    /// </summary>
    private static readonly (string[] Files, string[] Usages)[] Parsers =
    [
        (["Help.cs"], [Help.Header]),
        (["Program.cs", "Admin/CallerFlags.cs"], [Help.Migrate, Help.RebuildIndex, Help.Search, Help.Section, Help.Eval]),
        (["Admin/AdminCommands.cs"], [AdminCommands.Usage]),
        (["Admin/OAuthCommands.cs"], [OAuthCommands.Usage]),
        (["Admin/SourcesCommands.cs"], [SourcesCommands.Usage, SourcesCommands.IngestUsage]),
        (["Admin/SettingsCommands.cs"], [SettingsCommands.Usage]),
        (["Admin/ExtensionsCommands.cs"], [ExtensionsCommands.Usage]),
        (["Admin/ProfileCommands.cs"], [ProfileCommands.Usage]),
        (["Admin/RemindersCommands.cs"], [RemindersCommands.Usage]),
        (["Setup/SetupCommand.cs"], [SetupCommand.Usage]),
        (["Setup/RemoveCommand.cs"], [RemoveCommand.Usage]),
    ];

    /// <summary>
    /// Files whose flag literals are another program's arguments: the
    /// PostgreSQL tools the bundled server is made and stopped with, and the
    /// messages setup and remove print about their own flags.
    /// </summary>
    private static readonly string[] NotParsers = ["Setup/BundledPostgres.cs", "Setup/SetupEngine.cs", "Setup/RemovalEngine.cs"];

    [Fact]
    public void The_flags_a_usage_names_are_the_flags_its_parser_reads()
    {
        var cli = Path.Combine(RepositoryRoot(), "src", "Premagentic.Cli");
        var examined = new List<string>();

        foreach (var (files, usages) in Parsers)
        {
            var read = files.SelectMany(f => FlagLiterals(File.ReadAllText(Path.Combine(cli, f)))).ToHashSet();
            var named = usages.SelectMany(Help.FlagsIn).ToHashSet();
            // --help and -h are answered for every command before it runs.
            read.Remove("--help");
            named.Remove("--help");
            examined.AddRange(files);

            var unnamed = read.Except(named).Order().ToArray();
            var unread = named.Except(read).Order().ToArray();
            Assert.True(unnamed.Length == 0 && unread.Length == 0,
                $"{string.Join(", ", files)}: read but not in the usage [{string.Join(' ', unnamed)}], " +
                $"in the usage but never read [{string.Join(' ', unread)}]");
        }

        // A parser this list does not know is a finding, not a pass.
        var stray = Directory.EnumerateFiles(cli, "*.cs", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(cli, f).Replace('\\', '/'))
            .Where(f => !f.StartsWith("bin/") && !f.StartsWith("obj/"))
            .Where(f => FlagLiterals(File.ReadAllText(Path.Combine(cli, f))).Any())
            .Except(examined).Except(NotParsers)
            .ToArray();
        Assert.True(stray.Length == 0, "flag literals in a file no usage covers: " + string.Join(", ", stray));
    }

    [Fact]
    public void The_manual_page_is_the_text_the_program_prints()
    {
        var page = File.ReadAllText(Path.Combine(RepositoryRoot(), "docs", "cli.md")).ReplaceLineEndings("\n");
        Assert.True(page == Help.Markdown().ReplaceLineEndings("\n"),
            "docs/cli.md is not what 'prem help --markdown' prints. Regenerate it: prem help --markdown > docs/cli.md");
    }

    /// <summary>
    /// The texts are raw string literals, which carry the line endings of the
    /// source file they are in, and a Windows checkout can give that file
    /// either kind. What the program prints must not depend on it.
    /// </summary>
    [Fact]
    public void The_program_prints_the_same_text_whatever_line_endings_its_source_was_checked_out_with()
    {
        Assert.DoesNotContain('\r', Help.Markdown());
        Assert.DoesNotContain('\r', Help.Usage());
        foreach (var (verb, _, _) in Help.Commands) Assert.DoesNotContain('\r', Help.For(verb)!);
    }

    /// <summary>The <c>--flag</c> string literals in C# source, outside raw string literals, which is where the usage texts are.</summary>
    internal static IEnumerable<string> FlagLiterals(string source)
    {
        var code = Regex.Replace(source, "\"\"\"[\\s\\S]*?\"\"\"", "");
        return Regex.Matches(code, "\"(--[a-z][a-z0-9-]*)").Select(m => m.Groups[1].Value).Distinct();
    }

    private static string RepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Premagentic.slnx"))) return dir.FullName;
        throw new InvalidOperationException("The repository root (Premagentic.slnx) is not above " + AppContext.BaseDirectory);
    }

    /// <summary>
    /// A configuration that is set and cannot be used is refused with its own
    /// sentence and exit code 2, as it always was, even for a call that waits
    /// for the extensions: only where nothing is configured does such a call
    /// get the built-ins' refusal or the sentence naming the add-on.
    /// </summary>
    [Theory]
    [InlineData("missing", "audit prune", "prem cannot start: PREM_CREDENTIALS_FILE names ")]
    [InlineData("missing", "frobnicate", "prem cannot start: PREM_CREDENTIALS_FILE names ")]
    [InlineData("both", "audit prune", "prem cannot start: Both PREM_CREDENTIALS_FILE and PREM_CONNECTION_STRING are set.")]
    public async Task A_configuration_that_cannot_be_used_is_refused_whatever_the_call(string configuration, string call, string refusal)
    {
        var missing = Path.Combine(Directory.CreateTempSubdirectory("prem-no-credentials-").FullName, "app.credentials");
        var environment = new Dictionary<string, string> { ["PREM_CREDENTIALS_FILE"] = missing };
        if (configuration == "both") environment["PREM_CONNECTION_STRING"] = "Host=127.0.0.1;Database=none";

        var (code, output, errors) = await RunAsync(call.Split(' '), environment);

        Assert.True(code == 2, $"exit {code}{Environment.NewLine}{output}{errors}");
        Assert.StartsWith(refusal, errors.ReplaceLineEndings("\n").Split('\n')[0]);
        Assert.DoesNotContain("comes with the PremAgentic business add-on", errors);
    }

    private static async Task<(int Code, string Output, string Errors)> RunAsync(
        string[] arguments, IReadOnlyDictionary<string, string>? environment = null)
    {
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add(CliPath());
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        // No database: every PREM_ variable this process has is removed.
        foreach (var name in start.Environment.Keys.Where(k => k.StartsWith("PREM_", StringComparison.OrdinalIgnoreCase)).ToArray())
            start.Environment.Remove(name);
        foreach (var (name, value) in environment ?? new Dictionary<string, string>()) start.Environment[name] = value;

        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, await stdout, await stderr);
    }

    private static string CliPath()
    {
        var path = typeof(CliHelpTests).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(attribute => attribute.Key == "Premagentic.Cli.Path")
            .Value;
        Assert.True(File.Exists(path), "the CLI was not built where the test project recorded it: " + path);
        return path!;
    }
}

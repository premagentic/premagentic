using System.Text;
using Premagentic.Cli.Admin;
using Premagentic.Cli.Setup;
using Premagentic.Core.Extensions;

namespace Premagentic.Cli;

/// <summary>
/// What <c>prem</c> says about itself, answered before any database step, so
/// an agent or a person can learn the command line on a machine where nothing
/// is configured yet.
/// <para>
/// Each command has one usage text, and it is printed everywhere that command
/// is described: in the main usage, by <c>prem &lt;command&gt; --help</c>, and on
/// a call it cannot take. The flags a command accepts are the ones its text
/// names; any other is refused here, with the text, before the command runs.
/// The manual page <c>docs/cli.md</c> is <see cref="Markdown"/>, so the program
/// and the manual cannot say different things.
/// </para>
/// </summary>
internal static class Help
{
    public const string Header = """
        PremAgentic CLI: governed retrieval over an organization's own documents.

        prem --help                (also prem -h and prem help) this text
        prem <command> --help      (also prem help <command>) one command's part of it
        prem --version             the version, with the commit it was built from
        prem help --markdown       this text as the manual page, docs/cli.md
        """;

    public const string Migrate = """
        prem migrate
        prem init-db

          Applies the numbered migrations this build carries, or says the schema is current, and makes
          sure the tenant exists. Every other command does the same before it runs. init-db is another
          name for migrate.
        """;

    public const string RebuildIndex = """
        prem rebuild-index --confirm

          Empties every index table so the next ingest of each source rebuilds it. Users, settings and the
          audit trail are not touched. Search returns nothing until each source is ingested again.

          --confirm              required; without it nothing is emptied
        """;

    // The caller flags search and section share, explained in each of them.
    private const string CallerOptions = """
          --user name            as this PremAgentic user, with the user's groups as they are now
          --with-token           as the agent whose token is read from standard input or a prompt, never
                                 from an argument, with that agent's trust policy
          --as a,b               as a caller holding these principals, by name: group:Staff, user:alice
          --unrestricted reason  past the access gate; the reason is recorded on the event. Give at most
                                 one of --user, --with-token, --as and --unrestricted; with none, only what
                                 everyone may read

        """;

    public const string Search = """
        prem search "<query>" [--user name | --with-token | --as a,b | --unrestricted reason] [--historical] [--top N]

          Searches the index as one caller and prints the passages that caller may read, best first, each
          with its citation, its trust, its authorship and whether it is stale.


        """ + CallerOptions + """
          --historical           include superseded and archived documents
          --top N                how many passages to print (default: 5)
        """;

    public const string Section = """
        prem section <path> [heading] [--user name | --with-token | --as a,b | --unrestricted reason] [--historical]

          Prints one indexed document, or its section under a heading, when the caller may read it.


        """ + CallerOptions + """
          --historical           allow a superseded or archived document
        """;

    public const string Eval = """
        prem eval <golden-questions.json> [report-path]

          Runs a golden set: each question is searched as the caller it names, under the tuning the
          deployment has stored, and the passages it expects, forbids and expects to be absent are checked.
          Writes a Markdown report and says where (default: report-<date>.md in the current folder), never
          into a folder that holds a profile, and exits 1 when any question fails.
        """;

    public const string WhoMayRead = """
        Who may read a document is decided by folder rules (prem rules), and a
        folder no rule covers is readable by nobody. Search defaults to what
        everyone may read. There is no flag that quietly widens either one.
        """;

    public const string FromExtensions = """
        An extension can add commands and settings. prem extensions list names
        what the loaded ones add, and prem <command> --help prints one of them
        once a database is configured, since that is where the extensions
        allowed to load are kept.
        """;

    public const string Environment = """
        Environment:
          PREM_CREDENTIALS_FILE    the app.credentials file prem setup wrote; how an installed deployment connects
          PREM_CONNECTION_STRING   a PostgreSQL connection string instead; set one of these two, not both
          PREM_DEV_DATABASE=1      the local development database in docker-compose.yml, when neither is set
                                     With none of the three, every command but setup, remove, --help and
                                     --version refuses to start.
          PREM_EMBEDDING_PROVIDER  local | openai | hash, or one an extension registered (default: local, fully offline)
          PREM_ONNX_MODEL_DIR      (default: models/minilm under the current folder, then beside the
                                     program and each folder above it)
          PREM_EXTENSIONS_DIR      the folder whose subfolders hold extensions, when the setting
                                     extensions.folder does not say. Neither one means no extensions.
          PREM_TENANT_KEY          (default: default)
          PREM_TENANT_NAME         the deployment's name, used when its tenant is first made
                                     (default: Premagentic deployment)
          PREM_HEADING_PREFIX      0 disables the "title > heading" embedding context prefix (default on)
        """;

    /// <summary>Every command, in the order the main usage lists them, with its usage text.</summary>
    public static readonly IReadOnlyList<(string Verb, string Summary, string Usage)> Commands =
    [
        ("migrate", "The schema this build carries.", Migrate),
        ("setup", "Installs the database side of PremAgentic on a PostgreSQL that already exists.", SetupCommand.Usage),
        ("remove", "Takes away what setup registered, and keeps the data unless told otherwise.", RemoveCommand.Usage),
        ("rebuild-index", "Empties the index so every source can be ingested again.", RebuildIndex),
        ("ingest", "Reads a folder, or a registered source, into the index.", SourcesCommands.IngestUsage),
        ("search", "Searches as one caller.", Search),
        ("section", "Reads one document, or one section of it, as one caller.", Section),
        ("eval", "Runs a golden set against the index.", Eval),
        ("users", "The people who sign in.", AdminCommands.UsersUsage),
        ("groups", "Groups of people.", AdminCommands.GroupsUsage),
        ("agents", "The agents that read with a token.", AdminCommands.AgentsUsage),
        ("tokens", "Agent tokens.", AdminCommands.TokensUsage),
        ("oauth", "The assistants that connect through the MCP authorization flow, and their grants.", OAuthCommands.Usage),
        ("rules", "Folder rules: who may read what.", AdminCommands.RulesUsage),
        ("settings", "The deployment's settings and the change record.", SettingsCommands.Usage),
        ("sources", "Registered folders, their settings, their runs, and the chunkers they may name.", SourcesCommands.Usage),
        ("extensions", "What this deployment loads from its extensions folder, and what it allows.", ExtensionsCommands.Usage),
        ("profile", "A folder of plain files carrying a whole configuration.", ProfileCommands.Usage),
        ("reminders", "What each source owner is reminded of.", RemindersCommands.Usage),
    ];

    /// <summary>The whole usage: every command's text, what decides reading, and the environment.</summary>
    public static string Usage()
    {
        var text = new StringBuilder(Header).Append("\n\n");
        foreach (var (_, _, usage) in Commands) text.Append(usage.TrimEnd()).Append("\n\n");
        return Lines(text.Append(WhoMayRead).Append("\n\n").Append(FromExtensions).Append("\n\n").Append(Environment).ToString());
    }

    /// <summary>The usage text of one command, or null for a word that is not one.</summary>
    public static string? For(string verb) =>
        (verb == "init-db" ? Migrate : Commands.FirstOrDefault(c => c.Verb == verb).Usage) is { } usage ? Lines(usage) : null;

    /// <summary>
    /// The texts are raw string literals, which take the line endings of the
    /// file they are written in, and a checkout may give that file either kind.
    /// Everything this class returns ends its lines with a line feed alone, so
    /// what the program prints, and the manual page made from it, is the same
    /// on every checkout.
    /// </summary>
    private static string Lines(string text) => text.ReplaceLineEndings("\n");

    /// <summary>The flags a usage text names, which are the flags its command accepts.</summary>
    public static IReadOnlySet<string> FlagsIn(string usage) => CommandLine.FlagsIn(usage);

    /// <summary>
    /// The subcommands a usage text names: the word after <c>prem verb</c> on
    /// each of its lines, split at <c>|</c>. Empty for a command that has none.
    /// </summary>
    public static IReadOnlySet<string> SubcommandsIn(string verb, string usage) => CommandLine.SubcommandsIn(verb, usage);

    /// <summary>
    /// The version this build carries, with the commit it was built from when
    /// the build knew it: the one value the API and the MCP server report too.
    /// </summary>
    public static string Version() => Premagentic.Core.BuildVersion.Informational;

    /// <summary>
    /// Answers a call about the command line itself, and refuses a call a
    /// command's usage does not allow, before anything connects. Returns the
    /// exit code when it answered, or null when the command should run.
    /// <c>setup</c> and <c>remove</c> check their own arguments, so they are
    /// passed through; their texts are still part of the main usage.
    /// <para>
    /// A call that names no built-in command, or a word a built-in command
    /// that takes an extension's subcommands does not have, may be a command
    /// an extension adds, which is known only once the extensions load. It is
    /// passed through with <paramref name="notBuiltIn"/> set to the refusal the
    /// built-ins alone would give, for the caller to give when no extension
    /// added it.
    /// </para>
    /// </summary>
    public static int? Answer(string[] args, TextWriter output, TextWriter error, out string? notBuiltIn)
    {
        notBuiltIn = null;
        if (args.Length == 0 || args[0] is "--help" or "-h")
        {
            output.WriteLine(Usage());
            return 0;
        }
        if (args[0] == "help")
        {
            if (args.Length == 1) { output.WriteLine(Usage()); return 0; }
            if (args is [_, "--markdown"]) { output.Write(Markdown()); return 0; }
            if (args.Length == 2 && For(args[1]) is { } one) { output.WriteLine(one.TrimEnd()); return 0; }
            if (args.Length == 2 && CommandLine.IsWord(args[1]))
            {
                notBuiltIn = $"prem help takes --markdown or one command, and '{args[1]}' is neither.\n\n{Header}";
                return null;
            }
            error.WriteLine($"prem help takes --markdown or one command, and '{string.Join(' ', args[1..])}' is neither.\n\n{Header}");
            return 1;
        }
        if (args[0] == "--version")
        {
            output.WriteLine($"prem {Version()}");
            return 0;
        }

        var verb = args[0];
        if (verb is "setup" or "remove") return null;
        var usage = For(verb);
        if (usage is null)
        {
            var notACommand = $"Unknown command '{verb}'. prem --help lists every command.";
            if (CommandLine.IsWord(verb))
            {
                notBuiltIn = notACommand;
                return null;
            }
            error.WriteLine(notACommand);
            return 1;
        }
        usage = usage.TrimEnd();

        // A word a command that takes an extension's subcommands does not have
        // may be one an extension adds; that is decided once they load. Asked
        // for help, the built-in command answers here, as every other does.
        if (BuiltInCommands.Open.ContainsKey(verb) && args.Length > 1 && CommandLine.IsWord(args[1])
            && !SubcommandsIn(verb, usage).Contains(args[1]) && !args.Contains("--help") && !args.Contains("-h"))
        {
            notBuiltIn = $"prem {verb} has no subcommand '{args[1]}'.\n\n{usage}";
            return null;
        }

        if (args.Contains("--help") || args.Contains("-h"))
        {
            output.WriteLine(usage);
            return 0;
        }

        var subcommands = SubcommandsIn(verb, usage);
        if (subcommands.Count > 0)
        {
            if (args.Length == 1)
            {
                output.WriteLine(usage);
                return 0;
            }
            if (!subcommands.Contains(args[1]))
            {
                error.WriteLine($"prem {verb} has no subcommand '{args[1]}'.\n\n{usage}");
                return 1;
            }
        }
        else if (verb is "ingest" or "search" or "section" or "eval" && args.Length == 1)
        {
            error.WriteLine(usage);
            return 1;
        }

        var accepted = FlagsIn(usage);
        var unknown = args.Skip(1).FirstOrDefault(a => a.StartsWith("--", StringComparison.Ordinal) && !accepted.Contains(a));
        if (unknown is not null)
        {
            error.WriteLine($"prem {verb} does not take {unknown}.\n\n{usage}");
            return 1;
        }
        return null;
    }

    /// <summary>
    /// The manual page <c>docs/cli.md</c>: every command's usage text as the
    /// program prints it. A test compares the file with this.
    /// </summary>
    public static string Markdown()
    {
        var page = new StringBuilder();
        page.Append("""
            # The prem command

            Every `prem` command with its arguments and options, in the program's own words. `prem --help` prints all of it, `prem <command> --help` prints one command's part, and a command called with a subcommand or an option it does not take prints its part and exits 1. For a built-in command none of these needs a database; for a command an extension adds, they are answered once the extensions load, which needs one. This page is generated by `prem help --markdown`, and a test fails when the page and the program differ.

            The database a command uses, and the embedding provider, come from the environment; the variables are listed at the end of this page and on [Configuration](configuration.md).

            ## Who may read, in the program's words

            """).Append('\n');
        Fence(page, WhoMayRead);
        page.Append("## Asking the program\n\n");
        Fence(page, Header);
        foreach (var (verb, summary, usage) in Commands)
        {
            page.Append($"## prem {verb}\n\n{summary}\n\n");
            Fence(page, usage);
        }
        page.Append("## Commands an extension adds\n\n");
        Fence(page, FromExtensions);
        page.Append("## Environment\n\n");
        Fence(page, Environment);
        return Lines(page.ToString()).TrimEnd('\n') + "\n";
    }

    private static void Fence(StringBuilder page, string text) =>
        page.Append("```\n").Append(text.TrimEnd().ReplaceLineEndings("\n")).Append("\n```\n\n");
}

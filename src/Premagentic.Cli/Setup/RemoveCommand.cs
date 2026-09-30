using Premagentic.Core.Storage;

namespace Premagentic.Cli.Setup;

/// <summary>
/// The command line for <see cref="RemovalEngine"/>. Written for a person at a
/// prompt and for an uninstaller that shows what it prints: one line per step,
/// a label a program can match at the start of each, nothing secret, and one
/// closing sentence. Exit codes: 0 done (or a plan), 1 a purge not confirmed or
/// a step that failed, 2 refused before the step it names.
/// </summary>
internal static class RemoveCommand
{
    public const string Usage = """
        prem remove [--plan] [--purge [--yes]] [--credentials-dir dir] [--windows-service]
                    [--admin-connection-file file] [--bundled-postgres folder] [--data-dir folder]

        Takes away what prem setup registered, and keeps the data. The Windows
        services Premagentic and PremagenticDb are stopped and removed where they
        exist (from an elevated prompt); on Linux the systemd steps are printed for
        root to run. The database, its three roles, the credentials files and a
        bundled server's data folder all stay, so prem setup run again uses them.

          --plan                   report every step and change nothing
          --purge                  delete the data as well: drop the database as its owner, from owner.credentials,
                                   and the three roles as the admin role, stop the bundled server and delete its data
                                   folder, then delete the credentials files. Every document, user, setting and the
                                   audit trail goes, and it cannot be undone
          --yes                    carry out --purge; without it the purge is listed and nothing is changed
          --credentials-dir dir    where setup wrote the credentials files (default: as for prem setup)
          --windows-service        the install was made with --windows-service, so the defaults are in C:\ProgramData
          --admin-connection-file  a file holding a line connection=<connection string> for a role that can drop
                                   roles, for --purge on a PostgreSQL that existed before PremAgentic. The owner
                                   cannot: setup makes it without that right. A bundled server's superuser is read
                                   from postgres.credentials instead
          --bundled-postgres dir   the pgsql folder of the bundled PostgreSQL, to stop a server started by hand
          --data-dir dir           the bundled server's data folder (default: as for prem setup, when the install
                                   has a bundled server)

        The admin connection comes from --admin-connection-file or from
        PREM_SETUP_ADMIN_CONNECTION, as for setup, and is never printed.
        """;

    public static async Task<int> RunAsync(string[] args, CancellationToken ct = default)
    {
        if (args.Contains("--help") || args.Contains("-h"))
        {
            Console.WriteLine(Usage);
            return 0;
        }
        string[] flags = ["--plan", "--purge", "--yes", "--windows-service"];
        string[] valued = ["--credentials-dir", "--admin-connection-file", "--bundled-postgres", "--data-dir"];
        for (var i = 0; i < args.Length; i++)
        {
            if (flags.Contains(args[i])) continue;
            if (valued.Contains(args[i]) && Value(args, args[i]) is not null) { i++; continue; }
            Console.Error.WriteLine($"prem remove does not take '{args[i]}'{(valued.Contains(args[i]) ? " without a value" : "")}.\n\n{Usage}");
            return 2;
        }

        var purge = args.Contains("--purge");
        var yes = args.Contains("--yes");
        if (yes && !purge)
        {
            Console.Error.WriteLine("--yes confirms --purge, and was given without it. Nothing was changed.");
            return 2;
        }

        string? adminConnection;
        var adminFile = Value(args, "--admin-connection-file");
        try
        {
            adminConnection = adminFile is not null
                ? CredentialsFile.ReadConnectionString(adminFile)
                : Environment.GetEnvironmentVariable("PREM_SETUP_ADMIN_CONNECTION");
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Cannot read the admin connection from {adminFile}: {ex.Message}");
            return 2;
        }

        // The same defaults as setup, so a remove finds what a setup with the same
        // flags made.
        var premFolder = InstallFolders.Credentials(args.Contains("--windows-service"));
        var credentials = Value(args, "--credentials-dir") ?? premFolder;
        var bundled = Value(args, "--bundled-postgres");
        var bundledInstall = bundled is not null || Value(args, "--data-dir") is not null
                             || File.Exists(Path.Combine(credentials, InstallFiles.SuperuserCredentials));
        var options = new RemovalOptions(
            CredentialsDirectory: credentials,
            Purge: purge,
            Plan: args.Contains("--plan") || (purge && !yes),
            AdminConnectionString: string.IsNullOrWhiteSpace(adminConnection) ? null : adminConnection,
            BundledPostgres: bundled,
            DataDirectory: bundledInstall ? Value(args, "--data-dir") ?? Path.Combine(premFolder, "data") : null);

        var planOnly = args.Contains("--plan");
        try
        {
            if (options.Plan)
            {
                await new RemovalEngine(options, new HeadedWriter(Console.Out, purge
                    ? "prem remove --purge, the full list: nothing will be changed.\n"
                    : "prem remove, plan only: nothing will be changed.\n")).RunAsync(ct);
                Console.WriteLine();
                if (purge && !planOnly)
                {
                    Console.Error.WriteLine("This deletes every document, user, setting and the audit trail, and cannot be undone. Nothing " +
                                            "was changed. To go ahead, run the same command with --yes.");
                    return 1;
                }
                Console.WriteLine(purge ? "Plan: the purge above. Nothing was changed." : "Plan: the steps above. Nothing was changed.");
                return 0;
            }

            // A purge prints the whole list before it changes anything.
            if (purge)
                await new RemovalEngine(options with { Plan = true }, new HeadedWriter(Console.Out, "prem remove --purge, the full list:\n")).RunAsync(ct);

            var engine = new RemovalEngine(options, new HeadedWriter(Console.Out,
                purge ? "\nprem remove --purge --yes: removing.\n" : "prem remove\n"));
            await engine.RunAsync(ct);
            Console.WriteLine();
            if (engine.Failed)
            {
                Console.Error.WriteLine($"prem remove stopped at '{engine.Steps.Last(s => s.Outcome == RemovalOutcome.Failed).Name}'. " +
                                        "Fix the cause and run it again; what was removed stays removed.");
                return 1;
            }
            Console.WriteLine(purge
                ? "Purge complete: the steps above are done."
                : "Removed what setup registered. The data above stays; prem setup uses it again, and prem remove --purge --yes deletes it.");
            return 0;
        }
        catch (StartupRefusedException refused)
        {
            Console.Error.WriteLine($"prem remove refused: {refused.Message}");
            return StartupRefusedException.ExitCode;
        }
    }

    private static string? Value(string[] args, string flag)
    {
        var index = Array.IndexOf(args, flag);
        return index >= 0 && index + 1 < args.Length && !args[index + 1].StartsWith("--") ? args[index + 1] : null;
    }

    /// <summary>
    /// Writes its heading just before the first step line. The engine makes its
    /// whole list before it writes a line, so a run refused while making it
    /// prints the refusal alone, under no heading a wrapper would show empty.
    /// </summary>
    private sealed class HeadedWriter(TextWriter inner, string heading) : TextWriter
    {
        private bool _started;

        public override System.Text.Encoding Encoding => inner.Encoding;

        public override void Write(char value)
        {
            Start();
            inner.Write(value);
        }

        public override void Write(string? value)
        {
            Start();
            inner.Write(value);
        }

        public override void WriteLine(string? value)
        {
            Start();
            inner.WriteLine(value);
        }

        private void Start()
        {
            if (_started) return;
            _started = true;
            inner.WriteLine(heading);
        }
    }
}

using Premagentic.Core.Embeddings;
using Premagentic.Core.Storage;

namespace Premagentic.Cli.Setup;

/// <summary>
/// The command line for <see cref="SetupEngine"/>. Everything is a flag, and the
/// one secret it takes, the admin role's connection, comes from the environment
/// or a file, never from the command line, where process listings and shell
/// history would keep it.
/// </summary>
internal static class SetupCommand
{
    public const string Usage = """
        prem setup [--plan] [--database name] [--owner-role name] [--app-role name] [--search-role name]
                     [--credentials-dir dir] [--admin-connection-file file]
                     [--admin-user name] [--admin-password-file file]
                     [--host-name name] [--https-port port]
                     [--windows-service] [--api-path file]
                     [--bundled-postgres folder --data-dir folder [--postgres-port port]]

        Installs PremAgentic on a PostgreSQL that already exists (14 or later), then
        searches as the application role to prove it works. Safe to run again: a
        finished install is left as it is, and a half-finished one is completed.

          --plan                   report every step and change nothing
          --database name          the database to create or use (default: premagentic)
          --owner-role name        owns the schema; runs migrations and rebuild-index (default: premagentic_owner)
          --app-role name          reads and writes rows, nothing else (default: premagentic_app)
          --search-role name       reads documents and chunks for a caller, nothing else (default: premagentic_search)
          --credentials-dir dir    where the credentials files, the HTTPS certificate and its settings are
                                   written (default: the Premagentic folder in this account's local application
                                   data, or C:\ProgramData\Premagentic with --windows-service)
          --admin-connection-file  a file holding a line connection=<connection string> for a role that
                                   can create roles and databases
          --admin-user name        make this sign-in name the first administrator, when no administrator
                                   exists yet. Its password is asked for, or read from --admin-password-file
          --admin-password-file    a file whose first line is the first administrator's password
          --host-name name         the name clients use for the API; the self-signed HTTPS certificate is
                                   made for it (default: this computer's name)
          --https-port port        the API's HTTPS port (default: 8443)
          --windows-service        register the API as the Windows service Premagentic, under its own
                                   virtual account (needs an elevated prompt; the service is not started)
          --api-path file          the API program the service runs (default: Premagentic.Api.exe beside prem)
          --bundled-postgres dir   the pgsql folder of the PostgreSQL bundled with PremAgentic on Windows. Setup
                                   makes a cluster in --data-dir, listening on localhost only, starts it (as a
                                   service with --windows-service), and uses its superuser instead of an admin
                                   connection. The superuser's password goes to postgres.credentials, readable by
                                   this account and administrators only. Needs the Visual C++ 2015 to 2022 x64 runtime
          --data-dir dir           where the bundled server keeps its data (default: the Premagentic folder in
                                   C:\ProgramData with --windows-service, else in this account's application data)
          --postgres-port port     the bundled server's port on localhost (default: 5432)

        The admin connection comes from --admin-connection-file or from
        PREM_SETUP_ADMIN_CONNECTION. Generated passwords are written only to the
        credentials files and the HTTPS settings, and are never printed. Point the
        application at its file with PREM_CREDENTIALS_FILE; point it at
        owner.credentials only to migrate or rebuild the index. Every connection
        setup writes uses GSS Encryption Mode=Disable unless the admin connection
        sets that key itself.
        """;

    public static async Task<int> RunAsync(string[] args, CancellationToken ct = default)
    {
        if (args.Contains("--help") || args.Contains("-h"))
        {
            Console.WriteLine(Usage);
            return 0;
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
        var bundled = Value(args, "--bundled-postgres");
        if (bundled is not null && !string.IsNullOrWhiteSpace(adminConnection))
        {
            Console.Error.WriteLine("--bundled-postgres makes its own superuser, so it takes no admin connection. Pass one or the other.");
            return 2;
        }
        if (bundled is null && string.IsNullOrWhiteSpace(adminConnection))
        {
            Console.Error.WriteLine(
                "prem setup needs the connection of a PostgreSQL role that can create roles and databases. " +
                "Set PREM_SETUP_ADMIN_CONNECTION or pass --admin-connection-file.\n\n" + Usage);
            return 2;
        }

        var provider = Environment.GetEnvironmentVariable("PREM_EMBEDDING_PROVIDER")?.ToLowerInvariant() ?? "local";
        var modelDirectory = provider == "local"
            ? ModelFolder.FromEnvironment() is var model && model.Found is { } found ? found : model.LookedIn[0]
            : null;

        var httpsPort = Value(args, "--https-port");
        var postgresPort = Value(args, "--postgres-port");
        foreach (var (flag, value) in new[] { ("--https-port", httpsPort), ("--postgres-port", postgresPort) })
            if (value is not null && !(int.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var port) && port is > 0 and < 65536))
            {
                Console.Error.WriteLine($"{flag} takes a port number, not '{value}'.");
                return 2;
            }

        var windowsService = args.Contains("--windows-service");
        var passwordFile = Value(args, "--admin-password-file");
        var premFolder = InstallFolders.Credentials(windowsService);
        var defaults = new SetupOptions("", "", providers => EmbeddingProviderFactory.FromEnvironment(providers), null);
        var options = new SetupOptions(
            AdminConnectionString: adminConnection ?? "",
            CredentialsDirectory: Value(args, "--credentials-dir") ?? premFolder,
            EmbedderFactory: providers => EmbeddingProviderFactory.FromEnvironment(providers),
            RequiredModelDirectory: modelDirectory,
            DatabaseName: Value(args, "--database") ?? defaults.DatabaseName,
            OwnerRole: Value(args, "--owner-role") ?? defaults.OwnerRole,
            AppRole: Value(args, "--app-role") ?? defaults.AppRole,
            SearchRole: Value(args, "--search-role") ?? defaults.SearchRole,
            Plan: args.Contains("--plan"),
            TenantKey: Environment.GetEnvironmentVariable("PREM_TENANT_KEY") ?? defaults.TenantKey,
            TenantName: Environment.GetEnvironmentVariable("PREM_TENANT_NAME") ?? defaults.TenantName,
            AdministratorName: Value(args, "--admin-user"),
            AdministratorPassword: () => passwordFile is not null
                ? ReadPasswordFile(passwordFile)
                : Admin.ConsoleSecrets.Read("Password for the first administrator: ", confirm: true),
            HostName: Value(args, "--host-name") ?? System.Net.Dns.GetHostName(),
            HttpsPort: httpsPort is null ? defaults.HttpsPort : int.Parse(httpsPort, System.Globalization.CultureInfo.InvariantCulture),
            WindowsService: windowsService,
            ApiExecutable: Value(args, "--api-path"),
            BundledPostgres: bundled,
            DataDirectory: bundled is null ? null : Value(args, "--data-dir") ?? Path.Combine(premFolder, "data"),
            PostgresPort: postgresPort is null ? defaults.PostgresPort : int.Parse(postgresPort, System.Globalization.CultureInfo.InvariantCulture));

        Console.WriteLine(options.Plan
            ? "prem setup, plan only: nothing will be changed.\n"
            : "prem setup\n");
        var report = await new SetupEngine(options, Console.Out).RunAsync(ct);

        Console.WriteLine();
        if (report.Failed)
        {
            Console.Error.WriteLine($"Setup stopped at '{report.Steps.Last(s => s.Outcome == StepOutcome.Failed).Name}'. " +
                                    "Fix the cause and run it again; the steps already done are kept.");
            return 1;
        }
        if (options.Plan)
        {
            var planned = report.Steps.Count(s => s.Outcome == StepOutcome.WouldApply);
            Console.WriteLine(planned == 0
                ? "Plan: nothing to change. The install is complete."
                : $"Plan: {planned} step(s) would change the install. Nothing was changed.");
            return 0;
        }
        Console.WriteLine(report.Changed
            ? $"Setup complete. The application connects with PREM_CREDENTIALS_FILE={options.AppCredentialsPath}\n" +
              $"and serves https://{options.HostName}:{options.HttpsPort} with the settings in {options.KestrelSettingsPath}."
            : "Nothing changed: the install was already complete, and the health check passed.");
        return 0;
    }

    /// <summary>
    /// The first line of the file, without its line ending, read as the user
    /// verbs read theirs. Nothing else in the file is used, as the flag is
    /// documented. A refusal is said here and the step then fails as it does
    /// for a password not given.
    /// </summary>
    internal static string? ReadPasswordFile(string path)
    {
        try
        {
            return Admin.ConsoleSecrets.ReadFile(path, aloneOnItsLine: false);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return null;
        }
    }

    private static string? Value(string[] args, string flag)
    {
        var index = Array.IndexOf(args, flag);
        return index >= 0 && index + 1 < args.Length && !args[index + 1].StartsWith("--") ? args[index + 1] : null;
    }
}

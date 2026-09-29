using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Premagentic.Core;
using Premagentic.Core.Embeddings;
using Premagentic.Core.Extensions;
using Premagentic.Core.Identity;
using Premagentic.Core.Retrieval;
using Premagentic.Core.Security;
using Premagentic.Core.Storage;
using Npgsql;

namespace Premagentic.Cli.Setup;

/// <summary>
/// <c>prem setup</c> against a PostgreSQL that already exists: one unattended
/// engine, driven by <see cref="SetupOptions"/>, that installs the database
/// side of Premagentic and proves it works.
/// <para>
/// Every step looks before it acts, so the same code does three jobs. On a
/// fresh server it installs. On a finished install it changes nothing and says
/// so. On an install that stopped half way it does what is left. With
/// <see cref="SetupOptions.Plan"/> it only looks and reports what it would do.
/// </para>
/// <para>
/// Three roles, least privilege. The owner role owns the database and both
/// schemas, runs the migrations and <c>rebuild-index</c>, and nothing else. The
/// application role reads and writes rows and does nothing else: it owns no
/// object (a table owner would bypass row-level security), runs no DDL, creates
/// no role and is not a superuser. Grants that later migrations need come from
/// the owner's default privileges, so a table a migration adds is readable and
/// writable by the application role without running setup again. The search
/// role reads documents and chunks, where row-level security binds it to the
/// caller, and nothing else; a later table gives it nothing.
/// </para>
/// <para>
/// Generated passwords go to a credentials file per role
/// (<see cref="CredentialsFile"/>) and nowhere else: not the console, not a log,
/// not an environment variable, and not the server either, which receives only
/// a SCRAM verifier. The file is written before the role is created or changed,
/// so a run that stops in between never loses a password it set.
/// </para>
/// <para>
/// After the database it makes the first administrator, when asked and when
/// none exists, and the HTTPS certificate and settings the API reads; there is
/// never a default account and never a plain HTTP endpoint. With
/// <see cref="SetupOptions.WindowsService"/> it registers the API as a service.
/// </para>
/// </summary>
internal sealed class SetupEngine(SetupOptions options, TextWriter output)
{
    private static readonly Regex Identifier = new("^[a-z_][a-z0-9_]{0,62}$", RegexOptions.CultureInvariant);
    internal const int MinimumServerVersion = 140000;
    internal const int TestedMajorVersion = 17;
    private const string InvalidPassword = "28P01";
    private const string GssEncryptionModeKey = "GSS Encryption Mode";

    // A read-only rule for the service's own account is not a leak: setup put it
    // there with WindowsService, and a later run without that flag must keep it.
    private static readonly string? ServiceReader =
        OperatingSystem.IsWindows() ? WindowsServiceRegistration.ServiceSid(WindowsServiceRegistration.ServiceName) : null;

    private readonly SetupReport _report = new();
    private NpgsqlConnectionStringBuilder _admin = null!;
    private int _serverVersion;
    private string? _ownerPassword;
    private string? _appPassword;
    private string? _searchPassword;
    private bool _rolesExist = true;
    private bool _databaseExists;
    private bool _ownerLogsIn;
    private bool _appLogsIn;
    private bool _searchLogsIn;
    private bool _schemaCurrent;
    // The application role starts only once the text match body is this build's too.
    private bool _textMatchCurrent = true;
    private string? _bundledAdminConnection;
    // The bundled superuser's file could be read by other accounts, and its
    // cluster exists: a new password is set once the server runs.
    private bool _replaceSuperuserPassword;
    // Credentials files whose password other accounts could read, by role: the
    // new password goes to the file once the role has it, and not before.
    private readonly Dictionary<string, (string Path, string Label)> _pendingWrites = new();

    public async Task<SetupReport> RunAsync(CancellationToken ct = default)
    {
        try
        {
            if (!CheckNames()) return _report;
            if (!await PreflightAsync(ct)) return _report;
            if (!EnsureCredentialsFiles()) return _report;

            await using var admin = NpgsqlDataSource.Create(_admin.ConnectionString);
            if (!await EnsureRoleAsync(admin, options.OwnerRole, _ownerPassword, "role.owner", ct)) return _report;
            if (!await EnsureRoleAsync(admin, options.AppRole, _appPassword, "role.app", ct)) return _report;
            if (!await EnsureRoleAsync(admin, options.SearchRole, _searchPassword, "role.search", ct)) return _report;
            if (!await EnsureDatabaseAsync(admin, ct)) return _report;
            _ownerLogsIn = await EnsureLoginAsync(admin, options.OwnerRole, _ownerPassword, "login.owner", ct);
            if (_report.Failed) return _report;
            _appLogsIn = await EnsureLoginAsync(admin, options.AppRole, _appPassword, "login.app", ct);
            if (_report.Failed) return _report;
            _searchLogsIn = await EnsureLoginAsync(admin, options.SearchRole, _searchPassword, "login.search", ct);
            if (_report.Failed) return _report;
            if (!await EnsurePublicSchemaAsync(ct)) return _report;
            if (!await EnsureDatabasePrivilegesAsync(ct)) return _report;
            if (!await EnsureMigrationsAsync(ct)) return _report;
            if (!await EnsureGrantsAsync(ct)) return _report;
            if (!await EnsureSearchGrantsAsync(ct)) return _report;
            if (!await EnsureSearchTimeoutAsync(admin, ct)) return _report;
            if (!await EnsureIndexWriterAsync(ct)) return _report;
            if (!await CheckRoleAsync(options.AppRole, "role.app.audit", ct)) return _report;
            if (!await CheckRoleAsync(options.SearchRole, "role.search.audit", ct)) return _report;
            if (!await CheckFunctionHoldersAsync(ct)) return _report;
            if (!await EnsureAdministratorAsync(ct)) return _report;
            if (!EnsureHttps()) return _report;
            if (options.WindowsService && !EnsureWindowsService()) return _report;
            await HealthCheckAsync(ct);
        }
        catch (Exception ex) when (ex is NpgsqlException or InvalidOperationException or IOException
                                       or UnauthorizedAccessException or InvalidDataException or CryptographicException)
        {
            Record("setup", StepOutcome.Failed, Describe(ex));
        }
        return _report;
    }

    private bool CheckNames()
    {
        foreach (var (what, name) in new[] { ("database", options.DatabaseName), ("owner role", options.OwnerRole), ("application role", options.AppRole), ("search role", options.SearchRole) })
            if (!Identifier.IsMatch(name))
                return Fail("names", $"The {what} name '{name}' must be lower case letters, digits and underscores, starting with a letter or underscore, at most 63 characters.");
        if (new[] { options.OwnerRole, options.AppRole, options.SearchRole }.Distinct().Count() != 3)
            return Fail("names", "The owner role, the application role and the search role must be three different roles.");
        return true;
    }

    // ---------------------------------------------------------------- preflight

    private async Task<bool> PreflightAsync(CancellationToken ct)
    {
        // First, before anything is touched: a runtime that does not normalize
        // Unicode would store password hashes no other server can verify.
        if (TextCheck.Problem() is { } textProblem)
            return Fail("preflight.text", textProblem);
        Record("preflight.text", StepOutcome.Checked, $"Unicode text is normalized as password hashing needs ({TextCheck.IcuDescription()})");

        // Before any file is read from the folder or written to it.
        if (!EnsureCredentialsFolder()) return false;

        // The bundled server is made and started first; its superuser is then the
        // admin connection. A plan on a server that does not run yet stops here,
        // since nothing after it can be looked at.
        if (options.BundledPostgres is not null)
        {
            var running = await EnsureBundledPostgresAsync(ct);
            if (_report.Failed) return false;
            if (!running)
                return Record("preflight.postgres", StepOutcome.Skipped, "the rest is planned once the bundled server runs") && false;
        }

        _admin = new NpgsqlConnectionStringBuilder(_bundledAdminConnection ?? options.AdminConnectionString);
        // Without GSS encryption turned off, the driver looks for the Kerberos
        // library on every connection and, where it is not installed, prints an
        // error that looks like a fault. Off unless the admin connection chose.
        // Every connection setup makes, and both credentials files, inherit it.
        if (!_admin.Keys.Cast<string>().Contains(GssEncryptionModeKey))
            _admin.GssEncryptionMode = GssEncryptionMode.Disable;

        string version, user;
        bool superuser, createRole, createDb;
        try
        {
            await using var admin = NpgsqlDataSource.Create(_admin.ConnectionString);
            await using var cmd = admin.CreateCommand("""
                SELECT current_setting('server_version_num')::int, current_setting('server_version'),
                       current_user, r.rolsuper, r.rolcreaterole, r.rolcreatedb
                FROM pg_roles r WHERE r.rolname = current_user
                """);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            await reader.ReadAsync(ct);
            _serverVersion = reader.GetInt32(0);
            version = reader.GetString(1);
            user = reader.GetString(2);
            superuser = reader.GetBoolean(3);
            createRole = reader.GetBoolean(4);
            createDb = reader.GetBoolean(5);
        }
        catch (NpgsqlException ex)
        {
            return Fail("preflight.postgres", $"Cannot reach PostgreSQL at {_admin.Host}:{_admin.Port} as the admin role: {Describe(ex)}");
        }

        var where = $"PostgreSQL {version} at {_admin.Host}:{_admin.Port}, admin role {user}";
        var versionOutcome = VersionOutcome(_serverVersion);
        if (versionOutcome == StepOutcome.Failed)
            return Fail("preflight.postgres", $"{where}. Premagentic needs PostgreSQL {MinimumServerVersion / 10000} or later; upgrade the server first.");
        if (!superuser && !(createRole && createDb))
            Record("preflight.postgres", StepOutcome.Warning,
                $"{where}, which cannot create both roles and databases. Setup can finish only if they already exist.");
        else if (versionOutcome == StepOutcome.Warning)
            Record("preflight.postgres", StepOutcome.Warning, $"{where}. Premagentic is tested on PostgreSQL {TestedMajorVersion}.");
        else
            Record("preflight.postgres", StepOutcome.Checked, where);

        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(options.CredentialsDirectory))!;
            var free = new DriveInfo(root).AvailableFreeSpace;
            var detail = $"{Gib(free)} free on {root}";
            if (free < SetupOptions.MinimumFreeDiskBytes) return Fail("preflight.disk", $"{detail}; at least {Gib(SetupOptions.MinimumFreeDiskBytes)} is needed.");
            Record("preflight.disk", free < SetupOptions.RecommendedFreeDiskBytes ? StepOutcome.Warning : StepOutcome.Checked,
                free < SetupOptions.RecommendedFreeDiskBytes ? $"{detail}; {Gib(SetupOptions.RecommendedFreeDiskBytes)} is recommended." : detail);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            Record("preflight.disk", StepOutcome.Warning, $"Could not read free disk space for {options.CredentialsDirectory}: {ex.GetType().Name}.");
        }

        var memory = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        var memoryDetail = $"{Gib(memory)} of memory available to this process; the vector index holds about 1.6 KB per chunk";
        Record("preflight.memory", memory < SetupOptions.RecommendedMemoryBytes ? StepOutcome.Warning : StepOutcome.Checked,
            memory < SetupOptions.RecommendedMemoryBytes ? $"{memoryDetail}. {Gib(SetupOptions.RecommendedMemoryBytes)} is recommended." : memoryDetail);

        if (options.RequiredModelDirectory is { } modelDirectory)
        {
            var missing = new[] { "model.onnx", "vocab.txt" }.Where(f => !File.Exists(Path.Combine(modelDirectory, f))).ToArray();
            if (missing.Length > 0)
                return Fail("preflight.model", $"The embedding model is missing from {modelDirectory}: {string.Join(", ", missing)}.");
            Record("preflight.model", StepOutcome.Checked, $"model files present in {modelDirectory}");
        }
        else
        {
            Record("preflight.model", StepOutcome.Skipped, "the configured embedding provider needs no model files");
        }

        return !_report.Failed;
    }

    /// <summary>
    /// The folder every credentials file, the certificate and the HTTPS
    /// settings go to. On Windows it is made private from the moment it exists
    /// (<see cref="InstallAccess.CreatePrivateFolder"/>), never with the rules
    /// of the folder above it: C:\ProgramData lets every user make folders and
    /// files, so a folder made the plain way there is one anyone could have
    /// made first and filled. A folder that exists is used only when
    /// administrators, SYSTEM and this account alone own it and may change it
    /// (<see cref="InstallAccess.Untrusted"/>); anything else stops setup
    /// before a file in it is read. On Unix the folder is made mode 700 when
    /// the first file is written, as before.
    /// </summary>
    private bool EnsureCredentialsFolder()
    {
        const string Step = "credentials.folder";
        if (!OperatingSystem.IsWindows()) return true;

        var folder = Path.GetFullPath(options.CredentialsDirectory);
        var service = options.WindowsService ? ServiceReader : null;
        if (!Directory.Exists(folder) && !InstallAccess.IsLink(folder))
        {
            var who = service is not null && WindowsServiceRegistration.IsElevated()
                ? $"administrators and SYSTEM, and {WindowsServiceRegistration.Account} may read"
                : $"this account, administrators and SYSTEM{(service is null ? "" : $", and {WindowsServiceRegistration.Account} may read")}";
            if (options.Plan)
                return Record(Step, StepOutcome.WouldApply, $"would make {folder}, which only {who}");
            try
            {
                InstallAccess.CreatePrivateFolder(folder, service);
            }
            catch (IOException madeFirst)
            {
                // Another account made it between the look above and the make.
                return Fail(Step, madeFirst.Message);
            }
            return Record(Step, StepOutcome.Applied, $"made {folder}, which only {who}");
        }

        if (InstallAccess.Examine(folder) is { } finding)
            return Fail(Step, FolderRefusal(folder, finding));
        return Record(Step, StepOutcome.Done, $"{folder} is owned and can be changed only by administrators, SYSTEM and this account");
    }

    /// <summary>
    /// The refusal of the credentials folder. It never says to move the folder
    /// aside: it can hold the bundled server's data, and an install made by an
    /// earlier setup is put right by the commands on the manual's Upgrading and
    /// removing page.
    /// </summary>
    private static string FolderRefusal(string folder, AccessFinding finding) =>
        $"{folder} {finding.Clause}; " + finding.Problem switch
        {
            AccessProblem.Link => "setup does not follow it: pass --credentials-dir with the folder itself, or put the folder in place of the link, and run setup again.",
            AccessProblem.Unreadable => "run setup from an elevated prompt.",
            AccessProblem.Owner => $"once you have checked what is in it, make Administrators its owner with the command on {InstallAccess.RecoveryPage}, and run setup again.",
            _ => $"take that access away with the command on {InstallAccess.RecoveryPage}, from an elevated prompt, and run setup again.",
        };

    /// <summary>The refusal of a file setup would read from the folder.</summary>
    private static string FileRefusal(string path, AccessFinding finding) =>
        $"{path} {finding.Clause}; " + finding.Problem switch
        {
            AccessProblem.Link => "remove the link and run setup again, which writes the file itself.",
            AccessProblem.Unreadable => "run setup from an elevated prompt.",
            _ => "move it aside and run setup again.",
        };

    /// <summary>
    /// The refusal of the bundled server's superuser file. Once the cluster
    /// exists the file holds the only copy of its superuser's password, so it
    /// is never to be moved aside, which would leave setup no way in.
    /// </summary>
    private string SuperuserRefusal(string path, AccessFinding finding, bool clusterExists)
    {
        if (!clusterExists) return FileRefusal(path, finding);
        var holds = $"it holds the password of the server in {options.DataDirectory}, so do not move it";
        return $"{path} {finding.Clause}; " + finding.Problem switch
        {
            AccessProblem.Link => $"{holds}: put the file itself in place of the link and run setup again.",
            AccessProblem.Unreadable => "run setup from an elevated prompt.",
            AccessProblem.Owner => $"{holds}: run setup once more, from an elevated prompt, as the account that owns it, which gives it to administrators.",
            _ => $"{holds}: take that access away from the file, from an elevated prompt, and run setup again.",
        };
    }

    // ----------------------------------------------------------------- bundled

    /// <summary>
    /// The bundled PostgreSQL: its tools load, the superuser's credentials file
    /// exists, the cluster exists, and the server runs. Each part looks before it
    /// acts, like every other step. The superuser's password goes to its file
    /// first and reaches <c>initdb</c> only through a private temporary file,
    /// never a command line.
    /// </summary>
    /// <returns>True when the server runs and <see cref="_bundledAdminConnection"/> is set.</returns>
    private async Task<bool> EnsureBundledPostgresAsync(CancellationToken ct)
    {
        var pg = new BundledPostgres(options.BundledPostgres!);
        if (options.DataDirectory is null)
            return Fail("postgres.binaries", "The bundled PostgreSQL needs a data folder. Pass --data-dir.");
        var data = Path.GetFullPath(options.DataDirectory);

        if (pg.MissingTools() is { Count: > 0 } missing)
            return Fail("postgres.binaries", $"{pg.Root} is not a bundled PostgreSQL: it has no {string.Join(", ", missing)}.");
        if (pg.RuntimeProblem() is { } runtime)
            return Fail("postgres.binaries", runtime);
        var version = BundledPostgres.Last(BundledPostgres.Run(pg.Tool("postgres"), ["--version"], TimeSpan.FromSeconds(30)).Output);
        Record("postgres.binaries", StepOutcome.Checked, $"{version} from {pg.Root}");

        var password = EnsureSuperuserCredentials(clusterExists: pg.IsCluster(data));
        if (_report.Failed) return false;

        if (pg.IsCluster(data))
            Record("postgres.cluster", StepOutcome.Done, $"{data} holds a cluster");
        else if (Directory.Exists(data) && Directory.EnumerateFileSystemEntries(data).Any())
            return Fail("postgres.cluster", $"{data} is not empty and holds no cluster. Choose an empty or new folder with --data-dir.");
        else if (options.Plan)
            Record("postgres.cluster", StepOutcome.WouldApply,
                $"would make a cluster in {data}: UTF-8, ICU, SCRAM, listening on localhost port {options.PostgresPort}, superuser {BundledPostgres.Superuser}");
        else
        {
            if (!Directory.Exists(data)) BundledPostgres.CreatePrivateDirectory(data);
            var passwordFile = Path.Combine(options.CredentialsDirectory, $".initdb.{Convert.ToHexString(RandomNumberGenerator.GetBytes(6))}.tmp");
            var bytes = Encoding.UTF8.GetBytes(password!);
            (int Code, string Output) initdb;
            try
            {
                CredentialsFile.WritePrivate(passwordFile, bytes);
                initdb = pg.InitDb(data, passwordFile, options.PostgresPort);
            }
            finally
            {
                if (File.Exists(passwordFile)) File.Delete(passwordFile);
                CryptographicOperations.ZeroMemory(bytes);
            }
            if (initdb.Code != 0)
                return Fail("postgres.cluster", $"initdb failed with exit code {initdb.Code}: {BundledPostgres.Last(initdb.Output)}");
            Record("postgres.cluster", StepOutcome.Applied,
                $"made a cluster in {data}: UTF-8, ICU, SCRAM, listening on localhost port {options.PostgresPort}, superuser {BundledPostgres.Superuser}");
        }

        if (pg.IsCluster(data) && pg.IsRunning(data))
            Record("postgres.server", StepOutcome.Done, $"the server on {data} is running");
        else if (options.Plan)
            return Record("postgres.server", StepOutcome.WouldApply, options.WindowsService
                ? $"would register the server as the service {BundledPostgres.ServiceName} under {BundledPostgres.ServiceAccount} and start it"
                : "would start the server as this account") && false;
        else if (!BundledPostgres.IsPortFree(options.PostgresPort))
            return Fail("postgres.server", $"Port {options.PostgresPort} on this computer is in use by another program. Pass --postgres-port with a free port.");
        else if (options.WindowsService)
        {
            if (!await StartDatabaseServiceAsync(pg, data, ct)) return false;
        }
        else
        {
            var (code, output) = pg.Start(data);
            if (code != 0)
                return Fail("postgres.server", $"pg_ctl start failed with exit code {code}: {BundledPostgres.Last(output)}. See {Path.Combine(data, "server.log")}.");
            Record("postgres.server", StepOutcome.Applied,
                $"started the server as this account; it runs until stopped with: pg_ctl stop -D \"{data}\". " +
                "Run setup with --windows-service from an elevated prompt to run it as a service instead.");
        }

        if (_replaceSuperuserPassword && !await ReplaceSuperuserPasswordAsync(ct)) return false;
        _bundledAdminConnection = CredentialsFile.ReadConnectionString(options.SuperuserCredentialsPath);
        return true;
    }

    /// <summary>
    /// The superuser's credentials file: made with a generated password when it is
    /// missing, and on Windows readable by the account that ran setup and by
    /// administrators, and nobody else. From an elevated prompt it is handed to
    /// administrators (<see cref="InstallAccess.GiveFileToAdministrators"/>):
    /// owned by them and read through their one rule, so a second
    /// administrator's run finds it as the first one's did. A file another
    /// account could have written stops setup. A password other accounts could
    /// read is never kept: before the cluster exists a new one is generated for
    /// it, and once it exists a new one is set on the server as soon as it runs
    /// (<see cref="ReplaceSuperuserPasswordAsync"/>).
    /// </summary>
    /// <param name="clusterExists">Whether the data folder already holds a cluster, whose superuser has the file's password.</param>
    /// <returns>The superuser's password, or null after recording a failure.</returns>
    private string? EnsureSuperuserCredentials(bool clusterExists)
    {
        const string Step = "credentials.superuser";
        var path = options.SuperuserCredentialsPath;
        var reader = OperatingSystem.IsWindows() ? BundledPostgres.AdministratorsSid : null;
        if (File.Exists(path))
        {
            if (InstallAccess.Examine(path) is { } finding)
            {
                Fail(Step, SuperuserRefusal(path, finding, clusterExists));
                return null;
            }
            var existing = new NpgsqlConnectionStringBuilder(CredentialsFile.ReadConnectionString(path));
            if (existing.Username != BundledPostgres.Superuser || existing.Port != options.PostgresPort || string.IsNullOrEmpty(existing.Password))
            {
                Fail(Step, $"{path} is for {existing.Username} on port {existing.Port}, not {BundledPostgres.Superuser} on port " +
                           $"{options.PostgresPort}. Pass --postgres-port {existing.Port}" +
                           (clusterExists ? "." : ", or move the file aside for a new server."));
                return null;
            }
            if (CredentialsFile.IsPrivate(path, reader))
            {
                if (!options.Plan && HandOverSuperuserFile(path))
                    Record(Step, StepOutcome.Applied, $"gave {path}, which holds the bundled server's superuser, to administrators, who alone read it");
                else
                    Record(Step, StepOutcome.Done, $"{path} holds the bundled server's superuser");
            }
            else if (options.Plan)
                Record(Step, StepOutcome.WouldApply, $"would give the bundled server's superuser a new password and write it to {path}, since other accounts could read the old one");
            else if (clusterExists)
                // The server has the old password; the new one is set on it once it runs.
                _replaceSuperuserPassword = true;
            else
                return NewSuperuserPassword(Step, path, reader, $"generated a new superuser password, since other accounts could read the one in {path}, and wrote it there");
            return existing.Password;
        }

        if (options.Plan)
        {
            Record(Step, StepOutcome.WouldApply, $"would generate the bundled server's superuser password and write it to {path}");
            return null;
        }
        return NewSuperuserPassword(Step, path, reader, $"generated the bundled server's superuser password and wrote it to {path}");
    }

    private string NewSuperuserPassword(string step, string path, string? reader, string done)
    {
        var password = Secrets.NewPassword();
        WriteSuperuserFile(path, new NpgsqlConnectionStringBuilder
        {
            Host = "localhost",
            Port = options.PostgresPort,
            Database = "postgres",
            Username = BundledPostgres.Superuser,
            Password = password,
            GssEncryptionMode = GssEncryptionMode.Disable,
        }.ConnectionString, reader);
        Record(step, StepOutcome.Applied, $"{done}, readable only by this account and administrators");
        return password;
    }

    /// <summary>
    /// A new password for the running bundled server's superuser, whose file
    /// other accounts could read. Set through a connection made with the old
    /// one; written to the file only once the ALTER ROLE has succeeded, and
    /// before the change commits, so a write that fails rolls the change back
    /// and leaves the old password in force in both places. Written after the
    /// commit instead, a failed write would leave the new password nowhere, and
    /// nothing else can reset the superuser. The file's access rules are set
    /// after the commit.
    /// </summary>
    private async Task<bool> ReplaceSuperuserPasswordAsync(CancellationToken ct)
    {
        const string Step = "credentials.superuser";
        var path = options.SuperuserCredentialsPath;
        var old = new NpgsqlConnectionStringBuilder(CredentialsFile.ReadConnectionString(path));
        var password = Secrets.NewPassword();
        await using var source = NpgsqlDataSource.Create(old.ConnectionString);
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var change = await connection.BeginTransactionAsync(ct);
        await using (var alter = new NpgsqlCommand($"ALTER ROLE {Quote(BundledPostgres.Superuser)} WITH PASSWORD '{Secrets.ScramSha256Verifier(password)}'", connection, change))
            await alter.ExecuteNonQueryAsync(ct);
        CredentialsFile.Write(path, new NpgsqlConnectionStringBuilder(old.ConnectionString) { Password = password }.ConnectionString, "superuser");
        await change.CommitAsync(ct);
        ShareSuperuserFile(path, OperatingSystem.IsWindows() ? BundledPostgres.AdministratorsSid : null);
        _replaceSuperuserPassword = false;
        return Record(Step, StepOutcome.Applied,
            $"gave the bundled server's superuser a new password, since other accounts could read the old one, and wrote it to {path}, readable only by this account and administrators");
    }

    private static void WriteSuperuserFile(string path, string connection, string? reader)
    {
        CredentialsFile.Write(path, connection, "superuser");
        ShareSuperuserFile(path, reader);
    }

    /// <summary>Administrators' read rule on the superuser file, and from an elevated prompt the file handed to them.</summary>
    private static void ShareSuperuserFile(string path, string? reader)
    {
        if (reader is not null) CredentialsFile.GrantRead(path, reader);
        HandOverSuperuserFile(path);
    }

    /// <summary>
    /// From an elevated prompt on Windows, hands the superuser file to
    /// administrators when it is not theirs yet.
    /// </summary>
    /// <returns>True when it was handed over now.</returns>
    private static bool HandOverSuperuserFile(string path)
    {
        if (!OperatingSystem.IsWindows() || !WindowsServiceRegistration.IsElevated() || InstallAccess.IsAdministratorsOwn(path)) return false;
        InstallAccess.GiveFileToAdministrators(path);
        return true;
    }

    /// <summary>
    /// Registers the bundled server as the service <c>PremagenticDb</c> under its
    /// own virtual account, lets that account change the data folder, starts the
    /// service, and waits until the superuser can connect. Needs an elevated
    /// process; refuses without one.
    /// </summary>
    private async Task<bool> StartDatabaseServiceAsync(BundledPostgres pg, string data, CancellationToken ct)
    {
        const string Step = "postgres.server";
        if (!OperatingSystem.IsWindows())
            return Fail(Step, "--windows-service registers Windows services. On Linux, use the distribution's PostgreSQL service.");
        if (!WindowsServiceRegistration.IsElevated())
            return Fail(Step, "Registering the database service needs an elevated prompt. Open the prompt with Run as administrator " +
                              "and run the same setup command again; the steps already done are kept and skipped.");

        if (!WindowsServiceRegistration.Exists(BundledPostgres.ServiceName))
        {
            var (code, output) = pg.Register(data);
            if (code != 0)
                return Fail(Step, $"pg_ctl register failed with exit code {code}: {BundledPostgres.Last(output)}");
        }
        BundledPostgres.GrantServiceModify(data);
        var (started, startOutput) = WindowsServiceRegistration.Sc(["start", BundledPostgres.ServiceName]);
        if (started != 0)
            return Fail(Step, $"sc.exe start {BundledPostgres.ServiceName} failed ({started}): {startOutput.Trim()}");

        var connection = CredentialsFile.ReadConnectionString(options.SuperuserCredentialsPath);
        for (var attempt = 0; attempt < 60; attempt++)
        {
            try
            {
                await using var source = NpgsqlDataSource.Create(connection);
                await using var conn = await source.OpenConnectionAsync(ct);
                return Record(Step, StepOutcome.Applied,
                    $"registered and started the service {BundledPostgres.ServiceName} under {BundledPostgres.ServiceAccount}, which may change {data} and nothing else");
            }
            catch (NpgsqlException)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), ct);
            }
        }
        return Fail(Step, $"The service {BundledPostgres.ServiceName} started but did not accept a connection within a minute. " +
            "PostgreSQL writes why to the Application event log.");
    }

    /// <summary>
    /// 14 is the floor: 13 left community support in November 2025. Anything
    /// other than the tested major version is allowed, with a warning.
    /// </summary>
    internal static StepOutcome VersionOutcome(int serverVersionNum) =>
        serverVersionNum < MinimumServerVersion ? StepOutcome.Failed
        : serverVersionNum / 10000 != TestedMajorVersion ? StepOutcome.Warning
        : StepOutcome.Checked;

    // -------------------------------------------------------------- credentials

    private bool EnsureCredentialsFiles()
    {
        _ownerPassword = EnsureCredentialsFile(options.OwnerCredentialsPath, options.OwnerRole, "owner", "credentials.owner", reader: null);
        if (_report.Failed) return false;
        _appPassword = EnsureCredentialsFile(options.AppCredentialsPath, options.AppRole, "application", "credentials.app", reader: ServiceReader);
        if (_report.Failed) return false;
        // The same service reads it as reads the application's file.
        _searchPassword = EnsureCredentialsFile(options.SearchCredentialsPath, options.SearchRole, "search", "credentials.search", reader: ServiceReader);
        return !_report.Failed;
    }

    /// <summary>
    /// One role's credentials file. A file another account could have written
    /// stops setup: its password may be one that account chose. A file other
    /// accounts could read keeps its role, and never its password, which may be
    /// known: a new one is generated and set on the role by the login step,
    /// which writes it to the file once the role has it.
    /// </summary>
    private string? EnsureCredentialsFile(string path, string role, string label, string step, string? reader)
    {
        if (File.Exists(path))
        {
            if (InstallAccess.Examine(path) is { } finding)
            {
                Fail(step, FileRefusal(path, finding));
                return null;
            }
            NpgsqlConnectionStringBuilder existing;
            try
            {
                existing = new NpgsqlConnectionStringBuilder(CredentialsFile.ReadConnectionString(path));
            }
            catch (Exception ex) when (ex is InvalidDataException or ArgumentException)
            {
                Fail(step, $"{path} exists but is not a usable credentials file ({ex.GetType().Name}). Move it aside and run setup again.");
                return null;
            }
            if (existing.Username != role || existing.Database != options.DatabaseName || string.IsNullOrEmpty(existing.Password))
            {
                Fail(step, $"{path} is for role '{existing.Username}' on database '{existing.Database}', not '{role}' on " +
                           $"'{options.DatabaseName}'. Choose another --credentials-dir or move the file aside.");
                return null;
            }

            if (CredentialsFile.IsPrivate(path, reader))
            {
                Record(step, StepOutcome.Done, $"{path} holds the {label} role's credentials");
                return existing.Password;
            }
            if (options.Plan)
            {
                Record(step, StepOutcome.WouldApply, $"would generate a new password for {role}, since other accounts could read the one in {path}, and write it there");
                return null;
            }
            // The file keeps the old password until the role has the new one:
            // the login step sets it on the role and only then writes it.
            var fresh = Secrets.NewPassword();
            _pendingWrites[role] = (path, label);
            Record(step, StepOutcome.Checked,
                $"other accounts could read the password in {path}, so {role} gets a new one, written there once the role has it");
            return fresh;
        }

        if (options.Plan)
        {
            Record(step, StepOutcome.WouldApply, $"would generate a password for {role} and write it to {path}");
            return null;
        }

        var password = Secrets.NewPassword();
        CredentialsFile.Write(path, ConnectionFor(role, password), label);
        Record(step, StepOutcome.Applied, $"generated a password for {role} and wrote it to {path}, readable only by this account");
        return password;
    }

    // -------------------------------------------------------------------- roles

    private async Task<bool> EnsureRoleAsync(NpgsqlDataSource admin, string role, string? password, string step, CancellationToken ct)
    {
        await using var read = admin.CreateCommand("""
            SELECT rolsuper, rolcreatedb, rolcreaterole, rolreplication, rolbypassrls, rolcanlogin
            FROM pg_roles WHERE rolname = @role
            """);
        read.Parameters.AddWithValue("role", role);
        bool[]? attributes = null;
        await using (var reader = await read.ExecuteReaderAsync(ct))
        {
            if (await reader.ReadAsync(ct))
                attributes = Enumerable.Range(0, 6).Select(reader.GetBoolean).ToArray();
        }

        const string Wanted = "LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS";
        if (attributes is null)
        {
            _rolesExist = false;
            if (options.Plan)
                return Record(step, StepOutcome.WouldApply, $"would create role {role} ({Wanted})");
            await ExecuteAsync(admin, $"CREATE ROLE {Quote(role)} WITH {Wanted} PASSWORD '{Secrets.ScramSha256Verifier(password!)}'", ct);
            _rolesExist = true;
            return Record(step, StepOutcome.Applied, $"created role {role} ({Wanted})");
        }

        string[] names = ["SUPERUSER", "CREATEDB", "CREATEROLE", "REPLICATION", "BYPASSRLS"];
        var extra = names.Where((_, i) => attributes[i]).ToArray();
        var canLogin = attributes[5];
        if (extra.Length == 0 && canLogin)
            return Record(step, StepOutcome.Done, $"role {role} exists, can log in, and holds no elevated attribute");

        var change = string.Join(", ", extra.Select(e => "remove " + e).Concat(canLogin ? [] : ["allow LOGIN"]));
        if (options.Plan)
            return Record(step, StepOutcome.WouldApply, $"would change role {role}: {change}");
        await ExecuteAsync(admin, $"ALTER ROLE {Quote(role)} WITH {Wanted}", ct);
        return Record(step, StepOutcome.Applied, $"changed role {role}: {change}");
    }

    // ----------------------------------------------------------------- database

    private async Task<bool> EnsureDatabaseAsync(NpgsqlDataSource admin, CancellationToken ct)
    {
        await using var read = admin.CreateCommand("SELECT pg_get_userbyid(datdba) FROM pg_database WHERE datname = @db");
        read.Parameters.AddWithValue("db", options.DatabaseName);
        var owner = (string?)await read.ExecuteScalarAsync(ct);

        if (owner is null)
        {
            if (options.Plan)
                return Record("database", StepOutcome.WouldApply, $"would create database {options.DatabaseName}, owned by {options.OwnerRole}");
            // template0 and an explicit encoding, so the database is UTF-8 whatever
            // template1 on this server was created with.
            await ExecuteAsync(admin,
                $"CREATE DATABASE {Quote(options.DatabaseName)} OWNER {Quote(options.OwnerRole)} ENCODING 'UTF8' TEMPLATE template0", ct);
            _databaseExists = true;
            return Record("database", StepOutcome.Applied, $"created database {options.DatabaseName}, owned by {options.OwnerRole}");
        }

        _databaseExists = true;
        if (owner == options.OwnerRole)
            return Record("database", StepOutcome.Done, $"database {options.DatabaseName} exists, owned by {options.OwnerRole}");
        if (options.Plan)
            return Record("database", StepOutcome.WouldApply, $"would change the owner of {options.DatabaseName} from {owner} to {options.OwnerRole}");
        await ExecuteAsync(admin, $"ALTER DATABASE {Quote(options.DatabaseName)} OWNER TO {Quote(options.OwnerRole)}", ct);
        return Record("database", StepOutcome.Applied, $"changed the owner of {options.DatabaseName} from {owner} to {options.OwnerRole}");
    }

    /// <summary>
    /// Proves the role logs in with the password in its file. A new file for a
    /// role that already existed (a run that stopped after writing the file, or a
    /// file deliberately removed) fails that proof, and the role's password is
    /// then set to the file's.
    /// </summary>
    /// <returns>True when the role can be used by the steps after this one.</returns>
    private async Task<bool> EnsureLoginAsync(NpgsqlDataSource admin, string role, string? password, string step, CancellationToken ct)
    {
        if (password is null || !_databaseExists || !_rolesExist)
        {
            Record(step, StepOutcome.WouldApply, $"would log in to {options.DatabaseName} as {role} once the steps above are done");
            return false;
        }

        if (await CanLogInAsync(role, password, ct))
        {
            // A role made in this run with a new password has it already.
            if (WritePending(role, password) is { } written)
                return Record(step, StepOutcome.Applied, $"{role} logs in to {options.DatabaseName} with its new password{written}");
            Record(step, StepOutcome.Done, $"{role} logs in to {options.DatabaseName} with the password in its credentials file");
            return true;
        }

        if (options.Plan)
        {
            Record(step, StepOutcome.WouldApply, $"would set {role}'s password to the one in its credentials file");
            return false;
        }

        // On fresh connections: an idle one the server ended while it refused
        // the password above would fail this with no word from the server.
        admin.Clear();
        await ExecuteAsync(admin, $"ALTER ROLE {Quote(role)} WITH PASSWORD '{Secrets.ScramSha256Verifier(password)}'", ct);
        var rotated = WritePending(role, password);
        if (!await CanLogInAsync(role, password, ct))
            return Fail(step, $"{role} still cannot log in to {options.DatabaseName} after its password was set. Check pg_hba.conf allows it.");
        Record(step, StepOutcome.Applied, rotated is null
            ? $"set {role}'s password to the one in its credentials file"
            : $"set a new password on {role}{rotated}");
        return true;
    }

    /// <summary>
    /// Writes a role's new password to its credentials file, when the role was
    /// given one because other accounts could read the old one. Called only
    /// once the role has it.
    /// </summary>
    /// <returns>Null when nothing was pending; otherwise the end of the step's sentence, saying where it went and what to restart.</returns>
    private string? WritePending(string role, string password)
    {
        if (!_pendingWrites.Remove(role, out var pending)) return null;
        CredentialsFile.Write(pending.Path, ConnectionFor(role, password), pending.Label);
        return $", since other accounts could read the old one, and wrote it to {pending.Path}, readable only by this account. " +
               $"Restart the API, and anything else that connects as {role}, so it connects with the new password";
    }

    /// <summary>
    /// Whether <paramref name="role"/> logs in with <paramref name="password"/>.
    /// A server may end the connection without its answer when it refuses a
    /// password (a PostgreSQL on Windows was seen to), so an attempt that ends
    /// with no word from the server is made once more, and then counted as
    /// refused. That is safe: the login step then sets the role's password to
    /// the file's through the admin connection and must log in with it after,
    /// which a server that cannot be reached fails in its own words.
    /// </summary>
    private async Task<bool> CanLogInAsync(string role, string password, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            await using var source = NpgsqlDataSource.Create(ConnectionFor(role, password));
            try
            {
                await using var conn = await source.OpenConnectionAsync(ct);
                return true;
            }
            catch (PostgresException ex) when (ex.SqlState == InvalidPassword)
            {
                return false;
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.InsufficientPrivilege)
            {
                // The server checks the password before the right to connect, so the
                // password is right. A role new to an install whose database PUBLIC can
                // no longer connect to (a release that adds a role) meets this until
                // the privileges step grants it CONNECT.
                return true;
            }
            catch (NpgsqlException ex) when (ex is not PostgresException)
            {
                if (attempt == 2) return false;
            }
        }
    }

    /// <summary>
    /// Before PostgreSQL 15 every role may create objects in the public schema.
    /// The application role must not, so that default is withdrawn. On 15 and
    /// later it already is, and this only checks.
    /// </summary>
    private async Task<bool> EnsurePublicSchemaAsync(CancellationToken ct)
    {
        if (!_ownerLogsIn || !_rolesExist)
            return Record("schema.public", StepOutcome.WouldApply, "would check that the application role cannot create objects in the public schema");

        await using var owner = NpgsqlDataSource.Create(ConnectionFor(options.OwnerRole, _ownerPassword!));
        await using var check = owner.CreateCommand(
            "SELECT CASE WHEN to_regnamespace('public') IS NULL THEN false " +
            "ELSE has_schema_privilege(@app, 'public', 'CREATE') OR has_schema_privilege(@search, 'public', 'CREATE') END");
        check.Parameters.AddWithValue("app", options.AppRole);
        check.Parameters.AddWithValue("search", options.SearchRole);
        if (!(bool)(await check.ExecuteScalarAsync(ct))!)
            return Record("schema.public", StepOutcome.Done, $"{options.AppRole} and {options.SearchRole} cannot create objects in the public schema");

        if (options.Plan)
            return Record("schema.public", StepOutcome.WouldApply, "would withdraw CREATE on the public schema from PUBLIC");
        // The public schema belongs to the bootstrap superuser before 15, so the
        // admin role makes this change, in the new database.
        await using var admin = NpgsqlDataSource.Create(
            new NpgsqlConnectionStringBuilder(_admin.ConnectionString) { Database = options.DatabaseName }.ConnectionString);
        await ExecuteAsync(admin,
            $"REVOKE CREATE ON SCHEMA public FROM PUBLIC; REVOKE CREATE ON SCHEMA public FROM {Quote(options.AppRole)}, {Quote(options.SearchRole)}", ct);
        return Record("schema.public", StepOutcome.Applied, "withdrew CREATE on the public schema from PUBLIC");
    }

    /// <summary>
    /// PUBLIC loses every default privilege on the database (connecting and
    /// creating temporary tables), and the application role gets CONNECT and
    /// nothing more. The owner holds its privileges by owning the database.
    /// </summary>
    private async Task<bool> EnsureDatabasePrivilegesAsync(CancellationToken ct)
    {
        if (!_ownerLogsIn || !_rolesExist)
            return Record("database.privileges", StepOutcome.WouldApply,
                $"would withdraw PUBLIC's privileges on {options.DatabaseName} and grant {options.AppRole} and {options.SearchRole} CONNECT only");

        await using var owner = NpgsqlDataSource.Create(ConnectionFor(options.OwnerRole, _ownerPassword!));
        await using var check = owner.CreateCommand("""
            SELECT NOT EXISTS (
                       SELECT 1 FROM pg_database d, aclexplode(COALESCE(d.datacl, acldefault('d', d.datdba))) a
                       WHERE d.datname = current_database() AND a.grantee = 0)
               AND (SELECT bool_and(has_database_privilege(r, current_database(), 'CONNECT')
                                    AND NOT has_database_privilege(r, current_database(), 'CREATE')
                                    AND NOT has_database_privilege(r, current_database(), 'TEMPORARY'))
                    FROM unnest(ARRAY[@app, @search]) r)
            """);
        check.Parameters.AddWithValue("app", options.AppRole);
        check.Parameters.AddWithValue("search", options.SearchRole);
        if ((bool)(await check.ExecuteScalarAsync(ct))!)
            return Record("database.privileges", StepOutcome.Done,
                $"PUBLIC holds nothing on {options.DatabaseName}; {options.AppRole} and {options.SearchRole} may connect and not create");

        if (options.Plan)
            return Record("database.privileges", StepOutcome.WouldApply,
                $"would withdraw PUBLIC's privileges on {options.DatabaseName} and grant {options.AppRole} and {options.SearchRole} CONNECT only");
        var db = Quote(options.DatabaseName);
        var roles = $"{Quote(options.AppRole)}, {Quote(options.SearchRole)}";
        await ExecuteAsync(owner, $"REVOKE ALL ON DATABASE {db} FROM PUBLIC; REVOKE ALL ON DATABASE {db} FROM {roles}; GRANT CONNECT ON DATABASE {db} TO {roles}", ct);
        return Record("database.privileges", StepOutcome.Applied,
            $"withdrew PUBLIC's privileges on {options.DatabaseName}; {options.AppRole} and {options.SearchRole} may connect and not create");
    }

    // --------------------------------------------------------------- migrations

    private async Task<bool> EnsureMigrationsAsync(CancellationToken ct)
    {
        if (!_ownerLogsIn)
            return Record("migrations", StepOutcome.WouldApply, $"would apply every migration as {options.OwnerRole}");

        await using var owner = NpgsqlDataSource.Create(ConnectionFor(options.OwnerRole, _ownerPassword!));
        var runner = new MigrationRunner(owner);
        if (options.Plan)
        {
            var pending = await runner.PendingAsync(ct);
            // A stale text match body changes no object the grant steps look at,
            // but the application role refuses to start until it is installed.
            _schemaCurrent = pending.All(p => p.Kind != MigrationWork.Section);
            _textMatchCurrent = pending.All(p => p.Kind != MigrationWork.TextMatchBody);
            return pending.Count == 0
                ? Record("migrations", StepOutcome.Done, "the schema is current")
                : Record("migrations", StepOutcome.WouldApply,
                    $"would apply as {options.OwnerRole}: {string.Join(", ", pending.Select(p => p.Label))}");
        }

        var applied = await runner.MigrateAsync(ct);
        _schemaCurrent = true;
        return applied.Count == 0
            ? Record("migrations", StepOutcome.Done, "the schema is current")
            : Record("migrations", StepOutcome.Applied,
                $"applied as {options.OwnerRole}: {string.Join(", ", applied.Select(p => p.Label))}");
    }

    // ------------------------------------------------------------------- grants

    private static readonly string[] Schemas = [Migration.ConfigSchema, Migration.IndexSchema];

    // The caller-session functions row-level security runs on. Their migration
    // takes EXECUTE away from PUBLIC; only the application role, which opens and
    // closes a caller's session around each read, gets it back. The table behind
    // them is theirs alone, so the application role holds nothing on it.
    private static readonly string[] CallerSessionFunctions =
    [
        "prem_config.open_caller_session(text, uuid, uuid, uuid, bigint[], interval)",
        "prem_config.close_caller_session(text)",
    ];
    private const string CallerSessionTable = "prem_config.caller_session";

    // The function the text leg of every search calls, which applies the
    // caller's lists itself (under a row policy PostgreSQL would not use the
    // full-text index). Found by name: its arguments follow the gates, so they
    // change when a gate does. Its migration takes EXECUTE from PUBLIC; the
    // application and search roles get it back, and nobody else may hold it.
    private const string TextMatchesName = "prem_index.text_matches";

    // The only tables the search role may read, and only read.
    private static readonly string[] SearchReadable = ["prem_index.document", "prem_index.chunk"];

    /// <summary>
    /// The application role may use both schemas, read and write the rows of
    /// every table in them, read (never write) the migration records, and use
    /// their sequences. The owner's default privileges extend the same grants to
    /// every table and sequence a later migration creates. Where the schema has
    /// the caller-session functions, it may execute them, and it holds nothing on
    /// their table.
    /// </summary>
    private async Task<bool> EnsureGrantsAsync(CancellationToken ct)
    {
        if (!_ownerLogsIn || !_rolesExist || !_schemaCurrent)
            return Record("grants", StepOutcome.WouldApply, $"would grant {options.AppRole} row access in {string.Join(" and ", Schemas)}");

        await using var owner = NpgsqlDataSource.Create(ConnectionFor(options.OwnerRole, _ownerPassword!));
        var (functions, sessionTable, versions) = await SecurityObjectsAsync(owner, ct);
        if (versions > 1)
            return Fail("grants", $"There are {versions} functions named {TextMatchesName}, so a grant by name could reach the wrong one. " +
                                  $"Drop all but the one the current release installs, as {options.OwnerRole}, and run setup again.");
        var sessions = functions.Length == 0 ? "" : $", and may run {string.Join(", ", functions.Select(FunctionName))}";
        await using var check = owner.CreateCommand("""
            WITH t AS (
                SELECT c.oid, n.nspname || '.' || c.relname AS qualified, c.relname, c.relkind
                FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
                WHERE n.nspname = ANY(@schemas) AND c.relkind IN ('r', 'p', 'S'))
            SELECT
                (SELECT bool_and(has_schema_privilege(@app, s, 'USAGE') AND NOT has_schema_privilege(@app, s, 'CREATE'))
                 FROM unnest(@schemas) s)
                AND NOT EXISTS (
                    SELECT 1 FROM t WHERE t.relkind IN ('r', 'p') AND t.qualified = @session_table
                      AND has_table_privilege(@app, t.oid, 'SELECT, INSERT, UPDATE, DELETE, TRUNCATE, REFERENCES, TRIGGER'))
                AND NOT EXISTS (
                    SELECT 1 FROM unnest(@functions) f WHERE NOT has_function_privilege(@app, to_regprocedure(f), 'EXECUTE'))
                AND NOT EXISTS (
                    SELECT 1 FROM t WHERE t.relkind IN ('r', 'p') AND t.qualified <> @session_table AND NOT (
                        has_table_privilege(@app, t.oid, 'SELECT')
                        AND has_table_privilege(@app, t.oid, 'INSERT') = (t.relname <> 'schema_migration')
                        AND has_table_privilege(@app, t.oid, 'UPDATE') = (t.relname <> 'schema_migration')
                        AND has_table_privilege(@app, t.oid, 'DELETE') = (t.relname <> 'schema_migration')
                        AND NOT has_table_privilege(@app, t.oid, 'TRUNCATE')
                        AND NOT has_table_privilege(@app, t.oid, 'REFERENCES')
                        AND NOT has_table_privilege(@app, t.oid, 'TRIGGER')))
                -- CASE, because PostgreSQL may call a privilege function before the
                -- filter on the kind of relation, and each accepts only its own kind.
                AND NOT EXISTS (
                    SELECT 1 FROM t WHERE CASE WHEN t.relkind = 'S'
                        THEN NOT (has_sequence_privilege(@app, t.oid, 'USAGE') AND has_sequence_privilege(@app, t.oid, 'SELECT'))
                        ELSE false END)
                AND (SELECT count(DISTINCT (d.defaclnamespace, d.defaclobjtype, a.privilege_type))
                     FROM pg_default_acl d, aclexplode(d.defaclacl) a
                     WHERE d.defaclrole = @owner::regrole AND a.grantee = @app::regrole
                       AND d.defaclnamespace = ANY(SELECT oid FROM pg_namespace WHERE nspname = ANY(@schemas))
                       AND ((d.defaclobjtype = 'r' AND a.privilege_type IN ('SELECT', 'INSERT', 'UPDATE', 'DELETE'))
                         OR (d.defaclobjtype = 'S' AND a.privilege_type IN ('USAGE', 'SELECT')))) = 12
            """);
        check.Parameters.AddWithValue("schemas", Schemas);
        check.Parameters.AddWithValue("app", options.AppRole);
        check.Parameters.AddWithValue("owner", options.OwnerRole);
        check.Parameters.AddWithValue("session_table", CallerSessionTable);
        check.Parameters.AddWithValue("functions", functions);
        if (await check.ExecuteScalarAsync(ct) is true)
            return Record("grants", StepOutcome.Done, $"{options.AppRole} has row access in both schemas, and later tables inherit it{sessions}");

        if (options.Plan)
            return Record("grants", StepOutcome.WouldApply, $"would grant {options.AppRole} row access in {string.Join(" and ", Schemas)}{sessions}");

        var app = Quote(options.AppRole);
        var schemas = string.Join(", ", Schemas);
        var sql = new StringBuilder($"""
            GRANT USAGE ON SCHEMA {schemas} TO {app};
            GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA {schemas} TO {app};
            REVOKE TRUNCATE, REFERENCES, TRIGGER ON ALL TABLES IN SCHEMA {schemas} FROM {app};
            REVOKE INSERT, UPDATE, DELETE ON prem_config.schema_migration, prem_index.schema_migration FROM {app};
            GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA {schemas} TO {app};
            ALTER DEFAULT PRIVILEGES FOR ROLE {Quote(options.OwnerRole)} IN SCHEMA {schemas}
                GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO {app};
            ALTER DEFAULT PRIVILEGES FOR ROLE {Quote(options.OwnerRole)} IN SCHEMA {schemas}
                GRANT USAGE, SELECT ON SEQUENCES TO {app};

            """);
        foreach (var function in functions)
            sql.Append($"GRANT EXECUTE ON FUNCTION {function} TO {app};\n");
        if (sessionTable)
            sql.Append($"REVOKE ALL ON {CallerSessionTable} FROM {app};\n");
        await ExecuteAsync(owner, sql.ToString(), ct);
        return Record("grants", StepOutcome.Applied, $"granted {options.AppRole} row access in both schemas, and to tables later migrations add{sessions}");
    }

    /// <summary>
    /// The search role may use both schemas and read the documents and their
    /// chunks, where row-level security binds it to the caller's rights, and
    /// nothing else: no other table, no sequence, no write, no caller-session
    /// function, and no default privileges, so a table a later migration adds
    /// gives it nothing. Where the schema has the text-match function, it may run
    /// that. Anything more it holds is taken away.
    /// </summary>
    private async Task<bool> EnsureSearchGrantsAsync(CancellationToken ct)
    {
        const string Step = "grants.search";
        var readable = string.Join(" and ", SearchReadable);
        if (!_ownerLogsIn || !_rolesExist || !_schemaCurrent)
            return Record(Step, StepOutcome.WouldApply, $"would let {options.SearchRole} read {readable} and nothing else");

        await using var owner = NpgsqlDataSource.Create(ConnectionFor(options.OwnerRole, _ownerPassword!));
        var (present, _, _) = await SecurityObjectsAsync(owner, ct);
        var forbidden = present.Where(f => FunctionName(f) != TextMatchesName).ToArray();
        var required = present.Where(f => FunctionName(f) == TextMatchesName).ToArray();
        var runs = required.Length == 0 ? "" : $", and may run {TextMatchesName}";
        await using var check = owner.CreateCommand("""
            WITH t AS (
                SELECT c.oid, n.nspname || '.' || c.relname AS qualified, c.relkind
                FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
                WHERE n.nspname = ANY(@schemas) AND c.relkind IN ('r', 'p', 'v', 'm', 'f', 'S'))
            SELECT
                (SELECT bool_and(has_schema_privilege(@search, s, 'USAGE') AND NOT has_schema_privilege(@search, s, 'CREATE'))
                 FROM unnest(@schemas) s)
                -- CASE throughout, because PostgreSQL may call a privilege function
                -- before the filter on the kind of relation.
                AND NOT EXISTS (
                    SELECT 1 FROM t WHERE CASE
                        WHEN t.relkind = 'S' THEN false
                        WHEN t.qualified = ANY(@readable) THEN
                            NOT has_table_privilege(@search, t.oid, 'SELECT')
                            OR has_table_privilege(@search, t.oid, 'INSERT, UPDATE, DELETE, TRUNCATE, REFERENCES, TRIGGER')
                            OR has_any_column_privilege(@search, t.oid, 'INSERT, UPDATE, REFERENCES')
                        ELSE
                            has_table_privilege(@search, t.oid, 'SELECT, INSERT, UPDATE, DELETE, TRUNCATE, REFERENCES, TRIGGER')
                            OR has_any_column_privilege(@search, t.oid, 'SELECT, INSERT, UPDATE, REFERENCES')
                        END)
                AND NOT EXISTS (
                    SELECT 1 FROM t WHERE CASE WHEN t.relkind = 'S'
                        THEN has_sequence_privilege(@search, t.oid, 'USAGE, SELECT, UPDATE') ELSE false END)
                AND NOT EXISTS (
                    SELECT 1 FROM pg_default_acl d, aclexplode(d.defaclacl) a WHERE a.grantee = @search::regrole)
                AND NOT EXISTS (
                    SELECT 1 FROM unnest(@functions) f WHERE has_function_privilege(@search, to_regprocedure(f), 'EXECUTE'))
                AND NOT EXISTS (
                    SELECT 1 FROM unnest(@required) f WHERE NOT has_function_privilege(@search, to_regprocedure(f), 'EXECUTE'))
            """);
        check.Parameters.AddWithValue("schemas", Schemas);
        check.Parameters.AddWithValue("search", options.SearchRole);
        check.Parameters.AddWithValue("readable", SearchReadable);
        check.Parameters.AddWithValue("functions", forbidden);
        check.Parameters.AddWithValue("required", required);
        if (await check.ExecuteScalarAsync(ct) is true)
            return Record(Step, StepOutcome.Done, $"{options.SearchRole} reads {readable} and nothing else{runs}");

        if (options.Plan)
            return Record(Step, StepOutcome.WouldApply, $"would let {options.SearchRole} read {readable} and nothing else{runs}");

        var search = Quote(options.SearchRole);
        var schemas = string.Join(", ", Schemas);
        var sql = new StringBuilder($"""
            REVOKE ALL ON ALL TABLES IN SCHEMA {schemas} FROM {search};
            REVOKE ALL ON ALL SEQUENCES IN SCHEMA {schemas} FROM {search};
            REVOKE CREATE ON SCHEMA {schemas} FROM {search};
            ALTER DEFAULT PRIVILEGES FOR ROLE {Quote(options.OwnerRole)} IN SCHEMA {schemas} REVOKE ALL ON TABLES FROM {search};
            ALTER DEFAULT PRIVILEGES FOR ROLE {Quote(options.OwnerRole)} IN SCHEMA {schemas} REVOKE ALL ON SEQUENCES FROM {search};
            GRANT USAGE ON SCHEMA {schemas} TO {search};
            GRANT SELECT ON {string.Join(", ", SearchReadable)} TO {search};

            """);
        foreach (var function in forbidden)
            sql.Append($"REVOKE EXECUTE ON FUNCTION {function} FROM {search};\n");
        foreach (var function in required)
            sql.Append($"GRANT EXECUTE ON FUNCTION {function} TO {search};\n");
        await ExecuteAsync(owner, sql.ToString(), ct);
        return Record(Step, StepOutcome.Applied, $"let {options.SearchRole} read {readable} and nothing else{runs}");
    }

    /// <summary>
    /// The longest a statement the search role runs in this database may take
    /// before the server stops it. A read still running after this holds a
    /// connection and a core for a caller who has stopped waiting; the
    /// driver's own command timeout (30 seconds unless the connection sets
    /// another) goes with the client, and this holds whatever the client does.
    /// </summary>
    internal const string SearchStatementTimeout = "15s";

    /// <summary>
    /// The search role's statement_timeout in this database, set with the
    /// admin connection, since only a role that may create roles may change
    /// another role's settings and the owner may not. A timeout already set,
    /// such as one an operator chose, is kept; none, or zero, is replaced by
    /// <see cref="SearchStatementTimeout"/>.
    /// </summary>
    private async Task<bool> EnsureSearchTimeoutAsync(NpgsqlDataSource admin, CancellationToken ct)
    {
        const string Step = "timeout.search";
        var stop = $"stop any statement {options.SearchRole} runs in {options.DatabaseName} after";
        if (!_rolesExist || !_databaseExists)
            return Record(Step, StepOutcome.WouldApply, $"would make the server {stop} {SearchStatementTimeout}");

        await using var read = admin.CreateCommand("""
            SELECT split_part(s, '=', 2) FROM pg_db_role_setting r, unnest(r.setconfig) s
            WHERE r.setrole = @role::regrole AND r.setdatabase = (SELECT oid FROM pg_database WHERE datname = @db)
              AND split_part(s, '=', 1) = 'statement_timeout'
            """);
        read.Parameters.AddWithValue("role", options.SearchRole);
        read.Parameters.AddWithValue("db", options.DatabaseName);
        if (await read.ExecuteScalarAsync(ct) is string set && set != "0")
            return Record(Step, StepOutcome.Done, $"the server will {stop} {set}");

        if (options.Plan)
            return Record(Step, StepOutcome.WouldApply, $"would make the server {stop} {SearchStatementTimeout}");
        await ExecuteAsync(admin,
            $"ALTER ROLE {Quote(options.SearchRole)} IN DATABASE {Quote(options.DatabaseName)} SET statement_timeout = '{SearchStatementTimeout}'", ct);
        return Record(Step, StepOutcome.Applied, $"made the server {stop} {SearchStatementTimeout}");
    }

    /// <summary>
    /// The security functions this schema has, each as a signature the server
    /// resolves exactly: the caller-session functions by their fixed signatures,
    /// and the text-match function by name, with the arguments it has today.
    /// Also whether the session table exists, and how many functions carry the
    /// text-match name, since more than one would make its grant ambiguous.
    /// </summary>
    private static async Task<(string[] Functions, bool SessionTable, int TextMatchVersions)> SecurityObjectsAsync(NpgsqlDataSource owner, CancellationToken ct)
    {
        await using var cmd = owner.CreateCommand("""
            WITH text_match AS (
                SELECT p.oid::regprocedure::text AS signature FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace
                WHERE n.nspname || '.' || p.proname = @text_matches)
            SELECT array(SELECT f FROM unnest(@functions) f WHERE to_regprocedure(f) IS NOT NULL)
                       || array(SELECT signature FROM text_match ORDER BY 1),
                   to_regclass(@table) IS NOT NULL,
                   (SELECT count(*) FROM text_match)::int
            """);
        cmd.Parameters.AddWithValue("functions", CallerSessionFunctions);
        cmd.Parameters.AddWithValue("text_matches", TextMatchesName);
        cmd.Parameters.AddWithValue("table", CallerSessionTable);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        return (reader.GetFieldValue<string[]>(0), reader.GetBoolean(1), reader.GetInt32(2));
    }

    private static string FunctionName(string signature) => signature[..signature.IndexOf('(')];

    /// <summary>
    /// Who may run the caller-session and text-match functions, checked rather
    /// than assumed, since they run with rights no caller has: the owner and the
    /// application role, and for the text-match function the search role too.
    /// PUBLIC counts as anyone else. Setup stops, and changes nothing, when
    /// anyone else may.
    /// </summary>
    private async Task<bool> CheckFunctionHoldersAsync(CancellationToken ct)
    {
        const string Step = "functions.audit";
        if (!_ownerLogsIn || !_rolesExist || !_schemaCurrent)
            return Record(Step, StepOutcome.WouldApply, "would confirm who may run the caller-session and text-match functions");

        await using var owner = NpgsqlDataSource.Create(ConnectionFor(options.OwnerRole, _ownerPassword!));
        var (present, _, _) = await SecurityObjectsAsync(owner, ct);
        if (present.Length == 0)
            return Record(Step, StepOutcome.Skipped, "this schema has none of the caller-session or text-match functions");

        await using var cmd = owner.CreateCommand("""
            SELECT f, string_agg(CASE WHEN a.grantee = 0 THEN 'PUBLIC' ELSE pg_get_userbyid(a.grantee) END, ', ')
            FROM unnest(@functions) f
            JOIN pg_proc p ON p.oid = to_regprocedure(f)
            CROSS JOIN LATERAL aclexplode(COALESCE(p.proacl, acldefault('f', p.proowner))) a
            WHERE a.privilege_type = 'EXECUTE'
              AND a.grantee <> p.proowner
              AND a.grantee <> @app::regrole::oid
              AND NOT (split_part(f, '(', 1) = @text_matches AND a.grantee = @search::regrole::oid)
            GROUP BY f ORDER BY f
            """);
        cmd.Parameters.AddWithValue("functions", present);
        cmd.Parameters.AddWithValue("app", options.AppRole);
        cmd.Parameters.AddWithValue("search", options.SearchRole);
        cmd.Parameters.AddWithValue("text_matches", TextMatchesName);
        var others = new List<string>();
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct))
                others.Add($"{FunctionName(reader.GetString(0))} by {reader.GetString(1)}");
        if (others.Count > 0)
            return Fail(Step,
                $"These run with rights no caller has, and others may run them: {string.Join("; ", others)}. " +
                $"Revoke EXECUTE from them as {options.OwnerRole}, then run setup again.");
        return Record(Step, StepOutcome.Checked,
            $"only {options.OwnerRole} and {options.AppRole} may run {string.Join(", ", present.Select(FunctionName))}" +
            (present.Any(f => FunctionName(f) == TextMatchesName) ? $", and {options.SearchRole} {TextMatchesName}" : ""));
    }

    /// <summary>
    /// Where the schema keeps the list of roles that write the whole index (the
    /// list row-level security reads to exempt ingest), the application role is
    /// on it, and only the application role: never the search role, never the
    /// owner. The list is the owner's alone to read and write. Setup adds the
    /// application role when the list is empty and stops, without changing it,
    /// when the list holds anyone else.
    /// </summary>
    private async Task<bool> EnsureIndexWriterAsync(CancellationToken ct)
    {
        const string Step = "index.writer";
        if (!_ownerLogsIn || !_schemaCurrent)
            return Record(Step, StepOutcome.WouldApply, $"would list {options.AppRole} as the one role that writes the index, where the schema keeps that list");

        await using var owner = NpgsqlDataSource.Create(ConnectionFor(options.OwnerRole, _ownerPassword!));
        await using (var exists = owner.CreateCommand($"SELECT to_regclass('{IndexWriterTable}') IS NOT NULL"))
            if (await exists.ExecuteScalarAsync(ct) is not true)
                return Record(Step, StepOutcome.Skipped, "this schema keeps no list of index writers");

        var listed = new List<string>();
        await using (var read = owner.CreateCommand($"SELECT role_name::text FROM {IndexWriterTable} ORDER BY 1"))
        await using (var reader = await read.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct))
                listed.Add(reader.GetString(0));

        var others = listed.Where(r => r != options.AppRole).ToArray();
        if (others.Length > 0)
            return Fail(Step,
                $"{IndexWriterTable} lists {string.Join(", ", others)}, which would read and write every document whoever the caller is. " +
                $"Only {options.AppRole} may be listed. Remove the others as {options.OwnerRole} and run setup again.");
        if (listed.Count == 1)
            return Record(Step, StepOutcome.Done, $"{options.AppRole} is the one role listed as writing the index");
        if (options.Plan)
            return Record(Step, StepOutcome.WouldApply, $"would list {options.AppRole} as the one role that writes the index");

        await using (var insert = owner.CreateCommand($"INSERT INTO {IndexWriterTable}(role_name) VALUES (@app) ON CONFLICT DO NOTHING"))
        {
            insert.Parameters.AddWithValue("app", options.AppRole);
            await insert.ExecuteNonQueryAsync(ct);
        }
        return Record(Step, StepOutcome.Applied, $"listed {options.AppRole} as the one role that writes the index");
    }

    private const string IndexWriterTable = "prem_config.index_writer";

    /// <summary>
    /// What least privilege means for the application and search roles, checked
    /// rather than assumed: each owns nothing in the database, belongs to no other
    /// role (membership would lend it that role's privileges), and does not bypass
    /// row-level security, which would silently turn the second line off. Setup
    /// does not remove ownership or memberships it did not create; it stops and
    /// says so.
    /// </summary>
    private async Task<bool> CheckRoleAsync(string role, string step, CancellationToken ct)
    {
        if (!_ownerLogsIn || !_rolesExist)
            return Record(step, StepOutcome.WouldApply, $"would confirm {role} owns nothing, belongs to no role and does not bypass row-level security");

        await using var owner = NpgsqlDataSource.Create(ConnectionFor(options.OwnerRole, _ownerPassword!));
        await using var check = owner.CreateCommand("""
            SELECT
                (SELECT count(*) FROM pg_class WHERE relowner = @role::regrole)
              + (SELECT count(*) FROM pg_namespace WHERE nspowner = @role::regrole)
              + (SELECT count(*) FROM pg_database WHERE datdba = @role::regrole),
                (SELECT count(*) FROM pg_auth_members WHERE member = @role::regrole),
                (SELECT rolbypassrls OR rolsuper FROM pg_roles WHERE rolname = @role)
            """);
        check.Parameters.AddWithValue("role", role);
        await using var reader = await check.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        var owned = reader.GetInt64(0);
        var memberships = reader.GetInt64(1);
        var bypasses = reader.GetBoolean(2);
        if (owned > 0)
            return Fail(step, $"{role} owns {owned} object(s) in {options.DatabaseName}. An owner bypasses row-level security; reassign them to {options.OwnerRole}.");
        if (memberships > 0)
            return Fail(step, $"{role} is a member of {memberships} other role(s) and would inherit their privileges. Revoke those memberships.");
        if (bypasses)
            return Fail(step, $"{role} bypasses row-level security. Remove BYPASSRLS and SUPERUSER from it.");
        return Record(step, StepOutcome.Checked, $"{role} owns nothing, belongs to no role and does not bypass row-level security");
    }

    // ------------------------------------------------------------ administrator

    /// <summary>
    /// The first administrator, made through the identity store as the
    /// application role, exactly as the portal will later make users. Only when
    /// no administrator can sign in: a run that finds one changes nothing, whatever
    /// name it was given. The password is asked for only when it is about to be
    /// used, is hashed at once, and is never printed. There is no default account:
    /// without a sign-in name, setup makes none and says how to.
    /// </summary>
    private async Task<bool> EnsureAdministratorAsync(CancellationToken ct)
    {
        var name = options.AdministratorName;
        if (!_appLogsIn || !_schemaCurrent || !_textMatchCurrent)
            return name is null
                ? Record("administrator", StepOutcome.Warning, "no administrator will be made: pass --admin-user to make the first one")
                : Record("administrator", StepOutcome.WouldApply, $"would make '{name}' the first administrator, asking for the password");

        await using var db = new PremagenticDatabase(CredentialsFile.ReadConnectionString(options.AppCredentialsPath));
        await db.InitializeAsync(ct);
        var tenantId = await TenantIdAsync(db, ct);
        if (tenantId is null)
            return Record("administrator", StepOutcome.WouldApply, $"would make '{name}' the first administrator, asking for the password");
        var store = new IdentityStore(db, tenantId.Value);

        var ready = (await store.ListUsersAsync(ct)).Where(u => u.Role == Role.Administrator && !u.Disabled && u.HasPassword).ToList();
        if (ready.Count > 0)
            return Record("administrator", StepOutcome.Done,
                $"{ready.Count} administrator(s) can sign in, among them '{ready[0].SignInName}'; none was made" +
                (name is not null && !ready.Any(u => u.SignInName.Equals(name, StringComparison.OrdinalIgnoreCase)) ? $", so '{name}' was not" : ""));
        if (name is null)
            return Record("administrator", StepOutcome.Warning,
                "No administrator can sign in yet, and none was made. Run setup again with --admin-user <sign-in name> to make the first one.");

        var existing = await store.FindUserByNameAsync(name, ct);
        if (existing is not null && (existing.Role != Role.Administrator || existing.Disabled))
            return Fail("administrator",
                $"'{name}' is already a user, as {existing.Role.ToString().ToLowerInvariant()}{(existing.Disabled ? ", disabled" : "")}. " +
                "Choose another sign-in name for the first administrator.");
        if (options.Plan)
            return Record("administrator", StepOutcome.WouldApply,
                existing is null ? $"would make '{name}' the first administrator, asking for the password"
                                 : $"would set a password for administrator '{name}', which has none");

        var password = options.AdministratorPassword?.Invoke();
        if (string.IsNullOrEmpty(password))
            return Fail("administrator", $"No password was given for '{name}'. Give it at the prompt, or in the file named by --admin-password-file.");
        string hash;
        try
        {
            hash = new PasswordHasher().Hash(password);
        }
        catch (ArgumentException)
        {
            return Fail("administrator", $"The password for '{name}' must be 1 to {PasswordHasher.MaxPasswordLength} characters of well-formed text.");
        }

        User user;
        try
        {
            user = existing ?? await store.CreateUserAsync(name, name, Role.Administrator, ct);
        }
        catch (ArgumentException ex)
        {
            return Fail("administrator", ex.Message);
        }
        await store.SetPasswordHashAsync(user.Id, hash, ct);
        return Record("administrator", StepOutcome.Applied,
            existing is null ? $"made '{name}' the first administrator" : $"set the password of administrator '{name}', which had none");
    }

    // -------------------------------------------------------------------- https

    /// <summary>
    /// The API's HTTPS endpoint: a self-signed certificate for the host name,
    /// written with its private key to a private file under a generated password,
    /// and the Kestrel settings that name both, in a private file the API reads
    /// beside its credentials file. The password exists only in that settings
    /// file. A certificate setup did not make is never touched: a customer who
    /// points the settings at their own keeps it through every later run.
    /// </summary>
    private bool EnsureHttps()
    {
        if (!HttpsCertificate.IsValidHostName(options.HostName))
            return Fail("https", $"'{options.HostName}' is not a host name or an IP address. Pass the name clients use with --host-name.");
        if (options.HttpsPort is < 1 or > 65535)
            return Fail("https", $"{options.HttpsPort} is not a port number.");

        // The API reads these settings into its configuration, and the
        // certificate they name: a file another account could have written is
        // not read, let alone left in place.
        foreach (var file in new[] { options.KestrelSettingsPath, options.CertificatePath })
            if ((File.Exists(file) || InstallAccess.IsLink(file)) && InstallAccess.Examine(file) is { } finding)
                return Fail("https", FileRefusal(file, finding));
        // And settings the API would refuse to start with are refused here,
        // before setup says it is complete.
        if (KestrelSettingsRefusal() is { } beyondKestrel)
            return Fail("https", beyondKestrel);

        var url = $"https://*:{options.HttpsPort}";
        var current = ReadKestrelSettings();
        if (current is { OwnCertificate: false })
            return Record("https", StepOutcome.Done,
                $"{options.KestrelSettingsPath} names a certificate setup did not make ({current.CertificatePath}); left as it is");

        var reason = current is null ? null : WhyReplace(current, url);
        if (current is not null && reason is null)
            return Record("https", StepOutcome.Done,
                $"HTTPS on port {options.HttpsPort} with the certificate for {options.HostName}, valid until {current.NotAfter:yyyy-MM-dd}");

        var why = current is null ? "no HTTPS settings yet" : reason!;
        if (options.Plan)
            return Record("https", StepOutcome.WouldApply,
                $"would make a self-signed certificate for {options.HostName} and HTTPS settings for port {options.HttpsPort} ({why})");

        var password = Secrets.NewPassword();
        using var certificate = HttpsCertificate.Create(options.HostName, DateTimeOffset.UtcNow);
        var pfx = certificate.ExportPkcs12(Pkcs12ExportPbeParameters.Pbes2Aes256Sha256, password);
        var settings = Encoding.UTF8.GetBytes(KestrelSettingsJson(url, password));
        try
        {
            CredentialsFile.WritePrivate(options.CertificatePath, pfx);
            CredentialsFile.WritePrivate(options.KestrelSettingsPath, settings);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pfx);
            CryptographicOperations.ZeroMemory(settings);
        }
        File.WriteAllText(options.PublicCertificatePath, certificate.ExportCertificatePem() + "\n");
        return Record("https", StepOutcome.Applied,
            $"made a self-signed certificate for {options.HostName}, valid until {certificate.NotAfter.ToUniversalTime():yyyy-MM-dd}, " +
            $"and HTTPS settings for port {options.HttpsPort} in {options.KestrelSettingsPath} ({why}). " +
            $"Clients that must trust it can be given {options.PublicCertificatePath}.");
    }

    /// <summary>
    /// The refusal the API would give the Kestrel settings file for setting
    /// more than the Kestrel section, judged by its top-level keys; null when
    /// there is no such file, it sets the Kestrel section alone, or it is not
    /// JSON (setup then writes it anew).
    /// </summary>
    private string? KestrelSettingsRefusal()
    {
        if (!File.Exists(options.KestrelSettingsPath)) return null;
        try
        {
            using var stream = File.OpenRead(options.KestrelSettingsPath);
            return JsonNode.Parse(stream, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true })
                is JsonObject settings
                ? InstallFiles.KestrelSettingsRefusal(options.KestrelSettingsPath, settings.Select(property => property.Key))
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed record KestrelSettings(bool OwnCertificate, string? CertificatePath, string? Url, string? Password, DateTime NotAfter, bool CoversHost);

    private KestrelSettings? ReadKestrelSettings()
    {
        if (!File.Exists(options.KestrelSettingsPath)) return null;
        JsonNode? https;
        try
        {
            using var stream = File.OpenRead(options.KestrelSettingsPath);
            https = JsonNode.Parse(stream, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true })
                ?["Kestrel"]?["Endpoints"]?["Https"];
        }
        catch (JsonException)
        {
            // Unreadable: treated as setup's own, and replaced.
            return new KestrelSettings(true, null, null, null, default, false);
        }

        var path = https?["Certificate"]?["Path"]?.GetValue<string>();
        if (path is not null && path != InstallFiles.Certificate)
            return new KestrelSettings(false, path, null, null, default, false);

        var password = https?["Certificate"]?["Password"]?.GetValue<string>();
        var url = https?["Url"]?.GetValue<string>();
        if (password is null || !File.Exists(options.CertificatePath))
            return new KestrelSettings(true, path, url, null, default, false);
        try
        {
            using var loaded = X509CertificateLoader.LoadPkcs12FromFile(options.CertificatePath, password);
            return new KestrelSettings(true, path, url, password, loaded.NotAfter.ToUniversalTime(),
                CoversHost: HttpsCertificate.Covers(loaded, options.HostName));
        }
        catch (CryptographicException)
        {
            return new KestrelSettings(true, path, url, null, default, false);
        }
    }

    private string? WhyReplace(KestrelSettings current, string url)
    {
        if (current.Password is null) return "the certificate could not be read with the password in the settings";
        if (!current.CoversHost) return $"the certificate does not name {options.HostName}";
        if (current.NotAfter < DateTime.UtcNow.Add(HttpsCertificate.RenewWithin)) return $"the certificate expires {current.NotAfter:yyyy-MM-dd}";
        if (current.Url != url) return $"the port changed to {options.HttpsPort}";
        if (!CredentialsFile.IsPrivate(options.KestrelSettingsPath, ServiceReader) || !CredentialsFile.IsPrivate(options.CertificatePath, ServiceReader))
            return "the settings or the certificate were readable by other accounts";
        return null;
    }

    private static string KestrelSettingsJson(string url, string password) =>
        $$"""
        {
          // Written by prem setup. The API reads this file from the folder of its
          // PREM_CREDENTIALS_FILE. Relative paths are relative to this folder.
          //
          // To serve your own certificate, replace "Certificate" with a PKCS #12 file,
          //   "Certificate": { "Path": "C:/certs/search.pfx", "Password": "..." }
          // or a PEM pair,
          //   "Certificate": { "Path": "/etc/ssl/search.crt", "KeyPath": "/etc/ssl/search.key" }
          // and restart the API. Setup leaves a certificate it did not make alone.
          "Kestrel": {
            "Endpoints": {
              "Https": {
                "Url": "{{url}}",
                "Certificate": {
                  "Path": "{{InstallFiles.Certificate}}",
                  "Password": "{{password}}"
                }
              }
            }
          }
        }

        """;

    // ---------------------------------------------------------- windows service

    /// <summary>
    /// Registers the API as the Windows service <c>Premagentic</c> under its own
    /// virtual account, gives that account read access to the application's three
    /// files, and puts <c>PREM_CREDENTIALS_FILE</c> in its environment. Only with
    /// <see cref="SetupOptions.WindowsService"/>, and only from an elevated
    /// process. The service is registered, not started.
    /// </summary>
    private bool EnsureWindowsService()
    {
        const string step = "service.windows";
        if (!OperatingSystem.IsWindows())
            return Fail(step, "--windows-service registers a Windows service. On Linux, install deploy/systemd/premagentic-api.service instead.");

        var executable = options.ApiExecutable ?? Path.Combine(AppContext.BaseDirectory, "Premagentic.Api.exe");
        var files = new[] { options.AppCredentialsPath, options.SearchCredentialsPath, options.KestrelSettingsPath, options.CertificatePath };
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (Path.GetFullPath(options.CredentialsDirectory).StartsWith(profile + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            return Fail(step, $"{options.CredentialsDirectory} is inside this account's profile, which the service cannot open. " +
                              @"Run setup with --credentials-dir C:\ProgramData\Premagentic (the default with --windows-service).");
        if (!File.Exists(executable))
            return Fail(step, $"The API program was not found at {executable}. Pass its path with --api-path.");

        var exists = WindowsServiceRegistration.Exists();
        var missingGrants = files.Where(f => !File.Exists(f) || !CredentialsFile.CanRead(f, ServiceReader!)).ToArray();
        // The service refuses a folder or file it reads that anyone but
        // administrators and SYSTEM owns or may change, this account included.
        var folder = Path.GetFullPath(options.CredentialsDirectory);
        var administratorsOwn = Directory.Exists(folder)
                                && files.Where(f => File.Exists(f) || InstallAccess.IsLink(f)).Prepend(folder).All(p => InstallAccess.Untrusted(p, ServiceReader) is null);
        var describe = $"service {WindowsServiceRegistration.ServiceName} running {executable} as {WindowsServiceRegistration.Account}, " +
                       $"with read access to {string.Join(", ", files.Select(Path.GetFileName))} and PREM_CREDENTIALS_FILE={options.AppCredentialsPath}, " +
                       $"and {folder} and those files owned by administrators, who alone may change them";

        if (exists && missingGrants.Length == 0 && administratorsOwn && WindowsServiceRegistration.HasEnvironment(options.AppCredentialsPath))
            return Record(step, StepOutcome.Done, $"{describe}; start it with: sc.exe start {WindowsServiceRegistration.ServiceName}");
        if (options.Plan)
            return Record(step, StepOutcome.WouldApply, $"would register the {describe}. Needs an elevated prompt.");
        if (!WindowsServiceRegistration.IsElevated())
            return Fail(step, "Registering a Windows service needs an elevated prompt. Open the prompt with Run as administrator " +
                              "and run the same setup command again; the steps already done are kept and skipped.");

        if (!exists) WindowsServiceRegistration.Create(executable, options.AppCredentialsPath,
            dependsOn: options.BundledPostgres is null ? null : BundledPostgres.ServiceName);
        else WindowsServiceRegistration.SetEnvironment(options.AppCredentialsPath);
        // Gives the service its read access too. Refuses a link among them
        // before any rule is set, since the rules would land on its target.
        try
        {
            InstallAccess.GiveToAdministrators(folder, files.Where(f => File.Exists(f) || InstallAccess.IsLink(f)), ServiceReader!);
        }
        catch (IOException link)
        {
            return Fail(step, link.Message);
        }
        return Record(step, StepOutcome.Applied, $"registered the {describe}; start it with: sc.exe start {WindowsServiceRegistration.ServiceName}");
    }

    // ------------------------------------------------------------------- health

    /// <summary>
    /// Connects as the application role through its credentials file, exactly
    /// as the application will, and runs a search. The search is recorded in the
    /// audit trail like every other question, under the label
    /// <c>setup-health-check</c>; that row is the only thing a repeat run writes.
    /// </summary>
    private async Task HealthCheckAsync(CancellationToken ct)
    {
        if (options.Plan)
        {
            // Not even on a finished install: every search is written to the audit
            // trail, and a plan changes nothing.
            Record("health", StepOutcome.Skipped, "a plan does not search, because every search is recorded");
            return;
        }
        if (!_appLogsIn)
        {
            Record("health", StepOutcome.WouldApply, $"would search the index as {options.AppRole}");
            return;
        }

        var watch = System.Diagnostics.Stopwatch.StartNew();
        await using var db = new PremagenticDatabase(CredentialsFile.ReadConnectionString(options.AppCredentialsPath));
        await db.InitializeAsync(ct);
        var tenantId = (await TenantIdAsync(db, ct))!.Value;

        // The deployment's own extensions, which exist by now: the database is
        // there and the allow list is readable, so the health check embeds with
        // the provider the deployment will use, including one an extension
        // registered. Checking with the built-ins alone would refuse an
        // installation for naming a provider it supports.
        var extensions = await ExtensionHosting.LoadAsync(new SettingsStore(db, tenantId), ct: ct);
        IEmbeddingProvider embedder;
        try
        {
            embedder = options.EmbedderFactory(extensions.EmbeddingProviders);
        }
        catch (StartupRefusedException refused)
        {
            // A provider that cannot be made, such as a model or its runtime
            // that cannot be loaded, is recorded where it happened, with its
            // one sentence.
            Fail("health", refused.Message);
            return;
        }
        try
        {
            var result = await new HybridSearch(db, embedder).SearchAsync(
                tenantId, "prem setup health check",
                new SearchOptions(AccessScope.ForPrincipals("setup-health-check"), TopK: 1), ct);
            // And the search role reaches the index through its own file.
            if (_searchLogsIn)
            {
                await using var search = NpgsqlDataSource.Create(CredentialsFile.ReadConnectionString(options.SearchCredentialsPath));
                await using var read = search.CreateCommand("SELECT count(*) FROM prem_index.chunk");
                await read.ExecuteScalarAsync(ct);
            }
            Record("health", StepOutcome.Checked,
                $"searched as {options.AppRole} through {options.AppCredentialsPath}: {result.Hits.Count} hit(s), and read the index as " +
                $"{options.SearchRole} through {options.SearchCredentialsPath}, {watch.ElapsedMilliseconds} ms");
        }
        finally
        {
            (embedder as IDisposable)?.Dispose();
        }
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>
    /// The deployment's tenant, made when it does not exist yet, except in a plan,
    /// which gets null. An existing tenant is only read, so a repeat run does not
    /// rewrite its name.
    /// </summary>
    private async Task<Guid?> TenantIdAsync(PremagenticDatabase db, CancellationToken ct)
    {
        await using var find = db.DataSource.CreateCommand("SELECT id FROM prem_config.tenant WHERE key = @key");
        find.Parameters.AddWithValue("key", options.TenantKey);
        if (await find.ExecuteScalarAsync(ct) is Guid existing) return existing;
        return options.Plan ? null : await db.EnsureTenantAsync(options.TenantKey, options.TenantName, ct);
    }

    private string ConnectionFor(string role, string password) =>
        new NpgsqlConnectionStringBuilder(_admin.ConnectionString)
        {
            Database = options.DatabaseName,
            Username = role,
            Password = password,
        }.ConnectionString;

    private static string Quote(string identifier) => $"\"{identifier}\"";

    private static async Task ExecuteAsync(NpgsqlDataSource source, string sql, CancellationToken ct)
    {
        await using var cmd = source.CreateCommand(sql);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// An error for the operator. Statements are never quoted: the ones that
    /// create or change a role carry a password verifier.
    /// </summary>
    private static string Describe(Exception ex) => ex switch
    {
        PostgresException pg => $"{pg.SqlState}: {pg.MessageText}" + (pg.Hint is { } hint ? $" ({hint})" : ""),
        _ => ex.Message,
    };

    private static string Gib(long bytes) => $"{bytes / (1024.0 * 1024 * 1024):F1} GiB";

    private bool Record(string step, StepOutcome outcome, string detail)
    {
        _report.Add(new SetupStep(step, outcome, detail));
        output.WriteLine($"  {Label(outcome),-9} {step,-20} {detail}");
        return outcome != StepOutcome.Failed;
    }

    private bool Fail(string step, string detail) => Record(step, StepOutcome.Failed, detail);

    private static string Label(StepOutcome outcome) => outcome switch
    {
        StepOutcome.Done => "[ok]",
        StepOutcome.Applied => "[applied]",
        StepOutcome.WouldApply => "[plan]",
        StepOutcome.Checked => "[checked]",
        StepOutcome.Warning => "[warn]",
        StepOutcome.Skipped => "[skip]",
        _ => "[FAILED]",
    };
}

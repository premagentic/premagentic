using Premagentic.Core.Storage;
using Npgsql;

namespace Premagentic.Cli.Setup;

internal enum RemovalOutcome
{
    /// <summary>Removed on this run.</summary>
    Removed,
    /// <summary>Would be removed, in a plan; nothing was changed.</summary>
    WouldRemove,
    /// <summary>Stays, because only a purge removes it.</summary>
    Kept,
    /// <summary>A step for someone else to run, printed rather than run, such as the systemd steps for root.</summary>
    Manual,
    /// <summary>Not on this computer, or already gone.</summary>
    NotFound,
    Failed,
}

internal sealed record RemovalStep(string Name, RemovalOutcome Outcome, string Detail);

/// <param name="CredentialsDirectory">The folder setup wrote the credentials files into.</param>
/// <param name="Purge">Remove the data too: the database, the three roles, a bundled server's data folder and the credentials files.</param>
/// <param name="Plan">Report every step and change nothing.</param>
/// <param name="AdminConnectionString">
/// A role that can drop roles, as setup's admin connection. A purge needs it on a
/// PostgreSQL that existed before Premagentic; a bundled server's superuser is
/// read from <c>postgres.credentials</c> instead.
/// </param>
/// <param name="BundledPostgres">The bundled <c>pgsql</c> folder, so a server started by hand can be stopped.</param>
/// <param name="DataDirectory">The bundled server's data folder, when the install has one.</param>
/// <param name="SystemdUnitPath">Where the API's systemd unit is installed.</param>
/// <param name="Services">The Windows services; null for this computer's.</param>
internal sealed record RemovalOptions(
    string CredentialsDirectory,
    bool Purge = false,
    bool Plan = false,
    string? AdminConnectionString = null,
    string? BundledPostgres = null,
    string? DataDirectory = null,
    string SystemdUnitPath = RemovalEngine.SystemdUnit,
    IServiceControl? Services = null)
{
    public string OwnerCredentialsPath => Path.Combine(CredentialsDirectory, InstallFiles.OwnerCredentials);

    public string AppCredentialsPath => Path.Combine(CredentialsDirectory, InstallFiles.AppCredentials);

    public string SearchCredentialsPath => Path.Combine(CredentialsDirectory, InstallFiles.SearchCredentials);

    public string SuperuserCredentialsPath => Path.Combine(CredentialsDirectory, InstallFiles.SuperuserCredentials);
}

/// <summary>
/// <c>prem remove</c>: takes away what setup registered and, by default, keeps
/// every piece of data, so setup run again finds the install as it was.
/// <para>
/// It looks first and acts second, from one list, so a plan shows exactly what
/// a run does. Everything that would stop the run is found while looking,
/// before anything is changed, and is refused with a
/// <see cref="StartupRefusedException"/>: no elevated prompt for the Windows
/// services, no owner credentials, an owner that cannot connect, no role that
/// may drop roles, a data folder that is not a PostgreSQL cluster. The one
/// refusal that can come later is a session still open on the database when
/// the purge is about to drop it, after the services were stopped.
/// </para>
/// <para>
/// A purge drops the database as its owner, from <c>owner.credentials</c>, and
/// the three roles as a role that can drop roles, since setup makes the owner
/// without that right. Before any role is dropped, the database the owner sees
/// must be the one the admin connection sees, so a purge never drops roles on
/// another server. The credentials files are deleted last, the owner's after
/// the rest, so a purge that stops half way can be run again.
/// </para>
/// </summary>
internal sealed class RemovalEngine(RemovalOptions options, TextWriter output)
{
    /// <summary>Where <c>deploy/systemd/premagentic-api.service</c> says to install the unit.</summary>
    public const string SystemdUnit = "/etc/systemd/system/premagentic-api.service";

    private const string GssEncryptionModeKey = "GSS Encryption Mode";

    private readonly List<RemovalStep> _steps = [];
    private readonly IServiceControl _services = options.Services ?? ServiceControl.ForThisComputer();

    public IReadOnlyList<RemovalStep> Steps => _steps;

    public bool Failed => _steps.Any(s => s.Outcome == RemovalOutcome.Failed);

    public bool Changed => _steps.Any(s => s.Outcome == RemovalOutcome.Removed);

    /// <summary>One entry of the list: a thing to remove, or a line that only reports.</summary>
    private sealed record Item(string Name, RemovalOutcome Outcome, string Detail, Func<CancellationToken, Task<string>>? Act = null);

    /// <exception cref="StartupRefusedException">Something that stops the run, found before anything was changed (and, for an open session, before the database was touched).</exception>
    public async Task RunAsync(CancellationToken ct = default)
    {
        foreach (var item in await LookAsync(ct))
        {
            if (item.Act is null || options.Plan)
            {
                Record(item.Name, item.Outcome, item.Detail);
                continue;
            }
            try
            {
                Record(item.Name, RemovalOutcome.Removed, await item.Act(ct));
            }
            catch (Exception ex) when (ex is NpgsqlException or InvalidOperationException or IOException or UnauthorizedAccessException
                                           && ex is not StartupRefusedException)
            {
                Record(item.Name, RemovalOutcome.Failed, Describe(ex));
                return;
            }
        }
    }

    private async Task<List<Item>> LookAsync(CancellationToken ct)
    {
        var items = new List<Item>();

        // The Windows services, the API before the database it depends on.
        var apiService = _services.Exists(WindowsServiceRegistration.ServiceName);
        var databaseService = _services.Exists(BundledPostgres.ServiceName);
        if ((apiService || databaseService) && !options.Plan && !_services.CanChange)
            throw Refused("Stopping and removing the Windows services needs an elevated prompt. Open the prompt with Run as " +
                          "administrator and run the same command again. Nothing was changed.");
        items.Add(ServiceItem("service.api", WindowsServiceRegistration.ServiceName, apiService));

        if (File.Exists(options.SystemdUnitPath))
            items.Add(new Item("service.systemd", RemovalOutcome.Manual,
                $"prem does not run anything as root. As root: systemctl disable --now premagentic-api && rm {options.SystemdUnitPath} " +
                "&& systemctl daemon-reload"));
        else if (!OperatingSystem.IsWindows())
            items.Add(new Item("service.systemd", RemovalOutcome.NotFound,
                $"no systemd unit at {options.SystemdUnitPath}; if the API runs under another unit, stop and disable it as root"));

        var owner = ReadConnection(options.OwnerCredentialsPath, out var ownerProblem);
        var appRole = ReadConnection(options.AppCredentialsPath, out _)?.Username;
        var searchRole = ReadConnection(options.SearchCredentialsPath, out _)?.Username;
        var bundledSuperuser = ReadConnection(options.SuperuserCredentialsPath, out _);
        var data = options.DataDirectory is null ? null : Path.GetFullPath(options.DataDirectory);
        var pg = options.BundledPostgres is null ? null : new BundledPostgres(options.BundledPostgres);
        var bundledRunning = bundledSuperuser is not null && await CanConnectAsync(bundledSuperuser, ct);

        if (options.Purge)
            await PurgeDatabaseAsync(items, owner, ownerProblem, [searchRole, appRole], bundledSuperuser, bundledRunning, data, ct);

        // The bundled server stops after the database is dropped from it.
        items.Add(ServiceItem("service.database", BundledPostgres.ServiceName, databaseService));
        if (!databaseService && bundledRunning && data is not null)
        {
            if (pg is not null && pg.IsCluster(data) && pg.IsRunning(data))
                items.Add(new Item("postgres.server", RemovalOutcome.WouldRemove, $"would stop the bundled server on {data}", _ =>
                {
                    var (code, text) = pg.Stop(data);
                    if (code != 0) throw new InvalidOperationException($"pg_ctl stop failed with exit code {code}: {BundledPostgres.Last(text)}");
                    return Task.FromResult($"stopped the bundled server on {data}");
                }));
            else if (options.Purge)
                throw Refused($"The bundled server on {data} is running and cannot be stopped without its programs. Pass " +
                              "--bundled-postgres with its pgsql folder, or stop it, then run the same command again. Nothing was changed.");
            else
                items.Add(new Item("postgres.server", RemovalOutcome.Manual,
                    $"the bundled server on {data} is still running; stop it with: pg_ctl stop -D \"{data}\""));
        }

        if (options.Purge) PurgeFiles(items, data);
        else Keep(items, owner, ownerProblem, [owner?.Username, appRole, searchRole], data);
        return items;
    }

    // ---------------------------------------------------------------- services

    private Item ServiceItem(string step, string name, bool exists)
    {
        if (!exists)
            return new Item(step, RemovalOutcome.NotFound, _services.Applies ? $"no Windows service {name} on this computer" : "");
        return new Item(step, RemovalOutcome.WouldRemove,
            $"would stop and remove the Windows service {name}" + (_services.CanChange ? "" : ". Needs an elevated prompt."), _ =>
            {
                _services.StopAndDelete(name);
                return Task.FromResult($"stopped and removed the Windows service {name}");
            });
    }

    // ---------------------------------------------------------------- the data

    private async Task PurgeDatabaseAsync(
        List<Item> items, NpgsqlConnectionStringBuilder? owner, string? ownerProblem, string?[] otherRoles,
        NpgsqlConnectionStringBuilder? bundledSuperuser, bool bundledRunning, string? data, CancellationToken ct)
    {
        if (owner is null)
            throw Refused($"{ownerProblem} A purge drops the database as its owner, from that file. If the install's files are " +
                          "elsewhere, pass --credentials-dir. Nothing was changed.");
        var database = owner.Database!;
        var where = $"{owner.Host}:{owner.Port}";

        // A bundled server that is not running holds the database and the roles in
        // its data folder, which the purge deletes.
        if (bundledSuperuser is not null && !bundledRunning)
        {
            if (data is null || !File.Exists(Path.Combine(data, "PG_VERSION")))
                throw Refused($"The bundled server is not running, and no data folder holding its cluster was found{(data is null ? "" : $" at {data}")}. " +
                              "Pass --data-dir, or start the server, then run the same command again. Nothing was changed.");
            items.Add(new Item("database", RemovalOutcome.WouldRemove,
                $"the database {database} and its roles are in the bundled server's data folder {data}, and go with it"));
            return;
        }

        var adminText = options.AdminConnectionString ?? bundledSuperuser?.ConnectionString;
        if (string.IsNullOrWhiteSpace(adminText))
            throw Refused("Dropping the three roles needs a role that can drop roles, and the owner cannot: setup makes it without " +
                          "that right. Pass --admin-connection-file, or set PREM_SETUP_ADMIN_CONNECTION, with the admin connection " +
                          "setup used. Nothing was changed.");
        NpgsqlConnectionStringBuilder admin;
        try
        {
            admin = WithoutGss(new NpgsqlConnectionStringBuilder(adminText));
        }
        catch (ArgumentException)
        {
            // Not quoted: it holds a password.
            throw Refused("The admin connection is not a PostgreSQL connection string. Nothing was changed.");
        }

        long? adminDatabaseOid, adminOwnerOid;
        string adminUser;
        try
        {
            await using var source = NpgsqlDataSource.Create(admin.ConnectionString);
            await using var cmd = source.CreateCommand("""
                SELECT current_user, r.rolsuper OR r.rolcreaterole,
                       (SELECT oid::bigint FROM pg_database WHERE datname = @db),
                       (SELECT oid::bigint FROM pg_roles WHERE rolname = @owner)
                FROM pg_roles r WHERE r.rolname = current_user
                """);
            cmd.Parameters.AddWithValue("db", database);
            cmd.Parameters.AddWithValue("owner", owner.Username!);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            await reader.ReadAsync(ct);
            adminUser = reader.GetString(0);
            if (!reader.GetBoolean(1))
                throw Refused($"The admin connection's role {adminUser} may not drop roles: it needs SUPERUSER or CREATEROLE. Nothing was changed.");
            adminDatabaseOid = reader.IsDBNull(2) ? null : reader.GetInt64(2);
            adminOwnerOid = reader.IsDBNull(3) ? null : reader.GetInt64(3);
        }
        catch (NpgsqlException ex)
        {
            throw Refused($"Cannot connect with the admin connection to {admin.Host}:{admin.Port}: {Describe(ex)}. Nothing was changed.");
        }

        // The owner proves the file belongs to this install, and the oids prove
        // the admin connection reaches the same server, so no role is dropped on
        // another one. Only when the owner cannot log in anywhere and the admin
        // sees neither its database nor its role is the install already gone.
        var (inDatabase, databaseOid, databaseError) = await OwnerSeesAsync(owner, database, "SELECT oid::bigint FROM pg_database WHERE datname = current_database()", ct);
        if (inDatabase)
        {
            if (databaseOid != adminDatabaseOid) throw DifferentServers(database);
        }
        else
        {
            var (loggedIn, roleOid, _) = await OwnerSeesAsync(owner, MaintenanceDatabase, "SELECT oid::bigint FROM pg_roles WHERE rolname = current_user", ct);
            if (loggedIn && roleOid != adminOwnerOid) throw DifferentServers(database);
            if (adminDatabaseOid is not null || (!loggedIn && adminOwnerOid is not null))
                throw Refused($"The owner role {owner.Username} in {options.OwnerCredentialsPath} cannot connect to {database} on {where}: " +
                              $"{databaseError}. Nothing was changed.");
        }

        if (adminDatabaseOid is null)
            items.Add(new Item("database", RemovalOutcome.NotFound, $"the database {database} on {where} is already gone"));
        else
            items.Add(new Item("database", RemovalOutcome.WouldRemove,
                $"would drop the database {database} on {where} as {owner.Username}, with every document, user, setting and the audit " +
                "trail, once nothing else is connected to it", c => DropDatabaseAsync(owner, admin, database, where, c)));

        if (adminOwnerOid is null)
        {
            items.Add(new Item("roles", RemovalOutcome.NotFound, $"the owner role {owner.Username} is already gone, and the roles dropped before it"));
            return;
        }
        foreach (var (step, role) in new[] { ("role.search", otherRoles[0]), ("role.app", otherRoles[1]), ("role.owner", owner.Username) })
        {
            if (role is null)
            {
                items.Add(new Item(step, RemovalOutcome.Kept,
                    $"its credentials file is missing, so its name is not known and nothing is dropped for it; drop it by hand if it exists"));
                continue;
            }
            items.Add(new Item(step, RemovalOutcome.WouldRemove, $"would drop the role {role} as {adminUser}", async c =>
            {
                await using var source = NpgsqlDataSource.Create(admin.ConnectionString);
                await using var cmd = source.CreateCommand($"DROP ROLE IF EXISTS {Quote(role)}");
                await cmd.ExecuteNonQueryAsync(c);
                return $"dropped the role {role} as {adminUser}";
            }));
        }
    }

    private async Task<string> DropDatabaseAsync(
        NpgsqlConnectionStringBuilder owner, NpgsqlConnectionStringBuilder admin, string database, string where, CancellationToken ct)
    {
        // Any session left open would hold the database, and the owner cannot end
        // another role's session. The API is stopped by now where prem can stop it.
        // A session whose client has just gone can be listed for a moment longer,
        // so an open one is looked for again for a few seconds before refusing.
        await using (var source = NpgsqlDataSource.Create(admin.ConnectionString))
        {
            for (var attempt = 1; ; attempt++)
            {
                await using var cmd = source.CreateCommand("""
                    SELECT count(*), coalesce(string_agg(DISTINCT usename, ', '), '')
                    FROM pg_stat_activity WHERE datname = @db AND pid <> pg_backend_pid()
                    """);
                cmd.Parameters.AddWithValue("db", database);
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                await reader.ReadAsync(ct);
                var (open, who) = (reader.GetInt64(0), reader.GetString(1));
                if (open == 0) break;
                if (attempt == 10)
                    throw Refused($"{open} session(s) are still open on {database} (as {who}). Stop the API and anything else " +
                                  "connected to it, then run the same command again. The database was not touched.");
                await Task.Delay(TimeSpan.FromMilliseconds(500), ct);
            }
        }

        await using (var source = NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder(owner.ConnectionString) { Database = MaintenanceDatabase }.ConnectionString))
        await using (var cmd = source.CreateCommand($"DROP DATABASE {Quote(database)}"))
            await cmd.ExecuteNonQueryAsync(ct);
        return $"dropped the database {database} on {where} as {owner.Username}";
    }

    private void PurgeFiles(List<Item> items, string? data)
    {
        if (data is not null)
        {
            if (!Directory.Exists(data))
                items.Add(new Item("postgres.data", RemovalOutcome.NotFound, $"no bundled data folder at {data}"));
            else if (!File.Exists(Path.Combine(data, "PG_VERSION")))
                throw Refused($"{data} does not hold a PostgreSQL cluster (it has no PG_VERSION), so it is not deleted. Check --data-dir. " +
                              "Nothing was changed.");
            else
                items.Add(new Item("postgres.data", RemovalOutcome.WouldRemove, $"would delete the bundled server's data folder {data}", _ =>
                {
                    Directory.Delete(data, recursive: true);
                    return Task.FromResult($"deleted the bundled server's data folder {data}");
                }));
        }

        // The owner's file last: it is what lets a purge that stopped be run again.
        string[] files =
        [
            InstallFiles.KestrelSettings, InstallFiles.Certificate, InstallFiles.PublicCertificate, InstallFiles.SearchCredentials,
            InstallFiles.AppCredentials, InstallFiles.SuperuserCredentials, InstallFiles.OwnerCredentials,
        ];
        foreach (var path in files.Select(f => Path.Combine(options.CredentialsDirectory, f)).Where(File.Exists))
            items.Add(new Item("credentials", RemovalOutcome.WouldRemove, $"would delete {path}", _ =>
            {
                File.Delete(path);
                return Task.FromResult($"deleted {path}");
            }));
        if (Directory.Exists(options.CredentialsDirectory))
            items.Add(new Item("credentials", RemovalOutcome.WouldRemove, $"would delete the folder {options.CredentialsDirectory} if nothing else is in it", _ =>
            {
                var left = Directory.EnumerateFileSystemEntries(options.CredentialsDirectory).Count();
                if (left > 0) return Task.FromResult($"kept the folder {options.CredentialsDirectory}: it holds {left} other item(s)");
                Directory.Delete(options.CredentialsDirectory);
                return Task.FromResult($"deleted the folder {options.CredentialsDirectory}");
            }));
    }

    private void Keep(List<Item> items, NpgsqlConnectionStringBuilder? owner, string? ownerProblem, string?[] roles, string? data)
    {
        items.Add(owner is null
            ? new Item("database", RemovalOutcome.NotFound, $"{ownerProblem} No database to report.")
            : new Item("database", RemovalOutcome.Kept,
                $"the database {owner.Database} on {owner.Host}:{owner.Port} stays, with every document, user, setting and the audit trail"));

        var known = roles.OfType<string>().Distinct().ToArray();
        if (known.Length > 0)
            items.Add(new Item("roles", RemovalOutcome.Kept, $"the roles {string.Join(", ", known)} stay"));

        var files = Directory.Exists(options.CredentialsDirectory)
            ? Directory.EnumerateFiles(options.CredentialsDirectory).Select(Path.GetFileName).Where(f => !f!.StartsWith('.')).Order(StringComparer.Ordinal).ToArray()
            : [];
        items.Add(files.Length == 0
            ? new Item("credentials", RemovalOutcome.NotFound, $"nothing in {options.CredentialsDirectory}")
            : new Item("credentials", RemovalOutcome.Kept, $"{options.CredentialsDirectory} stays, with {string.Join(", ", files)}"));

        if (data is not null)
            items.Add(Directory.Exists(data)
                ? new Item("postgres.data", RemovalOutcome.Kept, $"the bundled server's data folder {data} stays")
                : new Item("postgres.data", RemovalOutcome.NotFound, $"no bundled data folder at {data}"));
    }

    // ----------------------------------------------------------------- helpers

    /// <summary>
    /// The database the owner connects to in order to drop its own, which it
    /// cannot be connected to while dropping it. Every PostgreSQL has it, and
    /// every role may connect to it unless an administrator took that away.
    /// </summary>
    private const string MaintenanceDatabase = "postgres";

    /// <summary>The connection in a credentials file, or null with the reason in <paramref name="problem"/>. The file's content is never quoted.</summary>
    private static NpgsqlConnectionStringBuilder? ReadConnection(string path, out string? problem)
    {
        problem = null;
        try
        {
            return WithoutGss(new NpgsqlConnectionStringBuilder(CredentialsFile.ReadConnectionString(path)));
        }
        catch (FileNotFoundException)
        {
            problem = $"{path} does not exist.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
        {
            problem = $"{path} cannot be read ({ex.GetType().Name}); run as the account that ran setup.";
        }
        return null;
    }

    private static NpgsqlConnectionStringBuilder WithoutGss(NpgsqlConnectionStringBuilder builder)
    {
        if (!builder.Keys.Cast<string>().Contains(GssEncryptionModeKey)) builder.GssEncryptionMode = GssEncryptionMode.Disable;
        return builder;
    }

    private static async Task<bool> CanConnectAsync(NpgsqlConnectionStringBuilder connection, CancellationToken ct)
    {
        try
        {
            await using var source = NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder(connection.ConnectionString) { Timeout = 5 }.ConnectionString);
            await using var conn = await source.OpenConnectionAsync(ct);
            return true;
        }
        catch (Exception ex) when (ex is NpgsqlException or TimeoutException)
        {
            return false;
        }
    }

    /// <summary>Whether the owner can connect to <paramref name="database"/>, what <paramref name="sql"/> returns there, and why not.</summary>
    private static async Task<(bool Connected, long? Value, string? Error)> OwnerSeesAsync(
        NpgsqlConnectionStringBuilder owner, string database, string sql, CancellationToken ct)
    {
        try
        {
            await using var source = NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder(owner.ConnectionString) { Database = database }.ConnectionString);
            await using var cmd = source.CreateCommand(sql);
            return (true, (long?)await cmd.ExecuteScalarAsync(ct), null);
        }
        catch (NpgsqlException ex)
        {
            return (false, null, Describe(ex));
        }
    }

    private StartupRefusedException DifferentServers(string database) =>
        Refused($"The admin connection does not reach the server in {options.OwnerCredentialsPath}: the database {database} and the " +
                "owner role are not the same objects there. Give the admin connection of that server. Nothing was changed.");

    private static StartupRefusedException Refused(string message) => new(message);

    private static string Quote(string identifier) => $"\"{identifier.Replace("\"", "\"\"")}\"";

    /// <summary>An error for the operator. Statements are never quoted, and neither is any connection string.</summary>
    private static string Describe(Exception ex) => ex switch
    {
        PostgresException pg => $"{pg.SqlState}: {pg.MessageText}",
        _ => ex.Message,
    };

    private void Record(string step, RemovalOutcome outcome, string detail)
    {
        if (detail.Length == 0) return;
        _steps.Add(new RemovalStep(step, outcome, detail));
        output.WriteLine($"  {Label(outcome),-9} {step,-20} {detail}");
    }

    internal static string Label(RemovalOutcome outcome) => outcome switch
    {
        RemovalOutcome.Removed => "[removed]",
        RemovalOutcome.WouldRemove => "[plan]",
        RemovalOutcome.Kept => "[kept]",
        RemovalOutcome.Manual => "[manual]",
        RemovalOutcome.NotFound => "[none]",
        _ => "[FAILED]",
    };
}

/// <summary>The Windows services <c>prem remove</c> stops and removes. A seam, so the order and the refusals can be tested without an elevated prompt.</summary>
internal interface IServiceControl
{
    /// <summary>False where there are no Windows services to look for.</summary>
    bool Applies { get; }

    /// <summary>True when this process may stop and remove services.</summary>
    bool CanChange { get; }

    bool Exists(string name);

    /// <exception cref="InvalidOperationException">The service did not stop, or could not be removed.</exception>
    void StopAndDelete(string name);
}

internal static class ServiceControl
{
    public static IServiceControl ForThisComputer() => OperatingSystem.IsWindows() ? new Windows() : new None();

    private sealed class None : IServiceControl
    {
        public bool Applies => false;
        public bool CanChange => false;
        public bool Exists(string name) => false;
        public void StopAndDelete(string name) => throw new InvalidOperationException("There are no Windows services here.");
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private sealed class Windows : IServiceControl
    {
        public bool Applies => true;
        public bool CanChange => WindowsServiceRegistration.IsElevated();
        public bool Exists(string name) => WindowsServiceRegistration.Exists(name);
        public void StopAndDelete(string name) => WindowsServiceRegistration.StopAndDelete(name);
    }
}

using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using Premagentic.Cli.Setup;
using Premagentic.Core.Storage;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Premagentic.Tests;

/// <summary>Windows services that exist only in the test: records what was stopped and removed, in order.</summary>
internal sealed class FakeServices(bool canChange, params string[] present) : IServiceControl
{
    public List<string> Removed { get; } = [];
    public bool Applies => true;
    public bool CanChange => canChange;
    public bool Exists(string name) => present.Contains(name) && !Removed.Contains(name);
    public void StopAndDelete(string name) => Removed.Add(name);
}

/// <summary>
/// <c>prem remove</c> against a real, stock PostgreSQL, on installs made by
/// setup. The Windows services are a fake, so the order and the refusals are
/// tested without an elevated prompt; sc.exe itself is not exercised here.
/// </summary>
public sealed class RemovalTests(DatastoreTestDatabase server) : IClassFixture<DatastoreTestDatabase>, IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "premagentic-removal-tests", Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
    }

    private async Task<SetupOptions> InstallAsync()
    {
        var suffix = Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant();
        var options = new SetupOptions(
            AdminConnectionString: server.AdminConnectionString,
            CredentialsDirectory: Path.Combine(_folder, suffix, "credentials"),
            EmbedderFactory: _ => new SeededEmbeddingProvider(),
            RequiredModelDirectory: null,
            DatabaseName: "prem_" + suffix,
            OwnerRole: "owner_" + suffix,
            AppRole: "app_" + suffix,
            SearchRole: "search_" + suffix);
        var output = new StringWriter();
        var report = await new SetupEngine(options, output).RunAsync();
        Assert.False(report.Failed, output.ToString());
        return options;
    }

    private RemovalOptions Removal(SetupOptions install, bool purge = false, bool plan = false, string? admin = null,
                                   IServiceControl? services = null, string? unit = null, string? data = null) =>
        new(install.CredentialsDirectory, purge, plan, admin, DataDirectory: data,
            SystemdUnitPath: unit ?? Path.Combine(_folder, "no-such-unit"), Services: services ?? new FakeServices(false));

    private static async Task<(RemovalEngine Engine, string Output)> RunAsync(RemovalOptions options)
    {
        var output = new StringWriter();
        var engine = new RemovalEngine(options, output);
        await engine.RunAsync();
        return (engine, output.ToString());
    }

    private async Task<bool> DatabaseExistsAsync(string name) =>
        await ScalarAsync(server.AdminConnectionString, $"SELECT count(*) FROM pg_database WHERE datname = '{name}'") is 1L;

    private async Task<string[]> RolesAsync(SetupOptions install) =>
        (await ScalarAsync(server.AdminConnectionString,
            $"SELECT coalesce(string_agg(rolname, ',' ORDER BY rolname), '') FROM pg_roles WHERE rolname IN ('{install.OwnerRole}', '{install.AppRole}', '{install.SearchRole}')")
            as string)!.Split(',', StringSplitOptions.RemoveEmptyEntries);

    private static async Task<object?> ScalarAsync(string connection, string sql)
    {
        await using var source = NpgsqlDataSource.Create(connection);
        await using var cmd = source.CreateCommand(sql);
        return await cmd.ExecuteScalarAsync();
    }

    /// <summary>
    /// An admin connection whose password could not appear in any output by
    /// accident (the container's own superuser's password is its name).
    /// </summary>
    private async Task<string> SecretAdminAsync()
    {
        var name = "admin_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant();
        var password = Secrets.NewPassword();
        await ScalarAsync(server.AdminConnectionString, $"CREATE ROLE {name} LOGIN SUPERUSER PASSWORD '{password}'");
        return new NpgsqlConnectionStringBuilder(server.AdminConnectionString) { Username = name, Password = password }.ConnectionString;
    }

    /// <summary>No password of the install or of the admin connection is in <paramref name="output"/>.</summary>
    private static void AssertNoSecret(string output, string admin, params string[] credentialsFiles)
    {
        foreach (var password in credentialsFiles.Select(f => new NpgsqlConnectionStringBuilder(CredentialsFile.ReadConnectionString(f)).Password)
                     .Append(new NpgsqlConnectionStringBuilder(admin).Password))
        {
            Assert.False(string.IsNullOrEmpty(password), "a password the check needs is missing");
            Assert.DoesNotContain(password!, output);
        }
    }

    /// <summary>Everything the install made is still there, and the application still logs in.</summary>
    private async Task AssertUntouchedAsync(SetupOptions install)
    {
        Assert.True(await DatabaseExistsAsync(install.DatabaseName), "the database was dropped");
        Assert.Equal(3, (await RolesAsync(install)).Length);
        foreach (var file in new[] { install.OwnerCredentialsPath, install.AppCredentialsPath, install.SearchCredentialsPath })
            Assert.True(File.Exists(file), file + " was deleted");
        Assert.Equal(1, await ScalarAsync(CredentialsFile.ReadConnectionString(install.AppCredentialsPath), "SELECT 1"));
    }

    // ------------------------------------------------------------ keeping

    [Fact]
    public async Task A_plan_lists_every_step_and_changes_nothing()
    {
        var install = await InstallAsync();
        var services = new FakeServices(canChange: false, WindowsServiceRegistration.ServiceName, BundledPostgres.ServiceName);

        var (engine, output) = await RunAsync(Removal(install, plan: true, services: services));

        Assert.False(engine.Failed, output);
        Assert.Contains("[plan]    service.api          would stop and remove the Windows service Premagentic. Needs an elevated prompt.", output);
        Assert.Contains("[plan]    service.database     would stop and remove the Windows service PremagenticDb", output);
        Assert.Contains($"[kept]    database             the database {install.DatabaseName} on ", output);
        Assert.Contains($"the roles {install.OwnerRole}, {install.AppRole}, {install.SearchRole} stay", output);
        Assert.Contains($"[kept]    credentials          {install.CredentialsDirectory} stays, with app.credentials", output);
        Assert.Empty(services.Removed);
        Assert.False(engine.Changed);
        await AssertUntouchedAsync(install);
    }

    [Fact]
    public async Task Without_purge_the_services_go_and_every_piece_of_data_stays()
    {
        var install = await InstallAsync();
        var services = new FakeServices(canChange: true, WindowsServiceRegistration.ServiceName, BundledPostgres.ServiceName);

        var (engine, output) = await RunAsync(Removal(install, services: services));

        Assert.False(engine.Failed, output);
        // The API first, since it depends on the database's service.
        Assert.Equal([WindowsServiceRegistration.ServiceName, BundledPostgres.ServiceName], services.Removed);
        Assert.Contains("[removed] service.api          stopped and removed the Windows service Premagentic", output);
        Assert.Contains($"[kept]    database             the database {install.DatabaseName}", output);
        Assert.DoesNotContain(engine.Steps, s => s.Outcome == RemovalOutcome.Removed && s.Name is not ("service.api" or "service.database"));
        await AssertUntouchedAsync(install);

        // Setup finds the install as it was left.
        var again = new StringWriter();
        var report = await new SetupEngine(install, again).RunAsync();
        Assert.False(report.Failed, again.ToString());
        Assert.False(report.Changed, again.ToString());
    }

    [Fact]
    public async Task The_services_are_not_touched_without_an_elevated_prompt()
    {
        var install = await InstallAsync();
        var services = new FakeServices(canChange: false, WindowsServiceRegistration.ServiceName);

        var refused = await Assert.ThrowsAsync<StartupRefusedException>(() => RunAsync(Removal(install, services: services)));

        Assert.Contains("needs an elevated prompt", refused.Message);
        Assert.Empty(services.Removed);
        await AssertUntouchedAsync(install);
    }

    [Fact]
    public async Task The_systemd_steps_are_printed_for_root_and_not_run()
    {
        var install = await InstallAsync();
        var unit = Path.Combine(_folder, "premagentic-api.service");
        Directory.CreateDirectory(_folder);
        await File.WriteAllTextAsync(unit, "[Unit]\n");

        var (engine, output) = await RunAsync(Removal(install, unit: unit));

        Assert.False(engine.Failed, output);
        Assert.Contains($"[manual]  service.systemd      prem does not run anything as root. As root: systemctl disable --now premagentic-api && rm {unit} && systemctl daemon-reload", output);
        Assert.True(File.Exists(unit));
        await AssertUntouchedAsync(install);
    }

    [Theory]
    [InlineData("SERVICE_NAME: Premagentic\r\n        TYPE               : 10  WIN32_OWN_PROCESS\r\n        STATE              : 4  RUNNING\r\n", 4)]
    [InlineData("SERVICE_NAME: Premagentic\r\n        TYPE               : 10  WIN32_OWN_PROCESS\r\n        STATE              : 1  STOPPED\r\n", 1)]
    [InlineData("[SC] EnumQueryServicesStatus:OpenService FAILED 1060:\r\n", null)]
    public void The_state_is_read_from_what_sc_query_prints(string output, int? state) =>
        Assert.Equal(state, WindowsServiceRegistration.State(output));

    // ------------------------------------------------------------- purging

    [Fact]
    public async Task A_purge_drops_the_database_and_the_roles_and_deletes_the_files()
    {
        var install = await InstallAsync();
        var admin = await SecretAdminAsync();
        var files = Path.Combine(_folder, "copies");
        Directory.CreateDirectory(files);
        var copies = new[] { install.OwnerCredentialsPath, install.AppCredentialsPath, install.SearchCredentialsPath }
            .Select(f => { var copy = Path.Combine(files, Path.GetFileName(f)); File.Copy(f, copy); return copy; }).ToArray();

        var (engine, output) = await RunAsync(Removal(install, purge: true, admin: admin));

        Assert.False(engine.Failed, output);
        Assert.Contains($"[removed] database             dropped the database {install.DatabaseName} on ", output);
        Assert.Contains($"[removed] role.owner           dropped the role {install.OwnerRole} as ", output);
        Assert.False(await DatabaseExistsAsync(install.DatabaseName));
        Assert.Empty(await RolesAsync(install));
        Assert.False(Directory.Exists(install.CredentialsDirectory), output);
        AssertNoSecret(output, admin, copies);
    }

    [Fact]
    public async Task A_purge_without_the_owner_file_is_refused_and_changes_nothing()
    {
        var install = await InstallAsync();
        var kept = Path.Combine(_folder, "owner.credentials.kept");
        File.Move(install.OwnerCredentialsPath, kept);

        var refused = await Assert.ThrowsAsync<StartupRefusedException>(() =>
            RunAsync(Removal(install, purge: true, admin: server.AdminConnectionString)));

        Assert.Contains($"{install.OwnerCredentialsPath} does not exist.", refused.Message);
        File.Move(kept, install.OwnerCredentialsPath);
        await AssertUntouchedAsync(install);
    }

    [Fact]
    public async Task A_purge_is_refused_when_the_owner_cannot_connect()
    {
        var install = await InstallAsync();
        var real = CredentialsFile.ReadConnectionString(install.OwnerCredentialsPath);
        CredentialsFile.Write(install.OwnerCredentialsPath,
            new NpgsqlConnectionStringBuilder(real) { Password = "not-the-password" }.ConnectionString, "owner");

        var refused = await Assert.ThrowsAsync<StartupRefusedException>(() =>
            RunAsync(Removal(install, purge: true, admin: server.AdminConnectionString)));

        Assert.Contains($"The owner role {install.OwnerRole} in {install.OwnerCredentialsPath} cannot connect to {install.DatabaseName}", refused.Message);
        CredentialsFile.Write(install.OwnerCredentialsPath, real, "owner");
        await AssertUntouchedAsync(install);
    }

    [Fact]
    public async Task A_purge_without_a_role_that_may_drop_roles_is_refused()
    {
        var install = await InstallAsync();

        var refused = await Assert.ThrowsAsync<StartupRefusedException>(() => RunAsync(Removal(install, purge: true)));

        Assert.Contains("--admin-connection-file", refused.Message);
        await AssertUntouchedAsync(install);
    }

    [Fact]
    public async Task A_purge_is_refused_while_a_session_is_open_on_the_database()
    {
        var install = await InstallAsync();
        await using var source = NpgsqlDataSource.Create(CredentialsFile.ReadConnectionString(install.AppCredentialsPath));
        await using var open = await source.OpenConnectionAsync();

        var refused = await Assert.ThrowsAsync<StartupRefusedException>(() =>
            RunAsync(Removal(install, purge: true, admin: server.AdminConnectionString)));

        Assert.Contains($"1 session(s) are still open on {install.DatabaseName} (as {install.AppRole})", refused.Message);
        await AssertUntouchedAsync(install);
    }

    [Fact]
    public async Task A_purge_never_drops_roles_through_another_servers_admin_connection()
    {
        var install = await InstallAsync();
        await using var other = new PostgreSqlBuilder(DatastoreTestDatabase.Image).Build();
        await other.StartAsync();
        // The same role names on the other server, which the purge must not drop.
        foreach (var role in new[] { install.OwnerRole, install.AppRole, install.SearchRole })
            await ScalarAsync(other.GetConnectionString(), $"CREATE ROLE {role}");

        var refused = await Assert.ThrowsAsync<StartupRefusedException>(() =>
            RunAsync(Removal(install, purge: true, admin: other.GetConnectionString())));

        Assert.Contains("does not reach the server in", refused.Message);
        Assert.Equal(3L, await ScalarAsync(other.GetConnectionString(),
            $"SELECT count(*) FROM pg_roles WHERE rolname IN ('{install.OwnerRole}', '{install.AppRole}', '{install.SearchRole}')"));
        await AssertUntouchedAsync(install);
    }

    [Fact]
    public async Task A_data_folder_that_is_not_a_cluster_is_never_deleted()
    {
        var install = await InstallAsync();
        var notACluster = Directory.CreateDirectory(Path.Combine(_folder, "documents")).FullName;
        await File.WriteAllTextAsync(Path.Combine(notACluster, "keep.txt"), "not a database");

        var refused = await Assert.ThrowsAsync<StartupRefusedException>(() =>
            RunAsync(Removal(install, purge: true, admin: server.AdminConnectionString, data: notACluster)));

        Assert.Contains("does not hold a PostgreSQL cluster", refused.Message);
        Assert.True(File.Exists(Path.Combine(notACluster, "keep.txt")));
        await AssertUntouchedAsync(install);
    }

    [Fact]
    public async Task A_purge_that_stopped_after_the_database_can_be_run_again()
    {
        var install = await InstallAsync();
        // As if a first purge stopped between the database and the roles.
        await ScalarAsync(server.AdminConnectionString, $"DROP DATABASE {install.DatabaseName}");

        var (engine, output) = await RunAsync(Removal(install, purge: true, admin: server.AdminConnectionString));

        Assert.False(engine.Failed, output);
        Assert.Contains($"[none]    database             the database {install.DatabaseName} on ", output);
        Assert.Empty(await RolesAsync(install));
        Assert.False(Directory.Exists(install.CredentialsDirectory));
    }

    // ------------------------------------------------------------ the CLI

    [Fact]
    public async Task The_cli_lists_a_purge_until_it_is_confirmed_then_carries_it_out()
    {
        var install = await InstallAsync();
        var admin = await SecretAdminAsync();
        var adminFile = Path.Combine(_folder, "admin.credentials");
        CredentialsFile.Write(adminFile, admin, "admin");
        var owner = Path.Combine(_folder, "owner.credentials.copy");
        File.Copy(install.OwnerCredentialsPath, owner);
        string[] purge = ["remove", "--purge", "--credentials-dir", install.CredentialsDirectory, "--admin-connection-file", adminFile];

        var (unconfirmed, listed) = await RunCliAsync(purge);
        Assert.Equal(1, unconfirmed);
        Assert.Contains($"[plan]    database             would drop the database {install.DatabaseName}", listed);
        Assert.Contains("To go ahead, run the same command with --yes.", listed);
        await AssertUntouchedAsync(install);

        var (yesAlone, yesText) = await RunCliAsync(["remove", "--yes", "--credentials-dir", install.CredentialsDirectory]);
        Assert.Equal(2, yesAlone);
        Assert.Contains("--yes confirms --purge", yesText);

        var (confirmed, done) = await RunCliAsync([.. purge, "--yes"]);
        Assert.Equal(0, confirmed);
        Assert.True(done.IndexOf("[plan]    database", StringComparison.Ordinal) < done.IndexOf("[removed] database", StringComparison.Ordinal), done);
        Assert.Contains("Purge complete", done);
        Assert.False(await DatabaseExistsAsync(install.DatabaseName));
        AssertNoSecret(listed + done, admin, owner);

        // Once it is gone, a purge is refused, with exit code 2 and the reason.
        var (again, againText) = await RunCliAsync([.. purge, "--yes"]);
        Assert.Equal(2, again);
        Assert.Contains("prem remove refused: ", againText);
        Assert.Contains("does not exist", againText);
        // Refused while the list was being made: the refusal alone, under no empty heading.
        Assert.DoesNotContain("the full list", againText);
    }

    private static async Task<(int ExitCode, string Output)> RunCliAsync(string[] arguments)
    {
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add(CliPath());
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        start.Environment.Remove("PREM_SETUP_ADMIN_CONNECTION");

        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, await stdout + await stderr);
    }

    // The CLI's own build output, as the test project records it (see its project file).
    private static string CliPath()
    {
        var path = typeof(RemovalTests).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(attribute => attribute.Key == "Premagentic.Cli.Path")
            .Value;
        Assert.True(File.Exists(path), "the CLI was not built where the test project recorded it: " + path);
        return path!;
    }
}

/// <summary>
/// <c>prem remove</c> on the PostgreSQL bundled on Windows, started by hand as
/// this account. Needs the bundle: these run when PREM_TEST_POSTGRES_BUNDLE names
/// its pgsql folder, and otherwise return at once.
/// </summary>
public sealed class RemovalBundledPostgresTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "premagentic-removal-bundled", Guid.NewGuid().ToString("N")[..8]);
    private readonly List<(BundledPostgres Pg, string Data)> _servers = [];

    private static string? Bundle => Environment.GetEnvironmentVariable("PREM_TEST_POSTGRES_BUNDLE") is { Length: > 0 } path ? path : null;

    public void Dispose()
    {
        foreach (var (pg, data) in _servers)
            if (pg.IsCluster(data) && pg.IsRunning(data)) pg.Stop(data);
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
    }

    private async Task<SetupOptions> InstallAsync(string name)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        var options = new SetupOptions(
            AdminConnectionString: "",
            CredentialsDirectory: Path.Combine(_folder, name, "credentials"),
            EmbedderFactory: _ => new SeededEmbeddingProvider(),
            RequiredModelDirectory: null,
            HostName: "localhost",
            BundledPostgres: Bundle,
            DataDirectory: Path.Combine(_folder, name, "data"),
            PostgresPort: port);
        _servers.Add((new BundledPostgres(Bundle!), options.DataDirectory!));
        var output = new StringWriter();
        var report = await new SetupEngine(options, output).RunAsync();
        Assert.False(report.Failed, output.ToString());
        return options;
    }

    private static RemovalOptions Removal(SetupOptions install, bool purge) =>
        new(install.CredentialsDirectory, purge, BundledPostgres: Bundle, DataDirectory: install.DataDirectory,
            SystemdUnitPath: Path.Combine(install.CredentialsDirectory, "no-such-unit"), Services: new FakeServices(false));

    [Fact]
    public async Task Without_purge_the_bundled_server_is_stopped_and_its_data_stays()
    {
        if (Bundle is null) return;
        var install = await InstallAsync("keep");
        var pg = new BundledPostgres(Bundle);

        var output = new StringWriter();
        var engine = new RemovalEngine(Removal(install, purge: false), output);
        await engine.RunAsync();

        Assert.False(engine.Failed, output.ToString());
        Assert.Contains($"[removed] postgres.server      stopped the bundled server on {Path.GetFullPath(install.DataDirectory!)}", output.ToString());
        Assert.Contains("[kept]    postgres.data", output.ToString());
        Assert.False(pg.IsRunning(install.DataDirectory!));
        Assert.True(pg.IsCluster(install.DataDirectory!));
        Assert.True(File.Exists(install.OwnerCredentialsPath));

        // Setup starts it again and finds everything as it was.
        var again = new StringWriter();
        var report = await new SetupEngine(install, again).RunAsync();
        Assert.False(report.Failed, again.ToString());
        Assert.Equal(StepOutcome.Applied, report.Find("postgres.server")!.Outcome);
        Assert.Equal(StepOutcome.Done, report.Find("database")!.Outcome);
    }

    [Fact]
    public async Task A_purge_drops_what_is_inside_stops_the_bundled_server_and_deletes_its_data_folder()
    {
        if (Bundle is null) return;
        var install = await InstallAsync("purge");
        var pg = new BundledPostgres(Bundle);

        var output = new StringWriter();
        var engine = new RemovalEngine(Removal(install, purge: true), output);
        await engine.RunAsync();

        Assert.False(engine.Failed, output.ToString());
        Assert.Contains("[removed] database             dropped the database premagentic", output.ToString());
        Assert.Contains("[removed] role.owner           dropped the role premagentic_owner as premagentic_admin", output.ToString());
        Assert.Contains("[removed] postgres.data", output.ToString());
        Assert.False(Directory.Exists(install.DataDirectory));
        Assert.False(Directory.Exists(install.CredentialsDirectory), output.ToString());
        Assert.False(pg.IsCluster(install.DataDirectory!));
    }
}

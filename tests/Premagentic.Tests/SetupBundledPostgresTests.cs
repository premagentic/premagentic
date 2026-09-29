using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.AccessControl;
using System.Security.Cryptography;
using Premagentic.Cli.Setup;
using Premagentic.Core.Storage;
using Npgsql;

namespace Premagentic.Tests;

/// <summary>
/// The PostgreSQL bundled on Windows. The arguments and the runtime check are
/// tested everywhere. The whole cluster (initdb, a start on a spare port as
/// this account, setup against it, a dump and restore round trip, a stop) needs
/// the bundle, which is laid out by installer/windows/fetch-postgresql.ps1 and
/// never committed: those tests run when PREM_TEST_POSTGRES_BUNDLE names its
/// pgsql folder, and otherwise return at once. Nothing here registers a service
/// or runs elevated.
/// </summary>
public sealed class SetupBundledPostgresTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "premagentic-bundled-tests", Guid.NewGuid().ToString("N")[..8]);
    private readonly List<(BundledPostgres Pg, string Data)> _servers = [];

    private static string? Bundle => Environment.GetEnvironmentVariable("PREM_TEST_POSTGRES_BUNDLE") is { Length: > 0 } path ? path : null;

    public void Dispose()
    {
        foreach (var (pg, data) in _servers)
            if (pg.IsCluster(data) && pg.IsRunning(data)) pg.Stop(data);
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private SetupOptions NewOptions(string name, int port) => new(
        AdminConnectionString: "",
        CredentialsDirectory: Path.Combine(_folder, name, "credentials"),
        EmbedderFactory: _ => new SeededEmbeddingProvider(),
        RequiredModelDirectory: null,
        HostName: "localhost",
        BundledPostgres: Bundle,
        DataDirectory: Path.Combine(_folder, name, "data"),
        PostgresPort: port);

    private async Task<(SetupReport Report, string Output)> RunAsync(SetupOptions options)
    {
        _servers.Add((new BundledPostgres(options.BundledPostgres!), options.DataDirectory!));
        var output = new StringWriter();
        var report = await new SetupEngine(options, output).RunAsync();
        return (report, output.ToString());
    }

    private static async Task<object?> ScalarAsync(string connection, string sql)
    {
        await using var source = NpgsqlDataSource.Create(connection);
        await using var cmd = source.CreateCommand(sql);
        return await cmd.ExecuteScalarAsync();
    }

    // ---------------------------------------------------------- everywhere

    [Fact]
    public void Initdb_is_given_scram_utf8_icu_and_localhost_and_never_a_password()
    {
        var arguments = BundledPostgres.InitDbArguments(@"D:\data", @"C:\secret\pw.tmp", 5544);

        Assert.Equal(@"C:\secret\pw.tmp", arguments[arguments.ToList().IndexOf("--pwfile") + 1]);
        Assert.Contains("--auth=scram-sha-256", arguments);
        Assert.Equal("UTF8", arguments[arguments.ToList().IndexOf("-E") + 1]);
        Assert.Contains("--locale-provider=icu", arguments);
        Assert.Contains("listen_addresses=localhost", arguments);
        Assert.Contains("port=5544", arguments);
        Assert.Equal(BundledPostgres.Superuser, arguments[arguments.ToList().IndexOf("-U") + 1]);
        Assert.DoesNotContain(arguments, a => a.StartsWith("--pw", StringComparison.Ordinal) && a != "--pwfile");
    }

    [Fact]
    public void The_database_service_runs_under_its_own_virtual_account_with_no_password()
    {
        var arguments = BundledPostgres.RegisterArguments(@"D:\data");

        Assert.Equal(["register", "-N", "PremagenticDb", "-U", @"NT SERVICE\PremagenticDb", "-D", @"D:\data", "-S", "auto"], arguments);
        Assert.DoesNotContain("-P", arguments);
    }

    [Fact]
    public void Without_the_visual_cpp_runtime_setup_says_what_to_install()
    {
        if (!OperatingSystem.IsWindows()) return; // The runtime is a Windows prerequisite.

        var empty = Directory.CreateDirectory(Path.Combine(_folder, "system")).FullName;
        Assert.Equal(BundledPostgres.RuntimeMissing, new BundledPostgres(Path.Combine(_folder, "pgsql")).RuntimeProblem(empty));
        Assert.Contains("vc_redist.x64.exe", BundledPostgres.RuntimeMissing);
    }

    [Fact]
    public void The_api_service_depends_on_the_database_service_when_both_are_registered()
    {
        var alone = WindowsServiceRegistration.CreateCommands(@"C:\Premagentic\Premagentic.Api.exe");
        var withDatabase = WindowsServiceRegistration.CreateCommands(@"C:\Premagentic\Premagentic.Api.exe", BundledPostgres.ServiceName);

        Assert.DoesNotContain(alone, c => c[0] == "config");
        Assert.Equal(["config", "Premagentic", "depend=", "PremagenticDb"], withDatabase.Single(c => c[0] == "config"));
    }

    [Fact]
    public void A_tools_whole_output_is_collected_before_it_is_read()
    {
        if (!OperatingSystem.IsWindows()) return; // cmd.exe stands in for a tool here.

        // Many tools at once, each ending on a burst of output, so the readers fall
        // behind the exits the way they did under load: one quiet run keeps up and
        // could not tell reading too early from reading everything.
        var results = new (int Code, string Output)[48];
        Parallel.For(0, results.Length, new ParallelOptions { MaxDegreeOfParallelism = 48 }, i =>
            results[i] = BundledPostgres.Run("cmd.exe", ["/d", "/c", "for /L %i in (1,1,2000) do @echo line %i"], TimeSpan.FromMinutes(2)));

        foreach (var (code, output) in results)
        {
            Assert.Equal(0, code);
            var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            Assert.Equal(2000, lines.Length);
            Assert.Equal("line 2000", lines[^1]);
        }
    }

    // ------------------------------------------------------ with the bundle

    [Fact]
    public void A_bundle_missing_a_library_is_named_as_incomplete()
    {
        if (Bundle is null || !OperatingSystem.IsWindows()) return;

        // The two tools the check runs, without the libraries beside them.
        var partial = Directory.CreateDirectory(Path.Combine(_folder, "partial", "bin")).FullName;
        foreach (var tool in new[] { "postgres.exe", "initdb.exe" })
            File.Copy(Path.Combine(Bundle, "bin", tool), Path.Combine(partial, tool));

        var problem = new BundledPostgres(Path.GetDirectoryName(partial)!).RuntimeProblem();

        Assert.NotNull(problem);
        Assert.Contains("cannot load a library it needs", problem);
        Assert.Contains("fetch-postgresql.ps1", problem);
        // The control: the whole bundle loads.
        Assert.Null(new BundledPostgres(Bundle).RuntimeProblem());
    }

    [Fact]
    public async Task Setup_makes_starts_and_uses_the_bundled_server_as_this_account()
    {
        if (Bundle is null) return;

        var options = NewOptions("full", FreePort());
        var (report, output) = await RunAsync(options);

        Assert.False(report.Failed, output);
        Assert.Equal(StepOutcome.Checked, report.Find("postgres.binaries")!.Outcome);
        Assert.Contains("PostgreSQL) 17.", report.Find("postgres.binaries")!.Detail);
        Assert.Equal(StepOutcome.Applied, report.Find("credentials.superuser")!.Outcome);
        Assert.Equal(StepOutcome.Applied, report.Find("postgres.cluster")!.Outcome);
        Assert.Equal(StepOutcome.Applied, report.Find("postgres.server")!.Outcome);
        Assert.Equal(StepOutcome.Checked, report.Find("health")!.Outcome);

        var superuser = CredentialsFile.ReadConnectionString(options.SuperuserCredentialsPath);
        Assert.DoesNotContain(new NpgsqlConnectionStringBuilder(superuser).Password!, output);
        Assert.Equal("localhost", await ScalarAsync(superuser, "SHOW listen_addresses"));
        Assert.Equal("scram-sha-256", await ScalarAsync(superuser, "SHOW password_encryption"));
        Assert.Equal("UTF8", await ScalarAsync(superuser, "SHOW server_encoding"));
        Assert.Equal(options.PostgresPort.ToString(), await ScalarAsync(superuser, "SHOW port"));

        if (OperatingSystem.IsWindows())
        {
            // Readable by this account and administrators; the control is that it
            // is not private without naming the administrators.
            Assert.True(CredentialsFile.IsPrivate(options.SuperuserCredentialsPath, BundledPostgres.AdministratorsSid));
            Assert.False(CredentialsFile.IsPrivate(options.SuperuserCredentialsPath));
            Assert.True(new DirectoryInfo(options.DataDirectory!).GetAccessControl().AreAccessRulesProtected);
        }

        // Again: everything is found as it was left.
        var (again, againOutput) = await RunAsync(options);
        Assert.False(again.Failed, againOutput);
        Assert.DoesNotContain(again.Steps, s => s.Outcome == StepOutcome.Applied);
        Assert.Equal(StepOutcome.Done, again.Find("postgres.server")!.Outcome);

        // The dump and restore round trip, with the bundle's own tools.
        var pg = new BundledPostgres(Bundle);
        var dump = Path.Combine(_folder, "full", "premagentic.dump");
        await ExecuteAsync(superuser, "CREATE DATABASE premagentic_copy");
        Run(pg, superuser, "pg_dump", "-Fc", "-d", "premagentic", "-f", dump);
        Run(pg, superuser, "pg_restore", "-d", "premagentic_copy", "--no-owner", dump);
        var query = "SELECT (SELECT count(*) FROM prem_config.retrieval_event)::text || '/' || (SELECT count(*) FROM prem_config.app_user)::text || '/' || " +
                    "(SELECT string_agg(version::text, ',' ORDER BY version) FROM prem_config.schema_migration)";
        var original = await ScalarAsync(new NpgsqlConnectionStringBuilder(superuser) { Database = "premagentic" }.ConnectionString, query);
        var restored = await ScalarAsync(new NpgsqlConnectionStringBuilder(superuser) { Database = "premagentic_copy" }.ConnectionString, query);
        Assert.Equal(original, restored);

        // And it stops cleanly.
        Assert.Equal(0, pg.Stop(options.DataDirectory!).Code);
        Assert.False(pg.IsRunning(options.DataDirectory!));
    }

    [Fact]
    public async Task A_plan_goes_as_far_as_the_server_and_makes_nothing()
    {
        if (Bundle is null) return;

        var options = NewOptions("plan", FreePort()) with { Plan = true };
        var (report, output) = await RunAsync(options);

        Assert.False(report.Failed, output);
        Assert.Equal(StepOutcome.WouldApply, report.Find("postgres.cluster")!.Outcome);
        Assert.Equal(StepOutcome.WouldApply, report.Find("postgres.server")!.Outcome);
        Assert.Equal(StepOutcome.Skipped, report.Find("preflight.postgres")!.Outcome);
        Assert.Null(report.Find("role.owner"));
        Assert.False(Directory.Exists(options.DataDirectory));
        Assert.False(Directory.Exists(options.CredentialsDirectory));
    }

    [Fact]
    public async Task A_port_in_use_stops_setup_before_the_server_starts()
    {
        if (Bundle is null) return;

        var busy = new TcpListener(IPAddress.Loopback, 0);
        busy.Start();
        try
        {
            var options = NewOptions("busy", ((IPEndPoint)busy.LocalEndpoint).Port);
            var (report, _) = await RunAsync(options);

            Assert.True(report.Failed);
            Assert.Contains("in use by another program", report.Find("postgres.server")!.Detail);
            Assert.False(new BundledPostgres(Bundle).IsRunning(options.DataDirectory!));
        }
        finally
        {
            busy.Stop();
        }
    }

    [Fact]
    public async Task A_folder_that_holds_something_else_is_not_made_into_a_cluster()
    {
        if (Bundle is null) return;

        var options = NewOptions("occupied", FreePort());
        Directory.CreateDirectory(options.DataDirectory!);
        await File.WriteAllTextAsync(Path.Combine(options.DataDirectory!, "notes.txt"), "someone's files");

        var (report, _) = await RunAsync(options);

        Assert.True(report.Failed);
        Assert.Contains("is not empty and holds no cluster", report.Find("postgres.cluster")!.Detail);
        Assert.Equal(["notes.txt"], Directory.GetFiles(options.DataDirectory!).Select(Path.GetFileName));
    }

    [Fact]
    public async Task A_superuser_file_another_account_could_have_written_stops_setup_before_a_cluster_is_made()
    {
        if (Bundle is null || !OperatingSystem.IsWindows()) return;

        var options = NewOptions("planted", FreePort());
        InstallAccess.CreatePrivateFolder(options.CredentialsDirectory);
        // Waiting for setup, with a password its writer chose, and changeable by every user.
        await File.WriteAllTextAsync(options.SuperuserCredentialsPath,
            $"role=superuser\nconnection=Host=localhost;Port={options.PostgresPort};Database=postgres;Username={BundledPostgres.Superuser};Password=chosen-by-someone-else\n");
        AccessRules.LetUsersWrite(options.SuperuserCredentialsPath);

        var (report, output) = await RunAsync(options);

        Assert.True(report.Failed, output);
        var step = report.Find("credentials.superuser")!;
        Assert.Equal(StepOutcome.Failed, step.Outcome);
        Assert.StartsWith($"{options.SuperuserCredentialsPath} can be changed by {AccessRules.UsersName}, ", step.Detail);
        Assert.Null(report.Find("postgres.cluster"));
        Assert.False(Directory.Exists(options.DataDirectory));
    }

    [Fact]
    public async Task A_superuser_file_refused_once_its_cluster_exists_is_never_to_be_moved_aside()
    {
        if (Bundle is null || !OperatingSystem.IsWindows()) return;

        var options = NewOptions("kept", FreePort());
        var (report, output) = await RunAsync(options);
        Assert.False(report.Failed, output);
        var before = await File.ReadAllBytesAsync(options.SuperuserCredentialsPath);
        AccessRules.LetUsersWrite(options.SuperuserCredentialsPath);

        (report, output) = await RunAsync(options);

        // Moved aside, the file would take the only copy of the password with it.
        Assert.True(report.Failed, output);
        var step = report.Find("credentials.superuser")!;
        Assert.Equal(StepOutcome.Failed, step.Outcome);
        Assert.StartsWith($"{options.SuperuserCredentialsPath} can be changed by {AccessRules.UsersName}, ", step.Detail);
        Assert.Contains($"it holds the password of the server in {options.DataDirectory}, so do not move it", step.Detail);
        Assert.DoesNotContain("move it aside", step.Detail);
        Assert.Equal(before, await File.ReadAllBytesAsync(options.SuperuserCredentialsPath));
    }

    [Fact]
    public async Task A_superuser_password_other_accounts_could_read_is_replaced_on_the_running_server()
    {
        if (Bundle is null) return;

        var options = NewOptions("replaced", FreePort());
        var (report, output) = await RunAsync(options);
        Assert.False(report.Failed, output);
        var old = CredentialsFile.ReadConnectionString(options.SuperuserCredentialsPath);
        AccessRules.LetOthersRead(options.SuperuserCredentialsPath);

        (report, output) = await RunAsync(options);

        Assert.False(report.Failed, output);
        var step = report.Find("credentials.superuser")!;
        Assert.Equal(StepOutcome.Applied, step.Outcome);
        Assert.StartsWith("gave the bundled server's superuser a new password, since other accounts could read the old one", step.Detail);
        var fresh = CredentialsFile.ReadConnectionString(options.SuperuserCredentialsPath);
        Assert.NotEqual(new NpgsqlConnectionStringBuilder(old).Password, new NpgsqlConnectionStringBuilder(fresh).Password);
        Assert.DoesNotContain(new NpgsqlConnectionStringBuilder(fresh).Password!, output);
        if (OperatingSystem.IsWindows())
            Assert.True(CredentialsFile.IsPrivate(options.SuperuserCredentialsPath, BundledPostgres.AdministratorsSid));
        // The server took it: the old password is refused and the new one connects.
        var refused = await Assert.ThrowsAsync<PostgresException>(() => ScalarAsync(old, "SELECT 1"));
        Assert.Equal("28P01", refused.SqlState);
        Assert.Equal(1, await ScalarAsync(fresh, "SELECT 1"));
    }

    private static async Task ExecuteAsync(string connection, string sql)
    {
        await using var source = NpgsqlDataSource.Create(connection);
        await using var cmd = source.CreateCommand(sql);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>A bundle tool as the superuser, the password handed over in the environment, never on the command line.</summary>
    private static void Run(BundledPostgres pg, string superuser, string tool, params string[] arguments)
    {
        var connection = new NpgsqlConnectionStringBuilder(superuser);
        var start = new ProcessStartInfo(pg.Tool(tool)) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in new[] { "-h", "localhost", "-p", connection.Port.ToString(), "-U", connection.Username! }.Concat(arguments))
            start.ArgumentList.Add(argument);
        start.Environment["PGPASSWORD"] = connection.Password;
        using var process = Process.Start(start)!;
        var stderr = process.StandardError.ReadToEnd();
        process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"{tool} failed: {stderr}");
    }
}

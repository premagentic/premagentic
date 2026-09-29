using System.Security.Cryptography;
using Premagentic.Cli.Setup;
using Premagentic.Core;
using Premagentic.Core.Embeddings;
using Premagentic.Core.Ingestion;
using Premagentic.Core.Retrieval;
using Premagentic.Core.Security;
using Premagentic.Core.Storage;
using Npgsql;

namespace Premagentic.Tests;

/// <summary>
/// <c>prem setup</c> against a real, stock PostgreSQL, as its superuser. Roles
/// belong to the whole server, so every test uses its own database, its own two
/// role names and its own credentials folder.
/// </summary>
public sealed class SetupEngineTests(DatastoreTestDatabase server) : IClassFixture<DatastoreTestDatabase>, IDisposable
{
    private readonly List<string> _folders = [];

    public void Dispose()
    {
        foreach (var folder in _folders)
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
    }

    private sealed record Install(SetupOptions Options, string Suffix)
    {
        public string AppConnection => CredentialsFile.ReadConnectionString(Options.AppCredentialsPath);
        public string OwnerConnection => CredentialsFile.ReadConnectionString(Options.OwnerCredentialsPath);
    }

    private Install NewInstall(bool plan = false)
    {
        var suffix = Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant();
        var folder = Path.Combine(Path.GetTempPath(), "premagentic-setup-tests", suffix);
        _folders.Add(folder);
        return new Install(new SetupOptions(
            AdminConnectionString: server.AdminConnectionString,
            CredentialsDirectory: folder,
            EmbedderFactory: _ => new SeededEmbeddingProvider(),
            RequiredModelDirectory: null,
            DatabaseName: "prem_" + suffix,
            OwnerRole: "owner_" + suffix,
            AppRole: "app_" + suffix,
            Plan: plan), suffix);
    }

    private static async Task<(SetupReport Report, string Output)> RunAsync(SetupOptions options)
    {
        var output = new StringWriter();
        var report = await new SetupEngine(options, output).RunAsync();
        return (report, output.ToString());
    }

    private static async Task<SetupReport> InstallAsync(Install install)
    {
        var (report, output) = await RunAsync(install.Options);
        Assert.False(report.Failed, output);
        return report;
    }

    private async Task<List<string>> ScalarsAsync(string sql, string? connection = null)
    {
        await using var source = NpgsqlDataSource.Create(connection ?? server.AdminConnectionString);
        await using var cmd = source.CreateCommand(sql);
        var values = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            values.Add(reader.IsDBNull(0) ? "<null>" : Convert.ToString(reader.GetValue(0))!);
        return values;
    }

    private static async Task<PostgresException> RefusedAsync(string connection, string sql)
    {
        await using var source = NpgsqlDataSource.Create(connection);
        await using var cmd = source.CreateCommand(sql);
        return await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync());
    }

    /// <summary>
    /// The health check embeds with what the deployment embeds with. By the
    /// time it runs the database exists and the allow list is readable, so the
    /// engine hands over the providers this deployment's own extensions
    /// registered. Given nothing instead, a deployment whose configuration
    /// names a provider an extension brought would be refused by its own
    /// installer for naming a provider it supports.
    /// </summary>
    [Fact]
    public async Task The_health_check_is_given_the_providers_the_deployment_has()
    {
        IReadOnlyDictionary<string, Func<IServiceProvider, IEmbeddingProvider>>? given = null;
        var install = NewInstall();

        var (report, output) = await RunAsync(install.Options with
        {
            EmbedderFactory = providers =>
            {
                given = providers;
                return new SeededEmbeddingProvider();
            },
        });

        Assert.False(report.Failed, output);
        Assert.Equal(StepOutcome.Checked, report.Find("health")!.Outcome);
        // Empty here, because this deployment has no extensions. Not null is
        // the whole point: the engine read them and handed them over.
        Assert.NotNull(given);
    }

    /// <summary>
    /// A local model that cannot be loaded stops setup at the health check,
    /// recorded under that step with the model's one sentence, not as a stack
    /// trace and not under a step of its own.
    /// </summary>
    [Fact]
    public async Task A_model_that_cannot_be_loaded_is_recorded_as_the_failed_health_check_with_its_sentence()
    {
        var install = NewInstall();
        var model = Directory.CreateTempSubdirectory("prem-setup-damaged-model-").FullName;
        _folders.Add(model);
        File.WriteAllBytes(Path.Combine(model, "model.onnx"), "not a model, invented bytes"u8.ToArray());
        File.WriteAllText(Path.Combine(model, "vocab.txt"), "[PAD]\n[UNK]\n[CLS]\n[SEP]\nhangar\n");

        var (report, output) = await RunAsync(install.Options with { EmbedderFactory = _ => new LocalOnnxEmbeddingProvider(model) });

        Assert.True(report.Failed, output);
        var failed = Assert.Single(report.Steps, s => s.Outcome == StepOutcome.Failed);
        Assert.Equal("health", failed.Name);
        Assert.StartsWith($"The local embedding model in {model} could not be loaded: ", failed.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("   at ", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Plan_mode_reports_every_step_and_changes_nothing()
    {
        var install = NewInstall(plan: true);
        var rolesBefore = await ScalarsAsync("SELECT rolname FROM pg_roles ORDER BY 1");
        var databasesBefore = await ScalarsAsync("SELECT datname FROM pg_database ORDER BY 1");

        var (report, output) = await RunAsync(install.Options);

        Assert.False(report.Failed, output);
        Assert.False(report.Changed);
        Assert.Contains(report.Steps, s => s.Name == "role.app" && s.Outcome == StepOutcome.WouldApply);
        Assert.Contains(report.Steps, s => s.Name == "database" && s.Outcome == StepOutcome.WouldApply);
        Assert.Contains(report.Steps, s => s.Name == "migrations" && s.Outcome == StepOutcome.WouldApply);
        Assert.Equal(rolesBefore, await ScalarsAsync("SELECT rolname FROM pg_roles ORDER BY 1"));
        Assert.Equal(databasesBefore, await ScalarsAsync("SELECT datname FROM pg_database ORDER BY 1"));
        Assert.False(Directory.Exists(install.Options.CredentialsDirectory));
    }

    [Fact]
    public async Task Setup_installs_and_the_application_role_ingests_and_searches()
    {
        var install = NewInstall();
        var report = await InstallAsync(install);

        Assert.True(report.Changed);
        Assert.Equal(StepOutcome.Checked, report.Find("health")!.Outcome);

        // The application role does the work an installed service does: write
        // rows through ingest, then find them.
        await using var db = new PremagenticDatabase(install.AppConnection);
        await db.InitializeAsync();
        var tenantId = await db.EnsureTenantAsync("default", "Default");
        var embedder = new SeededEmbeddingProvider();
        await new IngestPipeline(db, embedder).RunAsync(tenantId, new DatastoreSource("s", [
            DatastoreSource.Doc("s/policy.md", "## Claims\nsubmit a sprocket claim within thirty days", DocumentAccess.Everyone)]));
        var hits = (await new HybridSearch(db, embedder).SearchAsync(
            tenantId, "sprocket claim", new SearchOptions(AccessScope.PublicOnly))).Hits;
        Assert.Equal("s/policy.md", Assert.Single(hits).Path);
    }

    [Fact]
    public async Task The_application_role_can_create_nothing_and_change_no_structure()
    {
        var install = NewInstall();
        await InstallAsync(install);
        var app = install.AppConnection;

        string[] refused =
        [
            "CREATE TABLE prem_index.intruder(id INT)",
            "CREATE TABLE prem_config.intruder(id INT)",
            "CREATE TABLE public.intruder(id INT)",
            "CREATE TEMP TABLE intruder(id INT)",
            "CREATE SCHEMA intruder",
            "ALTER TABLE prem_index.document ADD COLUMN intruder INT",
            "DROP TABLE prem_index.chunk",
            "TRUNCATE prem_index.chunk",
            "INSERT INTO prem_config.schema_migration(version, name, checksum) VALUES (9999, 'x', 'x')",
            "DELETE FROM prem_index.schema_migration",
            $"CREATE ROLE intruder_{install.Suffix}",
        ];
        foreach (var sql in refused)
        {
            var ex = await RefusedAsync(app, sql);
            Assert.True(ex.SqlState == PostgresErrorCodes.InsufficientPrivilege,
                $"'{sql}' failed with {ex.SqlState}, not a permission error");
        }

        // The control: the same connection reads and writes rows.
        Assert.Single(await ScalarsAsync("SELECT count(*) FROM prem_index.chunk", app));
    }

    [Fact]
    public async Task The_application_role_cannot_read_pg_authid()
    {
        var install = NewInstall();
        await InstallAsync(install);

        var ex = await RefusedAsync(install.AppConnection, "SELECT rolpassword FROM pg_authid");
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, ex.SqlState);
        // The control: the admin role can, so the query itself is sound.
        Assert.NotEmpty(await ScalarsAsync("SELECT rolname FROM pg_authid"));
    }

    [Fact]
    public async Task The_application_role_owns_nothing_holds_no_attribute_and_belongs_to_no_role()
    {
        var install = NewInstall();
        await InstallAsync(install);
        var app = install.Options.AppRole;
        var owner = install.Options.OwnerRole;
        var inDatabase = new NpgsqlConnectionStringBuilder(server.AdminConnectionString) { Database = install.Options.DatabaseName }.ConnectionString;

        Assert.Equal(["f|f|f|f|f|t"], await ScalarsAsync(
            $"SELECT concat_ws('|', rolsuper, rolcreatedb, rolcreaterole, rolreplication, rolbypassrls, rolcanlogin) FROM pg_roles WHERE rolname = '{app}'"));
        Assert.Equal(["0"], await ScalarsAsync(
            $"SELECT (SELECT count(*) FROM pg_class WHERE relowner = '{app}'::regrole) + (SELECT count(*) FROM pg_namespace WHERE nspowner = '{app}'::regrole)", inDatabase));
        Assert.Equal(["0"], await ScalarsAsync($"SELECT count(*) FROM pg_auth_members WHERE member = '{app}'::regrole"));

        // And the owner role owns every table in both schemas, which is what keeps
        // the application role from owning any.
        Assert.Equal([owner], await ScalarsAsync("""
            SELECT DISTINCT pg_get_userbyid(c.relowner) FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname IN ('prem_config', 'prem_index') AND c.relkind = 'r'
            """, inDatabase));
        Assert.Equal([$"f|{owner}"], await ScalarsAsync(
            $"SELECT concat_ws('|', r.rolsuper, pg_get_userbyid(d.datdba)) FROM pg_roles r, pg_database d WHERE r.rolname = '{owner}' AND d.datname = '{install.Options.DatabaseName}'"));
    }

    [Fact]
    public async Task Running_setup_again_changes_nothing_and_says_so()
    {
        var install = NewInstall();
        await InstallAsync(install);
        var inDatabase = new NpgsqlConnectionStringBuilder(server.AdminConnectionString) { Database = install.Options.DatabaseName }.ConnectionString;

        async Task<List<string>> SnapshotAsync() =>
        [
            File.ReadAllText(install.Options.AppCredentialsPath),
            File.ReadAllText(install.Options.OwnerCredentialsPath),
            File.ReadAllText(install.Options.KestrelSettingsPath),
            Convert.ToHexString(File.ReadAllBytes(install.Options.CertificatePath)),
            // The stored password verifiers: a reset would change them.
            ..await ScalarsAsync($"SELECT rolname || rolpassword FROM pg_authid WHERE rolname IN ('{install.Options.AppRole}', '{install.Options.OwnerRole}') ORDER BY 1"),
            ..await ScalarsAsync($"SELECT datacl::text FROM pg_database WHERE datname = '{install.Options.DatabaseName}'"),
            ..await ScalarsAsync("SELECT defaclacl::text FROM pg_default_acl ORDER BY 1", inDatabase),
            ..await ScalarsAsync("SELECT relname || relacl::text FROM pg_class WHERE relacl IS NOT NULL AND relnamespace IN ('prem_config'::regnamespace, 'prem_index'::regnamespace) ORDER BY 1", inDatabase),
            ..await ScalarsAsync("SELECT version || checksum FROM prem_config.schema_migration UNION ALL SELECT version || checksum FROM prem_index.schema_migration ORDER BY 1", inDatabase),
        ];

        var before = await SnapshotAsync();
        var events = await ScalarsAsync("SELECT count(*) FROM prem_config.retrieval_event", inDatabase);

        var (again, output) = await RunAsync(install.Options);

        Assert.False(again.Failed, output);
        Assert.False(again.Changed, output);
        Assert.DoesNotContain(again.Steps, s => s.Outcome == StepOutcome.Applied);
        Assert.Equal(StepOutcome.Checked, again.Find("health")!.Outcome);
        Assert.Equal(before, await SnapshotAsync());
        // The one row a repeat run writes: its own health check, in the audit trail.
        Assert.Equal(int.Parse(events[0]) + 1, int.Parse((await ScalarsAsync("SELECT count(*) FROM prem_config.retrieval_event", inDatabase))[0]));

        // And a plan against the finished install finds nothing to do.
        var (plan, planOutput) = await RunAsync(install.Options with { Plan = true });
        Assert.DoesNotContain(plan.Steps, s => s.Outcome is StepOutcome.WouldApply or StepOutcome.Failed);
        Assert.False(plan.Changed, planOutput);
    }

    [Fact]
    public async Task A_role_left_by_an_earlier_run_without_its_file_gets_the_new_password()
    {
        // As if a run died after creating the owner role and before anything else,
        // or someone made the role by hand: it exists, its password is unknown.
        var install = NewInstall();
        await using (var admin = NpgsqlDataSource.Create(server.AdminConnectionString))
        await using (var cmd = admin.CreateCommand($"CREATE ROLE {install.Options.OwnerRole} LOGIN PASSWORD 'unknown-to-setup'"))
            await cmd.ExecuteNonQueryAsync();

        var report = await InstallAsync(install);

        Assert.Equal(StepOutcome.Done, report.Find("role.owner")!.Outcome);
        Assert.Equal(StepOutcome.Applied, report.Find("login.owner")!.Outcome);
        Assert.Equal(StepOutcome.Checked, report.Find("health")!.Outcome);
    }

    [Fact]
    public async Task A_half_finished_or_damaged_install_is_completed_by_running_setup_again()
    {
        var install = NewInstall();
        await InstallAsync(install);
        var oldApp = install.AppConnection;

        // Damage it two ways: the application's credentials file is gone, and one
        // table's grant has been taken away.
        File.Delete(install.Options.AppCredentialsPath);
        await using (var owner = NpgsqlDataSource.Create(install.OwnerConnection))
        await using (var cmd = owner.CreateCommand($"REVOKE SELECT ON prem_index.chunk FROM {install.Options.AppRole}"))
            await cmd.ExecuteNonQueryAsync();

        var report = await InstallAsync(install);

        Assert.Equal(StepOutcome.Applied, report.Find("credentials.app")!.Outcome);
        Assert.Equal(StepOutcome.Applied, report.Find("login.app")!.Outcome);
        Assert.Equal(StepOutcome.Applied, report.Find("grants")!.Outcome);
        Assert.Equal(StepOutcome.Done, report.Find("migrations")!.Outcome);
        Assert.Equal(StepOutcome.Checked, report.Find("health")!.Outcome);

        // The new password works and the old one no longer does.
        Assert.NotEqual(oldApp, install.AppConnection);
        Assert.Single(await ScalarsAsync("SELECT count(*) FROM prem_index.chunk", install.AppConnection));
        var refused = await Assert.ThrowsAsync<PostgresException>(() => ScalarsAsync("SELECT 1", oldApp));
        Assert.Equal("28P01", refused.SqlState);
    }

    private static Migration Later() => Migration.Parse("0999_later.sql", """
        -- schema: prem_config
        CREATE TABLE prem_config.later_setting(id BIGSERIAL PRIMARY KEY, value TEXT NOT NULL);
        -- schema: prem_index
        CREATE TABLE prem_index.later_fact(id BIGSERIAL PRIMARY KEY, value TEXT NOT NULL);
        """);

    [Fact]
    public async Task Tables_a_later_migration_adds_are_reachable_by_the_application_role()
    {
        var install = NewInstall();
        await InstallAsync(install);

        // The owner applies a migration this build did not have when setup ran.
        await using (var owner = NpgsqlDataSource.Create(install.OwnerConnection))
            Assert.NotEmpty(await new MigrationRunner(owner, [.. Migration.LoadEmbedded(), Later()]).MigrateAsync());

        // No second setup run: the owner's default privileges already cover them,
        // sequences included.
        var app = install.AppConnection;
        foreach (var table in new[] { "prem_config.later_setting", "prem_index.later_fact" })
        {
            await using var source = NpgsqlDataSource.Create(app);
            await using (var insert = source.CreateCommand($"INSERT INTO {table}(value) VALUES ('written by the application role')"))
                await insert.ExecuteNonQueryAsync();
            Assert.Equal(["1"], await ScalarsAsync($"SELECT count(*) FROM {table}", app));
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, (await RefusedAsync(app, $"TRUNCATE {table}")).SqlState);
        }
    }

    [Fact]
    public async Task The_application_role_starts_on_a_current_schema_and_is_told_what_to_do_when_it_is_not()
    {
        var install = NewInstall();
        await InstallAsync(install);
        await using var app = NpgsqlDataSource.Create(install.AppConnection);

        // Current: the runner only reads, so the role with no DDL starts normally.
        Assert.Empty(await new MigrationRunner(app).MigrateAsync());

        // Pending: a clear instruction, not a raw permission error.
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => new MigrationRunner(app, [.. Migration.LoadEmbedded(), Later()]).MigrateAsync());
        Assert.Contains("owner role's credentials", ex.Message);
        Assert.IsType<PostgresException>(ex.InnerException);
    }

    [Fact]
    public async Task Credentials_files_are_private_and_the_passwords_are_never_printed()
    {
        var install = NewInstall();
        var (report, output) = await RunAsync(install.Options);
        Assert.False(report.Failed, output);

        foreach (var path in new[] { install.Options.AppCredentialsPath, install.Options.OwnerCredentialsPath })
        {
            Assert.True(CredentialsFile.IsPrivate(path), $"{path} is readable by other accounts");
            var password = new NpgsqlConnectionStringBuilder(CredentialsFile.ReadConnectionString(path)).Password!;
            Assert.Equal(43, password.Length);
            Assert.DoesNotContain(password, output);
        }

        // The control: an ordinary file written beside them is not private, so
        // the check can tell the difference.
        var ordinary = Path.Combine(install.Options.CredentialsDirectory, "ordinary.txt");
        File.WriteAllText(ordinary, "not a secret");
        Assert.False(CredentialsFile.IsPrivate(ordinary));
    }
}

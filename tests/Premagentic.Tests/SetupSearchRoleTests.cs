using System.Security.Cryptography;
using Premagentic.Cli.Setup;
using Premagentic.Core.Storage;
using Npgsql;

namespace Premagentic.Tests;

/// <summary>
/// The third role, the one the search and section reads connect as, and the two
/// things setup does for row-level security: the caller-session functions for
/// the application role, and the list of roles that write the whole index. The
/// functions and the list come from migration 0007; the tests read them as the
/// first install leaves them.
/// </summary>
public sealed class SetupSearchRoleTests(DatastoreTestDatabase server) : IClassFixture<DatastoreTestDatabase>, IDisposable
{
    // The text-match function's arguments today; setup finds it by name.
    private const string TextMatches = "prem_index.text_matches(uuid, tsquery, boolean, bigint[], boolean, smallint, boolean, timestamptz, uuid[], integer)";

    private readonly List<string> _folders = [];

    public void Dispose()
    {
        foreach (var folder in _folders)
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
    }

    private SetupOptions NewOptions()
    {
        var suffix = Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant();
        var folder = Path.Combine(Path.GetTempPath(), "premagentic-search-role-tests", suffix);
        _folders.Add(folder);
        return new SetupOptions(
            AdminConnectionString: server.AdminConnectionString,
            CredentialsDirectory: folder,
            EmbedderFactory: _ => new SeededEmbeddingProvider(),
            RequiredModelDirectory: null,
            DatabaseName: "prem_" + suffix,
            OwnerRole: "owner_" + suffix,
            AppRole: "app_" + suffix,
            SearchRole: "search_" + suffix);
    }

    private static async Task<SetupReport> InstallAsync(SetupOptions options)
    {
        var output = new StringWriter();
        var report = await new SetupEngine(options, output).RunAsync();
        Assert.False(report.Failed, output.ToString());
        return report;
    }

    private static async Task<SetupReport> RunAsync(SetupOptions options) =>
        await new SetupEngine(options, new StringWriter()).RunAsync();

    private static string Search(SetupOptions options) => CredentialsFile.ReadConnectionString(options.SearchCredentialsPath);
    private static string App(SetupOptions options) => CredentialsFile.ReadConnectionString(options.AppCredentialsPath);
    private static string Owner(SetupOptions options) => CredentialsFile.ReadConnectionString(options.OwnerCredentialsPath);
    private string Admin(SetupOptions options) =>
        new NpgsqlConnectionStringBuilder(server.AdminConnectionString) { Database = options.DatabaseName }.ConnectionString;

    private static async Task<object?> ScalarAsync(string connection, string sql)
    {
        await using var source = NpgsqlDataSource.Create(connection);
        await using var cmd = source.CreateCommand(sql);
        return await cmd.ExecuteScalarAsync();
    }

    private static async Task ExecuteAsync(string connection, string sql)
    {
        await using var source = NpgsqlDataSource.Create(connection);
        await using var cmd = source.CreateCommand(sql);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task RefusedAsync(string connection, string sql)
    {
        var ex = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(connection, sql));
        Assert.True(ex.SqlState == PostgresErrorCodes.InsufficientPrivilege, $"'{sql}' failed with {ex.SqlState}, not a permission error");
    }

    [Fact]
    public async Task The_search_role_reads_documents_and_chunks_and_nothing_else()
    {
        var options = NewOptions();
        var report = await InstallAsync(options);
        Assert.Equal(StepOutcome.Applied, report.Find("grants.search")!.Outcome);
        var search = Search(options);

        // The control: it reads the two tables it is for.
        Assert.Equal(0L, await ScalarAsync(search, "SELECT count(*) FROM prem_index.document"));
        Assert.Equal(0L, await ScalarAsync(search, "SELECT count(*) FROM prem_index.chunk"));

        var sequence = (string)(await ScalarAsync(Owner(options), """
            SELECT n.nspname || '.' || c.relname FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE c.relkind = 'S' AND n.nspname IN ('prem_config', 'prem_index') ORDER BY 1 LIMIT 1
            """))!;
        foreach (var sql in new[]
                 {
                     "SELECT count(*) FROM prem_config.app_user",
                     "SELECT count(*) FROM prem_config.tenant",
                     "SELECT count(*) FROM prem_config.schema_migration",
                     "SELECT count(*) FROM prem_index.schema_migration",
                     "INSERT INTO prem_index.document(id) VALUES (gen_random_uuid())",
                     "UPDATE prem_index.chunk SET seq = seq",
                     "DELETE FROM prem_index.document",
                     "TRUNCATE prem_index.chunk",
                     $"SELECT nextval('{sequence}')",
                     "CREATE TABLE prem_index.intruder(id INT)",
                     "CREATE TEMP TABLE intruder(id INT)",
                 })
            await RefusedAsync(search, sql);

        Assert.Equal("f|f|f|f|f|t", await ScalarAsync(server.AdminConnectionString,
            $"SELECT concat_ws('|', rolsuper, rolcreatedb, rolcreaterole, rolreplication, rolbypassrls, rolcanlogin) FROM pg_roles WHERE rolname = '{options.SearchRole}'"));
        Assert.Equal(StepOutcome.Checked, report.Find("role.search.audit")!.Outcome);
    }

    [Fact]
    public async Task A_table_a_later_migration_adds_gives_the_search_role_nothing()
    {
        var options = NewOptions();
        await InstallAsync(options);

        await using (var owner = NpgsqlDataSource.Create(Owner(options)))
            Assert.NotEmpty(await new MigrationRunner(owner, [.. Migration.LoadEmbedded(), Migration.Parse("0999_later.sql", """
                -- schema: prem_index
                CREATE TABLE prem_index.later_fact(id BIGSERIAL PRIMARY KEY, value TEXT NOT NULL);
                """)]).MigrateAsync());

        // The application role inherits it through the owner's default privileges;
        // the search role, which has none, does not.
        await ExecuteAsync(App(options), "INSERT INTO prem_index.later_fact(value) VALUES ('x')");
        await RefusedAsync(Search(options), "SELECT count(*) FROM prem_index.later_fact");
    }

    [Fact]
    public async Task Anything_more_the_search_role_holds_is_taken_away_on_the_next_run()
    {
        var options = NewOptions();
        await InstallAsync(options);
        var role = options.SearchRole;
        await ExecuteAsync(Owner(options), $"""
            GRANT INSERT ON prem_index.document TO {role};
            GRANT SELECT ON prem_config.app_user TO {role};
            GRANT UPDATE (title) ON prem_index.document TO {role};
            ALTER DEFAULT PRIVILEGES IN SCHEMA prem_index GRANT SELECT ON TABLES TO {role};
            """);
        await ExecuteAsync(server.AdminConnectionString, $"ALTER ROLE {role} BYPASSRLS");

        var report = await InstallAsync(options);

        Assert.Equal(StepOutcome.Applied, report.Find("role.search")!.Outcome);
        Assert.Equal(StepOutcome.Applied, report.Find("grants.search")!.Outcome);
        Assert.Equal(StepOutcome.Checked, report.Find("role.search.audit")!.Outcome);
        var search = Search(options);
        await RefusedAsync(search, "INSERT INTO prem_index.document(id) VALUES (gen_random_uuid())");
        await RefusedAsync(search, "SELECT count(*) FROM prem_config.app_user");
        await RefusedAsync(search, "UPDATE prem_index.document SET title = title");
        Assert.Equal(0L, await ScalarAsync(Admin(options), $"SELECT count(*) FROM pg_default_acl d, aclexplode(d.defaclacl) a WHERE a.grantee = '{role}'::regrole"));
        Assert.Equal(false, await ScalarAsync(server.AdminConnectionString, $"SELECT rolbypassrls FROM pg_roles WHERE rolname = '{role}'"));
        // And it still reads what it is for.
        Assert.Equal(0L, await ScalarAsync(search, "SELECT count(*) FROM prem_index.chunk"));

        Assert.DoesNotContain((await RunAsync(options)).Steps, s => s.Outcome is StepOutcome.Applied or StepOutcome.Failed);
    }

    [Fact]
    public async Task A_column_grant_alone_is_found_and_taken_away()
    {
        var options = NewOptions();
        await InstallAsync(options);
        await ExecuteAsync(Owner(options), $"GRANT UPDATE (title) ON prem_index.document TO {options.SearchRole}");

        var report = await InstallAsync(options);

        Assert.Equal(StepOutcome.Applied, report.Find("grants.search")!.Outcome);
        await RefusedAsync(Search(options), "UPDATE prem_index.document SET title = title");
    }

    [Fact]
    public async Task The_caller_session_functions_are_the_application_roles_alone()
    {
        var options = NewOptions();
        var report = await InstallAsync(options);

        // The first install grants the migration's functions.
        Assert.Equal(StepOutcome.Applied, report.Find("grants")!.Outcome);
        Assert.Contains("may run prem_config.open_caller_session, prem_config.close_caller_session", report.Find("grants")!.Detail);
        foreach (var function in new[] { "prem_config.open_caller_session(text, uuid, uuid, uuid, bigint[], interval)", "prem_config.close_caller_session(text)" })
        {
            Assert.Equal(true, await ScalarAsync(Admin(options), $"SELECT has_function_privilege('{options.AppRole}', '{function}', 'EXECUTE')"));
            Assert.Equal(false, await ScalarAsync(Admin(options), $"SELECT has_function_privilege('{options.SearchRole}', '{function}', 'EXECUTE')"));
        }
        await ExecuteAsync(App(options), "SELECT prem_config.close_caller_session('x')");
        await RefusedAsync(Search(options), "SELECT prem_config.close_caller_session('x')");
        Assert.Equal(false, await ScalarAsync(Admin(options),
            $"SELECT has_table_privilege('{options.AppRole}', 'prem_config.caller_session', 'SELECT, INSERT, UPDATE, DELETE, TRUNCATE, REFERENCES, TRIGGER')"));

        // A privilege on the table, as default privileges give the application role
        // every table the owner creates, is taken away on the next run.
        await ExecuteAsync(Owner(options), $"GRANT INSERT ON prem_config.caller_session TO {options.AppRole}");
        Assert.Equal(true, await ScalarAsync(Admin(options), $"SELECT has_table_privilege('{options.AppRole}', 'prem_config.caller_session', 'INSERT')"));
        var taken = await InstallAsync(options);
        Assert.Equal(StepOutcome.Applied, taken.Find("grants")!.Outcome);
        Assert.Equal(false, await ScalarAsync(Admin(options),
            $"SELECT has_table_privilege('{options.AppRole}', 'prem_config.caller_session', 'SELECT, INSERT, UPDATE, DELETE, TRUNCATE, REFERENCES, TRIGGER')"));

        var again = await InstallAsync(options);
        Assert.Equal(StepOutcome.Done, again.Find("grants")!.Outcome);
    }

    [Fact]
    public async Task The_text_match_function_is_run_by_the_two_roles_and_nobody_else()
    {
        var options = NewOptions();
        // The migration's function, which setup finds by name since its
        // arguments follow the gates; the first install grants and audits it.
        var report = await InstallAsync(options);

        Assert.Equal(StepOutcome.Applied, report.Find("grants")!.Outcome);
        Assert.Equal(StepOutcome.Applied, report.Find("grants.search")!.Outcome);
        Assert.Equal(StepOutcome.Checked, report.Find("functions.audit")!.Outcome);
        foreach (var role in new[] { options.AppRole, options.SearchRole })
            Assert.Equal(true, await ScalarAsync(Admin(options), $"SELECT has_function_privilege('{role}', '{TextMatches}', 'EXECUTE')"));
        await ExecuteAsync(Search(options),
            "SELECT prem_index.text_matches(gen_random_uuid(), to_tsquery('simple', 'x'), false, '{}', false, 0::smallint, false, now(), '{}', 5)");
        Assert.DoesNotContain((await RunAsync(options)).Steps, s => s.Outcome is StepOutcome.Applied or StepOutcome.Failed);

        // Anyone else able to run it stops setup, which changes nothing.
        await ExecuteAsync(Owner(options), "GRANT EXECUTE ON FUNCTION prem_index.text_matches TO PUBLIC");
        var refused = await RunAsync(options);
        Assert.True(refused.Failed);
        Assert.Contains("prem_index.text_matches by PUBLIC", refused.Find("functions.audit")!.Detail);
        Assert.Equal(true, await ScalarAsync(Admin(options), $"SELECT has_function_privilege('public', '{TextMatches}', 'EXECUTE')"));
    }

    [Fact]
    public async Task Two_functions_by_the_text_match_name_stop_setup_before_any_grant()
    {
        var options = NewOptions();
        await InstallAsync(options);
        // The first install granted the migration's function; take that away, so
        // a grant made by the next run would show, and add a second by the name.
        await ExecuteAsync(Owner(options), $"""
            REVOKE EXECUTE ON FUNCTION {TextMatches} FROM {options.SearchRole};
            CREATE FUNCTION prem_index.text_matches(uuid, tsquery) RETURNS boolean LANGUAGE sql SECURITY DEFINER AS 'SELECT true';
            REVOKE EXECUTE ON FUNCTION prem_index.text_matches(uuid, tsquery) FROM PUBLIC;
            """);

        var report = await RunAsync(options);

        // Migrate, which installs the function's body, is the first to find the
        // second one and stops the run; the grants step never runs.
        Assert.True(report.Failed);
        Assert.Contains(report.Steps, s => s.Outcome == StepOutcome.Failed && s.Detail.Contains("prem_index.text_matches exists 2 times"));
        Assert.NotEqual(StepOutcome.Applied, report.Find("grants")?.Outcome);
        Assert.Equal(false, await ScalarAsync(Admin(options), $"SELECT has_function_privilege('{options.SearchRole}', '{TextMatches}', 'EXECUTE')"));
    }

    [Fact]
    public async Task The_index_writer_list_holds_the_application_role_and_only_it()
    {
        var options = NewOptions();
        // The migration's list, which the first install fills.
        var listed = await InstallAsync(options);
        Assert.Equal(StepOutcome.Applied, listed.Find("index.writer")!.Outcome);
        Assert.Equal(options.AppRole, await ScalarAsync(Owner(options), "SELECT string_agg(role_name::text, ',') FROM prem_config.index_writer"));
        Assert.Equal(StepOutcome.Done, (await InstallAsync(options)).Find("index.writer")!.Outcome);

        // Anyone else on the list stops setup, and setup changes nothing.
        await ExecuteAsync(Owner(options), $"INSERT INTO prem_config.index_writer VALUES ('{options.SearchRole}')");
        var refused = await RunAsync(options);
        Assert.True(refused.Failed);
        Assert.Contains(options.SearchRole, refused.Find("index.writer")!.Detail);
        Assert.Equal(2L, await ScalarAsync(Owner(options), "SELECT count(*) FROM prem_config.index_writer"));
    }

    [Fact]
    public async Task A_search_role_new_to_an_existing_install_is_made_and_connects()
    {
        // An install from a release before the search role: PUBLIC can no longer
        // connect to the database, and the role and its file do not exist.
        var options = NewOptions();
        await InstallAsync(options);
        var role = options.SearchRole;
        await ExecuteAsync(Admin(options), $"REVOKE ALL ON ALL TABLES IN SCHEMA prem_index, prem_config FROM {role}; REVOKE ALL ON ALL FUNCTIONS IN SCHEMA prem_index, prem_config FROM {role}; REVOKE ALL ON SCHEMA prem_index, prem_config FROM {role}");
        await ExecuteAsync(server.AdminConnectionString, $"REVOKE ALL ON DATABASE {options.DatabaseName} FROM {role}; DROP ROLE {role}");
        File.Delete(options.SearchCredentialsPath);

        var report = await InstallAsync(options);

        Assert.Equal(StepOutcome.Applied, report.Find("role.search")!.Outcome);
        Assert.Equal(StepOutcome.Done, report.Find("login.search")!.Outcome);
        Assert.Equal(StepOutcome.Applied, report.Find("database.privileges")!.Outcome);
        Assert.Equal(StepOutcome.Checked, report.Find("health")!.Outcome);
        Assert.Equal(0L, await ScalarAsync(Search(options), "SELECT count(*) FROM prem_index.chunk"));
    }

    [Fact]
    public async Task The_server_stops_a_statement_of_the_search_role_after_its_timeout_and_an_operators_own_is_kept()
    {
        var options = NewOptions();
        var report = await InstallAsync(options);

        Assert.Equal(StepOutcome.Applied, report.Find("timeout.search")!.Outcome);
        Assert.Equal(SetupEngine.SearchStatementTimeout, await ScalarAsync(Search(options), "SHOW statement_timeout"));
        // The control: the application role, which ingests, has none.
        Assert.Equal("0", await ScalarAsync(App(options), "SHOW statement_timeout"));

        // One an operator chose is kept.
        var role = $"ALTER ROLE {options.SearchRole} IN DATABASE {options.DatabaseName} SET statement_timeout";
        await ExecuteAsync(server.AdminConnectionString, $"{role} = '40s'");
        Assert.Equal(StepOutcome.Done, (await InstallAsync(options)).Find("timeout.search")!.Outcome);
        Assert.Equal("40s", await ScalarAsync(Search(options), "SHOW statement_timeout"));

        // None, said as zero, is replaced.
        await ExecuteAsync(server.AdminConnectionString, $"{role} = 0");
        Assert.Equal("0", await ScalarAsync(Search(options), "SHOW statement_timeout"));
        Assert.Equal(StepOutcome.Applied, (await InstallAsync(options)).Find("timeout.search")!.Outcome);
        Assert.Equal(SetupEngine.SearchStatementTimeout, await ScalarAsync(Search(options), "SHOW statement_timeout"));
    }

    [Fact]
    public async Task The_search_credentials_file_is_private_and_its_password_is_never_printed()
    {
        var options = NewOptions();
        var output = new StringWriter();
        var report = await new SetupEngine(options, output).RunAsync();
        Assert.False(report.Failed, output.ToString());

        Assert.True(CredentialsFile.IsPrivate(options.SearchCredentialsPath));
        Assert.Contains("role=search", await File.ReadAllTextAsync(options.SearchCredentialsPath));
        var password = new NpgsqlConnectionStringBuilder(Search(options)).Password!;
        Assert.Equal(43, password.Length);
        Assert.DoesNotContain(password, output.ToString());
        Assert.Equal(options.SearchRole, new NpgsqlConnectionStringBuilder(Search(options)).Username);
    }

    [Theory]
    [InlineData("search_is_app")]
    [InlineData("search_is_owner")]
    public async Task The_three_roles_must_be_three_roles(string clash)
    {
        var options = NewOptions();
        options = clash == "search_is_app" ? options with { SearchRole = options.AppRole } : options with { SearchRole = options.OwnerRole };

        var report = await RunAsync(options);

        Assert.True(report.Failed);
        Assert.Contains("three different roles", report.Find("names")!.Detail);
    }
}

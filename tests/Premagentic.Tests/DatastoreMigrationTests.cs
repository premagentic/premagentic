using Premagentic.Core.Storage;
using Npgsql;

namespace Premagentic.Tests;

/// <summary>
/// The migration runner and the baseline, against stock PostgreSQL. Each test
/// gets its own empty database. Runner tests use their own small migrations so
/// they prove the runner, not the baseline.
/// </summary>
public sealed class DatastoreMigrationTests(DatastoreTestDatabase server) : IClassFixture<DatastoreTestDatabase>
{
    private static Migration M(string file, string sql) => Migration.Parse(file, sql);

    private static async Task<List<object?[]>> Rows(NpgsqlDataSource ds, string sql)
    {
        var rows = new List<object?[]>();
        await using var cmd = ds.CreateCommand(sql);
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var row = new object?[reader.FieldCount];
            for (var i = 0; i < row.Length; i++) row[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            rows.Add(row);
        }
        return rows;
    }

    [Fact]
    public async Task The_baseline_applies_on_stock_PostgreSQL_with_no_extension()
    {
        await using var db = new PremagenticDatabase(await server.CreateDatabaseAsync());
        var applied = await db.MigrateAsync();

        // Later migrations add their own parts and tables. This test is about
        // the baseline, so it looks only at version 1 and at the tables it owns.
        Assert.Equal(
            [(1, "baseline", "prem_config"), (1, "baseline", "prem_index")],
            applied.Where(a => a.Version == 1).Select(a => (a.Version, a.Name, a.Schema)).ToArray());

        // The control: this image could not even install pgvector, and the
        // baseline did not try.
        Assert.Empty(await Rows(db.DataSource, "SELECT 1 FROM pg_available_extensions WHERE name = 'vector'"));
        Assert.Empty(await Rows(db.DataSource, "SELECT 1 FROM pg_extension WHERE extname = 'vector'"));

        var tables = (await Rows(db.DataSource, """
            SELECT table_schema || '.' || table_name FROM information_schema.tables
            WHERE table_schema IN ('prem_config', 'prem_index') ORDER BY 1
            """)).Select(r => (string)r[0]!).ToArray();
        foreach (var table in new[]
                 {
                     "prem_config.retrieval_event", "prem_config.schema_migration", "prem_config.tenant",
                     "prem_index.chunk", "prem_index.document", "prem_index.schema_migration",
                 })
            Assert.Contains(table, tables);

        // Nothing lands in public: every statement names its schema.
        Assert.Empty(await Rows(db.DataSource,
            "SELECT 1 FROM information_schema.tables WHERE table_schema = 'public'"));
    }

    [Fact]
    public async Task The_embedding_column_refuses_a_length_that_does_not_match_its_dimensions()
    {
        await using var db = new PremagenticDatabase(await server.CreateDatabaseAsync());
        await db.MigrateAsync();
        var tenant = await db.EnsureTenantAsync("t", "T");

        await using var doc = db.DataSource.CreateCommand("""
            INSERT INTO prem_index.document(tenant_id, path, content_hash) VALUES(@t, 'a.md', 'h') RETURNING id
            """);
        doc.Parameters.AddWithValue("t", tenant);
        var docId = (Guid)(await doc.ExecuteScalarAsync())!;

        async Task Insert(int dims, int bytes)
        {
            await using var cmd = db.DataSource.CreateCommand("""
                INSERT INTO prem_index.chunk(document_id, seq, content, embedding, embedding_model, embedding_dims)
                VALUES(@d, 0, 'x', @e, 'm', @dims)
                """);
            cmd.Parameters.AddWithValue("d", docId);
            cmd.Parameters.AddWithValue("e", new byte[bytes]);
            cmd.Parameters.AddWithValue("dims", dims);
            await cmd.ExecuteNonQueryAsync();
        }

        await Insert(dims: 4, bytes: 16);
        var ex = await Assert.ThrowsAsync<PostgresException>(() => Insert(dims: 4, bytes: 12));
        Assert.Equal("chunk_embedding_length", ex.ConstraintName);
    }

    [Fact]
    public async Task Migrations_apply_in_version_order_whatever_order_they_are_given_in()
    {
        await using var ds = NpgsqlDataSource.Create(await server.CreateDatabaseAsync());
        var runner = new MigrationRunner(ds, [
            M("0003_third.sql", "-- schema: prem_config\nINSERT INTO prem_config.steps(n) VALUES(3);"),
            M("0001_first.sql", "-- schema: prem_config\nCREATE TABLE prem_config.steps(id SERIAL PRIMARY KEY, n INT NOT NULL);"),
            M("0002_second.sql", "-- schema: prem_config\nINSERT INTO prem_config.steps(n) VALUES(2);"),
        ]);

        var applied = await runner.MigrateAsync();

        Assert.Equal([1, 2, 3], applied.Select(a => a.Version).ToArray());
        // 0002 and 0003 would fail outright if run before 0001 created the table,
        // and the serial ids show 0002 ran before 0003.
        var steps = (await Rows(ds, "SELECT n FROM prem_config.steps ORDER BY id")).Select(r => (int)r[0]!).ToArray();
        Assert.Equal([2, 3], steps);
        var recorded = (await Rows(ds, "SELECT version FROM prem_config.schema_migration ORDER BY applied_at, version"))
            .Select(r => (int)r[0]!).ToArray();
        Assert.Equal([1, 2, 3], recorded);
    }

    [Fact]
    public async Task A_second_run_applies_nothing_and_changes_nothing()
    {
        await using var db = new PremagenticDatabase(await server.CreateDatabaseAsync());
        const string recordedParts =
            "SELECT 1 FROM prem_config.schema_migration UNION ALL SELECT 1 FROM prem_index.schema_migration";

        var applied = await db.MigrateAsync();
        Assert.NotEmpty(applied);
        var tenant = await db.EnsureTenantAsync("t", "T");

        // One row per applied part, however many migrations this build carries.
        var recorded = (await Rows(db.DataSource, recordedParts)).Count;
        Assert.Equal(applied.Count, recorded);

        Assert.Empty(await db.MigrateAsync());
        await db.InitializeAsync();

        Assert.Equal(tenant, await db.EnsureTenantAsync("t", "T"));
        Assert.Equal(recorded, (await Rows(db.DataSource, recordedParts)).Count);
    }

    [Fact]
    public async Task A_changed_migration_is_refused_and_nothing_after_it_runs()
    {
        await using var ds = NpgsqlDataSource.Create(await server.CreateDatabaseAsync());
        const string original = "-- schema: prem_config\nCREATE TABLE prem_config.a(id INT);";
        await new MigrationRunner(ds, [M("0001_a.sql", original)]).MigrateAsync();

        var edited = new MigrationRunner(ds, [
            M("0001_a.sql", "-- schema: prem_config\nCREATE TABLE prem_config.a(id BIGINT);"),
            M("0002_b.sql", "-- schema: prem_config\nCREATE TABLE prem_config.b(id INT);"),
        ]);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => edited.MigrateAsync());
        Assert.Contains("0001_a", ex.Message);
        Assert.Contains("has changed since it was applied", ex.Message);

        // Refused before anything ran: 0002 is not there.
        Assert.Empty(await Rows(ds, "SELECT 1 FROM information_schema.tables WHERE table_name = 'b'"));

        // The control: a checkout that only converted line endings is the SAME
        // migration, and must not be refused.
        var crlf = new MigrationRunner(ds, [M("0001_a.sql", original.Replace("\n", "\r\n"))]);
        Assert.Empty(await crlf.MigrateAsync());
    }

    [Fact]
    public async Task A_database_migrated_by_a_newer_build_is_refused()
    {
        await using var ds = NpgsqlDataSource.Create(await server.CreateDatabaseAsync());
        await new MigrationRunner(ds, [
            M("0001_a.sql", "-- schema: prem_config\nCREATE TABLE prem_config.a(id INT);"),
            M("0002_b.sql", "-- schema: prem_config\nCREATE TABLE prem_config.b(id INT);"),
        ]).MigrateAsync();

        var older = new MigrationRunner(ds, [M("0001_a.sql", "-- schema: prem_config\nCREATE TABLE prem_config.a(id INT);")]);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => older.MigrateAsync());
        Assert.Contains("newer build", ex.Message);
    }

    [Fact]
    public async Task Two_runners_at_once_apply_each_migration_exactly_once()
    {
        var connectionString = await server.CreateDatabaseAsync();
        // pg_sleep widens the window: without the lock, the second runner reads
        // the version table while the first is still inside its transaction, and
        // both apply. The INSERT is what would double; the version row's primary
        // key would then fail the second runner outright.
        Migration[] migrations = [
            M("0001_slow.sql", """
                -- schema: prem_config
                CREATE TABLE IF NOT EXISTS prem_config.applications(at TIMESTAMPTZ NOT NULL DEFAULT clock_timestamp());
                SELECT pg_sleep(1);
                INSERT INTO prem_config.applications DEFAULT VALUES;
                """),
        ];

        await using var first = NpgsqlDataSource.Create(connectionString);
        await using var second = NpgsqlDataSource.Create(connectionString);

        // Schemas and version tables exist before the race, so what is raced is
        // the apply itself and not only the first-run bootstrap (which the lock
        // also covers).
        Assert.Empty(await new MigrationRunner(first, []).MigrateAsync());

        var results = await Task.WhenAll(
            new MigrationRunner(first, migrations).MigrateAsync(),
            new MigrationRunner(second, migrations).MigrateAsync());

        Assert.Equal(1, results.Sum(r => r.Count));
        Assert.Single(await Rows(first, "SELECT at FROM prem_config.applications"));
        Assert.Single(await Rows(first, "SELECT version FROM prem_config.schema_migration"));
    }

    // The documented rebuild empties the index tables (DatastoreRebuildTests).
    // Dropping the whole schema is the recovery path, and must bring back every
    // index part, later migrations included, without re-running any config part.
    [Fact]
    public async Task A_dropped_index_schema_is_recreated_from_every_index_part_and_config_is_untouched()
    {
        await using var ds = NpgsqlDataSource.Create(await server.CreateDatabaseAsync());
        Migration[] migrations = [
            M("0001_base.sql", """
                -- schema: prem_config
                CREATE TABLE prem_config.kept(v TEXT);
                INSERT INTO prem_config.kept VALUES ('seed');
                -- schema: prem_index
                CREATE TABLE prem_index.rebuilt(v TEXT);
                """),
            M("0002_index_only.sql", "-- schema: prem_index\nALTER TABLE prem_index.rebuilt ADD COLUMN w INT;"),
        ];
        await new MigrationRunner(ds, migrations).MigrateAsync();
        await using (var cmd = ds.CreateCommand("INSERT INTO prem_config.kept VALUES ('written later')"))
            await cmd.ExecuteNonQueryAsync();

        await using (var drop = ds.CreateCommand("DROP SCHEMA prem_index CASCADE"))
            await drop.ExecuteNonQueryAsync();
        var applied = await new MigrationRunner(ds, migrations).MigrateAsync();

        // Only the index sections ran again, in order; the config section, which
        // would have inserted 'seed' a second time, did not.
        Assert.Equal([(1, "prem_index"), (2, "prem_index")], applied.Select(a => (a.Version, a.Schema)).ToArray());
        Assert.Equal(["seed", "written later"],
            (await Rows(ds, "SELECT v FROM prem_config.kept ORDER BY v")).Select(r => (string)r[0]!).ToArray());
        Assert.Single(await Rows(ds,
            "SELECT 1 FROM information_schema.columns WHERE table_schema = 'prem_index' AND table_name = 'rebuilt' AND column_name = 'w'"));
    }

    [Theory]
    [InlineData("1_short.sql", "-- schema: prem_config\nSELECT 1;", "NNNN_lower_case_name")]
    [InlineData("0001_x.sql", "SELECT 1;\n-- schema: prem_config\nSELECT 1;", "before its first")]
    [InlineData("0001_x.sql", "-- schema: public\nSELECT 1;", "unknown schema")]
    [InlineData("0001_x.sql", "-- only a comment", "no '-- schema:' section")]
    [InlineData("0001_x.sql", "-- schema: prem_config\nSELECT 1;\n-- schema: prem_config\nSELECT 2;", "two 'prem_config' sections")]
    public void A_malformed_migration_file_is_refused(string file, string sql, string expected)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Migration.Parse(file, sql));
        Assert.Contains(expected, ex.Message);
    }

    [Fact]
    public void The_embedded_baseline_loads_with_both_sections_config_first()
    {
        var baseline = Assert.Single(Migration.LoadEmbedded(), m => m.Version == 1);
        Assert.Equal("baseline", baseline.Name);
        Assert.Equal(["prem_config", "prem_index"], baseline.Sections.Select(s => s.Schema).ToArray());
        Assert.DoesNotContain(baseline.Sections, s => s.Sql.Contains("EXTENSION", StringComparison.OrdinalIgnoreCase));
    }
}

using Npgsql;

namespace Premagentic.Core.Storage;

/// <summary>
/// The connection to one deployment's PostgreSQL. Plain PostgreSQL: no
/// extension is required, so any stock server the customer already runs will
/// do, managed or not.
/// </summary>
public sealed class PremagenticDatabase : IAsyncDisposable
{
    public NpgsqlDataSource DataSource { get; }

    public PremagenticDatabase(string connectionString)
    {
        VectorCodec.EnsureSupportedHost();
        DataSource = NpgsqlDataSource.Create(connectionString);
    }

    /// <summary>The switch that allows <see cref="DevelopmentConnectionString"/>: set it to 1.</summary>
    public const string DevelopmentSwitch = "PREM_DEV_DATABASE";

    /// <summary>
    /// The local instance from docker-compose.yml. Port 5434 rather than the
    /// default, so it never collides with a PostgreSQL already listening on 5432
    /// or 5433. GSS encryption off, as <c>prem setup</c> writes it, so a
    /// machine without the Kerberos library does not print its error on every
    /// connection.
    /// </summary>
    public const string DevelopmentConnectionString =
        "Host=localhost;Port=5434;Database=premagentic;Username=prem;Password=prem_dev;GSS Encryption Mode=Disable";

    /// <summary>
    /// The connection for this process: from the file named by
    /// <c>PREM_CREDENTIALS_FILE</c> (see <see cref="CredentialsFile"/>), which is
    /// how an installed deployment runs, or from <c>PREM_CONNECTION_STRING</c>.
    /// Setting both is refused rather than guessed at. With neither, the process
    /// refuses to start, unless <c>PREM_DEV_DATABASE=1</c> asks for the local
    /// development instance: an installed service that lost its configuration
    /// must stop, not connect somewhere with a published password.
    /// </summary>
    /// <exception cref="StartupRefusedException">
    /// Nothing is configured, both sources are, or the credentials file cannot be read.
    /// </exception>
    public static string ConnectionStringFromEnvironment() => ConnectionStringFrom(Environment.GetEnvironmentVariable);

    /// <summary>
    /// The same resolution over any source of settings, such as a host's
    /// configuration, which also holds the environment.
    /// </summary>
    /// <exception cref="StartupRefusedException">As for <see cref="ConnectionStringFromEnvironment"/>.</exception>
    public static string ConnectionStringFrom(Func<string, string?> setting) =>
        ResolveConnectionString(
            setting("PREM_CONNECTION_STRING"),
            setting("PREM_CREDENTIALS_FILE"),
            setting(DevelopmentSwitch) == "1");

    internal static string ResolveConnectionString(string? connectionString, string? credentialsFile, bool development)
    {
        if (!string.IsNullOrEmpty(credentialsFile))
        {
            if (!string.IsNullOrEmpty(connectionString))
                throw new StartupRefusedException(
                    "Both PREM_CREDENTIALS_FILE and PREM_CONNECTION_STRING are set. Set one of them.");
            try
            {
                return CredentialsFile.ReadConnectionString(credentialsFile);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                // A named file that is missing or unreadable fails closed; it never falls back.
                throw new StartupRefusedException(
                    $"PREM_CREDENTIALS_FILE names {credentialsFile}, which cannot be used: {ex.Message}", ex);
            }
        }
        if (!string.IsNullOrEmpty(connectionString))
            return connectionString;
        if (development)
            return DevelopmentConnectionString;
        throw new StartupRefusedException(
            "No database is configured. Set PREM_CREDENTIALS_FILE to the app.credentials file that " +
            "prem setup wrote, or PREM_CONNECTION_STRING to a PostgreSQL connection string. For the " +
            $"local development database in docker-compose.yml, set {DevelopmentSwitch}=1.");
    }

    /// <summary>
    /// Brings the schema up to date through <see cref="MigrationRunner"/>. Safe to
    /// call from every process at startup: the runner takes a lock, applies only
    /// what is missing, and refuses a schema it does not recognize.
    /// </summary>
    public async Task InitializeAsync(CancellationToken ct = default) => await MigrateAsync(ct);

    /// <inheritdoc cref="MigrationRunner.MigrateAsync"/>
    public Task<IReadOnlyList<AppliedMigration>> MigrateAsync(CancellationToken ct = default) =>
        new MigrationRunner(DataSource).MigrateAsync(ct);

    /// <summary>
    /// Empties the index: every table in <c>prem_index</c> except its migration
    /// record, in one statement. The structure stays, because it belongs to the
    /// migrations; only the rows are disposable, and re-running ingest rebuilds
    /// them from the customer's files. <c>prem_config</c> is not touched.
    /// <para>
    /// No CASCADE, on purpose. Nothing in <c>prem_config</c> may reference the
    /// index; if something ever does, this fails loudly instead of emptying a
    /// config table along with it.
    /// </para>
    /// </summary>
    /// <returns>The documents and chunks removed.</returns>
    public async Task<(long Documents, long Chunks)> ClearIndexAsync(CancellationToken ct = default)
    {
        await using var conn = await DataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        long documents, chunks;
        await using (var count = new NpgsqlCommand(
            "SELECT (SELECT count(*) FROM prem_index.document), (SELECT count(*) FROM prem_index.chunk)", conn, tx))
        await using (var reader = await count.ExecuteReaderAsync(ct))
        {
            await reader.ReadAsync(ct);
            documents = reader.GetInt64(0);
            chunks = reader.GetInt64(1);
        }

        var tables = new List<string>();
        await using (var list = new NpgsqlCommand("""
            SELECT quote_ident(table_name) FROM information_schema.tables
            WHERE table_schema = 'prem_index' AND table_type = 'BASE TABLE' AND table_name <> 'schema_migration'
            ORDER BY table_name
            """, conn, tx))
        await using (var reader = await list.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
                tables.Add("prem_index." + reader.GetString(0));
        }

        if (tables.Count > 0)
        {
            await using var truncate = new NpgsqlCommand($"TRUNCATE {string.Join(", ", tables)}", conn, tx);
            await truncate.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
        return (documents, chunks);
    }

    /// <summary>
    /// The tenant with this key, made if it is not there. The groups Premagentic
    /// maintains are made with it, in the same statement: migration 0040 made
    /// them for the tenants that existed then, and a tenant made later would
    /// otherwise have no group for a rule to deny.
    /// </summary>
    public async Task<Guid> EnsureTenantAsync(string key, string name, CancellationToken ct = default)
    {
        await using var cmd = DataSource.CreateCommand("""
            WITH tenant AS (
                INSERT INTO prem_config.tenant(key, name) VALUES(@key, @name)
                ON CONFLICT (key) DO UPDATE SET name = EXCLUDED.name
                RETURNING id),
            reserved AS (
                INSERT INTO prem_config.app_group(tenant_id, name, system_key)
                SELECT t.id, @hostedName, @hostedKey FROM tenant t
                ON CONFLICT DO NOTHING)
            SELECT id FROM tenant
            """);
        cmd.Parameters.AddWithValue("key", key);
        cmd.Parameters.AddWithValue("name", name);
        cmd.Parameters.AddWithValue("hostedKey", Identity.SystemGroups.HostedModelAgents);
        cmd.Parameters.AddWithValue("hostedName", Identity.SystemGroups.HostedModelAgentsName);
        return (Guid)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public ValueTask DisposeAsync() => DataSource.DisposeAsync();
}

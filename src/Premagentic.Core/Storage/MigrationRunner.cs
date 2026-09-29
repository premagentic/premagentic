using System.Data;
using Npgsql;

namespace Premagentic.Core.Storage;

/// <summary>
/// Applies the numbered migrations, and refuses to run against a schema it does
/// not recognize.
/// <list type="bullet">
///   <item>One run at a time: a session advisory lock is held for the whole
///   run, so two processes starting together cannot both apply a
///   migration. The second waits, then finds nothing left to do.</item>
///   <item>One transaction per migration, covering its sections and the rows
///   that record them, so a failure leaves the migration wholly unapplied.</item>
///   <item>A migration already applied whose text has since changed stops the
///   run before anything is applied. Edit history by adding a migration, never
///   by changing one.</item>
///   <item>A database that records a migration this build does not have is
///   refused too: it was migrated by a newer build.</item>
/// </list>
/// </summary>
public sealed class MigrationRunner(NpgsqlDataSource dataSource, IReadOnlyList<Migration>? migrations = null)
{
    /// <summary>The advisory lock key, "PremagMg" as ASCII. Any fixed value works; it only has to be unique to this purpose.</summary>
    internal const long AdvisoryLockKey = 0x4861727269734D67;

    private readonly IReadOnlyList<Migration> _migrations =
        migrations is null ? Migration.LoadEmbedded() : Migration.Ordered(migrations);

    /// <summary>
    /// Brings the schema up to date. It reads first, with no lock and no DDL:
    /// when every migration is already recorded with its checksum and the
    /// generated text match function is the one this build writes, it returns
    /// having changed nothing, which is how a role that may not run DDL (the
    /// application role) starts. Only when something is pending does it take the
    /// lock and apply.
    /// </summary>
    /// <returns>The sections applied on this call, in order; empty when the schema was already current.</returns>
    /// <exception cref="InvalidOperationException">
    /// Something is pending and this role may not apply it, or the recorded
    /// history does not match this build.
    /// </exception>
    public async Task<IReadOnlyList<AppliedMigration>> MigrateAsync(CancellationToken ct = default)
    {
        var recorded = await ReadRecordedAsync(ct);
        if (recorded is not null)
        {
            Verify(recorded);
            if (Pending(recorded).Count == 0 && await TextMatchCurrentAsync(ct)) return [];
        }

        try
        {
            return await ApplyAsync(ct);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.InsufficientPrivilege)
        {
            throw new InvalidOperationException(
                "The schema needs migrating and this database role may not change it. Run 'prem migrate' " +
                "(or 'prem setup') with the owner role's credentials; the application role reads and writes " +
                "rows and runs no DDL.", ex);
        }
    }

    /// <summary>
    /// What <see cref="MigrateAsync"/> would do, in order, without applying or
    /// locking anything: the sections not yet applied, and last, when the text
    /// match function holds a body other than the one this build generates, an
    /// entry of the kind <see cref="MigrationWork.TextMatchBody"/>, since migrate
    /// would install it. Everything is pending on a database with no schema yet;
    /// there the function does not exist, and the migration that creates it
    /// stands for its body too.
    /// </summary>
    /// <exception cref="InvalidOperationException">The recorded history does not match this build.</exception>
    public async Task<IReadOnlyList<AppliedMigration>> PendingAsync(CancellationToken ct = default)
    {
        var recorded = await ReadRecordedAsync(ct) ?? [];
        Verify(recorded);
        var pending = Pending(recorded);
        if (!await TextMatchCurrentAsync(ct)) pending.Add(AppliedMigration.TextMatchBody);
        return pending;
    }

    /// <summary>
    /// What both schemas record, or null when either record is missing or this
    /// role may not read it, in which case the locked path decides.
    /// </summary>
    private async Task<Dictionary<(string Schema, int Version), string>?> ReadRecordedAsync(CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        try
        {
            await using (var exists = new NpgsqlCommand(
                "SELECT to_regclass('prem_config.schema_migration') IS NOT NULL AND to_regclass('prem_index.schema_migration') IS NOT NULL",
                conn))
            {
                if (!(bool)(await exists.ExecuteScalarAsync(ct))!) return null;
            }

            var recorded = new Dictionary<(string Schema, int Version), string>();
            foreach (var schema in Migration.Schemas)
            {
                await using var read = new NpgsqlCommand($"SELECT version, checksum FROM {schema}.schema_migration", conn);
                await using var reader = await read.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                    recorded[(schema, reader.GetInt32(0))] = reader.GetString(1);
            }
            return recorded;
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.InsufficientPrivilege)
        {
            return null;
        }
    }

    private async Task<bool> TextMatchCurrentAsync(CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        return await Retrieval.TextMatchFunction.IsCurrentAsync(conn, ct);
    }

    private List<AppliedMigration> Pending(Dictionary<(string Schema, int Version), string> recorded) =>
        _migrations
            .SelectMany(m => m.Sections
                .Where(s => !recorded.ContainsKey((s.Schema, m.Version)))
                .Select(s => new AppliedMigration(m.Version, m.Name, s.Schema)))
            .ToList();

    private async Task<IReadOnlyList<AppliedMigration>> ApplyAsync(CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await ExecuteAsync(conn, $"SELECT pg_advisory_lock({AdvisoryLockKey})", ct);
        try
        {
            // Inside the lock, so two first runs cannot race on CREATE SCHEMA.
            foreach (var schema in Migration.Schemas)
                await ExecuteAsync(conn, $"""
                    CREATE SCHEMA IF NOT EXISTS {schema};
                    CREATE TABLE IF NOT EXISTS {schema}.schema_migration(
                        version INT PRIMARY KEY,
                        name TEXT NOT NULL,
                        checksum TEXT NOT NULL,
                        applied_at TIMESTAMPTZ NOT NULL DEFAULT now()
                    );
                    """, ct);

            var recorded = new Dictionary<(string Schema, int Version), string>();
            foreach (var schema in Migration.Schemas)
            {
                await using var read = new NpgsqlCommand($"SELECT version, checksum FROM {schema}.schema_migration", conn);
                await using var reader = await read.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                    recorded[(schema, reader.GetInt32(0))] = reader.GetString(1);
            }

            Verify(recorded);

            var applied = new List<AppliedMigration>();
            foreach (var migration in _migrations)
            {
                var pending = migration.Sections
                    .Where(s => !recorded.ContainsKey((s.Schema, migration.Version)))
                    .ToArray();
                if (pending.Length == 0) continue;

                await using var tx = await conn.BeginTransactionAsync(ct);
                foreach (var section in pending)
                {
                    await using (var apply = new NpgsqlCommand(section.Sql, conn, tx))
                        await apply.ExecuteNonQueryAsync(ct);

                    await using var record = new NpgsqlCommand(
                        $"INSERT INTO {section.Schema}.schema_migration(version, name, checksum) VALUES(@v, @n, @c)",
                        conn, tx);
                    record.Parameters.AddWithValue("v", migration.Version);
                    record.Parameters.AddWithValue("n", migration.Name);
                    record.Parameters.AddWithValue("c", section.Checksum);
                    await record.ExecuteNonQueryAsync(ct);

                    applied.Add(new AppliedMigration(migration.Version, migration.Name, section.Schema));
                }
                await tx.CommitAsync(ct);
            }

            // Generated from the gates rather than written in a migration, so it
            // is installed at the end of every run. See TextMatchFunction.
            await Retrieval.TextMatchFunction.InstallAsync(conn, ct);
            return applied;
        }
        finally
        {
            // Released explicitly rather than left to the pool's session reset. A
            // broken connection has already released it by closing.
            if (conn.State == ConnectionState.Open)
                await ExecuteAsync(conn, $"SELECT pg_advisory_unlock({AdvisoryLockKey})", CancellationToken.None);
        }
    }

    private void Verify(Dictionary<(string Schema, int Version), string> recorded)
    {
        var known = _migrations
            .SelectMany(m => m.Sections.Select(s => (Key: (s.Schema, m.Version), Migration: m, Section: s)))
            .ToDictionary(x => x.Key);

        foreach (var ((schema, version), checksum) in recorded.OrderBy(r => r.Key.Version))
        {
            if (!known.TryGetValue((schema, version), out var expected))
                throw new InvalidOperationException(
                    $"The database records migration {version:D4} in {schema}, which this build does not have. " +
                    "It was migrated by a newer build; run that build or restore a backup taken before it.");

            if (!string.Equals(expected.Section.Checksum, checksum, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    $"Migration {version:D4}_{expected.Migration.Name} ({schema}) has changed since it was applied " +
                    $"(recorded checksum {Short(checksum)}, this build {Short(expected.Section.Checksum)}). " +
                    "An applied migration is history and is never edited; add a new migration instead. Nothing was applied.");
        }
    }

    private static string Short(string checksum) => checksum.Length <= 12 ? checksum : checksum[..12];

    private static async Task ExecuteAsync(NpgsqlConnection conn, string sql, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}

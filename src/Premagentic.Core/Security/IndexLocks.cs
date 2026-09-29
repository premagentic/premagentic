using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Premagentic.Core.Storage;
using Npgsql;

namespace Premagentic.Core.Security;

/// <summary>
/// The advisory locks that keep ingest and folder-rule changes from undoing
/// each other.
/// <list type="bullet">
/// <item>Ingest loads the folder rules once, at the start of a run, and decides
/// every document with them. A rule change in the middle of a run would be
/// undone for whatever the run reached afterwards, until the next run. So a
/// run holds the tenant's rules lock shared for its whole length, and a rule
/// change takes it exclusively, waiting a while for running ingests and then
/// refusing.</item>
/// <item>Two runs of one source at once would each delete what the other has
/// not reached yet. So a run holds a lock on its connector and path prefix, and
/// a second run of the same is refused at once.</item>
/// </list>
/// Advisory locks belong to the database session, so a process that dies
/// releases its locks with its connection.
/// </summary>
public static class IndexLocks
{
    /// <summary>How long a rule change waits for running ingests before it refuses.</summary>
    public static readonly TimeSpan DefaultRuleChangeWait = TimeSpan.FromSeconds(30);

    private const string LockNotAvailable = "55P03";

    /// <summary>
    /// Takes the locks for one ingest run and holds them until the result is
    /// disposed: the run's own source lock, refused at once when another run of
    /// the same connector and prefix holds it, and the tenant's rules lock,
    /// shared, which waits for a rule change in progress to commit.
    /// </summary>
    /// <exception cref="InvalidOperationException">Another run of the same source is in progress.</exception>
    public static async Task<IAsyncDisposable> ForIngestAsync(
        PremagenticDatabase db, Guid tenantId, string connectorName, string pathPrefix, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        var sourceKey = SourceKey(tenantId, connectorName, pathPrefix);
        var rulesKey = RulesKey(tenantId);

        var connection = await db.DataSource.OpenConnectionAsync(ct);
        try
        {
            if (!await ScalarAsync<bool>(connection, "SELECT pg_try_advisory_lock(@key)", sourceKey, ct))
                throw new InvalidOperationException(
                    $"Another ingest of {connectorName}:{(pathPrefix.Length == 0 ? "(whole source)" : pathPrefix)} is running. " +
                    "Wait for it to finish, then run this one.");
            await ScalarAsync<object>(connection, "SELECT pg_advisory_lock_shared(@key)", rulesKey, ct);
            return new IngestLease(connection, sourceKey, rulesKey);
        }
        catch
        {
            // Closing the connection releases whatever it took.
            await connection.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// Takes the tenant's rules lock exclusively, inside <paramref name="tx"/>,
    /// until it ends. Waits up to <paramref name="wait"/> for running ingests.
    /// </summary>
    /// <exception cref="InvalidOperationException">An ingest held the lock for the whole wait.</exception>
    internal static async Task LockRulesAsync(
        NpgsqlConnection connection, NpgsqlTransaction tx, Guid tenantId, TimeSpan wait, CancellationToken ct)
    {
        string previous;
        await using (var show = new NpgsqlCommand("SELECT current_setting('lock_timeout')", connection, tx))
            previous = (string)(await show.ExecuteScalarAsync(ct))!;

        await using var cmd = new NpgsqlCommand("""
            SELECT set_config('lock_timeout', @wait, true);
            SELECT pg_advisory_xact_lock(@key);
            SELECT set_config('lock_timeout', @previous, true);
            """, connection, tx);
        cmd.Parameters.AddWithValue("wait", Math.Max(1, (long)wait.TotalMilliseconds).ToString(CultureInfo.InvariantCulture) + "ms");
        cmd.Parameters.AddWithValue("key", RulesKey(tenantId));
        cmd.Parameters.AddWithValue("previous", previous);
        try
        {
            await cmd.ExecuteNonQueryAsync(ct);
        }
        catch (PostgresException ex) when (ex.SqlState == LockNotAvailable)
        {
            throw new InvalidOperationException(
                "A folder rule cannot change while an ingest is running in this deployment, because the run would " +
                "undo the change for the documents it reaches afterwards. Try again when the ingest finishes.", ex);
        }
    }

    /// <summary>The tenant's rules lock.</summary>
    internal static long RulesKey(Guid tenantId) => Key("premagentic.rules/" + tenantId.ToString("D"));

    /// <summary>One source's run lock: the connector and the path prefix it reconciles under.</summary>
    internal static long SourceKey(Guid tenantId, string connectorName, string pathPrefix) =>
        Key("premagentic.ingest/" + tenantId.ToString("D") + "/" + connectorName + "/" + pathPrefix);

    private static long Key(string text) =>
        BitConverter.ToInt64(SHA256.HashData(Encoding.UTF8.GetBytes(text)), 0);

    private static async Task<T> ScalarAsync<T>(NpgsqlConnection connection, string sql, long key, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("key", key);
        return (T)(await cmd.ExecuteScalarAsync(ct))!;
    }

    private sealed class IngestLease(NpgsqlConnection connection, long sourceKey, long rulesKey) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            try
            {
                await using var cmd = new NpgsqlCommand(
                    "SELECT pg_advisory_unlock_shared(@rules), pg_advisory_unlock(@source)", connection);
                cmd.Parameters.AddWithValue("rules", rulesKey);
                cmd.Parameters.AddWithValue("source", sourceKey);
                await cmd.ExecuteNonQueryAsync();
            }
            catch (NpgsqlException)
            {
                // A broken connection has released the locks already.
            }
            finally
            {
                await connection.DisposeAsync();
            }
        }
    }
}

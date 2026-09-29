using Premagentic.Core.Security;
using Premagentic.Core.Storage;
using Npgsql;

namespace Premagentic.Core.Retrieval;

/// <summary>
/// The gated reads of one search or section fetch, on one connection, in one
/// transaction, bound to one caller session.
/// <para>
/// With a search role, opening one opens a caller session holding the scope's
/// permitted list ids, then a connection as the search role, and binds the
/// session to its transaction. Every gated read of the search runs on that
/// connection, so row-level security judges each against the same caller.
/// Disposing it ends the transaction, which unbinds the session, returns the
/// connection, and closes the session.
/// </para>
/// <para>
/// With no search role the reads connect as the application role, which the
/// policy does not bind, so no session is opened: the reads still share one
/// connection and one transaction, and the SQL gate alone filters them.
/// </para>
/// </summary>
internal sealed class BoundRead : IAsyncDisposable
{
    private readonly PremagenticDatabase _db;
    private readonly NpgsqlConnection _connection;
    private readonly NpgsqlTransaction _transaction;
    private readonly string? _session;

    private BoundRead(PremagenticDatabase db, NpgsqlConnection connection, NpgsqlTransaction transaction, string? session)
    {
        _db = db;
        _connection = connection;
        _transaction = transaction;
        _session = session;
    }

    /// <param name="db">The application role's database: the caller session is opened through it.</param>
    /// <param name="searchRole">The role the reads connect as; null reads as the application role, unbound.</param>
    /// <param name="scope">A resolved scope (see <see cref="PermittedSetReader"/>).</param>
    public static async Task<BoundRead> OpenAsync(
        PremagenticDatabase db, SearchRole? searchRole, Guid tenantId, AccessScope scope, CancellationToken ct)
    {
        if (searchRole is null)
        {
            var own = await db.DataSource.OpenConnectionAsync(ct);
            try
            {
                return new BoundRead(db, own, await own.BeginTransactionAsync(ct), session: null);
            }
            catch
            {
                await own.DisposeAsync();
                throw;
            }
        }

        var session = await CallerSessions.OpenAsync(db, tenantId, scope, ct);
        NpgsqlConnection? connection = null;
        try
        {
            connection = await searchRole.DataSource.OpenConnectionAsync(ct);
            var transaction = await connection.BeginTransactionAsync(ct);
            await using (var bind = new NpgsqlCommand("SELECT prem_config.bind_caller_session(@session)", connection, transaction))
            {
                bind.Parameters.AddWithValue("session", session);
                await bind.ExecuteNonQueryAsync(ct);
            }
            return new BoundRead(db, connection, transaction, session);
        }
        catch
        {
            if (connection is not null) await connection.DisposeAsync();
            await CallerSessions.CloseAsync(db, session);
            throw;
        }
    }

    /// <summary>A command on the bound connection, inside its transaction.</summary>
    public NpgsqlCommand Command(string sql) => new(sql, _connection, _transaction);

    public async ValueTask DisposeAsync()
    {
        try
        {
            // Nothing was written, so ending the transaction either way is the
            // same, and rolling back also ends one that a failed read aborted.
            await _transaction.RollbackAsync();
        }
        catch (Exception ex) when (ex is NpgsqlException or InvalidOperationException)
        {
            // A broken connection has already ended the transaction.
        }
        finally
        {
            await _transaction.DisposeAsync();
            await _connection.DisposeAsync();
            if (_session is not null) await CallerSessions.CloseAsync(_db, _session);
        }
    }
}

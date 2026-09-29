using System.Security.Cryptography;
using Premagentic.Core.Storage;
using NpgsqlTypes;

namespace Premagentic.Core.Security;

/// <summary>
/// Opens and closes the caller sessions row-level security binds reads to. A
/// session holds the access-list ids one scope may read, computed here by the
/// same evaluation the SQL gate uses, and lives for one read.
/// <para>
/// Sessions are opened through the application role's connection, the only
/// role setup lets execute <c>prem_config.open_caller_session</c>. The search role
/// can bind a session by its id and cannot open one, so nothing on a read path
/// can choose the ids it reads under.
/// </para>
/// </summary>
internal static class CallerSessions
{
    /// <summary>How long a session may outlive a read that never closed it.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Opens a session for a resolved scope and returns its id, 64 lower-case
    /// hex characters. An unrestricted scope may read every list of the tenant;
    /// a scope that holds nothing, none.
    /// </summary>
    /// <exception cref="InvalidOperationException">The scope was never resolved, so there are no ids to bind.</exception>
    public static async Task<string> OpenAsync(PremagenticDatabase db, Guid tenantId, AccessScope scope, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var ids = scope.Unrestricted
            ? await AllSetIdsAsync(db, tenantId, ct)
            : scope.PermittedSetIds?.ToArray()
              ?? throw new InvalidOperationException("A scope must be resolved before a read is bound to it.");

        var session = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        await using var cmd = db.DataSource.CreateCommand(
            "SELECT prem_config.open_caller_session(@session, @tenant, @user, @agent, @sets, @lifetime)");
        cmd.Parameters.AddWithValue("session", session);
        cmd.Parameters.AddWithValue("tenant", tenantId);
        cmd.Parameters.AddWithValue("user", NpgsqlDbType.Uuid, (object?)scope.UserId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("agent", NpgsqlDbType.Uuid, (object?)scope.AgentId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("sets", NpgsqlDbType.Array | NpgsqlDbType.Bigint, ids);
        cmd.Parameters.AddWithValue("lifetime", Lifetime);
        await cmd.ExecuteNonQueryAsync(ct);
        return session;
    }

    /// <summary>Closes a session. A session that could not be closed expires on its own.</summary>
    public static async Task CloseAsync(PremagenticDatabase db, string session)
    {
        try
        {
            await using var cmd = db.DataSource.CreateCommand("SELECT prem_config.close_caller_session(@session)");
            cmd.Parameters.AddWithValue("session", session);
            await cmd.ExecuteNonQueryAsync();
        }
        catch (Exception ex) when (ex is Npgsql.NpgsqlException or InvalidOperationException)
        {
            // Nothing to do: the row is swept once it expires, and an expired
            // session binds nothing.
        }
    }

    private static async Task<long[]> AllSetIdsAsync(PremagenticDatabase db, Guid tenantId, CancellationToken ct)
    {
        await using var cmd = db.DataSource.CreateCommand("SELECT id FROM prem_config.acl_set WHERE tenant_id = @tenant");
        cmd.Parameters.AddWithValue("tenant", tenantId);
        var ids = new List<long>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) ids.Add(reader.GetInt64(0));
        return ids.ToArray();
    }
}

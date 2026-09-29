using System.Text.Json;
using Premagentic.Core.Storage;
using Npgsql;
using NpgsqlTypes;

namespace Premagentic.Core.Admin;

/// <summary>
/// Who made an administrator change, as the change record keeps it.
/// </summary>
/// <param name="Surface">Where the change was made: <c>cli</c>, or later the portal.</param>
/// <param name="Account">The operating-system account that ran the command, where nobody signs in.</param>
/// <param name="UserId">The signed-in Premagentic user, when there is one.</param>
public sealed record AdminActor(string Surface, string? Account, Guid? UserId = null)
{
    /// <summary>
    /// The CLI's actor: the word <c>cli</c> and the operating-system account
    /// running it. Nobody signs in to the CLI; whoever can run it with the
    /// database credentials is acting as an administrator.
    /// </summary>
    public static AdminActor Cli() => new(
        "cli",
        OperatingSystem.IsWindows() ? $"{Environment.UserDomainName}\\{Environment.UserName}" : Environment.UserName);

    /// <summary>How the actor reads in a listing.</summary>
    public string Describe() => (Account, UserId) switch
    {
        ({ } account, { } user) => $"{Surface} ({account}, user {user})",
        ({ } account, null) => $"{Surface} ({account})",
        (null, { } user) => $"{Surface} (user {user})",
        _ => Surface,
    };
}

/// <summary>One row of the change record.</summary>
/// <param name="OldValue">The value before, or null when there was none.</param>
/// <param name="NewValue">The value after, or null when the object was removed.</param>
public sealed record AdminEvent(
    long Id,
    DateTimeOffset OccurredAt,
    string Kind,
    string Target,
    JsonElement? OldValue,
    JsonElement? NewValue,
    AdminActor Actor);

/// <summary>
/// The change record, <c>prem_config.admin_event</c>: an append-only list of
/// administrator changes, each with when, what, the old and the new value, and
/// who. Every change is written in the same transaction as the row it records.
/// <para>
/// There is an append and a read, and nothing else. The table refuses update,
/// delete and truncate from every role (migration 0011), so the record cannot
/// be corrected by editing it.
/// </para>
/// </summary>
public sealed class ChangeRecord(PremagenticDatabase db, Guid tenantId)
{
    /// <summary>
    /// The advisory lock class for administrator changes in one tenant. Taken
    /// for the length of a change's transaction, so two changes to the same
    /// thing are recorded one after the other, each with the true old value.
    /// </summary>
    private const int LockClass = 0x48434144; // "HCAD"

    /// <summary>Takes the tenant's administrator-change lock until <paramref name="tx"/> ends.</summary>
    internal static async Task LockAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid tenantId, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("SELECT pg_advisory_xact_lock(@class, hashtext(@tenant::text))", conn, tx);
        cmd.Parameters.AddWithValue("class", LockClass);
        cmd.Parameters.AddWithValue("tenant", tenantId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Appends one row inside the caller's transaction.</summary>
    internal static async Task AppendAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, Guid tenantId,
        string kind, string target, string? oldJson, string? newJson, AdminActor actor, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("""
            INSERT INTO prem_config.admin_event(
                tenant_id, kind, target, old_value, new_value, actor_surface, actor_account, actor_user_id)
            VALUES(@tenant, @kind, @target, @old, @new, @surface, @account, @user)
            """, conn, tx);
        cmd.Parameters.AddWithValue("tenant", tenantId);
        cmd.Parameters.AddWithValue("kind", kind);
        cmd.Parameters.AddWithValue("target", target);
        cmd.Parameters.Add(new NpgsqlParameter("old", NpgsqlDbType.Jsonb) { Value = (object?)oldJson ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("new", NpgsqlDbType.Jsonb) { Value = (object?)newJson ?? DBNull.Value });
        cmd.Parameters.AddWithValue("surface", actor.Surface);
        cmd.Parameters.Add(new NpgsqlParameter("account", NpgsqlDbType.Text) { Value = (object?)actor.Account ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("user", NpgsqlDbType.Uuid) { Value = (object?)actor.UserId ?? DBNull.Value });
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>The most recent changes, newest first.</summary>
    public Task<IReadOnlyList<AdminEvent>> ListAsync(int limit = 50, CancellationToken ct = default) =>
        ListAsync(limit, beforeId: null, ct);

    /// <summary>Up to <paramref name="limit"/> changes older than <paramref name="beforeId"/>, or the newest when null, newest first.</summary>
    public async Task<IReadOnlyList<AdminEvent>> ListAsync(int limit, long? beforeId, CancellationToken ct = default)
    {
        await using var cmd = db.DataSource.CreateCommand("""
            SELECT id, occurred_at, kind, target, old_value::text, new_value::text, actor_surface, actor_account, actor_user_id
            FROM prem_config.admin_event
            WHERE tenant_id = @tenant AND (@before IS NULL OR id < @before)
            ORDER BY id DESC
            LIMIT @limit
            """);
        cmd.Parameters.AddWithValue("tenant", tenantId);
        cmd.Parameters.Add(new NpgsqlParameter("before", NpgsqlDbType.Bigint) { Value = (object?)beforeId ?? DBNull.Value });
        cmd.Parameters.AddWithValue("limit", limit);

        var events = new List<AdminEvent>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            events.Add(new AdminEvent(
                reader.GetInt64(0),
                reader.GetFieldValue<DateTimeOffset>(1),
                reader.GetString(2),
                reader.GetString(3),
                Json(reader, 4),
                Json(reader, 5),
                new AdminActor(
                    reader.GetString(6),
                    reader.IsDBNull(7) ? null : reader.GetString(7),
                    reader.IsDBNull(8) ? null : reader.GetGuid(8))));
        return events;
    }

    private static JsonElement? Json(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : JsonDocument.Parse(reader.GetString(ordinal)).RootElement.Clone();
}

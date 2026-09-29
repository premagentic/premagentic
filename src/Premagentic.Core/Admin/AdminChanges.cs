using System.Text.Json;
using Premagentic.Core.Identity;
using Premagentic.Core.Security;
using Premagentic.Core.Storage;
using Npgsql;

namespace Premagentic.Core.Admin;

/// <summary>
/// One administrator change to users, groups, agents, tokens or folder rules,
/// and its rows in the change record, in one transaction: the change and its
/// record land together or not at all.
/// <para>
/// Settings and sources are not changed through here: their stores open their
/// own transaction and take the same lock, so calling them inside this one
/// would wait on itself.
/// </para>
/// </summary>
public sealed class AdminChanges(PremagenticDatabase db, Guid tenantId, TimeProvider? time = null)
{
    /// <summary>
    /// Runs <paramref name="change"/> inside one transaction, holding the
    /// tenant's administrator-change lock, then appends every row it recorded
    /// and commits. A change that records nothing changed nothing.
    /// </summary>
    public async Task<T> RunAsync<T>(AdminActor actor, Func<AdminChange, Task<T>> change, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        await using var conn = await db.DataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await ChangeRecord.LockAsync(conn, tx, tenantId, ct);

        var unit = new AdminChange(db, tenantId, time, tx, actor);
        var result = await change(unit);
        foreach (var row in unit.Rows)
            await ChangeRecord.AppendAsync(conn, tx, tenantId, row.Kind, row.Target, row.Old, row.New, actor, ct);

        await tx.CommitAsync(ct);
        return result;
    }
}

/// <summary>The stores one change works through, and the rows it will record.</summary>
public sealed class AdminChange
{
    private readonly List<(string Kind, string Target, string? Old, string? New)> _rows = [];

    internal AdminChange(PremagenticDatabase db, Guid tenantId, TimeProvider? time, NpgsqlTransaction transaction, AdminActor actor)
    {
        Transaction = transaction;
        Actor = actor;
        Identity = new IdentityStore(db, tenantId, time, transaction);
        Rules = new AclStore(db, tenantId, transaction);
    }

    /// <summary>The transaction the change and its record share.</summary>
    public NpgsqlTransaction Transaction { get; }

    /// <summary>Who is making the change, as its rows in the change record will name them.</summary>
    public AdminActor Actor { get; }

    /// <summary>The identity store, running every command inside <see cref="Transaction"/>.</summary>
    public IdentityStore Identity { get; }

    /// <summary>
    /// The folder rules, inside <see cref="Transaction"/>. A rule change holds the
    /// tenant's exclusive rules lock until the change commits, and is refused
    /// while an ingest holds it.
    /// </summary>
    public AclStore Rules { get; }

    internal IReadOnlyList<(string Kind, string Target, string? Old, string? New)> Rows => _rows;

    /// <summary>
    /// Records one change: its kind, what it changed, and the values before and
    /// after, each serialized as JSON, or null when there was none. Never pass a
    /// secret or a hash.
    /// </summary>
    public void Record(string kind, string target, object? before, object? after) =>
        _rows.Add((kind, target, Json(before), Json(after)));

    private static string? Json(object? value) => value is null ? null : JsonSerializer.Serialize(value);
}

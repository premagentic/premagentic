using Premagentic.Core.Storage;

namespace Premagentic.Core.Reminders;

/// <summary>
/// The built-in reminder sink: it keeps each run in the deployment's own
/// database, where <see cref="ReminderSummaries"/> reads the latest for the
/// portal, and logs one line. It calls nothing outside the database.
/// <para>
/// A run replaces the one before it in one transaction, so a reader sees one
/// whole run or the one before, never part of either.
/// </para>
/// </summary>
public sealed class TableReminderSink : IReminderSink
{
    /// <summary>The built-in sink's name, which no extension can register.</summary>
    public const string SinkName = "table";

    internal const string StaleList = "stale";
    internal const string ReviewList = "review";
    internal const string UnownedList = "unowned";

    private readonly PremagenticDatabase _db;
    private readonly Guid _tenantId;
    private readonly Action<string>? _log;

    public TableReminderSink(PremagenticDatabase db, Guid tenantId, Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(db);
        _db = db;
        _tenantId = tenantId;
        _log = log;
    }

    public string Name => SinkName;

    public async Task DeliverAsync(IReadOnlyList<ReminderSummary> summaries, DateTimeOffset computedAt, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(summaries);
        var (stale, review, unowned) =
            (summaries.Sum(s => s.Stale.Count), summaries.Sum(s => s.InReview.Count), summaries.Sum(s => s.Unowned.Count));

        await using var conn = await _db.DataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        await using (var delete = new Npgsql.NpgsqlCommand(
            "DELETE FROM prem_config.reminder_run WHERE tenant_id = @tenant", conn, tx))
        {
            delete.Parameters.AddWithValue("tenant", _tenantId);
            await delete.ExecuteNonQueryAsync(ct);
        }

        Guid runId;
        await using (var insert = new Npgsql.NpgsqlCommand("""
            INSERT INTO prem_config.reminder_run(tenant_id, computed_at, owners, stale, in_review, unowned)
            VALUES (@tenant, @at, @owners, @stale, @review, @unowned)
            RETURNING id
            """, conn, tx))
        {
            insert.Parameters.AddWithValue("tenant", _tenantId);
            insert.Parameters.AddWithValue("at", computedAt);
            insert.Parameters.AddWithValue("owners", summaries.Count);
            insert.Parameters.AddWithValue("stale", stale);
            insert.Parameters.AddWithValue("review", review);
            insert.Parameters.AddWithValue("unowned", unowned);
            runId = (Guid)(await insert.ExecuteScalarAsync(ct))!;
        }

        foreach (var summary in summaries)
            foreach (var (list, items) in new[] { (StaleList, summary.Stale), (ReviewList, summary.InReview), (UnownedList, summary.Unowned) })
                foreach (var item in items)
                {
                    await using var row = new Npgsql.NpgsqlCommand("""
                        INSERT INTO prem_config.reminder_item(run_id, owner, list, path, title, since)
                        VALUES (@run, @owner, @list, @path, @title, @since)
                        """, conn, tx);
                    row.Parameters.AddWithValue("run", runId);
                    row.Parameters.AddWithValue("owner", summary.OwnerPrincipal);
                    row.Parameters.AddWithValue("list", list);
                    row.Parameters.AddWithValue("path", item.Path);
                    row.Parameters.AddWithValue("title", (object?)item.Title ?? DBNull.Value);
                    row.Parameters.AddWithValue("since", (object?)item.Since ?? DBNull.Value);
                    await row.ExecuteNonQueryAsync(ct);
                }

        await tx.CommitAsync(ct);
        _log?.Invoke(
            $"reminders: {summaries.Count} owner(s), {stale} stale, {review} in review, {unowned} unowned, computed {computedAt:u}");
    }
}

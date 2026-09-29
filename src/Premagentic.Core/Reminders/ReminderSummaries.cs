using Premagentic.Core.Storage;

namespace Premagentic.Core.Reminders;

/// <summary>What the portal reads: the summaries the latest delivered run kept.</summary>
public sealed class ReminderSummaries
{
    private readonly PremagenticDatabase _db;
    private readonly Guid _tenantId;

    public ReminderSummaries(PremagenticDatabase db, Guid tenantId)
    {
        ArgumentNullException.ThrowIfNull(db);
        _db = db;
        _tenantId = tenantId;
    }

    /// <summary>
    /// The latest run's summaries and when they were computed, or null when no
    /// run has been delivered. Owners in principal order, the administrators'
    /// summary last; items in path order.
    /// </summary>
    public async Task<(DateTimeOffset ComputedAt, IReadOnlyList<ReminderSummary> Summaries)?> LatestAsync(CancellationToken ct)
    {
        await using var cmd = _db.DataSource.CreateCommand("""
            SELECT r.computed_at, i.owner, i.list, i.path, i.title, i.since
            FROM prem_config.reminder_run r
            LEFT JOIN prem_config.reminder_item i ON i.run_id = r.id
            WHERE r.tenant_id = @tenant
            ORDER BY i.owner = @administrators, i.owner, i.list, i.path
            """);
        cmd.Parameters.AddWithValue("tenant", _tenantId);
        cmd.Parameters.AddWithValue("administrators", ReminderSummary.Administrators);

        DateTimeOffset? computedAt = null;
        var owners = new List<string>();
        var lists = new Dictionary<(string Owner, string List), List<ReminderItem>>();
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
            {
                computedAt ??= r.GetFieldValue<DateTimeOffset>(0);
                if (r.IsDBNull(1)) continue; // a run that found nothing
                var owner = r.GetString(1);
                if (!owners.Contains(owner)) owners.Add(owner);
                var key = (owner, r.GetString(2));
                if (!lists.TryGetValue(key, out var items)) lists[key] = items = [];
                items.Add(new ReminderItem(r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4),
                    r.IsDBNull(5) ? null : r.GetFieldValue<DateTimeOffset>(5)));
            }

        if (computedAt is not { } at) return null;
        IReadOnlyList<ReminderItem> Of(string owner, string list) => lists.GetValueOrDefault((owner, list)) ?? [];
        return (at, owners.Select(o => new ReminderSummary(o,
            Of(o, TableReminderSink.StaleList), Of(o, TableReminderSink.ReviewList), Of(o, TableReminderSink.UnownedList))).ToList());
    }
}

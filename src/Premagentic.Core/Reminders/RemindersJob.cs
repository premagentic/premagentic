using Premagentic.Core.Okf;
using Premagentic.Core.Storage;

namespace Premagentic.Core.Reminders;

/// <summary>A sink that threw while a run was delivered to it; the others were still delivered to.</summary>
public sealed record ReminderSinkFailure(string Sink, string Message);

/// <summary>
/// Computes, from the index, what each source owner is reminded of, and hands
/// the summaries to the built-in sink and then to every sink an extension
/// added. Meant to be run on a schedule by <c>prem reminders run</c>.
/// <para>
/// A document is on a list when it is past its <c>stale_after</c> (stale), or
/// in the review queue by the queue's own condition (in review); one that is
/// both is on both lists, once each. It belongs to the owner of the registered
/// source whose prefix holds its path, the longest such prefix when several
/// do. A document whose source has no owner, or that came from a folder given
/// by hand, is on the administrators' summary as unowned, once, and on no other
/// list. Nothing here reads a document's text, and nothing here writes: only
/// the sinks do, and <c>--plan</c> delivers to none.
/// </para>
/// </summary>
public sealed class RemindersJob
{
    private readonly PremagenticDatabase _db;
    private readonly Guid _tenantId;
    private readonly IReadOnlyList<IReminderSink> _sinks;
    private readonly TimeProvider _time;

    /// <param name="sinks">
    /// The sinks the loaded extensions added (<c>ExtensionHost.ReminderSinks</c>).
    /// The built-in sink is not in this list: the job always delivers to it
    /// first.
    /// </param>
    /// <param name="log">Where the built-in sink writes its one line per run; nowhere when null.</param>
    public RemindersJob(PremagenticDatabase db, Guid tenantId, IReadOnlyList<IReminderSink> sinks,
        Action<string>? log = null, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(sinks);
        _db = db;
        _tenantId = tenantId;
        _time = time ?? TimeProvider.System;
        _sinks = [new TableReminderSink(db, tenantId, log), .. sinks];
    }

    /// <summary>The summaries the last <see cref="RunAsync"/> computed, per owner, in principal order.</summary>
    public IReadOnlyList<ReminderSummary> Summaries { get; private set; } = [];

    /// <summary>The sinks that threw during the last delivered run; empty when every one took it.</summary>
    public IReadOnlyList<ReminderSinkFailure> Failures { get; private set; } = [];

    /// <param name="plan">Compute and report, deliver to nobody, write nothing.</param>
    public async Task<ReminderRun> RunAsync(bool plan, CancellationToken ct)
    {
        var computedAt = _time.GetUtcNow();
        Summaries = await ComputeAsync(computedAt, ct);
        Failures = [];

        var run = new ReminderRun(computedAt, Summaries.Count,
            Summaries.Sum(s => s.Stale.Count), Summaries.Sum(s => s.InReview.Count), Summaries.Sum(s => s.Unowned.Count),
            Planned: plan);
        if (plan) return run;

        var failures = new List<ReminderSinkFailure>();
        foreach (var sink in _sinks)
        {
            try
            {
                await sink.DeliverAsync(Summaries, computedAt, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One sink that cannot take the run does not keep it from the others.
                failures.Add(new ReminderSinkFailure(sink.Name, ex.Message));
            }
        }
        Failures = failures;
        return run;
    }

    private async Task<IReadOnlyList<ReminderSummary>> ComputeAsync(DateTimeOffset now, CancellationToken ct)
    {
        // Who owns each registered source, by prefix. A source whose owner was
        // removed as a user is unowned, like one that never had an owner.
        var owners = new List<(string Prefix, string? Owner)>();
        await using (var cmd = _db.DataSource.CreateCommand("""
            SELECT s.path_prefix, u.sign_in_name
            FROM prem_config.source s
            LEFT JOIN prem_config.app_user u
              ON u.tenant_id = s.tenant_id AND u.id = s.owner_user_id AND u.deleted_at IS NULL
            WHERE s.tenant_id = @tenant AND s.deleted_at IS NULL
            """))
        {
            cmd.Parameters.AddWithValue("tenant", _tenantId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                owners.Add((r.GetString(0), r.IsDBNull(1) ? null : "user:" + r.GetString(1)));
        }

        var stale = new Dictionary<string, List<ReminderItem>>(StringComparer.Ordinal);
        var review = new Dictionary<string, List<ReminderItem>>(StringComparer.Ordinal);
        var unowned = new List<ReminderItem>();

        await using (var cmd = _db.DataSource.CreateCommand($"""
            SELECT d.path, d.title, d.stale_after, d.updated_at,
                   (d.stale_after IS NOT NULL AND d.stale_after <= @now) AS is_stale,
                   ({ReviewQueue.Condition}) AS in_review
            FROM prem_index.document d
            WHERE d.tenant_id = @tenant
              AND ((d.stale_after IS NOT NULL AND d.stale_after <= @now) OR ({ReviewQueue.Condition}))
            ORDER BY d.path
            """))
        {
            cmd.Parameters.AddWithValue("tenant", _tenantId);
            cmd.Parameters.AddWithValue("now", now);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var path = r.GetString(0);
                var title = r.IsDBNull(1) ? null : r.GetString(1);
                DateTimeOffset? staleAfter = r.IsDBNull(2) ? null : r.GetFieldValue<DateTimeOffset>(2);
                var updatedAt = r.GetFieldValue<DateTimeOffset>(3);
                var isStale = r.GetBoolean(4);
                var inReview = r.GetBoolean(5);

                var owner = OwnerOf(path, owners);
                if (owner is null)
                {
                    unowned.Add(new ReminderItem(path, title, isStale ? staleAfter : updatedAt));
                    continue;
                }
                if (isStale) Add(stale, owner, new ReminderItem(path, title, staleAfter));
                if (inReview) Add(review, owner, new ReminderItem(path, title, updatedAt));
            }
        }

        var summaries = stale.Keys.Union(review.Keys)
            .Order(StringComparer.Ordinal)
            .Select(owner => new ReminderSummary(owner,
                stale.GetValueOrDefault(owner) ?? [], review.GetValueOrDefault(owner) ?? [], []))
            .ToList();
        if (unowned.Count > 0) summaries.Add(new ReminderSummary(ReminderSummary.Administrators, [], [], unowned));
        return summaries;
    }

    /// <summary>The owner of the source whose prefix holds <paramref name="path"/>, the longest such prefix; null when it has none.</summary>
    private static string? OwnerOf(string path, IReadOnlyList<(string Prefix, string? Owner)> owners) =>
        owners.Where(o => o.Prefix.Length == 0 || path.StartsWith(o.Prefix + "/", StringComparison.Ordinal))
            .OrderByDescending(o => o.Prefix.Length)
            .Select(o => o.Owner)
            .FirstOrDefault();

    private static void Add(Dictionary<string, List<ReminderItem>> lists, string owner, ReminderItem item)
    {
        if (!lists.TryGetValue(owner, out var list)) lists[owner] = list = [];
        list.Add(item);
    }
}

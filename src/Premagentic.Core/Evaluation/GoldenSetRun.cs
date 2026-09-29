using System.Text.Json;
using Premagentic.Core.Admin;
using Premagentic.Core.Identity;
using Premagentic.Core.Retrieval;
using Premagentic.Core.Storage;

namespace Premagentic.Core.Evaluation;

public enum GoldenRunOutcome
{
    /// <summary>Every case ran and was judged.</summary>
    Completed,

    /// <summary>The run passed its time limit and was stopped.</summary>
    TimedOut,

    /// <summary>Another run was in progress in this process, so this one did not start.</summary>
    Busy,

    /// <summary>The golden set is not set or cannot be read, so the run did not start.</summary>
    Refused,
}

/// <param name="Summary">What the run found, when it completed.</param>
/// <param name="Problem">Why the run did not start, when it was refused.</param>
public sealed record GoldenRunResult(GoldenRunOutcome Outcome, EvalSummary? Summary = null, string? Problem = null);

/// <summary>One recorded run, newest first when listed.</summary>
/// <param name="Value">The summary as recorded: the outcome, the counts, the failed cases and the settings.</param>
public sealed record GoldenRunRecord(DateTimeOffset At, AdminActor Actor, string GoldenSetPath, JsonElement Value);

/// <summary>
/// Runs the golden set at <see cref="TuningSettingsStore.GoldenSetPath"/> on
/// behalf of an administrator, so a change to the tuning is judged rather
/// than guessed.
/// <list type="bullet">
/// <item>One run at a time in the process: a run that finds another in
/// progress does not wait, it says so.</item>
/// <item>Bounded by a time limit, <see cref="Limit"/> by default.</item>
/// <item>Each run that starts is recorded in the change record as
/// <see cref="Kind"/>, with its counts, its failed cases and the settings it
/// ran under, including a run stopped by the limit. Nothing is written
/// anywhere else: no report file, no result table.</item>
/// </list>
/// </summary>
public sealed class GoldenSetRun(
    PremagenticDatabase db, Guid tenantId, HybridSearch search, PrincipalNames names, TimeProvider? time = null)
{
    public const string Kind = "evaluation.run";

    public static readonly TimeSpan Limit = TimeSpan.FromMinutes(2);

    private static readonly SemaphoreSlim OneAtATime = new(1, 1);

    /// <summary>Held while a run is in progress; a test holds it to stand for one.</summary>
    internal static SemaphoreSlim InProgress => OneAtATime;

    /// <param name="limit">The time limit; <see cref="Limit"/> when null.</param>
    public async Task<GoldenRunResult> RunAsync(AdminActor actor, TimeSpan? limit = null)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (!await OneAtATime.WaitAsync(0)) return new GoldenRunResult(GoldenRunOutcome.Busy);
        try
        {
            var golden = await new TuningSettingsStore(db, tenantId, time).ReadGoldenSetPathAsync();
            if (golden.Path is not { } path)
                return new GoldenRunResult(GoldenRunOutcome.Refused, Problem: golden.Problem ??
                    $"No golden set is set. Set one on the machine that runs Premagentic: " +
                    $"prem settings set {TuningSettingsStore.GoldenSetPath} <absolute path>.");
            if (!EvalRunner.TryLoadCases(path, out var cases, out var problem))
                return new GoldenRunResult(GoldenRunOutcome.Refused, Problem: problem);

            var bound = limit ?? Limit;
            using var timeout = new CancellationTokenSource(bound);
            EvalSummary summary;
            try
            {
                summary = await new EvalRunner(search, tenantId, names).EvaluateAsync(cases, timeout.Token);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                await RecordAsync(actor, path, new Dictionary<string, object>
                {
                    ["outcome"] = "timed_out",
                    ["limit_seconds"] = bound.TotalSeconds,
                    ["cases"] = cases.Count,
                });
                return new GoldenRunResult(GoldenRunOutcome.TimedOut);
            }

            await RecordAsync(actor, path, SummaryValue(summary));
            return new GoldenRunResult(GoldenRunOutcome.Completed, summary);
        }
        finally
        {
            OneAtATime.Release();
        }
    }

    /// <summary>The recorded runs, newest first.</summary>
    public static async Task<IReadOnlyList<GoldenRunRecord>> LatestAsync(
        PremagenticDatabase db, Guid tenantId, int limit, CancellationToken ct = default)
    {
        await using var cmd = db.DataSource.CreateCommand("""
            SELECT occurred_at, target, new_value::text, actor_surface, actor_account, actor_user_id
            FROM prem_config.admin_event
            WHERE tenant_id = @tenant AND kind = @kind
            ORDER BY id DESC
            LIMIT @limit
            """);
        cmd.Parameters.AddWithValue("tenant", tenantId);
        cmd.Parameters.AddWithValue("kind", Kind);
        cmd.Parameters.AddWithValue("limit", limit);

        var runs = new List<GoldenRunRecord>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            using var value = JsonDocument.Parse(reader.GetString(2));
            runs.Add(new GoldenRunRecord(
                reader.GetFieldValue<DateTimeOffset>(0),
                new AdminActor(
                    reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.IsDBNull(5) ? null : reader.GetGuid(5)),
                reader.GetString(1),
                value.RootElement.Clone()));
        }
        return runs;
    }

    /// <summary>
    /// A completed run as recorded: its counts, its failed cases, and the
    /// settings it ran under. When those changed between cases, the settings of
    /// each case are listed too.
    /// </summary>
    public static Dictionary<string, object> SummaryValue(EvalSummary summary)
    {
        var value = new Dictionary<string, object>
        {
            ["outcome"] = "completed",
            ["cases"] = summary.Cases.Count,
            ["passed"] = summary.Passed,
            ["no_answer_cases"] = summary.NoAnswerCases,
            ["no_answer_correct"] = summary.NoAnswerCorrect,
            ["failed"] = summary.Failed,
            ["mean_latency_ms"] = summary.MeanLatencyMs,
            ["settings_changed"] = summary.SettingsChanged,
        };
        if (summary.Settings is { } settings) value["settings"] = RetrievalReadings.ToValue(settings);
        if (summary.SettingsChanged)
            value["settings_by_case"] = summary.Cases
                .Where(c => c.Result is not null)
                .Select(c => new Dictionary<string, object> { ["id"] = c.Case.Id, ["settings"] = RetrievalReadings.ToValue(c.Result!.Settings) })
                .ToArray();
        return value;
    }

    private Task RecordAsync(AdminActor actor, string path, Dictionary<string, object> value) =>
        new AdminChanges(db, tenantId, time).RunAsync(actor, change =>
        {
            change.Record(Kind, path, null, value);
            return Task.FromResult(0);
        });
}

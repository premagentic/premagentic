using System.Text.Json;
using System.Text.Json.Serialization;
using Premagentic.Core.Ingestion;
using Premagentic.Core.Okf;
using Premagentic.Core.Storage;
using Npgsql;
using NpgsqlTypes;

namespace Premagentic.Core.Sources.Registry;

public enum IngestRunOutcome
{
    /// <summary>Started and not finished. A run whose process died stays here.</summary>
    Running,

    Completed,

    /// <summary>The source yielded nothing while documents are indexed under its prefix, so nothing was deleted.</summary>
    ReconciliationRefused,

    /// <summary>The run stopped with an error, such as a folder that does not exist.</summary>
    Failed,
}

/// <summary>
/// One ingest run as <c>prem_config.ingest_run</c> keeps it.
/// </summary>
/// <param name="SourceId">The registered source, or null for a folder given on the command line.</param>
/// <param name="Chunker">The name of the chunker the run was asked to cut with.</param>
/// <param name="Summary">The run's counts, or null while it runs and when it failed.</param>
/// <param name="OkfVersion">From a run in bundle mode: the declared <c>okf_version</c>, or null when none is declared.</param>
/// <param name="OkfVersionKnown">Null when the run was not in bundle mode.</param>
public sealed record IngestRunRecord(
    Guid Id,
    Guid? SourceId,
    string Connector,
    string Folder,
    string PathPrefix,
    bool OkfBundle,
    bool UndeclaredIsMachine,
    string Chunker,
    DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt,
    IngestRunOutcome Outcome,
    string? Error,
    IngestSummary? Summary,
    string? OkfVersion,
    bool? OkfVersionKnown,
    IReadOnlyList<OkfConformanceIssue> ConformanceIssues);

/// <param name="BundleReport">The conformance report, when the run was in bundle mode.</param>
public sealed record RecordedIngestResult(Guid RunId, IngestSummary Summary, OkfBundleReport? BundleReport);

/// <summary>
/// The record of every ingest run, <c>prem_config.ingest_run</c>: what was read,
/// with what settings, when it started and ended, every count in its summary,
/// what it skipped and could not read, and, in bundle mode, the declared OKF
/// version and the files that do not conform.
/// <para>
/// <see cref="RunAsync"/> is the way to ingest a folder and record it; it
/// records a run that throws as failed and throws on.
/// </para>
/// </summary>
public sealed class IngestRuns(PremagenticDatabase db, Guid tenantId)
{
    private const string Columns = """
        id, source_id, connector, folder, path_prefix, okf_bundle, undeclared_is_machine,
        started_at, finished_at, outcome, error,
        scanned, ingested, unchanged, chunks_embedded, orphans_removed, denied_to_everyone, unreadable,
        skipped_by_extension::text, unreadable_paths::text, undeclared_authorship,
        okf_version, okf_version_known, conformance_issues::text, chunker, unmapped_principals,
        kept_no_reader, removed_now_skipped
        """;

    /// <summary>
    /// Runs <paramref name="pipeline"/> over <paramref name="source"/> and
    /// records the run from start to end.
    /// </summary>
    /// <param name="sourceId">The registered source, or null for a folder that is not registered.</param>
    /// <param name="chunker">
    /// The name of the chunker to cut with. When the pipeline has no chunker by
    /// that name, the run is recorded as failed with the name and nothing is
    /// read or changed.
    /// </param>
    public async Task<RecordedIngestResult> RunAsync(
        IngestPipeline pipeline, FileSystemSource source, Guid? sourceId,
        Action<string>? log = null, bool allowEmptySource = false,
        string chunker = ChunkerRegistry.DefaultName, CancellationToken ct = default)
    {
        await using var locks = await Security.IndexLocks.ForIngestAsync(db, tenantId, source.Name, source.PathPrefix, ct);
        var runId = await StartAsync(source, sourceId, chunker, ct);
        IngestSummary summary;
        try
        {
            summary = await pipeline.RunAsync(tenantId, source, chunker, log, allowEmptySource, ct);
        }
        catch (Exception ex)
        {
            // Recorded even when the caller canceled, so a run never stays
            // "running" because it stopped early.
            await FailAsync(runId, $"{ex.GetType().Name}: {ex.Message}", CancellationToken.None);
            throw;
        }

        await CompleteAsync(runId, summary, source.BundleReport, ct);
        return new RecordedIngestResult(runId, summary, source.BundleReport);
    }

    /// <summary>Records that a run has started, with what it reads, and returns its id.</summary>
    public async Task<Guid> StartAsync(
        FileSystemSource source, Guid? sourceId, string chunker = ChunkerRegistry.DefaultName, CancellationToken ct = default)
    {
        await using var cmd = db.DataSource.CreateCommand("""
            INSERT INTO prem_config.ingest_run(
                tenant_id, source_id, connector, folder, path_prefix, okf_bundle, undeclared_is_machine, chunker)
            VALUES(@tenant, @source, @connector, @folder, @prefix, @bundle, @undeclared, @chunker)
            RETURNING id
            """);
        cmd.Parameters.AddWithValue("tenant", tenantId);
        cmd.Parameters.Add(new NpgsqlParameter("source", NpgsqlDbType.Uuid) { Value = (object?)sourceId ?? DBNull.Value });
        cmd.Parameters.AddWithValue("connector", source.Name);
        cmd.Parameters.AddWithValue("folder", Path.GetFullPath(source.Root));
        cmd.Parameters.AddWithValue("prefix", source.PathPrefix);
        cmd.Parameters.AddWithValue("bundle", source.OkfBundle);
        cmd.Parameters.AddWithValue("undeclared", source.UndeclaredAuthorshipIsMachine);
        cmd.Parameters.AddWithValue("chunker", chunker);
        return (Guid)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public async Task CompleteAsync(Guid runId, IngestSummary summary, OkfBundleReport? report, CancellationToken ct = default)
    {
        await using var cmd = db.DataSource.CreateCommand("""
            UPDATE prem_config.ingest_run SET
                finished_at = clock_timestamp(),
                outcome = @outcome,
                scanned = @scanned, ingested = @ingested, unchanged = @unchanged,
                chunks_embedded = @chunks, orphans_removed = @orphans,
                denied_to_everyone = @denied, unreadable = @unreadable,
                skipped = @skipped, skipped_by_extension = @skippedBy, unreadable_paths = @unreadablePaths,
                undeclared_authorship = @undeclared, unmapped_principals = @unmapped,
                kept_no_reader = @kept, removed_now_skipped = @removedSkipped,
                okf_version = @okfVersion, okf_version_known = @okfKnown, conformance_issues = @issues
            WHERE tenant_id = @tenant AND id = @id AND outcome = 'running'
            """);
        cmd.Parameters.AddWithValue("tenant", tenantId);
        cmd.Parameters.AddWithValue("id", runId);
        cmd.Parameters.AddWithValue("outcome", OutcomeText(
            summary.ReconciliationSkipped ? IngestRunOutcome.ReconciliationRefused : IngestRunOutcome.Completed));
        cmd.Parameters.AddWithValue("scanned", summary.Scanned);
        cmd.Parameters.AddWithValue("ingested", summary.Ingested);
        cmd.Parameters.AddWithValue("unchanged", summary.Unchanged);
        cmd.Parameters.AddWithValue("chunks", summary.ChunksEmbedded);
        cmd.Parameters.AddWithValue("orphans", summary.OrphansRemoved);
        cmd.Parameters.AddWithValue("denied", summary.DeniedToEveryone);
        cmd.Parameters.AddWithValue("unreadable", summary.Unreadable);
        cmd.Parameters.AddWithValue("skipped", summary.Skipped);
        cmd.Parameters.Add(new NpgsqlParameter("skippedBy", NpgsqlDbType.Jsonb)
        {
            Value = JsonSerializer.Serialize(summary.SkippedFormats),
        });
        cmd.Parameters.Add(new NpgsqlParameter("unreadablePaths", NpgsqlDbType.Jsonb)
        {
            Value = JsonSerializer.Serialize(summary.FailureList.Select(f => new UnreadablePath(f.Path, f.Reason))),
        });
        cmd.Parameters.AddWithValue("undeclared", summary.UndeclaredAuthorship);
        cmd.Parameters.AddWithValue("unmapped", summary.UnmappedPrincipals);
        cmd.Parameters.AddWithValue("kept", summary.KeptWithoutReader);
        cmd.Parameters.AddWithValue("removedSkipped", summary.RemovedNowSkipped);
        cmd.Parameters.Add(new NpgsqlParameter("okfVersion", NpgsqlDbType.Text) { Value = (object?)report?.OkfVersion ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("okfKnown", NpgsqlDbType.Boolean) { Value = (object?)report?.VersionKnown ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("issues", NpgsqlDbType.Jsonb)
        {
            Value = report is null
                ? DBNull.Value
                : JsonSerializer.Serialize(report.Issues.Select(i => new ConformanceIssue(i.Path, i.Problem.ToString(), i.Detail))),
        });
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task FailAsync(Guid runId, string error, CancellationToken ct = default)
    {
        await using var cmd = db.DataSource.CreateCommand("""
            UPDATE prem_config.ingest_run SET finished_at = clock_timestamp(), outcome = 'failed', error = @error
            WHERE tenant_id = @tenant AND id = @id AND outcome = 'running'
            """);
        cmd.Parameters.AddWithValue("tenant", tenantId);
        cmd.Parameters.AddWithValue("id", runId);
        cmd.Parameters.AddWithValue("error", error);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<IngestRunRecord?> GetAsync(Guid runId, CancellationToken ct = default) =>
        (await QueryAsync("id = @id", cmd => cmd.Parameters.AddWithValue("id", runId), 1, ct)).FirstOrDefault();

    /// <summary>The most recent run of a registered source, or null when it has never run.</summary>
    public async Task<IngestRunRecord?> LastAsync(Guid sourceId, CancellationToken ct = default) =>
        (await QueryAsync("source_id = @source", cmd => cmd.Parameters.AddWithValue("source", sourceId), 1, ct)).FirstOrDefault();

    /// <summary>The most recent runs, newest first; of one source when <paramref name="sourceId"/> is given.</summary>
    public Task<IReadOnlyList<IngestRunRecord>> ListAsync(Guid? sourceId = null, int limit = 20, CancellationToken ct = default) =>
        sourceId is { } id
            ? QueryAsync("source_id = @source", cmd => cmd.Parameters.AddWithValue("source", id), limit, ct)
            : QueryAsync("TRUE", _ => { }, limit, ct);

    private async Task<IReadOnlyList<IngestRunRecord>> QueryAsync(
        string where, Action<NpgsqlCommand> bind, int limit, CancellationToken ct)
    {
        await using var cmd = db.DataSource.CreateCommand($"""
            SELECT {Columns} FROM prem_config.ingest_run
            WHERE tenant_id = @tenant AND {where}
            ORDER BY started_at DESC, id
            LIMIT @limit
            """);
        cmd.Parameters.AddWithValue("tenant", tenantId);
        cmd.Parameters.AddWithValue("limit", limit);
        bind(cmd);

        var runs = new List<IngestRunRecord>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) runs.Add(Read(reader));
        return runs;
    }

    private static IngestRunRecord Read(NpgsqlDataReader r)
    {
        var outcome = ParseOutcome(r.GetString(9));
        IngestSummary? summary = null;
        if (!r.IsDBNull(11))
        {
            summary = new IngestSummary(
                Scanned: r.GetInt32(11),
                Ingested: r.GetInt32(12),
                Unchanged: r.GetInt32(13),
                ChunksEmbedded: r.GetInt32(14),
                OrphansRemoved: r.GetInt32(15),
                DeniedToEveryone: r.GetInt32(16),
                Unreadable: r.GetInt32(17),
                ReconciliationSkipped: outcome == IngestRunOutcome.ReconciliationRefused,
                Failures: r.IsDBNull(19)
                    ? []
                    : JsonSerializer.Deserialize<List<UnreadablePath>>(r.GetString(19))!.Select(u => new SourceFailure(u.Path, u.Reason)).ToArray(),
                SkippedByExtension: r.IsDBNull(18)
                    ? new Dictionary<string, int>()
                    : JsonSerializer.Deserialize<Dictionary<string, int>>(r.GetString(18))!,
                UndeclaredAuthorship: r.IsDBNull(20) ? 0 : r.GetInt32(20),
                UnmappedPrincipals: r.IsDBNull(25) ? 0 : r.GetInt32(25),
                KeptWithoutReader: r.IsDBNull(26) ? 0 : r.GetInt32(26),
                RemovedNowSkipped: r.IsDBNull(27) ? 0 : r.GetInt32(27));
        }

        var issues = r.IsDBNull(23)
            ? []
            : JsonSerializer.Deserialize<List<ConformanceIssue>>(r.GetString(23))!
                .Select(i => new OkfConformanceIssue(
                    i.Path,
                    Enum.TryParse<OkfConformanceProblem>(i.Problem, out var problem) ? problem : OkfConformanceProblem.MalformedField,
                    i.Detail))
                .ToArray();

        return new IngestRunRecord(
            Id: r.GetGuid(0),
            SourceId: r.IsDBNull(1) ? null : r.GetGuid(1),
            Connector: r.GetString(2),
            Folder: r.GetString(3),
            PathPrefix: r.GetString(4),
            OkfBundle: r.GetBoolean(5),
            UndeclaredIsMachine: r.GetBoolean(6),
            Chunker: r.GetString(24),
            StartedAt: r.GetFieldValue<DateTimeOffset>(7),
            FinishedAt: r.IsDBNull(8) ? null : r.GetFieldValue<DateTimeOffset>(8),
            Outcome: outcome,
            Error: r.IsDBNull(10) ? null : r.GetString(10),
            Summary: summary,
            OkfVersion: r.IsDBNull(21) ? null : r.GetString(21),
            OkfVersionKnown: r.IsDBNull(22) ? null : r.GetBoolean(22),
            ConformanceIssues: issues);
    }

    private static string OutcomeText(IngestRunOutcome outcome) => outcome switch
    {
        IngestRunOutcome.Running => "running",
        IngestRunOutcome.Completed => "completed",
        IngestRunOutcome.ReconciliationRefused => "reconciliation_refused",
        IngestRunOutcome.Failed => "failed",
        _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, null),
    };

    private static IngestRunOutcome ParseOutcome(string text) => text switch
    {
        "completed" => IngestRunOutcome.Completed,
        "reconciliation_refused" => IngestRunOutcome.ReconciliationRefused,
        "failed" => IngestRunOutcome.Failed,
        _ => IngestRunOutcome.Running,
    };

    private sealed record UnreadablePath(
        [property: JsonPropertyName("path")] string Path,
        [property: JsonPropertyName("reason")] string Reason);

    private sealed record ConformanceIssue(
        [property: JsonPropertyName("path")] string Path,
        [property: JsonPropertyName("problem")] string Problem,
        [property: JsonPropertyName("detail")] string? Detail);
}

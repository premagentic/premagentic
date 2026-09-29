using Npgsql;
using NpgsqlTypes;
using Premagentic.Core.Identity;
using Premagentic.Core.Storage;

namespace Premagentic.Core.Audit;

/// <summary>How the usage totals are grouped in time.</summary>
public enum UsagePeriod { Day, Week, Month }

/// <summary>
/// The span of time a usage query reads, from <see cref="From"/> up to but
/// not including <see cref="To"/>. <see cref="To"/> must be after
/// <see cref="From"/> and at most 366 days after it, so a page cannot scan
/// years of the audit trail by accident.
/// </summary>
/// <exception cref="ArgumentException">The window is empty, backwards, or longer than 366 days.</exception>
public sealed record UsageWindow(DateTimeOffset From, DateTimeOffset To)
{
    /// <summary>The longest window a usage query reads.</summary>
    public const int MaximumDays = 366;

    public DateTimeOffset To { get; init; } = Check(From, To);

    /// <summary>Checks a window built with <c>with</c>, which skips the check at construction.</summary>
    internal static void Require(UsageWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);
        Check(window.From, window.To);
    }

    private static DateTimeOffset Check(DateTimeOffset from, DateTimeOffset to)
    {
        if (to <= from)
            throw new ArgumentException("A usage window ends after it starts.");
        if (to - from > TimeSpan.FromDays(MaximumDays))
            throw new ArgumentException(
                $"A usage window is at most {MaximumDays} days, and this one is {(to - from).TotalDays:0.#}. Choose a shorter span.");
        return to;
    }
}

/// <summary>The counts for one period.</summary>
/// <param name="Questions">Searches asked. A section fetched by its path is not a question.</param>
/// <param name="People">People who asked for themselves, not through an agent.</param>
/// <param name="Agents">Agents that asked.</param>
/// <param name="NoPassage">Questions that returned no passage.</param>
/// <param name="PassagesToHostedModels">
/// Passages served to agents whose model runs outside the network, by search
/// or by section. An agent registered as local is not counted, whatever model
/// it uses.
/// </param>
public sealed record UsageTotals(int Questions, int People, int Agents, int NoPassage, int PassagesToHostedModels);

/// <summary>One person's or one agent's use in the window.</summary>
/// <param name="Kind"><c>person</c> or <c>agent</c>.</param>
/// <param name="LastActive">The last read of any kind in the window.</param>
/// <param name="ModelLocation">Where an agent's model runs now; null for a person.</param>
/// <param name="Removed">True for an agent that was removed since; its use stays, under its name.</param>
public sealed record UsageByCaller(string Kind, Guid Id, string Name, int Questions, int NoPassage,
    DateTimeOffset LastActive, string? ModelLocation, bool Removed = false);

/// <summary>A document and how many times a passage of it was served.</summary>
/// <param name="Title">The document's title as indexed now, or null when it has none or is no longer indexed.</param>
public sealed record ServedDocument(string Path, string? Title, int Times);

/// <summary>A question that returned no passage: a gap in the content.</summary>
public sealed record ContentGap(string Query, int Times, DateTimeOffset LastAsked);

/// <summary>
/// The manager's view of the audit trail: how much the deployment is used, by
/// whom, what it served, what it could not answer, and what went to hosted-model agents.
/// Reads only, and only inside a bounded window.
/// <para>
/// With <c>hostedOnly</c>, every query reads only the reads whose model ran
/// outside the network, as the audit row recorded it at the moment of the
/// read, so an agent moved to a local model later does not rewrite what left.
/// </para>
/// </summary>
public sealed class UsageQueries
{
    /// <summary>The most rows a top-N query returns.</summary>
    public const int MaximumTop = 500;

    private readonly PremagenticDatabase _db;
    private readonly Guid _tenantId;

    public UsageQueries(PremagenticDatabase db, Guid tenantId)
    {
        _db = db;
        _tenantId = tenantId;
    }

    // The rows a window covers, narrowed to hosted reads when asked. Every
    // query starts from this, so the bound and the filter are never left out.
    private const string InWindow = """
        e.tenant_id = @tenant AND e.created_at >= @from AND e.created_at < @to
          AND (NOT @hostedOnly OR e.model_location = 'hosted')
        """;

    /// <summary>
    /// The totals for each period that starts inside the window, oldest first,
    /// with a row for every period, including one with nothing in it. Periods
    /// are cut in UTC, and a week starts on Monday.
    /// </summary>
    /// <exception cref="ArgumentException">The window is empty, backwards, or longer than 366 days.</exception>
    public async Task<IReadOnlyList<(DateTimeOffset PeriodStart, UsageTotals Totals)>> TotalsAsync(
        UsageWindow window, UsagePeriod period, bool hostedOnly, CancellationToken ct)
    {
        UsageWindow.Require(window);
        var unit = period switch
        {
            UsagePeriod.Day => "day",
            UsagePeriod.Week => "week",
            UsagePeriod.Month => "month",
            _ => throw new ArgumentOutOfRangeException(nameof(period), period, "Unknown usage period."),
        };

        await using var cmd = Command(window, hostedOnly, $"""
            WITH periods AS (
                SELECT p AS start, p + interval '1 {unit}' AS stop
                FROM generate_series(
                    date_trunc('{unit}', @from AT TIME ZONE 'UTC'),
                    (@to AT TIME ZONE 'UTC') - interval '1 microsecond',
                    interval '1 {unit}') AS p),
            events AS (
                SELECT date_trunc('{unit}', e.created_at AT TIME ZONE 'UTC') AS start, e.kind, e.caller_user_id,
                       e.caller_agent_id, jsonb_array_length(e.passages) AS served, e.model_location
                FROM prem_config.retrieval_event e
                WHERE {InWindow})
            SELECT p.start,
                   count(e.kind) FILTER (WHERE e.kind = 'search'),
                   count(DISTINCT e.caller_user_id) FILTER (WHERE e.caller_agent_id IS NULL),
                   count(DISTINCT e.caller_agent_id),
                   count(e.kind) FILTER (WHERE e.kind = 'search' AND e.served = 0),
                   coalesce(sum(e.served) FILTER (WHERE e.model_location = 'hosted'), 0)
            FROM periods p
            LEFT JOIN events e ON e.start = p.start
            GROUP BY p.start
            ORDER BY p.start
            """);

        var rows = new List<(DateTimeOffset, UsageTotals)>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            rows.Add((
                new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(0), DateTimeKind.Utc)),
                new UsageTotals(Int(reader, 1), Int(reader, 2), Int(reader, 3), Int(reader, 4), Int(reader, 5))));
        return rows;
    }

    /// <summary>Every person and every agent that read anything in the window, most questions first.</summary>
    /// <exception cref="ArgumentException">The window is empty, backwards, or longer than 366 days.</exception>
    public async Task<IReadOnlyList<UsageByCaller>> ByCallerAsync(UsageWindow window, bool hostedOnly, CancellationToken ct)
    {
        UsageWindow.Require(window);
        await using var cmd = Command(window, hostedOnly, $"""
            WITH callers AS (
                SELECT CASE WHEN e.caller_agent_id IS NULL THEN 'person' ELSE 'agent' END AS kind,
                       coalesce(e.caller_agent_id, e.caller_user_id) AS id,
                       count(*) FILTER (WHERE e.kind = 'search') AS questions,
                       count(*) FILTER (WHERE e.kind = 'search' AND jsonb_array_length(e.passages) = 0) AS no_passage,
                       max(e.created_at) AS last_active
                FROM prem_config.retrieval_event e
                WHERE {InWindow} AND (e.caller_agent_id IS NOT NULL OR e.caller_user_id IS NOT NULL)
                GROUP BY 1, 2)
            SELECT c.kind, c.id, coalesce(a.name, u.sign_in_name, c.id::text), c.questions, c.no_passage, c.last_active,
                   a.model_location, a.deleted_at IS NOT NULL
            FROM callers c
            LEFT JOIN prem_config.agent a ON c.kind = 'agent' AND a.tenant_id = @tenant AND a.id = c.id
            LEFT JOIN prem_config.app_user u ON c.kind = 'person' AND u.tenant_id = @tenant AND u.id = c.id
            ORDER BY c.questions DESC, c.last_active DESC, c.id
            """);

        var rows = new List<UsageByCaller>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            rows.Add(new UsageByCaller(
                reader.GetString(0), reader.GetGuid(1), reader.GetString(2), Int(reader, 3), Int(reader, 4),
                reader.GetFieldValue<DateTimeOffset>(5), reader.IsDBNull(6) ? null : reader.GetString(6),
                !reader.IsDBNull(7) && reader.GetBoolean(7)));
        return rows;
    }

    /// <summary>The documents a passage was most often served from in the window, most first.</summary>
    /// <exception cref="ArgumentException">The window is bad, or <paramref name="top"/> is not from 1 to 500.</exception>
    public async Task<IReadOnlyList<ServedDocument>> MostServedAsync(UsageWindow window, bool hostedOnly, int top, CancellationToken ct)
    {
        UsageWindow.Require(window);
        RequireTop(top);
        await using var cmd = Command(window, hostedOnly, $"""
            WITH served AS (
                SELECT p ->> 'path' AS path, count(*) AS times
                FROM prem_config.retrieval_event e, jsonb_array_elements(e.passages) p
                WHERE {InWindow} AND p ->> 'path' IS NOT NULL
                GROUP BY 1
                ORDER BY times DESC, path
                LIMIT @top)
            SELECT s.path, d.title, s.times
            FROM served s
            LEFT JOIN prem_index.document d ON d.tenant_id = @tenant AND d.path = s.path
            ORDER BY s.times DESC, s.path
            """);
        cmd.Parameters.AddWithValue("top", top);

        var rows = new List<ServedDocument>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            rows.Add(new ServedDocument(reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1), Int(reader, 2)));
        return rows;
    }

    /// <summary>
    /// The questions that returned no passage in the window, most asked first:
    /// the content the deployment does not have, or has where the asker cannot
    /// reach it. Two questions are the same when their text is, ignoring case
    /// and the space around them.
    /// </summary>
    /// <exception cref="ArgumentException">The window is bad, or <paramref name="top"/> is not from 1 to 500.</exception>
    public async Task<IReadOnlyList<ContentGap>> GapsAsync(UsageWindow window, bool hostedOnly, int top, CancellationToken ct)
    {
        UsageWindow.Require(window);
        RequireTop(top);
        await using var cmd = Command(window, hostedOnly, $"""
            SELECT min(btrim(e.query)), count(*), max(e.created_at)
            FROM prem_config.retrieval_event e
            WHERE {InWindow} AND e.kind = 'search' AND jsonb_array_length(e.passages) = 0
            GROUP BY lower(btrim(e.query))
            ORDER BY count(*) DESC, max(e.created_at) DESC, lower(btrim(e.query))
            LIMIT @top
            """);
        cmd.Parameters.AddWithValue("top", top);

        var rows = new List<ContentGap>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            rows.Add(new ContentGap(reader.GetString(0), Int(reader, 1), reader.GetFieldValue<DateTimeOffset>(2)));
        return rows;
    }

    private NpgsqlCommand Command(UsageWindow window, bool hostedOnly, string sql)
    {
        var cmd = _db.DataSource.CreateCommand(sql);
        cmd.Parameters.AddWithValue("tenant", _tenantId);
        cmd.Parameters.Add(new NpgsqlParameter("from", NpgsqlDbType.TimestampTz) { Value = window.From.ToUniversalTime() });
        cmd.Parameters.Add(new NpgsqlParameter("to", NpgsqlDbType.TimestampTz) { Value = window.To.ToUniversalTime() });
        cmd.Parameters.AddWithValue("hostedOnly", hostedOnly);
        return cmd;
    }

    private static void RequireTop(int top)
    {
        if (top is < 1 or > MaximumTop)
            throw new ArgumentException($"Ask for 1 to {MaximumTop} rows, not {top}.", nameof(top));
    }

    private static int Int(NpgsqlDataReader reader, int ordinal) => checked((int)reader.GetInt64(ordinal));
}

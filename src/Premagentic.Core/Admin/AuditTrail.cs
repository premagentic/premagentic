using System.Text.Json;
using Premagentic.Core.Identity;
using Premagentic.Core.Storage;
using Npgsql;
using NpgsqlTypes;

namespace Premagentic.Core.Admin;

/// <summary>One passage an answer served, as the audit trail keeps it.</summary>
public sealed record AuditedPassage(string Path, string HeadingPath, string ContentHash);

/// <summary>One question in the audit trail: who asked, when, under which policy, and what came back.</summary>
/// <param name="Kind"><c>search</c>, or <c>section</c> for a fetch of one document by its path.</param>
/// <param name="Query">The query, or for a section fetch the path asked for.</param>
/// <param name="TrustMinimumTier">Null on rows written before the policy was recorded.</param>
/// <param name="ModelLocation">
/// Where the model that received this answer ran, as it was at the moment of
/// the read: <c>hosted</c> means these passages went to an agent registered as
/// using a model outside the network. Null on rows
/// written before the location was recorded, and on a read whose caller the
/// host resolved itself.
/// </param>
/// <param name="AgentName">The agent's name as it is stored now, removed or not; null for a question no agent asked.</param>
/// <param name="AgentRemoved">True when the agent that asked was removed since; its questions stay, under its name.</param>
public sealed record AuditedQuestion(
    Guid Id,
    DateTimeOffset At,
    string Kind,
    string Query,
    string? Heading,
    string AccessLabel,
    Guid? UserId,
    Guid? AgentId,
    bool IncludeHistorical,
    short? TrustMinimumTier,
    bool? IncludeStale,
    DateTimeOffset? PolicyAt,
    long ElapsedMs,
    IReadOnlyList<AuditedPassage> Passages,
    string? ModelLocation,
    string? AgentName = null,
    bool AgentRemoved = false)
{
    /// <summary>True when this answer went to a model outside the network.</summary>
    public bool LeftTheNetwork => ModelLocation == ModelLocations.Hosted;
}

/// <summary>
/// Reads the audit trail, <c>prem_config.retrieval_event</c>, newest first, a page
/// at a time. Read only: the trail is written by search and section fetch.
/// </summary>
public sealed class AuditTrail(PremagenticDatabase db, Guid tenantId)
{
    // The event's columns through the alias e, then the asking agent's name and
    // whether it was removed, through a left join that finds a removed agent too.
    private const string Columns = """
        e.id, e.created_at, e.kind, e.query, e.requested_heading, e.access_label, e.caller_user_id, e.caller_agent_id,
        e.include_historical, e.trust_minimum_tier, e.include_stale, e.policy_at, e.elapsed_ms, e.passages::text,
        e.model_location, a.name, a.deleted_at IS NOT NULL
        """;

    /// <summary>
    /// Up to <paramref name="limit"/> questions older than <paramref name="before"/>
    /// (or the newest, when null), newest first; of one agent when
    /// <paramref name="agentId"/> is given.
    /// </summary>
    /// <param name="leftTheNetwork">
    /// True for the reads served to a caller whose model runs outside the
    /// network, false for the rest, null for both. Filtered in the query rather
    /// than after it, so a page of results is a page of matches.
    /// </param>
    public async Task<IReadOnlyList<AuditedQuestion>> PageAsync(
        int limit, AuditCursor? before = null, Guid? agentId = null, bool? leftTheNetwork = null,
        CancellationToken ct = default)
    {
        await using var cmd = db.DataSource.CreateCommand($"""
            SELECT {Columns} FROM prem_config.retrieval_event e
            LEFT JOIN prem_config.agent a ON a.tenant_id = e.tenant_id AND a.id = e.caller_agent_id
            WHERE e.tenant_id = @tenant
              AND (@agent IS NULL OR e.caller_agent_id = @agent)
              AND (@left IS NULL OR (e.model_location IS NOT DISTINCT FROM @hosted) = @left)
              AND (@beforeAt IS NULL OR (e.created_at, e.id) < (@beforeAt, @beforeId))
            ORDER BY e.created_at DESC, e.id DESC
            LIMIT @limit
            """);
        cmd.Parameters.AddWithValue("tenant", tenantId);
        cmd.Parameters.Add(new NpgsqlParameter("agent", NpgsqlDbType.Uuid) { Value = (object?)agentId ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("left", NpgsqlDbType.Boolean) { Value = (object?)leftTheNetwork ?? DBNull.Value });
        cmd.Parameters.AddWithValue("hosted", Identity.ModelLocations.Hosted);
        cmd.Parameters.Add(new NpgsqlParameter("beforeAt", NpgsqlDbType.TimestampTz) { Value = (object?)before?.At ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("beforeId", NpgsqlDbType.Uuid) { Value = (object?)before?.Id ?? DBNull.Value });
        cmd.Parameters.AddWithValue("limit", limit);

        var page = new List<AuditedQuestion>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) page.Add(Read(reader));
        return page;
    }

    /// <summary>Every row, oldest first, one JSON object per line, as the database holds it.</summary>
    public async IAsyncEnumerable<string> JsonLinesAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        await using var cmd = db.DataSource.CreateCommand(
            "SELECT row_to_json(e)::text FROM prem_config.retrieval_event e WHERE e.tenant_id = @tenant ORDER BY e.created_at, e.id");
        cmd.Parameters.AddWithValue("tenant", tenantId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) yield return reader.GetString(0);
    }

    /// <summary>
    /// The rows inside <paramref name="window"/>, oldest first, one JSON object
    /// per line, as the database holds them: what an export of a span writes.
    /// No page or command of the core calls it: an extension's export command
    /// does, and this repository's tests hold it to its window and filter.
    /// With <paramref name="hostedOnly"/>, only the reads whose model ran
    /// outside the network.
    /// </summary>
    /// <exception cref="ArgumentException">The window is empty, backwards, or longer than 366 days.</exception>
    public async IAsyncEnumerable<string> JsonLinesAsync(
        Audit.UsageWindow window, bool hostedOnly,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        Audit.UsageWindow.Require(window);
        await using var cmd = db.DataSource.CreateCommand("""
            SELECT row_to_json(e)::text FROM prem_config.retrieval_event e
            WHERE e.tenant_id = @tenant AND e.created_at >= @from AND e.created_at < @to
              AND (NOT @hostedOnly OR e.model_location = 'hosted')
            ORDER BY e.created_at, e.id
            """);
        cmd.Parameters.AddWithValue("tenant", tenantId);
        cmd.Parameters.Add(new NpgsqlParameter("from", NpgsqlDbType.TimestampTz) { Value = window.From.ToUniversalTime() });
        cmd.Parameters.Add(new NpgsqlParameter("to", NpgsqlDbType.TimestampTz) { Value = window.To.ToUniversalTime() });
        cmd.Parameters.AddWithValue("hostedOnly", hostedOnly);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) yield return reader.GetString(0);
    }

    private static AuditedQuestion Read(NpgsqlDataReader r)
    {
        using var passages = JsonDocument.Parse(r.GetString(13));
        return new AuditedQuestion(
            Id: r.GetGuid(0),
            At: r.GetFieldValue<DateTimeOffset>(1),
            Kind: r.GetString(2),
            Query: r.GetString(3),
            Heading: r.IsDBNull(4) ? null : r.GetString(4),
            AccessLabel: r.GetString(5),
            UserId: r.IsDBNull(6) ? null : r.GetGuid(6),
            AgentId: r.IsDBNull(7) ? null : r.GetGuid(7),
            IncludeHistorical: r.GetBoolean(8),
            TrustMinimumTier: r.IsDBNull(9) ? null : r.GetInt16(9),
            IncludeStale: r.IsDBNull(10) ? null : r.GetBoolean(10),
            PolicyAt: r.IsDBNull(11) ? null : r.GetFieldValue<DateTimeOffset>(11),
            ElapsedMs: r.GetInt64(12),
            Passages: passages.RootElement.EnumerateArray().Select(p => new AuditedPassage(
                Text(p, "path"), Text(p, "heading_path"), Text(p, "content_hash"))).ToArray(),
            ModelLocation: r.IsDBNull(14) ? null : r.GetString(14),
            AgentName: r.IsDBNull(15) ? null : r.GetString(15),
            AgentRemoved: !r.IsDBNull(16) && r.GetBoolean(16));
    }

    private static string Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()! : "";
}

/// <summary>Where the next page of the audit trail starts: after this row, going back in time.</summary>
public sealed record AuditCursor(DateTimeOffset At, Guid Id)
{
    /// <summary>The cursor as it travels in a link: the instant in ticks and the row id.</summary>
    public override string ToString() => $"{At.UtcTicks}_{Id:N}";

    public static AuditCursor? Parse(string? text)
    {
        var parts = text?.Split('_');
        return parts is [var ticks, var id]
               && long.TryParse(ticks, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var t)
               && t >= DateTimeOffset.MinValue.UtcTicks && t <= DateTimeOffset.MaxValue.UtcTicks
               && Guid.TryParseExact(id, "N", out var g)
            ? new AuditCursor(new DateTimeOffset(t, TimeSpan.Zero), g)
            : null;
    }
}

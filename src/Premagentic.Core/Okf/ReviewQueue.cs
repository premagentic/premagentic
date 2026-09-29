using Premagentic.Core.Storage;
using Npgsql;
using NpgsqlTypes;

namespace Premagentic.Core.Okf;

/// <summary>One document a person could review, and where its file is.</summary>
/// <param name="Source">The registered source that reads it, or null when it came from a folder given by hand.</param>
/// <param name="File">The file on the server, or null when no registered source or recorded run reads its prefix.</param>
public sealed record ReviewItem(
    string Path,
    string? Title,
    OkfTrustTier TrustTier,
    OkfAuthorship Authorship,
    DateTimeOffset? StaleAfter,
    string? ConceptId,
    DateTimeOffset UpdatedAt,
    string? Source,
    string? File);

/// <summary>A folder documents are read from: a registered source, or the last run of a folder given by hand.</summary>
/// <param name="Source">The registered source's name, or null for a folder given by hand.</param>
public sealed record ReviewPlace(string? Source, string PathPrefix, string Folder);

/// <summary>
/// The review queue, read only: the documents below human-reviewed that a
/// person's sign-off would change, and where each file is, so a person can
/// review it and sign it off in the file itself. The next ingest reads the
/// sign-off. Nothing here writes, to the index or to a file.
/// <para>
/// A document is in the queue when its trust tier is below human-reviewed and
/// it is machine-written (the trust gate holds it back from agents at the
/// default setting) or it is an OKF concept (a concept nobody has verified,
/// whoever wrote it). An ordinary file outside a bundle carries no trust
/// values to sign off, so it is not in the queue.
/// </para>
/// </summary>
public sealed class ReviewQueue(PremagenticDatabase db, Guid tenantId)
{
    /// <summary>The queue's condition, over the document alias <c>d</c>.</summary>
    public const string Condition = "d.trust_tier < 2 AND (d.authorship = 2 OR d.okf_concept_id IS NOT NULL)";

    /// <summary>How many documents are in the queue, under <paramref name="pathPrefix"/> and at <paramref name="tier"/> when given.</summary>
    public async Task<long> CountAsync(string? pathPrefix = null, OkfTrustTier? tier = null, CancellationToken ct = default)
    {
        await using var cmd = db.DataSource.CreateCommand($"SELECT count(*) FROM prem_index.document d WHERE {Where}");
        Bind(cmd, pathPrefix, tier);
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    /// <summary>
    /// The queue by path, under <paramref name="pathPrefix"/> and at
    /// <paramref name="tier"/> when given, each placed by
    /// <paramref name="places"/>.
    /// </summary>
    public async Task<IReadOnlyList<ReviewItem>> ListAsync(
        IReadOnlyList<ReviewPlace> places, string? pathPrefix, OkfTrustTier? tier, int limit, int offset,
        CancellationToken ct = default)
    {
        await using var cmd = db.DataSource.CreateCommand($"""
            SELECT d.path, d.title, d.trust_tier, d.authorship, d.stale_after, d.okf_concept_id, d.updated_at
            FROM prem_index.document d
            WHERE {Where}
            ORDER BY d.path
            LIMIT @limit OFFSET @offset
            """);
        Bind(cmd, pathPrefix, tier);
        cmd.Parameters.AddWithValue("limit", limit);
        cmd.Parameters.AddWithValue("offset", offset);

        var items = new List<ReviewItem>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var path = r.GetString(0);
            var place = PlaceOf(path, places);
            items.Add(new ReviewItem(
                Path: path,
                Title: r.IsDBNull(1) ? null : r.GetString(1),
                TrustTier: (OkfTrustTier)r.GetInt16(2),
                Authorship: (OkfAuthorship)r.GetInt16(3),
                StaleAfter: r.IsDBNull(4) ? null : r.GetFieldValue<DateTimeOffset>(4),
                ConceptId: r.IsDBNull(5) ? null : r.GetString(5),
                UpdatedAt: r.GetFieldValue<DateTimeOffset>(6),
                Source: place?.Source,
                File: place is null ? null : FileOf(path, place)));
        }
        return items;
    }

    /// <summary>
    /// Where documents are read from: every registered source, then, for each
    /// prefix no registered source reads, the folder its last run by hand read.
    /// </summary>
    public async Task<IReadOnlyList<ReviewPlace>> PlacesAsync(CancellationToken ct = default)
    {
        await using var cmd = db.DataSource.CreateCommand("""
            SELECT name, path_prefix, folder FROM prem_config.source
            WHERE tenant_id = @tenant AND deleted_at IS NULL
            UNION ALL
            SELECT * FROM (
                SELECT DISTINCT ON (r.path_prefix) NULL::text, r.path_prefix, r.folder
                FROM prem_config.ingest_run r
                WHERE r.tenant_id = @tenant AND r.source_id IS NULL
                  AND NOT EXISTS (SELECT 1 FROM prem_config.source s
                                  WHERE s.tenant_id = r.tenant_id AND s.deleted_at IS NULL AND s.path_prefix = r.path_prefix)
                ORDER BY r.path_prefix, r.started_at DESC) by_hand
            """);
        cmd.Parameters.AddWithValue("tenant", tenantId);
        var places = new List<ReviewPlace>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            places.Add(new ReviewPlace(r.IsDBNull(0) ? null : r.GetString(0), r.GetString(1), r.GetString(2)));
        return places;
    }

    /// <summary>The place whose prefix holds <paramref name="path"/>, the longest such prefix when several do.</summary>
    public static ReviewPlace? PlaceOf(string path, IReadOnlyList<ReviewPlace> places) =>
        places.Where(p => p.PathPrefix.Length == 0 || path.StartsWith(p.PathPrefix + "/", StringComparison.Ordinal))
            .OrderByDescending(p => p.PathPrefix.Length)
            .FirstOrDefault();

    /// <summary>The file on the server that <paramref name="path"/> was read from under <paramref name="place"/>.</summary>
    public static string FileOf(string path, ReviewPlace place)
    {
        var relative = place.PathPrefix.Length == 0 ? path : path[(place.PathPrefix.Length + 1)..];
        return System.IO.Path.Combine([place.Folder, .. relative.Split('/')]);
    }

    private const string Where = $"""
        d.tenant_id = @tenant
          AND {Condition}
          AND (@prefix IS NULL OR starts_with(d.path, @prefix))
          AND (@tier IS NULL OR d.trust_tier = @tier)
        """;

    private void Bind(NpgsqlCommand cmd, string? pathPrefix, OkfTrustTier? tier)
    {
        cmd.Parameters.AddWithValue("tenant", tenantId);
        cmd.Parameters.Add(new NpgsqlParameter("prefix", NpgsqlDbType.Text) { Value = (object?)pathPrefix ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("tier", NpgsqlDbType.Smallint) { Value = tier is { } t ? (object)(short)t : DBNull.Value });
    }
}

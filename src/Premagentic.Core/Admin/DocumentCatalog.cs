using Premagentic.Core.Okf;
using Premagentic.Core.Storage;
using Npgsql;
using NpgsqlTypes;

namespace Premagentic.Core.Admin;

/// <summary>
/// What the index holds about one document, for an administrator: never its
/// text, which reaches people only through search, under their own rights.
/// </summary>
/// <param name="AclText">The access list the gate evaluates, in its canonical text, or null when none is set.</param>
public sealed record CatalogDocument(
    Guid Id,
    string Path,
    string? Title,
    string? SourceName,
    string ContentHash,
    string LifecycleStatus,
    OkfTrustTier TrustTier,
    OkfAuthorship Authorship,
    DateTimeOffset? StaleAfter,
    string? ConceptId,
    long? AclSetId,
    string? AclText,
    bool AclFromRule,
    int Chunks,
    DateTimeOffset UpdatedAt);

/// <summary>The documents in the index, filtered and paged, for the portal's documents page.</summary>
public sealed class DocumentCatalog(PremagenticDatabase db, Guid tenantId)
{
    private const string Select = """
        SELECT d.id, d.path, d.title, d.source_name, d.content_hash, d.lifecycle_status, d.trust_tier, d.authorship,
               d.stale_after, d.okf_concept_id, d.acl_set_id, s.canonical_text, d.acl_from_rule,
               (SELECT count(*) FROM prem_index.chunk c WHERE c.document_id = d.id)::int, d.updated_at
        FROM prem_index.document d
        LEFT JOIN prem_config.acl_set s ON s.tenant_id = d.tenant_id AND s.id = d.acl_set_id
        """;

    /// <summary>
    /// Documents whose path starts with <paramref name="pathPrefix"/> and
    /// contains <paramref name="pathContains"/> (either may be null), by path.
    /// </summary>
    public async Task<IReadOnlyList<CatalogDocument>> ListAsync(
        string? pathPrefix, string? pathContains, int limit, int offset, CancellationToken ct = default)
    {
        await using var cmd = db.DataSource.CreateCommand($"""
            {Select}
            WHERE d.tenant_id = @tenant
              AND (@prefix IS NULL OR starts_with(d.path, @prefix))
              AND (@contains IS NULL OR strpos(lower(d.path), lower(@contains)) > 0)
            ORDER BY d.path
            LIMIT @limit OFFSET @offset
            """);
        cmd.Parameters.AddWithValue("tenant", tenantId);
        cmd.Parameters.Add(new NpgsqlParameter("prefix", NpgsqlDbType.Text) { Value = (object?)pathPrefix ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("contains", NpgsqlDbType.Text) { Value = (object?)pathContains ?? DBNull.Value });
        cmd.Parameters.AddWithValue("limit", limit);
        cmd.Parameters.AddWithValue("offset", offset);
        return await ReadAllAsync(cmd, ct);
    }

    public async Task<CatalogDocument?> FindAsync(string path, CancellationToken ct = default)
    {
        await using var cmd = db.DataSource.CreateCommand($"{Select} WHERE d.tenant_id = @tenant AND d.path = @path");
        cmd.Parameters.AddWithValue("tenant", tenantId);
        cmd.Parameters.AddWithValue("path", path);
        return (await ReadAllAsync(cmd, ct)).FirstOrDefault();
    }

    private static async Task<IReadOnlyList<CatalogDocument>> ReadAllAsync(NpgsqlCommand cmd, CancellationToken ct)
    {
        var documents = new List<CatalogDocument>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            documents.Add(new CatalogDocument(
                Id: r.GetGuid(0),
                Path: r.GetString(1),
                Title: r.IsDBNull(2) ? null : r.GetString(2),
                SourceName: r.IsDBNull(3) ? null : r.GetString(3),
                ContentHash: r.GetString(4),
                LifecycleStatus: r.GetString(5),
                TrustTier: (OkfTrustTier)r.GetInt16(6),
                Authorship: (OkfAuthorship)r.GetInt16(7),
                StaleAfter: r.IsDBNull(8) ? null : r.GetFieldValue<DateTimeOffset>(8),
                ConceptId: r.IsDBNull(9) ? null : r.GetString(9),
                AclSetId: r.IsDBNull(10) ? null : r.GetInt64(10),
                AclText: r.IsDBNull(11) ? null : r.GetString(11),
                AclFromRule: r.GetBoolean(12),
                Chunks: r.GetInt32(13),
                UpdatedAt: r.GetFieldValue<DateTimeOffset>(14)));
        return documents;
    }
}

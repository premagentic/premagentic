using System.Diagnostics;
using Premagentic.Core.Okf;
using Premagentic.Core.Retrieval.Gates;
using Premagentic.Core.Security;
using Premagentic.Core.Storage;
using Npgsql;
using NpgsqlTypes;

namespace Premagentic.Core.Retrieval;

public sealed record SectionChunk(int Seq, string HeadingPath, string Content);

/// <param name="ConceptId">The OKF concept id, when the document came from a source read as a bundle.</param>
/// <param name="TrustTier">How far the content has been confirmed.</param>
/// <param name="Authorship">Who wrote the content.</param>
/// <param name="Stale">Past its <c>stale_after</c> at the instant of the fetch.</param>
public sealed record DocumentSectionResult(
    string Path,
    string? Title,
    string LifecycleStatus,
    bool LifecycleGated,   // exists, is non-active, and historical access was not requested
    IReadOnlyList<SectionChunk> Chunks,
    string? ConceptId = null,
    OkfTrustTier TrustTier = OkfTrustTier.Unverified,
    OkfAuthorship Authorship = OkfAuthorship.Unknown,
    bool Stale = false);

/// <summary>
/// Completes the retrieval-as-router loop: search cites "path § heading", this
/// returns that section in full without handing the caller filesystem or
/// source-system access.
/// <para>
/// It applies the same gates as search. A caller who cannot retrieve a document
/// in search must not be able to fetch it by citing its path, and an
/// unauthorized path is reported as absent rather than as forbidden, so the
/// tool cannot be used to enumerate what exists. The trust and freshness gates
/// hide the same way: a citation an agent may not see is not fetchable either.
/// </para>
/// <para>
/// Both reads run on one connection bound to the caller (<see cref="BoundRead"/>),
/// as the search role when there is one, so row-level security judges them too.
/// </para>
/// </summary>
/// <param name="searchRole">The role gated reads connect as; null reads through <paramref name="db"/>.</param>
public sealed class SectionFetcher(PremagenticDatabase db, SearchRole? searchRole = null)
{
    private static readonly GateSet Gates = GateSet.Default;

    // The lookup reports the lifecycle gate instead of hiding by it, so a caller
    // who may read a superseded document learns it exists and can ask again
    // with historical access. Every other gate hides.
    private static readonly GateSet LookupGates = Gates.Without(LifecycleGate.GateName);

    /// <summary>
    /// Fetches under the strict trust policy, the one a search gets when it
    /// names none. <see cref="GetAsync(Guid, SearchOptions, string, string?, CancellationToken)"/>
    /// takes the caller's own policy.
    /// </summary>
    public Task<DocumentSectionResult?> GetAsync(
        Guid tenantId, AccessScope scope, string path, string? heading,
        bool includeHistorical, CancellationToken ct = default) =>
        GetAsync(tenantId, new SearchOptions(scope, IncludeHistorical: includeHistorical), path, heading, ct);

    /// <summary>
    /// Fetches the chunks of a document whose heading path contains
    /// <paramref name="heading"/>, case-insensitively, in document order.
    /// Null heading returns the whole document. Returns null when the document
    /// is not in the index, or is in it but not readable under these options:
    /// scope, trust policy and freshness, judged at one instant.
    /// <para>
    /// Every fetch is written to the audit trail the way a search is, with the
    /// caller, the passages served and their content hash, and the policy. A
    /// fetch that served nothing is recorded with no passages.
    /// </para>
    /// </summary>
    /// <exception cref="QueryTooLongException">The path or the heading is longer than <see cref="QueryLimits.MaxLength"/>; nothing was read or recorded.</exception>
    public async Task<DocumentSectionResult?> GetAsync(
        Guid tenantId, SearchOptions options, string path, string? heading, CancellationToken ct = default)
    {
        if (QueryLimits.SectionRefusal(path, heading) is { } tooLong) throw new QueryTooLongException(tooLong);
        var sw = Stopwatch.StartNew();
        // Both signatures come through here, so the scope is resolved once for
        // either: the caller's current principals against the current lists.
        options = options with { Scope = await PermittedSetReader.ResolveAsync(db, tenantId, options.Scope, ct) };
        var gate = new GateContext(options);

        DocumentSectionResult? result;
        IReadOnlyList<HybridSearch.AuditedPassage> served;
        await using (var read = await BoundRead.OpenAsync(db, searchRole, tenantId, options.Scope, ct))
            (result, served) = await FetchAsync(read, tenantId, gate, path, heading, ct);
        await HybridSearch.WriteEventAsync(
            db, tenantId, HybridSearch.SectionEventKind, path, heading, gate, served,
            sw.ElapsedMilliseconds, lexicalCount: null, bestDistance: null, ct);
        return result;
    }

    private static async Task<(DocumentSectionResult? Result, IReadOnlyList<HybridSearch.AuditedPassage> Served)> FetchAsync(
        BoundRead read, Guid tenantId, GateContext gate, string path, string? heading, CancellationToken ct)
    {
        var includeHistorical = gate.IncludeHistorical;

        Guid docId;
        string? title;
        string lifecycle;
        DocumentSectionResult what;
        await using (var doc = read.Command($"""
            SELECT d.id, d.title, d.lifecycle_status,
                   d.okf_concept_id, d.trust_tier, d.authorship,
                   (d.stale_after IS NOT NULL AND d.stale_after <= @fetch_now) AS stale
            FROM prem_index.document d
            WHERE d.tenant_id = @tenant AND d.path = @path
              AND {LookupGates.Sql}
            """))
        {
            doc.Parameters.AddWithValue("tenant", tenantId);
            doc.Parameters.AddWithValue("path", path);
            doc.Parameters.Add(new NpgsqlParameter("fetch_now", NpgsqlDbType.TimestampTz) { Value = gate.Now });
            LookupGates.AddParameters(doc, gate);
            await using var reader = await doc.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) return (null, []);
            docId = reader.GetGuid(0);
            title = reader.IsDBNull(1) ? null : reader.GetString(1);
            lifecycle = reader.GetString(2);
            what = new DocumentSectionResult(path, title, lifecycle, LifecycleGated: false, [],
                ConceptId: reader.IsDBNull(3) ? null : reader.GetString(3),
                TrustTier: (OkfTrustTier)reader.GetInt16(4),
                Authorship: (OkfAuthorship)reader.GetInt16(5),
                Stale: reader.GetBoolean(6));
        }

        if (lifecycle != DocumentLifecycle.Active && !includeHistorical)
            return (what with { LifecycleGated = true }, []);

        // Every gate again on the passages themselves, so nothing leaves on the
        // strength of the lookup alone.
        await using var cmd = read.Command($"""
            SELECT c.seq, c.heading_path, c.content, d.content_hash
            FROM prem_index.chunk c
            JOIN prem_index.document d ON d.id = c.document_id
            WHERE c.document_id = @doc
              AND d.tenant_id = @tenant
              AND {Gates.Sql}
              AND (@heading IS NULL OR c.heading_path ILIKE '%' || @heading || '%' ESCAPE '\')
            ORDER BY c.seq
            """);
        cmd.Parameters.AddWithValue("doc", docId);
        cmd.Parameters.AddWithValue("tenant", tenantId);
        Gates.AddParameters(cmd, gate);
        // Explicit type: Npgsql cannot infer a DBNull parameter's type inside
        // "(@heading IS NULL OR ...)" and fails with 42P08 on the
        // whole-document path.
        cmd.Parameters.Add(new NpgsqlParameter("heading", NpgsqlDbType.Text)
        {
            Value = (object?)EscapeLike(heading) ?? DBNull.Value
        });

        var chunks = new List<SectionChunk>();
        var served = new List<HybridSearch.AuditedPassage>();
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                chunks.Add(new SectionChunk(reader.GetInt32(0), reader.GetString(1), reader.GetString(2)));
                served.Add(new HybridSearch.AuditedPassage(path, reader.GetString(1), reader.GetString(3)));
            }
        }

        return (what with { Chunks = chunks }, served);
    }

    private static string? EscapeLike(string? value) =>
        value?.Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_");
}

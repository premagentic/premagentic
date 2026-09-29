using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Premagentic.Core.Embeddings;
using Premagentic.Core.Retrieval.Gates;
using Premagentic.Core.Retrieval.Vectors;
using Premagentic.Core.Storage;
using NpgsqlTypes;

namespace Premagentic.Core.Retrieval;

/// <summary>
/// Hybrid retrieval. The gates in <see cref="GateSet"/> are applied as hard SQL
/// at every read, so they define the candidate set and can never be outweighed
/// by a relevance signal:
/// <list type="number">
///   <item><b>Access.</b> A document is eligible only if its access list is
///   one the caller may read, decided per query.</item>
///   <item><b>Lifecycle.</b> Superseded, archived, draft and expired material
///   is out unless historical access was explicitly requested.</item>
///   <item><b>Trust.</b> Machine-written content is out below the policy's
///   minimum trust tier.</item>
///   <item><b>Freshness.</b> Content past its stale date is out unless the
///   policy includes it.</item>
/// </list>
/// What survives is fused with Reciprocal Rank Fusion, weighted by document
/// class, and returned with provenance. Every query is logged with the
/// authorization label it ran under.
/// <para>
/// The vector leg holds vectors in memory, so create one instance per process
/// and share it, and call <see cref="WarmUpAsync"/> at startup.
/// </para>
/// <para>
/// Every gated read of one search runs on one connection bound to the caller
/// (<see cref="BoundRead"/>), as the search role when there is one, so
/// row-level security refuses what the gates would, a second time, in the
/// database itself.
/// </para>
/// </summary>
/// <param name="authority">Fixed weights for every search. Leave null with <paramref name="storedTuning"/>.</param>
/// <param name="tuning">Fixed tuning for every search. Leave null with <paramref name="storedTuning"/>.</param>
/// <param name="searchRole">
/// The role gated reads connect as. Null reads through <paramref name="db"/>,
/// which the policy does not bind, so only the SQL gate filters.
/// </param>
/// <param name="storedTuning">
/// Reads the tuning and the authority weights the deployment has stored, once
/// per search, as a host does. Null uses <paramref name="tuning"/> and
/// <paramref name="authority"/>, or the code defaults.
/// </param>
public sealed class HybridSearch(
    PremagenticDatabase db,
    IEmbeddingProvider embedder,
    AuthorityWeights? authority = null,
    RetrievalTuning? tuning = null,
    bool headingPrefixSpace = false,
    Security.SearchRole? searchRole = null,
    RetrievalSettingsLoader? storedTuning = null)
{
    private readonly AuthorityWeights _authority = storedTuning is not null && (authority is not null || tuning is not null)
        ? throw new ArgumentException("Pass fixed tuning or a loader for the stored tuning, not both.", nameof(storedTuning))
        : authority ?? AuthorityWeights.Flat;
    private readonly RetrievalTuning _tuning = tuning ?? RetrievalTuning.Default;
    private readonly GateSet _gates = GateSet.Default;
    private readonly InMemoryVectorIndex _vectors = new(db, searchRole: searchRole);

    /// <summary>The vector leg, for tests that must see what it scored.</summary>
    internal InMemoryVectorIndex Vectors => _vectors;

    // Queries are embedded WITHOUT the heading prefix (asymmetric enrichment);
    // the flag only selects which stored vector space to search.
    internal string EmbeddingModelName => headingPrefixSpace
        ? embedder.Name + Ingestion.IngestPipeline.HeadingPrefixSuffix
        : embedder.Name;

    /// <summary>
    /// Loads every stored vector into memory. Optional, because a search loads
    /// what it is missing on the spot, but without it the first searches after
    /// a start pay for loading the index.
    /// </summary>
    /// <returns>The number of chunk vectors held.</returns>
    public Task<int> WarmUpAsync(CancellationToken ct = default) => _vectors.WarmUpAsync(ct);

    /// <exception cref="QueryTooLongException">The query is longer than <see cref="QueryLimits.MaxLength"/>; nothing was read or recorded.</exception>
    public async Task<SearchResult> SearchAsync(
        Guid tenantId, string query, SearchOptions options, CancellationToken ct = default)
    {
        if (QueryLimits.SearchRefusal(query) is { } tooLong) throw new QueryTooLongException(tooLong);
        var sw = Stopwatch.StartNew();
        // The access lists this caller may read, decided now from the tenant's
        // current lists and the caller's current principals, so a revoked group
        // or a changed rule applies to this very query.
        options = options with { Scope = await Security.PermittedSetReader.ResolveAsync(db, tenantId, options.Scope, ct) };
        var gate = new GateContext(options);

        // The tuning as the deployment has it now, so a change applies to the next search.
        var inForce = storedTuning is null
            ? new RetrievalSettingsReading(_tuning, _authority, [])
            : await storedTuning.LoadAsync(tenantId, ct);

        var queryEmbedding = (await embedder.EmbedAsync([query], ct))[0];

        IReadOnlyList<SearchHit> hits;
        await using (var read = await BoundRead.OpenAsync(db, searchRole, tenantId, options.Scope, ct))
            hits = await RankAsync(read, tenantId, query, queryEmbedding, options, gate, inForce.Tuning, inForce.Authority, ct);
        sw.Stop();

        await LogEventAsync(tenantId, query, gate, hits, sw.ElapsedMilliseconds, ct);
        return new SearchResult(query, options.IncludeHistorical, hits, sw.ElapsedMilliseconds, inForce);
    }

    /// <summary>Both legs, fusion, and the passages that will leave, all on one bound read.</summary>
    private async Task<IReadOnlyList<SearchHit>> RankAsync(
        BoundRead read, Guid tenantId, string query, float[] queryEmbedding, SearchOptions options, GateContext gate,
        RetrievalTuning tuning, AuthorityWeights authority, CancellationToken ct)
    {
        var docClasses = new Dictionary<Guid, string?>();
        var (lexical, lexicalPrimaryCount) = await LexicalCandidatesAsync(read, tenantId, query, options, gate, docClasses, ct);
        var vector = await VectorCandidatesAsync(read, tenantId, queryEmbedding, options, gate, docClasses, ct);

        // RRF fusion over the two ranked lists.
        var fused = new Dictionary<Guid, FusedCandidate>();
        for (var i = 0; i < lexical.Count; i++)
        {
            var fallback = i >= lexicalPrimaryCount;
            var score = (fallback ? tuning.FallbackRrfWeight : 1.0) / (tuning.RrfK + i + 1);
            fused[lexical[i]] = new FusedCandidate(i + 1, fallback, null, null, score);
        }
        for (var i = 0; i < vector.Count; i++)
        {
            var (id, dist) = vector[i];
            var score = 1.0 / (tuning.RrfK + i + 1);
            fused[id] = fused.TryGetValue(id, out var existing)
                ? existing with { VecRank = i + 1, Dist = dist, Score = existing.Score + score }
                : new FusedCandidate(null, false, i + 1, dist, score);
        }

        // Document-class weighting scales the fused score. A rank signal among
        // documents the gates already declared eligible, never a substitute for
        // either gate. Flat unless a deployment measured otherwise.
        foreach (var (id, v) in fused)
            fused[id] = v with { Score = v.Score * authority.For(docClasses.GetValueOrDefault(id)) };

        var topIds = fused.OrderByDescending(kv => kv.Value.Score)
                          .Take(options.TopK)
                          .Select(kv => kv.Key)
                          .ToArray();

        return await LoadHitsAsync(read, tenantId, topIds, fused, gate, ct);
    }

    private async Task<(List<Guid> Ids, int PrimaryCount)> LexicalCandidatesAsync(
        BoundRead read, Guid tenantId, string query, SearchOptions options, GateContext gate,
        Dictionary<Guid, string?> docClasses, CancellationToken ct)
    {
        // Primary pass: websearch AND-semantics. Natural phrasings often over-
        // constrain and return nothing, so when the pass cannot fill TopK an OR
        // pass backfills the tail. AND hits keep their rank; OR hits only ever
        // extend the list, and are marked as fallback so downstream heuristics
        // do not read a degraded match as primary lexical evidence.
        var ids = await LexicalPassAsync(
            read, tenantId, "websearch_to_tsquery('english', @query)", query,
            gate, exclude: [], limit: options.CandidatePoolSize, docClasses, ct);
        var primaryCount = ids.Count;

        if (ids.Count >= options.TopK) return (ids, primaryCount);

        var orQuery = OrTsQuery(query);
        if (orQuery is null) return (ids, primaryCount);

        // Tail capped at TopK, NOT the full candidate pool: on a large corpus a
        // pool-sized OR tail hands lexical RRF mass to every chunk sharing one
        // generic query term, which collectively buries a decisively close
        // vector hit.
        var tail = await LexicalPassAsync(
            read, tenantId, "to_tsquery('english', @query)", orQuery,
            gate, exclude: ids, limit: options.TopK - ids.Count, docClasses, ct);
        ids.AddRange(tail);
        return (ids, primaryCount);
    }

    private async Task<List<Guid>> LexicalPassAsync(
        BoundRead read, Guid tenantId, string tsquery, string queryArg, GateContext gate,
        IReadOnlyCollection<Guid> exclude, int limit,
        Dictionary<Guid, string?> docClasses, CancellationToken ct)
    {
        // Ties in rank are common (a title match weighs the same in every chunk
        // of a document), and without the path and sequence after it PostgreSQL
        // returns tied rows in physical order, so the same corpus rebuilt would
        // answer the same question differently.
        //
        // The match, its ts_rank_cd rank, the gates, the exclusions and the
        // limit are all in prem_index.text_matches, whose body is generated from
        // the same gates as every other read (TextMatchFunction). It runs as the
        // owner, because under row-level security PostgreSQL will not use the
        // full-text index for a match written here, and applies the bound
        // session's lists itself; ranking inside lets PostgreSQL sort to the top
        // rows in parallel, as it did before the policies. This read adds only
        // each document's class.
        await using var cmd = read.Command($"""
            SELECT m.chunk_id, d.doc_class
            FROM {TextMatchFunction.Call(tsquery)} m
            JOIN prem_index.document d ON d.id = m.document_id
            ORDER BY m.rank DESC, m.path, m.seq
            """);
        cmd.Parameters.AddWithValue("tenant", tenantId);
        TextMatchFunction.Gates.AddParameters(cmd, gate);
        cmd.Parameters.AddWithValue("query", queryArg);
        cmd.Parameters.AddWithValue("exclude", exclude.ToArray());
        cmd.Parameters.AddWithValue("limit", limit);

        var ids = new List<Guid>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var id = reader.GetGuid(0);
            ids.Add(id);
            docClasses[id] = reader.IsDBNull(1) ? null : reader.GetString(1);
        }
        return ids;
    }

    /// <summary>
    /// The vector leg, then each winner's document class for authority
    /// weighting, read under the gates. That read also drops a winner whose
    /// document was deleted or had its audience changed since the leg's own
    /// gated read, so it cannot take a fused rank from a passage that will be
    /// served.
    /// </summary>
    private async Task<List<VectorCandidate>> VectorCandidatesAsync(
        BoundRead read, Guid tenantId, float[] queryEmbedding, SearchOptions options, GateContext gate,
        Dictionary<Guid, string?> docClasses, CancellationToken ct)
    {
        var nearest = await _vectors.SearchAsync(
            read, tenantId, EmbeddingModelName, queryEmbedding, gate, options.CandidatePoolSize, ct);
        if (nearest.Count == 0) return [];

        await using var cmd = read.Command($"""
            SELECT c.id, d.doc_class
            FROM prem_index.chunk c
            JOIN prem_index.document d ON d.id = c.document_id
            WHERE c.id = ANY(@ids)
              AND d.tenant_id = @tenant
              AND {_gates.Sql}
            """);
        cmd.Parameters.AddWithValue("ids", nearest.Select(n => n.ChunkId).ToArray());
        cmd.Parameters.AddWithValue("tenant", tenantId);
        _gates.AddParameters(cmd, gate);

        var readable = new HashSet<Guid>();
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                var id = reader.GetGuid(0);
                readable.Add(id);
                docClasses[id] = reader.IsDBNull(1) ? null : reader.GetString(1);
            }
        }
        return nearest.Where(n => readable.Contains(n.ChunkId)).ToList();
    }

    /// <summary>
    /// Degrades a free-text query to OR-of-terms tsquery syntax. Tokens are
    /// alphanumeric runs, so user input cannot inject tsquery operators; the
    /// english config still stems and drops stopwords server side.
    /// </summary>
    private static string? OrTsQuery(string query)
    {
        var terms = System.Text.RegularExpressions.Regex
            .Split(query, @"[^\p{L}\p{N}]+")
            .Where(t => t.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return terms.Length == 0 ? null : string.Join(" | ", terms);
    }

    /// <summary>
    /// Reads the passages that will leave, under the gates again. The ids came
    /// out of gated reads, but the vector leg scores from memory, and a
    /// document can be deleted or have its audience changed between one read
    /// and the next. The database has the last word on every passage returned.
    /// Kept as a separate read so the pool can grow without fetching content
    /// for candidates that lose.
    /// </summary>
    internal async Task<IReadOnlyList<SearchHit>> LoadHitsAsync(
        Guid tenantId, Guid[] topIds, IReadOnlyDictionary<Guid, FusedCandidate> fused,
        GateContext gate, CancellationToken ct = default)
    {
        if (topIds.Length == 0) return [];
        await using var read = await BoundRead.OpenAsync(db, searchRole, tenantId, gate.Scope, ct);
        return await LoadHitsAsync(read, tenantId, topIds, fused, gate, ct);
    }

    private async Task<IReadOnlyList<SearchHit>> LoadHitsAsync(
        BoundRead read, Guid tenantId, Guid[] topIds, IReadOnlyDictionary<Guid, FusedCandidate> fused,
        GateContext gate, CancellationToken ct)
    {
        if (topIds.Length == 0) return [];

        // The stale flag uses the search's own instant, the same one the
        // freshness gate compared against, and is read whatever the policy.
        await using var cmd = read.Command($"""
            SELECT c.id, d.path, d.title, c.heading_path, c.content, d.lifecycle_status, d.content_hash,
                   d.okf_concept_id, d.trust_tier, d.authorship,
                   (d.stale_after IS NOT NULL AND d.stale_after <= @hit_now) AS stale
            FROM prem_index.chunk c
            JOIN prem_index.document d ON d.id = c.document_id
            WHERE c.id = ANY(@ids)
              AND d.tenant_id = @tenant
              AND {_gates.Sql}
            """);
        cmd.Parameters.AddWithValue("ids", topIds);
        cmd.Parameters.AddWithValue("tenant", tenantId);
        cmd.Parameters.Add(new Npgsql.NpgsqlParameter("hit_now", NpgsqlDbType.TimestampTz) { Value = gate.Now });
        _gates.AddParameters(cmd, gate);

        var byId = new Dictionary<Guid, SearchHit>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var id = reader.GetGuid(0);
            var f = fused[id];
            byId[id] = new SearchHit(
                ChunkId: id,
                Path: reader.GetString(1),
                Title: reader.IsDBNull(2) ? null : reader.GetString(2),
                HeadingPath: reader.GetString(3),
                Content: reader.GetString(4),
                LifecycleStatus: reader.GetString(5),
                LexicalRank: f.LexRank,
                LexicalFallback: f.LexFallback,
                VectorRank: f.VecRank,
                CosineDistance: f.Dist,
                FusedScore: f.Score,
                ContentHash: reader.GetString(6),
                ConceptId: reader.IsDBNull(7) ? null : reader.GetString(7),
                TrustTier: (Okf.OkfTrustTier)reader.GetInt16(8),
                Authorship: (Okf.OkfAuthorship)reader.GetInt16(9),
                Stale: reader.GetBoolean(10));
        }
        // Preserve fused-score ordering.
        return topIds.Where(byId.ContainsKey).Select(id => byId[id]).ToArray();
    }

    private Task LogEventAsync(
        Guid tenantId, string query, GateContext gate,
        IReadOnlyList<SearchHit> hits, long elapsedMs, CancellationToken ct)
    {
        // Quality signals for the drift report: primary lexical evidence and
        // best vector distance let weak answers be mined later without
        // re-running the query.
        var lexicalCount = hits.Count(h => h.LexicalRank is not null && !h.LexicalFallback);
        var bestDistance = hits.Select(h => h.CosineDistance).Min();

        return WriteEventAsync(
            db, tenantId, SearchEventKind, query, requestedHeading: null, gate,
            hits.Select(h => new AuditedPassage(h.Path, h.HeadingPath, h.ContentHash)), elapsedMs, lexicalCount, bestDistance, ct);
    }

    internal const string SearchEventKind = "search";
    internal const string SectionEventKind = "section";

    /// <summary>
    /// Writes one row of the audit trail, for a search or for a section fetch
    /// (<see cref="SectionFetcher"/>), so both are recorded the same way.
    /// </summary>
    /// <param name="query">The query, or for a section fetch the path asked for.</param>
    /// <param name="requestedHeading">For a section fetch, the heading asked for; null for the whole document and for a search.</param>
    internal static async Task WriteEventAsync(
        PremagenticDatabase db, Guid tenantId, string kind, string query, string? requestedHeading, GateContext gate,
        IEnumerable<AuditedPassage> served, long elapsedMs, int? lexicalCount, double? bestDistance, CancellationToken ct)
    {
        var options = gate.Options;

        // Path, heading and content hash rather than chunk ids, which every
        // re-ingest replaces. The hash pins the version of the file that was
        // served, so a bad answer can be traced to its passage after the fact.
        var passages = JsonSerializer.Serialize(served);

        // The policy beside the passages: the tier and stale choice the gates
        // applied, and the instant they judged staleness at, so a bad answer
        // can be traced to the setting that let it through, not only to the
        // file it came from.
        // Where the model that receives this answer runs, copied from the
        // caller as it was resolved for this read rather than looked up later:
        // an administrator can move an agent between a local and a hosted
        // model, and the row has to keep saying what was true when the passages
        // were served. Null when a host resolved the caller itself, which is
        // the one case Premagentic cannot know.
        await using var cmd = db.DataSource.CreateCommand("""
            INSERT INTO prem_config.retrieval_event(
                tenant_id, kind, query, requested_heading, access_label, caller_user_id, caller_agent_id, include_historical,
                passages, elapsed_ms, lexical_count, best_distance,
                trust_minimum_tier, include_stale, policy_at, model_location)
            VALUES(@tenant, @kind, @query, @heading, @access, @callerUser, @callerAgent, @historical,
                   @passages, @elapsed, @lexCount, @bestDist,
                   @trustMinimumTier, @includeStale, @policyAt, @modelLocation)
            """);
        cmd.Parameters.AddWithValue("tenant", tenantId);
        cmd.Parameters.AddWithValue("kind", kind);
        cmd.Parameters.AddWithValue("query", query);
        cmd.Parameters.AddWithValue("heading", NpgsqlDbType.Text, (object?)requestedHeading ?? DBNull.Value);
        cmd.Parameters.AddWithValue("access", options.Scope.AuditLabel);
        cmd.Parameters.AddWithValue("callerUser", NpgsqlDbType.Uuid, (object?)options.Scope.UserId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("callerAgent", NpgsqlDbType.Uuid, (object?)options.Scope.AgentId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("historical", options.IncludeHistorical);
        cmd.Parameters.AddWithValue("passages", NpgsqlDbType.Jsonb, passages);
        cmd.Parameters.AddWithValue("elapsed", elapsedMs);
        cmd.Parameters.AddWithValue("lexCount", NpgsqlDbType.Integer, (object?)lexicalCount ?? DBNull.Value);
        cmd.Parameters.AddWithValue("bestDist", NpgsqlDbType.Double, (object?)bestDistance ?? DBNull.Value);
        cmd.Parameters.AddWithValue("trustMinimumTier", (short)gate.Trust.MinimumMachineTier);
        cmd.Parameters.AddWithValue("includeStale", gate.Trust.IncludeStale);
        cmd.Parameters.Add(new Npgsql.NpgsqlParameter("policyAt", NpgsqlDbType.TimestampTz) { Value = gate.Now });
        cmd.Parameters.AddWithValue("modelLocation", NpgsqlDbType.Text,
            options.Scope.ModelLocation is { } where ? Identity.ModelLocations.Text(where) : DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    internal sealed record FusedCandidate(int? LexRank, bool LexFallback, int? VecRank, double? Dist, double Score);

    internal sealed record AuditedPassage(
        [property: JsonPropertyName("path")] string Path,
        [property: JsonPropertyName("heading_path")] string HeadingPath,
        [property: JsonPropertyName("content_hash")] string ContentHash);
}

using System.Collections.Concurrent;
using System.Data;
using Premagentic.Core.Retrieval.Gates;
using Premagentic.Core.Storage;
using Npgsql;

namespace Premagentic.Core.Retrieval.Vectors;

/// <summary>
/// Exact vector search in this process, over plain PostgreSQL.
/// <para>
/// The database stays the gate. Each search asks PostgreSQL, under the same
/// gates as the text leg, which DOCUMENTS this caller may read and at which
/// version, and scores the chunks of exactly those documents and no others.
/// Memory is a copy of vectors, never a copy of permissions, so a document
/// deleted or re-permissioned a moment ago drops out of the very next search
/// with nothing to invalidate.
/// </para>
/// <para>
/// Why documents and not chunks: the gates decide per document, so asking for
/// documents returns the same answer in about a tenth of the rows. What keeps
/// the copy simple: a chunk row is never updated and never moves to another
/// document. A changed document has its chunks deleted and inserted again with
/// new ids in the same transaction that sets its <c>updated_at</c>
/// (<see cref="Ingestion.IngestPipeline"/>), so a document the database returns
/// at a version memory does not hold is reloaded on the spot, and a document it
/// no longer returns is simply never scored until a sweep reclaims its memory.
/// Every write that replaces a document's chunks must change its
/// <c>updated_at</c>; that is the one rule this class depends on.
/// </para>
/// <para>
/// The permitted read is the one read here that serves a caller, so it runs on
/// a read bound to that caller, where row-level security judges it too.
/// Loading vectors, warming up and sweeping serve nobody and run on the
/// application role, which reads the whole index.
/// </para>
/// </summary>
/// <param name="searchRole">The role bound reads connect as; null reads as the application role.</param>
internal sealed class InMemoryVectorIndex(PremagenticDatabase db, GateSet? gates = null, Security.SearchRole? searchRole = null) : IVectorLeg
{
    private const int LoadBatchRows = 4096;

    private readonly GateSet _gates = gates ?? GateSet.Default;
    private readonly ConcurrentDictionary<string, VectorSpace> _spaces = new(StringComparer.Ordinal);
    private long _lastSweepAt = Environment.TickCount64;
    private int _sweeping;

    /// <summary>How long a search waits after the last sweep before starting another in the background.</summary>
    internal TimeSpan SweepInterval { get; set; } = TimeSpan.FromMinutes(5);

    internal IReadOnlyCollection<VectorSpace> Spaces => _spaces.Values.ToArray();

    /// <summary>Chunk vectors held across every embedding model.</summary>
    public int Count => _spaces.Values.Sum(s => s.ChunkCount);

    public async Task<int> WarmUpAsync(CancellationToken ct = default)
    {
        // Size each array once from the counts, so loading a large index never
        // holds two copies of it while an array grows.
        await using (var counts = db.DataSource.CreateCommand("""
            SELECT c.embedding_model, max(c.embedding_dims), count(*)
            FROM prem_index.chunk c
            GROUP BY c.embedding_model
            """))
        await using (var reader = await counts.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
                Space(reader.GetString(0)).Reserve((int)reader.GetInt64(2), reader.GetInt32(1));
        }

        // Every chunk, in no particular order. A document's chunks become
        // searchable together, when the whole read is done, and a document that
        // a search loaded on demand meanwhile is left as that search loaded it.
        await using (var all = db.DataSource.CreateCommand("""
            SELECT d.id, d.updated_at, c.id, c.embedding_model, c.embedding_dims, c.embedding
            FROM prem_index.chunk c
            JOIN prem_index.document d ON d.id = c.document_id
            """))
            await LoadAsync(all, onlyIfAbsent: true, ct);

        await SweepAsync(ct);
        return Count;
    }

    public async Task<IReadOnlyList<VectorCandidate>> SearchAsync(
        Guid tenantId, string embeddingModel, float[] query, GateContext gates, int limit,
        CancellationToken ct = default)
    {
        await using var read = await BoundRead.OpenAsync(db, searchRole, tenantId, gates.Scope, ct);
        return await SearchAsync(read, tenantId, embeddingModel, query, gates, limit, ct);
    }

    public async Task<IReadOnlyList<VectorCandidate>> SearchAsync(
        BoundRead read, Guid tenantId, string embeddingModel, float[] query, GateContext gates, int limit,
        CancellationToken ct = default)
    {
        if (limit <= 0) return [];

        // A query with no direction is as near to everything as to nothing.
        var unitQuery = VectorCodec.Normalize(query);
        if (unitQuery is null) return [];

        var permitted = await PermittedDocumentsAsync(read, tenantId, gates, ct);
        if (permitted.Count == 0) return [];

        var space = Space(embeddingModel);
        var stale = space.Stale(permitted);
        if (stale.Count > 0)
        {
            // Not gated: loading a vector serves nothing. Only the chunks of
            // documents the gated read above returned are ever scored. The left
            // join records a document with no chunks in this model too, so it is
            // not looked for again on every search.
            await using var load = db.DataSource.CreateCommand("""
                SELECT d.id, d.updated_at, c.id, c.embedding_model, c.embedding_dims, c.embedding
                FROM prem_index.document d
                LEFT JOIN prem_index.chunk c ON c.document_id = d.id AND c.embedding_model = @model
                WHERE d.id = ANY(@documents)
                """);
            load.Parameters.AddWithValue("documents", stale.ToArray());
            load.Parameters.AddWithValue("model", embeddingModel);
            await LoadAsync(load, onlyIfAbsent: false, ct, emptyDocumentsModel: embeddingModel);
        }

        var nearest = space.Nearest(unitQuery, permitted, limit);
        SweepInBackgroundWhenDue();

        // Rounding can carry a unit dot product a hair past 1; distance never goes below 0.
        return nearest.Select(n => new VectorCandidate(n.ChunkId, 1.0 - Math.Clamp((double)n.Dot, -1.0, 1.0))).ToArray();
    }

    /// <summary>
    /// The one gated read per search: every document the caller may read, with
    /// its version. The gates see only the alias <c>d</c>, which is why every
    /// gate must be expressible over the document alone.
    /// </summary>
    private async Task<List<PermittedDocument>> PermittedDocumentsAsync(
        BoundRead read, Guid tenantId, GateContext gates, CancellationToken ct)
    {
        await using var cmd = read.Command($"""
            SELECT d.id, d.updated_at
            FROM prem_index.document d
            WHERE d.tenant_id = @tenant
              AND {_gates.Sql}
            """);
        cmd.Parameters.AddWithValue("tenant", tenantId);
        _gates.AddParameters(cmd, gates);

        var documents = new List<PermittedDocument>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            documents.Add(new PermittedDocument(reader.GetGuid(0), Version(reader, 1)));
        return documents;
    }

    /// <summary>
    /// Streams rows of (document id, updated_at, chunk id, embedding_model,
    /// embedding_dims, embedding) into memory. Vectors are staged in batches, one
    /// write lock per batch; documents are published once the read is complete,
    /// so no search ever scores half of a document.
    /// </summary>
    /// <param name="emptyDocumentsModel">
    /// For an on-demand load, the model whose space should record a document the
    /// left join returned with no chunk.
    /// </param>
    private async Task LoadAsync(
        NpgsqlCommand cmd, bool onlyIfAbsent, CancellationToken ct, string? emptyDocumentsModel = null)
    {
        var batches = new Dictionary<string, StagingBatch>(StringComparer.Ordinal);
        var documents = new Dictionary<(string Model, Guid Document), (long Version, List<int> Slots)>();
        byte[] buffer = [];

        await using (var reader = await cmd.ExecuteReaderAsync(CommandBehavior.SequentialAccess, ct))
        {
            while (await reader.ReadAsync(ct))
            {
                var documentId = reader.GetGuid(0);
                var version = Version(reader, 1);
                if (reader.IsDBNull(2))
                {
                    documents.TryAdd((emptyDocumentsModel!, documentId), (version, []));
                    continue;
                }
                var chunkId = reader.GetGuid(2);
                var model = reader.GetString(3);
                var dims = reader.GetInt32(4);
                var length = dims * sizeof(float);
                if (buffer.Length < length) buffer = new byte[length];
                // The schema checks octet_length(embedding) = embedding_dims * 4, so
                // this read fills the vector exactly.
                reader.GetBytes(5, 0, buffer, 0, length);

                if (!documents.TryGetValue((model, documentId), out var document))
                    documents[(model, documentId)] = document = (version, []);
                if (!batches.TryGetValue(model, out var batch))
                    batches[model] = batch = new StagingBatch(Space(model), dims);
                batch.Append(chunkId, document.Slots, buffer.AsSpan(0, length), dims);
                if (batch.Count == LoadBatchRows) batch.Flush();
            }
        }

        foreach (var batch in batches.Values)
            batch.Flush();

        foreach (var group in documents.GroupBy(d => d.Key.Model))
            Space(group.Key).Publish(
                group.Select(d => (d.Key.Document, d.Value.Version, (IReadOnlyList<int>)d.Value.Slots)).ToArray(),
                onlyIfAbsent);
    }

    /// <summary>
    /// Forgets documents the database no longer holds, or holds at another
    /// version. Memory reclamation only: correctness never waits on it.
    /// </summary>
    /// <returns>The chunk vectors freed.</returns>
    internal async Task<int> SweepAsync(CancellationToken ct = default)
    {
        var clocks = _spaces.Values.ToDictionary(s => s.EmbeddingModel, s => s.Clock, StringComparer.Ordinal);

        var stored = new Dictionary<Guid, long>();
        await using (var cmd = db.DataSource.CreateCommand("SELECT d.id, d.updated_at FROM prem_index.document d"))
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
                stored[reader.GetGuid(0)] = Version(reader, 1);
        }

        var freed = 0;
        foreach (var (model, clock) in clocks)
            freed += _spaces[model].Sweep(stored, clock);
        Volatile.Write(ref _lastSweepAt, Environment.TickCount64);
        return freed;
    }

    private void SweepInBackgroundWhenDue()
    {
        if (Environment.TickCount64 - Volatile.Read(ref _lastSweepAt) < SweepInterval.TotalMilliseconds) return;
        if (Interlocked.CompareExchange(ref _sweeping, 1, 0) != 0) return;

        _ = Task.Run(async () =>
        {
            try { await SweepAsync(); }
            catch
            {
                // Swallowed on purpose. A sweep only frees memory; a failed one
                // leaves documents that are never scored, and the next one retries.
                Volatile.Write(ref _lastSweepAt, Environment.TickCount64);
            }
            finally { Volatile.Write(ref _sweeping, 0); }
        });
    }

    /// <summary>A document's version: its updated_at, in ticks.</summary>
    private static long Version(NpgsqlDataReader reader, int ordinal) => reader.GetFieldValue<DateTime>(ordinal).Ticks;

    private VectorSpace Space(string embeddingModel) =>
        _spaces.GetOrAdd(embeddingModel, m => new VectorSpace(m));

    private sealed class StagingBatch(VectorSpace space, int dimensions)
    {
        private readonly Guid[] _chunkIds = new Guid[LoadBatchRows];
        private readonly List<int>[] _owners = new List<int>[LoadBatchRows];
        private readonly float[] _vectors = new float[LoadBatchRows * dimensions];

        public int Count { get; private set; }

        /// <summary>Queues one chunk; its slot is added to <paramref name="owner"/> when the batch is staged.</summary>
        public void Append(Guid chunkId, List<int> owner, ReadOnlySpan<byte> stored, int dims)
        {
            if (dims != dimensions)
                throw new InvalidOperationException(
                    $"Stored vectors for '{space.EmbeddingModel}' have both {dimensions} and {dims} dimensions. " +
                    "One model name must mean one vector space; re-ingest with the current provider.");
            VectorCodec.Decode(stored, _vectors.AsSpan(Count * dimensions, dimensions));
            _chunkIds[Count] = chunkId;
            _owners[Count] = owner;
            Count++;
        }

        public void Flush()
        {
            if (Count == 0) return;
            var slots = space.Stage(_chunkIds.AsSpan(0, Count), _vectors.AsSpan(0, Count * dimensions), dimensions);
            for (var i = 0; i < Count; i++)
                _owners[i].Add(slots[i]);
            Array.Clear(_owners, 0, Count);
            Count = 0;
        }
    }
}

using System.Numerics.Tensors;
using Premagentic.Core.Storage;

namespace Premagentic.Core.Retrieval.Vectors;

/// <summary>A document the gates permitted, and the version of it the database holds.</summary>
internal readonly record struct PermittedDocument(Guid DocumentId, long Version);

/// <summary>
/// Every vector held for one embedding model, grouped by document: one
/// contiguous float array, a slot per chunk, and for each document the version
/// it was loaded at and the slots of its chunks. It knows nothing about
/// permissions; <see cref="Nearest"/> scores the chunks of exactly the documents
/// it is handed, at exactly the versions it is handed, and no others.
/// <para>
/// A document's version is its <c>updated_at</c>, which every write that
/// replaces its chunks also changes. A chunk never moves to another document, so
/// the version is the only thing that can make a held document stale.
/// </para>
/// <para>
/// Thread safety: searches score under a read lock, and loading or sweeping
/// takes the write lock briefly. No lock is ever held across an await.
/// </para>
/// </summary>
internal sealed class VectorSpace(string embeddingModel)
{
    private sealed record DocumentEntry(long Version, int[] Slots, long AddedAt);

    private readonly ReaderWriterLockSlim _lock = new(LockRecursionPolicy.NoRecursion);
    private readonly Dictionary<Guid, DocumentEntry> _documents = new();
    private readonly Stack<int> _freeSlots = new();
    private float[] _vectors = [];
    private Guid[] _chunkBySlot = [];
    private int _dimensions;
    private int _highWater;
    private int _chunks;
    private long _clock;

    public string EmbeddingModel => embeddingModel;

    /// <summary>Chunk vectors held and scorable. A chunk stored with no direction is not held.</summary>
    public int ChunkCount => Read(() => _chunks);

    public int DocumentCount => Read(() => _documents.Count);

    /// <summary>Bytes held by the vector array itself, including spare capacity.</summary>
    public long VectorBytes => Read(() => (long)_vectors.Length * sizeof(float));

    /// <summary>
    /// Advances on every publish. A sweep only removes documents published at or
    /// before the clock value it read before asking the database what exists, so
    /// a document loaded while the sweep was reading is never swept by it.
    /// </summary>
    public long Clock => Read(() => _clock);

    /// <summary>The permitted documents this space does not hold at the version the database returned.</summary>
    public List<Guid> Stale(IReadOnlyList<PermittedDocument> permitted)
    {
        _lock.EnterReadLock();
        try
        {
            var stale = new List<Guid>();
            foreach (var (id, version) in permitted)
                if (!_documents.TryGetValue(id, out var entry) || entry.Version != version)
                    stale.Add(id);
            return stale;
        }
        finally { _lock.ExitReadLock(); }
    }

    /// <summary>Sizes the array for <paramref name="chunks"/> vectors ahead of a bulk load, so it grows once.</summary>
    public void Reserve(int chunks, int dimensions)
    {
        _lock.EnterWriteLock();
        try
        {
            SetDimensions(dimensions);
            EnsureSlots(chunks, exact: true);
        }
        finally { _lock.ExitWriteLock(); }
    }

    /// <summary>
    /// Copies decoded vectors, <paramref name="dimensions"/> floats each, into free
    /// slots and returns one slot per chunk, or -1 for a chunk stored with no
    /// direction, which is never given a slot. The slots belong to no document,
    /// and so are never scored, until <see cref="Publish"/> hands them to one.
    /// </summary>
    public int[] Stage(ReadOnlySpan<Guid> chunkIds, ReadOnlySpan<float> vectors, int dimensions)
    {
        _lock.EnterWriteLock();
        try
        {
            SetDimensions(dimensions);
            EnsureSlots(_highWater + Math.Max(0, chunkIds.Length - _freeSlots.Count), exact: false);
            var slots = new int[chunkIds.Length];
            for (var i = 0; i < chunkIds.Length; i++)
            {
                var vector = vectors.Slice(i * dimensions, dimensions);
                if (VectorCodec.IsZero(vector))
                {
                    slots[i] = -1;
                    continue;
                }
                var slot = _freeSlots.Count > 0 ? _freeSlots.Pop() : _highWater++;
                vector.CopyTo(_vectors.AsSpan(slot * dimensions, dimensions));
                _chunkBySlot[slot] = chunkIds[i];
                slots[i] = slot;
            }
            return slots;
        }
        finally { _lock.ExitWriteLock(); }
    }

    /// <summary>
    /// Makes staged slots a document's chunks at a version, replacing whatever the
    /// space held for it. When the space already holds the document at that
    /// version (another search loaded it first), or holds it at all and
    /// <paramref name="onlyIfAbsent"/> is set, the staged slots are freed instead.
    /// </summary>
    public void Publish(IReadOnlyList<(Guid DocumentId, long Version, IReadOnlyList<int> Slots)> documents, bool onlyIfAbsent)
    {
        _lock.EnterWriteLock();
        try
        {
            foreach (var (id, version, staged) in documents)
            {
                var slots = staged.Where(s => s >= 0).ToArray();
                if (_documents.TryGetValue(id, out var existing))
                {
                    if (onlyIfAbsent || existing.Version == version)
                    {
                        Free(slots);
                        continue;
                    }
                    Free(existing.Slots);
                    _chunks -= existing.Slots.Length;
                }
                _documents[id] = new DocumentEntry(version, slots, ++_clock);
                _chunks += slots.Length;
            }
        }
        finally { _lock.ExitWriteLock(); }
    }

    /// <summary>
    /// Scores the chunks of the documents in <paramref name="permitted"/> against a
    /// unit-length query and returns the best <paramref name="limit"/> by dot
    /// product, highest first. A document held at another version than the one
    /// permitted, or not held at all, is skipped: this search's read of the
    /// database did not see those chunks.
    /// </summary>
    public List<(Guid ChunkId, float Dot)> Nearest(ReadOnlySpan<float> unitQuery, IReadOnlyList<PermittedDocument> permitted, int limit)
    {
        _lock.EnterReadLock();
        try
        {
            if (_dimensions == 0 || limit <= 0) return [];
            if (unitQuery.Length != _dimensions)
                throw new InvalidOperationException(
                    $"The query has {unitQuery.Length} dimensions and the stored vectors for '{embeddingModel}' " +
                    $"have {_dimensions}. The embedding provider changed without a re-ingest.");

            // A min-heap of the best so far: its root is the weakest keeper.
            var best = new PriorityQueue<Guid, float>(limit + 1);
            var vectors = _vectors.AsSpan();
            foreach (var (id, version) in permitted)
            {
                if (!_documents.TryGetValue(id, out var entry) || entry.Version != version) continue;
                foreach (var slot in entry.Slots)
                {
                    var dot = TensorPrimitives.Dot(unitQuery, vectors.Slice(slot * _dimensions, _dimensions));
                    if (best.Count < limit) best.Enqueue(_chunkBySlot[slot], dot);
                    else if (best.TryPeek(out _, out var weakest) && dot > weakest) best.DequeueEnqueue(_chunkBySlot[slot], dot);
                }
            }

            var result = new List<(Guid, float)>(best.Count);
            while (best.TryDequeue(out var chunkId, out var dot)) result.Add((chunkId, dot));
            result.Sort((a, b) => a.Item2 != b.Item2 ? b.Item2.CompareTo(a.Item2) : a.Item1.CompareTo(b.Item1));
            return result;
        }
        finally { _lock.ExitReadLock(); }
    }

    /// <summary>
    /// Forgets documents the database no longer holds at the version held here.
    /// Memory only: such a document is never scored anyway, because only
    /// permitted documents at their current version are.
    /// </summary>
    /// <returns>The chunk vectors freed.</returns>
    public int Sweep(IReadOnlyDictionary<Guid, long> stored, long clockBeforeRead)
    {
        _lock.EnterWriteLock();
        try
        {
            var freed = 0;
            foreach (var (id, entry) in _documents.ToArray())
            {
                if (entry.AddedAt > clockBeforeRead) continue;
                if (stored.TryGetValue(id, out var version) && version == entry.Version) continue;
                _documents.Remove(id);
                Free(entry.Slots);
                _chunks -= entry.Slots.Length;
                freed += entry.Slots.Length;
            }
            return freed;
        }
        finally { _lock.ExitWriteLock(); }
    }

    /// <summary>True when a published document holds this chunk. For tests.</summary>
    internal bool HoldsChunk(Guid chunkId) =>
        Read(() => _documents.Values.Any(e => e.Slots.Any(s => _chunkBySlot[s] == chunkId)));

    private void Free(IEnumerable<int> slots)
    {
        foreach (var slot in slots)
        {
            _chunkBySlot[slot] = Guid.Empty;
            _freeSlots.Push(slot);
        }
    }

    private void SetDimensions(int dimensions)
    {
        if (dimensions <= 0)
            throw new ArgumentOutOfRangeException(nameof(dimensions), dimensions, "A stored vector has no dimensions.");
        if (_dimensions == 0) _dimensions = dimensions;
        else if (_dimensions != dimensions)
            throw new InvalidOperationException(
                $"Stored vectors for '{embeddingModel}' have both {_dimensions} and {dimensions} dimensions. " +
                "One model name must mean one vector space; re-ingest with the current provider.");
    }

    private void EnsureSlots(int slots, bool exact)
    {
        if (slots <= _chunkBySlot.Length) return;
        var capacity = exact ? slots : Math.Max(slots, Math.Max(1024, _chunkBySlot.Length + _chunkBySlot.Length / 2));
        Array.Resize(ref _vectors, checked(capacity * _dimensions));
        Array.Resize(ref _chunkBySlot, capacity);
    }

    private T Read<T>(Func<T> read)
    {
        _lock.EnterReadLock();
        try { return read(); }
        finally { _lock.ExitReadLock(); }
    }
}

using Premagentic.Core.Retrieval.Gates;

namespace Premagentic.Core.Retrieval.Vectors;

/// <summary>A chunk the gates permitted, and its cosine distance from the query.</summary>
internal readonly record struct VectorCandidate(Guid ChunkId, double Distance);

/// <summary>
/// The vector leg of hybrid search: among the chunks the caller may read, the
/// ones nearest the query.
/// <para>
/// One implementation, <see cref="InMemoryVectorIndex"/>, which needs nothing
/// but plain PostgreSQL. The seam exists so a provider backed by a database
/// vector extension could serve a deployment too large to hold in memory.
/// Nothing is built behind it until one does.
/// </para>
/// </summary>
internal interface IVectorLeg
{
    /// <summary>Loads every stored vector so the first searches do not pay for it. Returns how many are held.</summary>
    Task<int> WarmUpAsync(CancellationToken ct = default);

    /// <summary>
    /// Up to <paramref name="limit"/> chunks the gates permit, nearest first, and
    /// never a chunk they do not: an implementation scores only ids the
    /// database returned as permitted for this read.
    /// Distance is cosine distance, 1 minus the cosine similarity, so 0 is
    /// identical, 1 is unrelated and 2 is opposite.
    /// </summary>
    Task<IReadOnlyList<VectorCandidate>> SearchAsync(
        Guid tenantId, string embeddingModel, float[] query, GateContext gates, int limit,
        CancellationToken ct = default);

    /// <summary>The same, with the permitted read made on a read already bound to the caller.</summary>
    Task<IReadOnlyList<VectorCandidate>> SearchAsync(
        BoundRead read, Guid tenantId, string embeddingModel, float[] query, GateContext gates, int limit,
        CancellationToken ct = default);
}

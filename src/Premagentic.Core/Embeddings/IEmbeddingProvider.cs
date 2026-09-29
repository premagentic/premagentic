namespace Premagentic.Core.Embeddings;

/// <summary>
/// Provider abstraction per the Premagentic charter: embedding providers are replaceable.
/// The store records Name + Dimensions per chunk so a provider swap forces a
/// visible re-embed rather than silently mixing vector spaces.
/// </summary>
public interface IEmbeddingProvider
{
    string Name { get; }
    int Dimensions { get; }
    Task<float[][]> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct = default);
}

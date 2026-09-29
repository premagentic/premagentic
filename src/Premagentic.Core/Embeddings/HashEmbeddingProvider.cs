using System.Security.Cryptography;
using System.Text;

namespace Premagentic.Core.Embeddings;

/// <summary>
/// Deterministic bag-of-words hashing embedder for tests: no model download,
/// no network, stable across runs. Real semantic similarity is weak, but
/// token-overlapping texts land measurably closer than disjoint ones, which is
/// all the integration tests need.
/// </summary>
public sealed class HashEmbeddingProvider : IEmbeddingProvider
{
    public string Name => "hash-bow-v1";
    public int Dimensions => 256;

    public Task<float[][]> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct = default)
        => Task.FromResult(texts.Select(Embed).ToArray());

    private float[] Embed(string text)
    {
        var vec = new float[Dimensions];
        foreach (var token in Tokenize(text))
        {
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(token));
            var bucket = BitConverter.ToUInt32(hash, 0) % (uint)Dimensions;
            var sign = (hash[4] & 1) == 0 ? 1f : -1f;
            vec[bucket] += sign;
        }
        var norm = MathF.Sqrt(vec.Sum(v => v * v));
        if (norm > 0)
            for (var i = 0; i < vec.Length; i++) vec[i] /= norm;
        return vec;
    }

    private static IEnumerable<string> Tokenize(string text) =>
        text.ToLowerInvariant()
            .Split(c => !char.IsLetterOrDigit(c))
            .Where(t => t.Length > 1);
}

file static class SplitExtensions
{
    public static IEnumerable<string> Split(this string s, Func<char, bool> isSeparator)
    {
        var start = 0;
        for (var i = 0; i < s.Length; i++)
        {
            if (!isSeparator(s[i])) continue;
            if (i > start) yield return s[start..i];
            start = i + 1;
        }
        if (start < s.Length) yield return s[start..];
    }
}

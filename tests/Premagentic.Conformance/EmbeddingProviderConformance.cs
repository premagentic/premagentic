using Premagentic.Core.Extensions;
using Premagentic.Core.Embeddings;

namespace Premagentic.Conformance;

/// <summary>
/// Inherit this and give it your embedding provider to prove it keeps the
/// provider contract. The store records a provider's name and dimension
/// against every chunk, so a provider that returns a vector of another length,
/// or that returns a different vector for the same text from one run to the
/// next, makes a corpus that cannot be searched and cannot be told it cannot.
/// <para>
/// Normalized means unit length. Ranking compares by cosine distance, and a
/// provider whose vectors are not normalized moves a document up the results
/// for having a longer vector than its neighbors.
/// </para>
/// </summary>
public abstract class EmbeddingProviderConformance
{
    /// <summary>
    /// This kit proves the version of this seam that the PremAgentic your
    /// extension builds against offers. A kit from another release fails here,
    /// naming the version to match, rather than passing in silence.
    /// </summary>
    [Fact]
    public void The_kit_proves_the_seams_this_extension_builds_against() =>
        KitSeams.AssertProves(SeamVersions.EmbeddingName, nameof(SeamVersions.Embedding));

    protected abstract IEmbeddingProvider Provider { get; }

    /// <summary>Texts to embed. Override for a provider with a language or a length in mind.</summary>
    protected virtual IReadOnlyList<string> Texts =>
    [
        "The side door opens at eight and the front door at nine.",
        "Expense claims are filed within thirty days.",
        "yard",
    ];

    private const double Tolerance = 1e-3;

    [Fact]
    public void It_names_itself_and_says_how_long_its_vectors_are()
    {
        Assert.False(string.IsNullOrWhiteSpace(Provider.Name),
            "A provider's name is stored with every chunk it embedded, so a change of provider is visible.");
        Assert.True(Provider.Dimensions > 0, "A provider says how long its vectors are before it makes one.");
    }

    [Fact]
    public async Task Every_vector_has_the_declared_length_and_is_normalized()
    {
        var vectors = await Provider.EmbedAsync(Texts, CancellationToken.None);

        Assert.Equal(Texts.Count, vectors.Length);
        foreach (var (text, vector) in Texts.Zip(vectors))
        {
            Assert.Equal(Provider.Dimensions, vector.Length);
            Assert.DoesNotContain(vector, v => float.IsNaN(v) || float.IsInfinity(v));

            var length = Math.Sqrt(vector.Sum(v => (double)v * v));
            Assert.True(Math.Abs(length - 1) < Tolerance,
                $"The provider '{Provider.Name}' returned a vector of length {length:F4} for \"{text}\". " +
                "Vectors are normalized, or a longer vector ranks higher for being longer.");
        }
    }

    [Fact]
    public async Task No_texts_gives_no_vectors()
    {
        Assert.Empty(await Provider.EmbedAsync([], CancellationToken.None));
    }

    [Fact]
    public async Task The_same_text_gives_the_same_vector()
    {
        var first = await Provider.EmbedAsync(Texts, CancellationToken.None);
        var again = await Provider.EmbedAsync(Texts, CancellationToken.None);

        foreach (var (before, after) in first.Zip(again))
            for (var i = 0; i < before.Length; i++)
                Assert.True(Math.Abs(before[i] - after[i]) < Tolerance,
                    $"The provider '{Provider.Name}' gave two different vectors for one text. " +
                    "A document is only re-embedded when its content changes, so a provider that drifts " +
                    "leaves a corpus embedded two ways.");
    }
}

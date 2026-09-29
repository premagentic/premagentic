using Premagentic.Core;
using Premagentic.Core.Embeddings;
using Premagentic.Core.Extensions;
using Premagentic.Core.Ingestion;
using Premagentic.Core.Ingestion.Readers;
using Premagentic.Core.Storage;

namespace Premagentic.Tests;

/// <summary>
/// What an extension may register, checked as it registers, so the extension
/// that brought an unusable name is the one refused and the others still load.
/// Also the one rule that keeps a provider seam from becoming a way to stand in
/// for the local, offline model. No database.
/// </summary>
public sealed class ExtensionRegistrationsTests
{
    private sealed class NamedChunker(string name) : IChunker
    {
        public string Name => name;

        public IReadOnlyList<DocumentChunk> Chunk(SourceDocument document) => [];
    }

    private sealed class NamedReader(string name, params string[] extensions) : IDocumentReader
    {
        public string Name => name;

        public IReadOnlyList<string> Extensions { get; } = extensions;

        public Task<ReadDocument> ReadAsync(Stream content, string path, CancellationToken ct) =>
            throw new NotSupportedException("This reader exists to be registered, not to read.");
    }

    private sealed class FakeEmbeddingProvider : IEmbeddingProvider
    {
        public string Name => "yard-model-v1";

        public int Dimensions => 8;

        public Task<float[][]> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct = default) =>
            Task.FromResult(texts.Select(_ => new float[Dimensions]).ToArray());
    }

    [Fact]
    public void A_name_this_process_cannot_hold_is_refused_as_it_is_registered()
    {
        var registrations = new ExtensionRegistrations();

        foreach (var bad in new[] { "", "two words", "-leading", "slash/name", new string('x', 65) })
        {
            Assert.Throws<ArgumentException>(() => registrations.AddChunker(new NamedChunker(bad)));
            Assert.Throws<ArgumentException>(() => registrations.AddReader(new NamedReader(bad, ".yard")));
            Assert.Throws<ArgumentException>(() => registrations.AddEmbeddingProvider(bad, _ => new FakeEmbeddingProvider()));
        }

        // A reader says which files reach it, in the one spelling the registry matches.
        Assert.Throws<ArgumentException>(() => registrations.AddReader(new NamedReader("yard")));
        foreach (var bad in new[] { "yard", ".", ".YARD", "yard/.md", ". md" })
            Assert.Throws<ArgumentException>(() => registrations.AddReader(new NamedReader("yard", bad)));

        // Nothing was kept: every one of those threw before it was added.
        Assert.True(registrations.IsEmpty);
    }

    [Fact]
    public void An_extension_cannot_register_an_embedding_provider_under_a_built_in_name()
    {
        var registrations = new ExtensionRegistrations();

        foreach (var builtIn in EmbeddingProviderFactory.BuiltInNames)
        {
            var refusal = Assert.Throws<ArgumentException>(
                () => registrations.AddEmbeddingProvider(builtIn.ToUpperInvariant(), _ => new FakeEmbeddingProvider()));
            Assert.Contains("built-in", refusal.Message);
        }

        registrations.AddEmbeddingProvider("yard-model", _ => new FakeEmbeddingProvider());
        Assert.Throws<ArgumentException>(() => registrations.AddEmbeddingProvider("YARD-MODEL", _ => new FakeEmbeddingProvider()));
    }

    [Fact]
    public void A_provider_an_extension_registered_is_offered_under_its_own_name_beside_the_built_ins()
    {
        var fake = new FakeEmbeddingProvider();
        var registered = new Dictionary<string, Func<IServiceProvider, IEmbeddingProvider>>(StringComparer.OrdinalIgnoreCase)
        {
            ["yard-model"] = _ => fake,
        };

        Assert.Same(fake, EmbeddingProviderFactory.From("yard-model", registered));
        Assert.Same(fake, EmbeddingProviderFactory.From("YARD-MODEL", registered));

        // The built-ins are still themselves, and are still there with no extension at all.
        Assert.IsType<HashEmbeddingProvider>(EmbeddingProviderFactory.From("hash", registered));
        Assert.IsType<HashEmbeddingProvider>(EmbeddingProviderFactory.From("hash"));

        var refusal = Assert.Throws<StartupRefusedException>(() => EmbeddingProviderFactory.From("nothing-by-that-name", registered));
        Assert.Contains("local, openai or hash", refusal.Message);
        Assert.Contains("yard-model", refusal.Message);
        Assert.DoesNotContain("yard-model", Assert.Throws<StartupRefusedException>(
            () => EmbeddingProviderFactory.From("nothing-by-that-name")).Message);
    }
}

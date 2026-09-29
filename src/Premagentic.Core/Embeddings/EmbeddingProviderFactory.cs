using Premagentic.Core.Storage;

namespace Premagentic.Core.Embeddings;

public static class EmbeddingProviderFactory
{
    /// <summary>
    /// The provider names this library has of its own. An extension cannot
    /// register one of these, so naming a provider can add a way to embed and
    /// can never quietly replace the local, offline one with something that
    /// calls out.
    /// </summary>
    public static IReadOnlyList<string> BuiltInNames { get; } = ["local", "openai", "hash"];

    /// <summary>
    /// Resolves the provider from PREM_EMBEDDING_PROVIDER: "local" (default),
    /// "openai", "hash" (tests only), or the name of a provider an extension
    /// registered. The local model's folder is found by <see cref="ModelFolder"/>.
    /// </summary>
    /// <param name="fromExtensions">
    /// What the extensions this process loaded registered, by name, from
    /// <c>ExtensionHost.EmbeddingProviders</c>. Null offers the built-ins alone.
    /// </param>
    /// <param name="services">
    /// What an extension's provider may ask for, in a host that has a service
    /// container. A host without one, such as the CLI, passes null and a
    /// provider that asks for a service is given nothing, as an empty container
    /// would give.
    /// </param>
    public static IEmbeddingProvider FromEnvironment(
        IReadOnlyDictionary<string, Func<IServiceProvider, IEmbeddingProvider>>? fromExtensions,
        IServiceProvider? services = null) =>
        From(Environment.GetEnvironmentVariable("PREM_EMBEDDING_PROVIDER")?.ToLowerInvariant() ?? "local",
            fromExtensions, services);

    /// <summary>
    /// The provider <paramref name="name"/> asks for. One an extension
    /// registered is looked for first; since an extension cannot register a
    /// built-in name, that adds a provider and never stands in for one.
    /// </summary>
    /// <exception cref="StartupRefusedException">No provider has that name.</exception>
    public static IEmbeddingProvider From(
        string name,
        IReadOnlyDictionary<string, Func<IServiceProvider, IEmbeddingProvider>>? fromExtensions = null,
        IServiceProvider? services = null)
    {
        if (fromExtensions is not null && fromExtensions.TryGetValue(name, out var registered))
            return registered(services ?? NoServices.Instance);

        return name switch
        {
            "openai" => new OpenAiEmbeddingProvider(),
            "hash" => new HashEmbeddingProvider(),
            "local" => Local(ModelFolder.FromEnvironment()),
            _ => throw new StartupRefusedException(
                $"PREM_EMBEDDING_PROVIDER is '{name}', which is not a provider: use {Names(fromExtensions)}."),
        };
    }

    /// <summary>
    /// The local provider over the folder the lookup found. When no folder holds
    /// the model, the refusal names every folder it looked in, so the person
    /// reading it can see where the model is expected and which one to fill,
    /// and says where the model comes from in each way of installing: inside a
    /// release, or downloaded by a script in a source checkout.
    /// </summary>
    internal static IEmbeddingProvider Local(ModelFolderLookup lookup) =>
        lookup.Found is { } folder
            ? new LocalOnnxEmbeddingProvider(folder)
            : throw new StartupRefusedException(
                $"The local embedding provider needs {string.Join(" and ", ModelFolder.ModelFiles)} in a models/minilm " +
                $"folder, and none was found. Looked in: {string.Join("; ", lookup.LookedIn)}. An installed release " +
                "carries the model in models/minilm beside bin/; put that folder back. From a source checkout, run " +
                $"scripts/download-model.sh or scripts/download-model.ps1 at the repository root first. Or set " +
                $"{ModelFolder.Variable} to the folder that holds the two files.");

    /// <summary>The built-in names, and the names of any an extension brought.</summary>
    private static string Names(IReadOnlyDictionary<string, Func<IServiceProvider, IEmbeddingProvider>>? fromExtensions)
    {
        const string builtIn = "local, openai or hash";
        var registered = fromExtensions?.Keys.Order(StringComparer.OrdinalIgnoreCase).ToArray() ?? [];
        return registered.Length == 0
            ? builtIn
            : $"{builtIn}, or one an extension registered: {string.Join(", ", registered)}";
    }

    /// <summary>For a host with no service container of its own.</summary>
    private sealed class NoServices : IServiceProvider
    {
        public static NoServices Instance { get; } = new();

        public object? GetService(Type serviceType) => null;
    }
}

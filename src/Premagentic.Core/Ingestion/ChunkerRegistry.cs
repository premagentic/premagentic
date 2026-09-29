namespace Premagentic.Core.Ingestion;

/// <summary>
/// The chunkers this process can use, by name. <c>markdown</c> is built in and
/// is the default. A host that adds its own passes them to the constructor and
/// gives the one registry to the ingest pipeline and the sources registry, so a
/// name is refused when a source is added and a run fails before it chunks
/// anything under a name the process does not have.
/// <para>
/// A name is matched ignoring case and stored as the chunker spells it.
/// </para>
/// </summary>
public sealed class ChunkerRegistry
{
    public const string DefaultName = "markdown";

    /// <summary>The built-in chunkers only, for a host that adds none.</summary>
    public static ChunkerRegistry BuiltIn { get; } = new();

    private readonly Dictionary<string, IChunker> _byName = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="added">Chunkers beside the built-in one.</param>
    /// <exception cref="ArgumentException">A name is not usable, or two chunkers share one.</exception>
    public ChunkerRegistry(params IChunker[] added)
    {
        foreach (var chunker in added.Prepend(MarkdownChunker.Instance))
        {
            if (!IsName(chunker.Name))
                throw new ArgumentException(
                    $"'{chunker.Name}' cannot name a chunker. A name is up to 64 letters, digits, dots, hyphens and underscores, starting with a letter or digit.");
            if (!_byName.TryAdd(chunker.Name, chunker))
                throw new ArgumentException($"Two chunkers are named '{chunker.Name}'. Names are compared ignoring case.");
        }
    }

    /// <summary>Every registered name, the default first, then the rest ignoring case.</summary>
    public IReadOnlyList<string> Names =>
        [DefaultName, .. _byName.Keys.Where(n => n != DefaultName).Order(StringComparer.OrdinalIgnoreCase)];

    /// <summary>The chunker by that name, or null when this process has none.</summary>
    public IChunker? Find(string name) => _byName.GetValueOrDefault(name);

    /// <exception cref="UnknownChunkerException">This process has no chunker by that name.</exception>
    public IChunker Resolve(string name) => Find(name) ?? throw new UnknownChunkerException(name, Names);

    /// <summary>
    /// The name as the registered chunker spells it, for storing.
    /// </summary>
    /// <exception cref="ArgumentException">This process has no chunker by that name.</exception>
    public string Canonical(string name) =>
        Find(name)?.Name
        ?? throw new ArgumentException($"There is no chunker named '{name}'. This process has: {string.Join(", ", Names)}.");

    public static bool IsName(string name) =>
        name.Length is > 0 and <= 64 && char.IsAsciiLetterOrDigit(name[0])
        && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '-');
}

/// <summary>
/// A run named a chunker this process does not have. The run stops before it
/// reads a document, so nothing is chunked, embedded, stored or removed.
/// </summary>
public sealed class UnknownChunkerException(string name, IReadOnlyList<string> known) : InvalidOperationException(
    $"The chunker '{name}' is not registered in this process, so nothing was read or chunked. This process has: {string.Join(", ", known)}.")
{
    public string ChunkerName => name;
}

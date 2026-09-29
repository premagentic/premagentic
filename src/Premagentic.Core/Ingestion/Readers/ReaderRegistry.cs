namespace Premagentic.Core.Ingestion.Readers;

/// <summary>
/// The readers this process can use, by the extensions they claim. Markdown and
/// plain text are built in. A host that adds its own passes them to the
/// constructor and gives the one registry to the ingest pipeline, so every
/// connector reads the same set of formats and a run says the same thing about
/// a file it cannot read whichever connector found it.
/// <para>
/// An extension is matched ignoring case. A path this registry has no reader
/// for is not read and is counted, never guessed at: a reader that cannot read
/// a format would index a PDF's compressed bytes as if they were prose.
/// </para>
/// </summary>
public sealed class ReaderRegistry
{
    /// <summary>The built-in readers only, for a host that adds none.</summary>
    public static ReaderRegistry BuiltIn { get; } = new();

    private readonly Dictionary<string, IDocumentReader> _byExtension = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<IDocumentReader> _readers = [];

    /// <param name="more">Readers beside the built-in ones.</param>
    /// <exception cref="ArgumentException">
    /// A name or an extension is not usable, two readers share a name, or two
    /// readers claim one extension. The registry refuses rather than picking
    /// for them, and it refuses at composition, where a host can report it,
    /// instead of mid-run.
    /// </exception>
    public ReaderRegistry(params IDocumentReader[] more)
    {
        foreach (var reader in more.Prepend(PlainTextReader.Instance).Prepend(MarkdownReader.Instance))
        {
            if (!ChunkerRegistry.IsName(reader.Name))
                throw new ArgumentException(
                    $"'{reader.Name}' cannot name a reader. A name is up to 64 letters, digits, dots, hyphens and underscores, starting with a letter or digit.");
            if (_readers.Any(r => r.Name.Equals(reader.Name, StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException($"Two readers are named '{reader.Name}'. Names are compared ignoring case.");
            if (reader.Extensions.Count == 0)
                throw new ArgumentException($"The reader '{reader.Name}' claims no extension, so nothing would ever reach it.");

            foreach (var extension in reader.Extensions)
            {
                if (!IsExtension(extension))
                    throw new ArgumentException(
                        $"The reader '{reader.Name}' claims '{extension}', which is not an extension. An extension is a dot and at least one more character, lower case, with no path separator or space.");
                if (_byExtension.TryGetValue(extension, out var claimed))
                    throw new ArgumentException(
                        $"The readers '{claimed.Name}' and '{reader.Name}' both claim '{extension}'. One extension has one reader, and a built-in one cannot be taken over.");
                _byExtension[extension] = reader;
            }

            _readers.Add(reader);
        }
    }

    /// <summary>Every registered name, the built-in ones first, then the rest ignoring case.</summary>
    public IReadOnlyList<string> Names =>
        [.. _readers.Take(BuiltInCount).Select(r => r.Name), .. _readers.Skip(BuiltInCount).Select(r => r.Name).Order(StringComparer.OrdinalIgnoreCase)];

    /// <summary>Every extension that is read, ordered, with the dot.</summary>
    public IReadOnlyList<string> Extensions => [.. _byExtension.Keys.Order(StringComparer.OrdinalIgnoreCase)];

    /// <summary>
    /// The reader for this path's extension, or null when this process has
    /// none, which means the file is not read and is counted as such.
    /// </summary>
    public IDocumentReader? ForPath(string path) =>
        System.IO.Path.GetExtension(path) is { Length: > 1 } extension ? _byExtension.GetValueOrDefault(extension) : null;

    private const int BuiltInCount = 2;

    private static bool IsExtension(string extension) =>
        extension.Length > 1 && extension[0] == '.'
        && extension == extension.ToLowerInvariant()
        && !extension.Any(c => c is '/' or '\\' || char.IsWhiteSpace(c));
}

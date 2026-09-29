namespace Premagentic.Conformance;

/// <summary>
/// How the kit compares text that has been through a reader or a chunker with
/// the text it came from.
/// <para>
/// Line endings and runs of whitespace are not the contract. A chunker that
/// trims a section, drops a carriage return or joins lines with a single space
/// is still returning text taken from the document. Inventing a sentence, a
/// label or a summary is not, and that is what these comparisons are for.
/// </para>
/// </summary>
public static class ConformanceText
{
    /// <summary>The same text with every run of whitespace as one space, trimmed.</summary>
    public static string Flatten(string text) =>
        string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    /// <summary>True when <paramref name="part"/> is text taken from <paramref name="whole"/>.</summary>
    public static bool IsTakenFrom(string part, string whole) =>
        Flatten(part) is var flattened && (flattened.Length == 0 || Flatten(whole).Contains(flattened, StringComparison.Ordinal));
}

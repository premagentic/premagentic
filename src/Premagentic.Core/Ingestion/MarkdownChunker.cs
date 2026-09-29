using System.Text;

namespace Premagentic.Core.Ingestion;

/// <summary>
/// Structural Markdown chunker: splits a document into heading-scoped chunks.
/// Heading detection is line-based with code-fence tracking so a "# comment"
/// inside a ``` block is never mistaken for a heading.
/// <para>
/// The default chunker, <c>markdown</c>. <see cref="Chunk(string)"/> is the
/// chunker itself; the <see cref="IChunker"/> member calls it with the
/// document's text.
/// </para>
/// </summary>
public sealed class MarkdownChunker : IChunker
{
    /// <summary>Soft cap per chunk; an oversized section is split on blank lines.</summary>
    public const int MaxChunkChars = 2000;

    /// <summary>
    /// The most one paragraph may hold before it is cut: twice the soft cap. A
    /// table or a code block up to this stays whole. One past it is cut into
    /// pieces of the soft cap, at the last line break inside each when there is
    /// one, because a chunk's search vector must stay under PostgreSQL's 1 MB,
    /// and a paragraph of a few megabytes kept whole would fail the write of
    /// every run of its source.
    /// </summary>
    public const int MaxParagraphChars = MaxChunkChars * 2;

    public static MarkdownChunker Instance { get; } = new();

    private MarkdownChunker()
    {
    }

    public string Name => ChunkerRegistry.DefaultName;

    IReadOnlyList<DocumentChunk> IChunker.Chunk(SourceDocument document) => Chunk(document.Text);

    public static IReadOnlyList<DocumentChunk> Chunk(string body)
    {
        var sections = SplitByHeadings(body);
        var chunks = new List<DocumentChunk>();
        var seq = 0;
        foreach (var (headingPath, text) in sections)
        {
            foreach (var piece in SplitOversized(text))
            {
                var trimmed = piece.Trim();
                if (trimmed.Length == 0) continue;
                chunks.Add(new DocumentChunk(seq++, headingPath, trimmed));
            }
        }
        return chunks;
    }

    private static List<(string HeadingPath, string Text)> SplitByHeadings(string body)
    {
        var results = new List<(string, string)>();
        // Heading stack indexed by level (1-based); level 0 unused.
        var headings = new string?[7];
        var current = new StringBuilder();
        var inFence = false;

        void Flush()
        {
            if (current.Length > 0)
            {
                var path = string.Join(" > ", headings.Where(h => !string.IsNullOrEmpty(h)));
                results.Add((path, current.ToString()));
                current.Clear();
            }
        }

        foreach (var rawLine in body.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.TrimStart().StartsWith("```")) inFence = !inFence;

            var level = inFence ? 0 : HeadingLevel(line);
            if (level > 0)
            {
                Flush();
                headings[level] = line.TrimStart('#').Trim();
                for (var l = level + 1; l < headings.Length; l++) headings[l] = null;
            }
            else
            {
                // Explicit '\n' (not AppendLine's \r\n) so "\n\n" paragraph
                // splitting works identically on every OS.
                current.Append(line).Append('\n');
            }
        }
        Flush();
        return results;
    }

    private static int HeadingLevel(string line)
    {
        if (!line.StartsWith('#')) return 0;
        var level = 0;
        while (level < line.Length && line[level] == '#') level++;
        // "#Heading" without a space is not a Markdown heading; "######" max level 6.
        return level <= 6 && level < line.Length && line[level] == ' ' ? level : 0;
    }

    private static IEnumerable<string> SplitOversized(string text)
    {
        if (text.Length <= MaxChunkChars)
        {
            yield return text;
            yield break;
        }

        var paragraphs = text.Split("\n\n").SelectMany(Pieces);
        var current = new StringBuilder();
        foreach (var p in paragraphs)
        {
            if (current.Length > 0 && current.Length + p.Length > MaxChunkChars)
            {
                yield return current.ToString();
                current.Clear();
            }
            current.Append(p).Append("\n\n");

            // A paragraph over the soft cap and within MaxParagraphChars (a
            // table or a code block) is emitted whole: splitting mid-table
            // destroys meaning, and the embedding provider truncates anyway.
            if (current.Length > MaxChunkChars * 2)
            {
                yield return current.ToString();
                current.Clear();
            }
        }
        if (current.Length > 0) yield return current.ToString();
    }

    private static readonly char[] Blanks = [' ', '\t'];

    /// <summary>
    /// A paragraph within <see cref="MaxParagraphChars"/> as it is; a longer
    /// one in pieces of at most <see cref="MaxChunkChars"/>, each cut after
    /// its last line break in the second half of the cap, or else after its
    /// last space there, so no word is cut in two; and when it has neither,
    /// at the cap, never between the two halves of a character.
    /// </summary>
    private static IEnumerable<string> Pieces(string paragraph)
    {
        if (paragraph.Length <= MaxParagraphChars)
        {
            yield return paragraph;
            yield break;
        }

        var start = 0;
        while (paragraph.Length - start > MaxChunkChars)
        {
            var cut = start + MaxChunkChars;
            var lineBreak = paragraph.LastIndexOf('\n', cut - 1, MaxChunkChars / 2);
            var blank = lineBreak >= 0 ? lineBreak : paragraph.LastIndexOfAny(Blanks, cut - 1, MaxChunkChars / 2);
            if (blank > start) cut = blank + 1;
            else if (char.IsHighSurrogate(paragraph[cut - 1])) cut--;
            yield return paragraph[start..cut];
            start = cut;
        }
        yield return paragraph[start..];
    }
}

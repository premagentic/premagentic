using System.Text;
using Premagentic.Core.Sources;

namespace Premagentic.Core.Ingestion.Readers;

/// <summary>
/// Reads Markdown: a leading frontmatter block is split off and its fields are
/// returned, and the body is the text to index.
/// </summary>
public sealed class MarkdownReader : IDocumentReader
{
    public static MarkdownReader Instance { get; } = new();

    public string Name => "markdown";

    public IReadOnlyList<string> Extensions { get; } = [".md", ".markdown"];

    public Task<ReadDocument> ReadAsync(Stream content, string path, CancellationToken ct) =>
        FrontmatterText.ReadAsync(content, ct);
}

/// <summary>
/// Reads plain text. It reads a frontmatter block the same way
/// <see cref="MarkdownReader"/> does, because that is what this product has
/// always done with a <c>.txt</c> file and a stored document's title and status
/// would change under anyone who has one.
/// </summary>
public sealed class PlainTextReader : IDocumentReader
{
    public static PlainTextReader Instance { get; } = new();

    public string Name => "text";

    public IReadOnlyList<string> Extensions { get; } = [".txt"];

    public Task<ReadDocument> ReadAsync(Stream content, string path, CancellationToken ct) =>
        FrontmatterText.ReadAsync(content, ct);
}

/// <summary>
/// The reading both built-ins do: decode as UTF-8, split the frontmatter block
/// off the body, and take the title from the block or from the first heading.
/// The whole file is read, so the size a file may be is the pipeline's limit,
/// <see cref="DocumentSourceReading.MaxFileBytes"/>, which refuses a larger one
/// before either built-in sees it.
/// </summary>
internal static class FrontmatterText
{
    internal static async Task<ReadDocument> ReadAsync(Stream content, CancellationToken ct)
    {
        // Byte-order mark detection on, matching how these files were read
        // before there was a reader seam: a mark is stripped rather than
        // decoded into the first character of the document.
        using var reader = new StreamReader(
            content, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        var raw = await reader.ReadToEndAsync(ct);

        var parsed = Frontmatter.Parse(raw);
        var title = parsed.Fields.GetValueOrDefault("title") ?? FirstH1(parsed.Body);
        return new ReadDocument(parsed.Body, title, parsed.Fields) { ParsedFrontmatter = parsed };
    }

    private static string? FirstH1(string body)
    {
        foreach (var line in body.Split('\n'))
            if (line.StartsWith("# ")) return line[2..].Trim();
        return null;
    }
}

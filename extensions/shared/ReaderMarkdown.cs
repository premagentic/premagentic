using System.Text;

namespace Premagentic.Extensions.Shared;

/// <summary>
/// Turns text taken out of a document into Markdown the chunkers cut on, with
/// the document unable to add structure of its own. A heading the reader writes
/// is a locator a citation carries, so a line of document text that begins with
/// <c>#</c> would otherwise pose as one, and a line that begins with three
/// backticks would open a fence that hides every heading after it. Such a line
/// is escaped with a backslash and keeps every character it had.
/// </summary>
internal static class ReaderMarkdown
{
    /// <summary>One line of document text, safe to place in the body.</summary>
    internal static string Line(string line)
    {
        var trimmed = line.TrimStart();
        if (trimmed.StartsWith('#') || trimmed.StartsWith("```", StringComparison.Ordinal) || trimmed.StartsWith("~~~", StringComparison.Ordinal))
            return "\\" + trimmed;
        return line;
    }

    /// <summary>
    /// A block of document text, line by line: line endings made <c>\n</c>,
    /// control characters other than tab removed, each line escaped by
    /// <see cref="Line"/>, and trailing white space dropped.
    /// </summary>
    internal static string Block(string text)
    {
        var clean = new StringBuilder(text.Length);
        foreach (var c in text.ReplaceLineEndings("\n"))
            if (c is '\n' or '\t' || !char.IsControl(c)) clean.Append(c);

        var lines = clean.ToString().Split('\n');
        for (var i = 0; i < lines.Length; i++) lines[i] = Line(lines[i].TrimEnd());
        return string.Join('\n', lines).Trim('\n');
    }

    /// <summary>A heading the reader writes, from text it chose, on one line.</summary>
    internal static string Heading(int level, string text) =>
        new string('#', Math.Clamp(level, 1, 6)) + " " + Block(text).Replace('\n', ' ').Trim();
}

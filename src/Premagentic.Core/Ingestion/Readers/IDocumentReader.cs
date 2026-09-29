using Premagentic.Core.Sources;

namespace Premagentic.Core.Ingestion.Readers;

/// <summary>
/// What a reader got out of one file: the text to index, a title if the format
/// carries one, and the frontmatter fields if it has any.
/// <para>
/// <paramref name="Text"/> is Markdown or close to it, because the chunkers cut
/// on headings. A reader for a format with its own structure converts to
/// Markdown, which is how extraction libraries emit text anyway.
/// </para>
/// <para>
/// A reader reads. It never writes, never calls out, and nothing in the file is
/// executed or followed: a link, a macro, a formula and an embedded script are
/// all just text or are left out.
/// </para>
/// </summary>
/// <param name="Title">
/// The title the format states, or null when it states none. The connector
/// decides what to fall back to, usually the file name.
/// </param>
/// <param name="Frontmatter">
/// The scalar frontmatter fields, or null from a format that has no
/// frontmatter. Read as text; nothing in it is interpreted beyond the fields
/// the connector looks up.
/// </param>
public sealed record ReadDocument(string Text, string? Title, IReadOnlyDictionary<string, string>? Frontmatter)
{
    /// <summary>
    /// The whole frontmatter parse, from a reader that parses one:
    /// <see cref="FrontmatterResult.Values"/> with its nested entries, and
    /// <see cref="FrontmatterResult.State"/>, which says whether a block was
    /// there and whether it could be read. <see cref="Frontmatter"/> flattens
    /// both away, and the Open Knowledge Format layer needs them: a concept
    /// whose block could not be read is held to a different rule than one with
    /// no block at all.
    /// <para>
    /// Null from a reader that parses no frontmatter, which a connector reads as
    /// a document with none.
    /// </para>
    /// </summary>
    public FrontmatterResult? ParsedFrontmatter { get; init; }

    /// <summary>
    /// Set by a reader that recognized the file and will not index it, and says
    /// why in a few words a person can act on: <c>no text layer</c>,
    /// <c>macro-enabled</c>. The run counts the file under its extension and
    /// this reason, so a folder of files nobody can search does not read as a
    /// clean run. Null for a document that is to be indexed. Make one with
    /// <see cref="Skipped"/>.
    /// <para>
    /// A file that exists and cannot be read at all, because it is password
    /// protected or damaged, is not this: throw
    /// <see cref="UnreadableDocumentException"/>, which keeps the file's
    /// existing index entry.
    /// </para>
    /// </summary>
    public string? SkipReason { get; init; }

    /// <summary>A file this reader recognized and will not index, for the stated reason.</summary>
    /// <exception cref="ArgumentException">The reason is blank.</exception>
    public static ReadDocument Skipped(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        return new ReadDocument("", null, null) { SkipReason = reason.Trim() };
    }
}

/// <summary>
/// Thrown by a reader for a file that exists and cannot be read: password
/// protected, damaged, or over a limit the reader keeps for hostile input. The
/// run reports the path with the message as its reason, keeps the file's
/// existing index entry, and goes on to the next file. The message is shown to
/// an administrator, so it says what is wrong with the file and quotes none of
/// its content.
/// </summary>
public sealed class UnreadableDocumentException(string reason) : IOException(reason);

/// <summary>
/// Turns one file's bytes into text to index. The seam that lets an
/// installation read a format this core does not: a reader ships in an
/// extension, the administrator allow-lists it, and every connector gains the
/// format at once, because connectors find paths and readers read them.
/// <para>
/// A reader is asked for a file only when its extension is one it declared, and
/// a path no reader claims is counted as not read rather than passed to
/// something that would guess at it.
/// </para>
/// </summary>
public interface IDocumentReader
{
    /// <summary>
    /// The name this reader is known by, shaped like a chunker name: up to 64
    /// letters, digits, dots, hyphens and underscores, starting with a letter
    /// or digit. Two readers in one process may not share a name.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// The extensions this reader claims, lower case and with the dot
    /// (<c>.md</c>, <c>.markdown</c>). Two readers in one process may not claim
    /// the same extension: a file has one reader, decided when the process is
    /// composed and not at the moment it is read.
    /// </summary>
    IReadOnlyList<string> Extensions { get; }

    /// <summary>
    /// Reads <paramref name="content"/>, which is positioned at the start and
    /// is the caller's to dispose. The pipeline hands a read-only
    /// <see cref="MemoryStream"/> over the file's bytes, already read and
    /// hashed, with its buffer visible: a reader that needs the whole file can
    /// take it with <see cref="MemoryStream.TryGetBuffer"/> instead of copying
    /// it, and only reads it.
    /// </summary>
    /// <param name="path">
    /// The path the content came from, for a reader that needs the file name or
    /// wants it in a message. It is the connector's path, not a local one, and
    /// nothing may be opened through it: the stream is the only content.
    /// </param>
    /// <remarks>
    /// One bad file never stops a run: whatever a reader throws is contained
    /// per file and reported as that file being unreadable.
    /// <see cref="UnreadableDocumentException"/> is the way to say so with a
    /// reason; <see cref="ReadDocument.Skipped"/> is the way to say a file is
    /// recognized and not indexed. A reader honors <paramref name="ct"/>.
    /// </remarks>
    Task<ReadDocument> ReadAsync(Stream content, string path, CancellationToken ct);
}

using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using Premagentic.Core.Sources;

namespace Premagentic.Core.Ingestion.Readers;

/// <summary>
/// The reading step between a connector and the pipeline: a connector says
/// which paths it found, and this picks the reader for each one. A path no
/// reader claims comes back as a skip with its extension, which is how a run
/// can say what it did not read; a path that could not be opened or read comes
/// back as a failure, which keeps its index entry.
/// </summary>
public static class DocumentSourceReading
{
    /// <summary>
    /// The largest file the pipeline reads at all, whatever reader it is for:
    /// 256 MB. A larger file is reported unreadable before any reader sees it,
    /// with the reason <c>larger than 256 MB, the most the pipeline reads</c>,
    /// and is never read more than one byte past this. It sits above the
    /// first-party readers' own limits, so a PDF or a Word file over theirs is
    /// still refused with a reason that names its format, and it is the only
    /// limit the built-in Markdown and text readers have.
    /// </summary>
    public const long MaxFileBytes = 256L * 1024 * 1024;

    /// <summary>
    /// Enumerates a connector and reads whatever it did not read itself. A
    /// connector that yields finished documents passes through untouched.
    /// </summary>
    public static async IAsyncEnumerable<SourceRead> ReadThroughAsync(
        this IDocumentSource source, ReaderRegistry readers,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var found in source.EnumerateAsync(ct))
            yield return found.Content is { } content ? await ReadAsync(content, readers, ct) : found;
    }

    /// <summary>
    /// Reads one path through the registry. Never throws for a file: an
    /// unreadable one is a <see cref="SourceFailure"/>, because one locked,
    /// encrypted or damaged file must not cost the run every document beside
    /// it. That holds for a reader that throws too: a reader is code an
    /// administrator allowed, and a file it chokes on is a fact about the file
    /// or a defect in the reader, never a reason to end the run.
    /// </summary>
    public static Task<SourceRead> ReadAsync(
        SourceContent content, ReaderRegistry readers, CancellationToken ct = default) =>
        ReadAsync(content, readers, MaxFileBytes, ct);

    /// <summary>
    /// <see cref="ReadAsync(SourceContent, ReaderRegistry, CancellationToken)"/>
    /// with another size limit, so a test can prove the limit without a file
    /// of 256 MB.
    /// </summary>
    internal static async Task<SourceRead> ReadAsync(
        SourceContent content, ReaderRegistry readers, long maxFileBytes, CancellationToken ct)
    {
        var reader = readers.ForPath(content.Path);
        if (reader is null) return SourceRead.Skipped(content.Path, SourceSkip.ExtensionOf(content.Path));

        ReadDocument read;
        string hash;
        try
        {
            // The bytes are held once and both hashed and handed to the reader,
            // so the hash covers exactly what was read. Hashing the stream as
            // the reader pulls it would describe only as much of the file as a
            // reader that stops early chose to look at.
            byte[] bytes;
            await using (var stream = await content.Open(ct))
                bytes = await ReadBoundedAsync(stream, maxFileBytes, ct);

            hash = Convert.ToHexString(SHA256.HashData(bytes));
            // The reader is handed the array itself, read-only as a stream and
            // with its buffer visible, so a reader that needs the whole file
            // takes it instead of copying it. The array is hashed above and
            // never read again after the reader returns: what the reader
            // returned is judged, not the array, so a reader that wrote into
            // the buffer would change only its own reading.
            using var forReader = new MemoryStream(bytes, 0, bytes.Length, writable: false, publiclyVisible: true);
            read = await reader.ReadAsync(forReader, content.Path, ct);
        }
        catch (UnreadableDocumentException ex)
        {
            // The reader says why in words meant for a person, so they are the
            // reason as they are, without a type name in front.
            return SourceRead.Failed(content.Path, ex.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // Encrypted with a key this account does not hold, denied by an
            // access list, or locked by another process. The path exists, so it
            // is reported as unreadable and its index entry is preserved.
            return SourceRead.Failed(content.Path, $"{ex.GetType().Name}: {ex.Message}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // A reader that threw on this file. Contained the way a locked file
            // is: the path is reported unreadable with the reader named, its
            // index entry stays, and the next file is read. A run that is being
            // canceled is the only thing allowed through.
            return SourceRead.Failed(content.Path,
                $"the reader '{reader.Name}' failed on this file ({ex.GetType().Name}: {Brief(ex.Message)})");
        }

        // Recognized and not indexed, for a reason the reader states. The file
        // is counted under its extension and the reason, and it is not in the
        // index, so nothing at this path is kept.
        if (read.SkipReason is { } why)
            return SourceRead.Skipped(content.Path, $"{SourceSkip.ExtensionOf(content.Path)} ({why})", why);

        // What a file holds is judged here, one file at a time. PostgreSQL
        // stores no NUL, and UTF-8 has no way to write half of a character, so
        // a document holding either would fail at the write, where the failure
        // ends the whole run; here it is one unreadable file, its existing
        // entry kept, and the next file is read.
        if (!Storable(read))
            return SourceRead.Failed(content.Path, UnstorableReason);

        // Outside the containment on purpose: finishing the document is the
        // connector's code, and a defect there must not be reported as a file
        // a reader could not read.
        return SourceRead.Ok(content.Complete(read, hash));
    }

    /// <summary>
    /// The whole stream, when it holds no more than <paramref name="limit"/>
    /// bytes. A stream that states its length is refused over the limit
    /// before a byte is read; any other is read at most one byte past the
    /// limit, which is what tells a stream longer than the limit from one
    /// exactly as long. A stream that states its length, and holds that many
    /// bytes, is read into an array of exactly that size, which is returned
    /// as it is rather than copied.
    /// </summary>
    internal static async Task<byte[]> ReadBoundedAsync(Stream stream, long limit, CancellationToken ct)
    {
        long? stated = stream.CanSeek ? stream.Length - stream.Position : null;
        if (stated > limit) throw TooLarge(limit);

        using var buffer = stated is { } known ? new MemoryStream((int)Math.Min(known, int.MaxValue)) : new MemoryStream();
        var chunk = new byte[81920];
        long total = 0;
        int got;
        while ((got = await stream.ReadAsync(chunk.AsMemory(0, (int)Math.Min(chunk.Length, limit + 1 - total)), ct)) > 0)
        {
            total += got;
            if (total > limit) throw TooLarge(limit);
            buffer.Write(chunk, 0, got);
        }
        return buffer.Length == buffer.Capacity ? buffer.GetBuffer() : buffer.ToArray();
    }

    private static UnreadableDocumentException TooLarge(long limit) => new(
        limit % (1024 * 1024) == 0
            ? $"larger than {limit / (1024 * 1024)} MB, the most the pipeline reads"
            : $"larger than {limit} bytes, the most the pipeline reads");

    internal const string UnstorableReason = "holds a NUL byte or half of a character, which the index cannot store";

    /// <summary>Whether the index can store everything a reader returned: the text, the title and every frontmatter key and value.</summary>
    private static bool Storable(ReadDocument read) =>
        Storable(read.Text) && Storable(read.Title)
        && (read.Frontmatter?.All(f => Storable(f.Key) && Storable(f.Value)) ?? true)
        && (read.ParsedFrontmatter is not { } parsed
            || (parsed.Fields.All(f => Storable(f.Key) && Storable(f.Value)) && StorableValue(parsed.Values)));

    /// <summary>A parsed frontmatter value, nested sequences and mappings included.</summary>
    private static bool StorableValue(object? value) => value switch
    {
        null => true,
        string text => Storable(text),
        IEnumerable<KeyValuePair<string, object>> mapping => mapping.All(p => Storable(p.Key) && StorableValue(p.Value)),
        System.Collections.IEnumerable sequence => sequence.Cast<object?>().All(StorableValue),
        _ => true,
    };

    /// <summary>
    /// Whether PostgreSQL can store <paramref name="text"/>: no NUL, and no half
    /// of a surrogate pair without its other half, which no UTF-8 encoder can
    /// write.
    /// </summary>
    internal static bool Storable(string? text)
    {
        if (text is null) return true;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\0') return false;
            if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                i++;
                continue;
            }
            if (char.IsSurrogate(c)) return false;
        }
        return true;
    }

    /// <summary>One line and at most 200 characters, so a library's message cannot fill a report.</summary>
    private static string Brief(string message)
    {
        var line = message.ReplaceLineEndings(" ").Trim();
        return line.Length <= 200 ? line : line[..200] + "...";
    }
}

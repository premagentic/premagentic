using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Compression;
using System.Xml;
using System.Xml.Linq;
using Premagentic.Core.Ingestion.Readers;

namespace Premagentic.Extensions.Shared;

/// <summary>
/// How much of a zip-based document (a Word file, a workbook) a reader will
/// take on. Such a file is a zip, and a zip can be built to expand without end,
/// so every limit here is about the container. A file over any of them is
/// reported unreadable with the limit it crossed, never read in part.
/// </summary>
/// <param name="MaxEntries">The most entries the zip may hold.</param>
/// <param name="MaxUncompressedBytes">
/// The most bytes the zip may say it expands to, over every entry, pictures
/// and embedded files included, though those are never opened.
/// </param>
/// <param name="MaxReadBytes">
/// The most bytes the parts a reader does read may really expand to together,
/// counted as they are read. Checked apart from what the zip says, because the
/// sizes a zip states are written by whoever made it.
/// </param>
/// <param name="MaxCompressionRatio">
/// How many times its compressed size a part a reader reads may expand to,
/// once it is past <paramref name="RatioFloorBytes"/>.
/// </param>
/// <param name="RatioFloorBytes">How far a part may expand before its ratio is held to the limit.</param>
/// <param name="MaxDepth">
/// How deep the XML of a part a reader reads may nest, measured before the
/// part is handed over, so no walk of it can go deeper.
/// </param>
internal sealed record ZipLimits(
    int MaxEntries, long MaxUncompressedBytes, long MaxReadBytes, int MaxCompressionRatio, long RatioFloorBytes, int MaxDepth);

/// <summary>How long one file may take, checked at every step a reader repeats.</summary>
internal sealed class ReadClock(TimeSpan budget, CancellationToken ct)
{
    private readonly Stopwatch _elapsed = Stopwatch.StartNew();

    public void Check()
    {
        ct.ThrowIfCancellationRequested();
        if (_elapsed.Elapsed > budget)
            throw new UnreadableDocumentException($"took longer than {budget.TotalSeconds:0.#} seconds to read");
    }
}

/// <summary>
/// A zip-based document, opened only through the container's limits, and the
/// parts a reader reads from it. A part is found by its name as the packaging
/// rules compare names, ignoring case, and a name held by two entries refuses
/// the file: two readers of it could see two documents. Every part is read
/// through a count of the bytes it really expands to, refused when it is itself
/// an archive, and parsed with no document type definition, so no entity can
/// name a file or an address, and no resolver, so nothing a part names is ever
/// fetched.
/// </summary>
internal sealed class ZipParts : IDisposable
{
    private static readonly byte[] ZipLocalHeader = [0x50, 0x4B, 0x03, 0x04];
    private const string OfficeDocumentRelationship = "/officeDocument";

    private readonly ZipArchive _zip;
    private readonly ZipLimits _limits;
    private readonly ReadClock _clock;
    private readonly string _damaged;
    private long _read;

    private ZipParts(ZipArchive zip, IReadOnlyList<ZipArchiveEntry> entries, ZipLimits limits, ReadClock clock, string damaged)
    {
        _zip = zip;
        Entries = entries;
        _limits = limits;
        _clock = clock;
        _damaged = damaged;
    }

    /// <summary>Every entry the zip holds, none of them opened.</summary>
    public IReadOnlyList<ZipArchiveEntry> Entries { get; }

    /// <summary>
    /// Opens <paramref name="bytes"/> as a zip, refusing it before anything is
    /// read when it holds too many entries or says it expands too far.
    /// </summary>
    /// <param name="damaged">
    /// The reason given for a file that is not a zip or whose parts cannot be
    /// parsed, naming the format the reader expected: <c>damaged, or not a Word file</c>.
    /// </param>
    public static ZipParts Open(byte[] bytes, ZipLimits limits, ReadClock clock, string damaged)
    {
        // The count a zip states for itself, read before the archive is
        // opened: opening it reads every entry's record into memory.
        if (StatedEntryCount(bytes) is { } stated && stated > limits.MaxEntries)
            throw new UnreadableDocumentException($"holds {stated} entries, more than the {limits.MaxEntries} this reader opens");

        ZipArchive zip;
        try
        {
            zip = new ZipArchive(new MemoryStream(bytes, writable: false), ZipArchiveMode.Read);
        }
        catch (InvalidDataException)
        {
            throw new UnreadableDocumentException(damaged);
        }

        try
        {
            IReadOnlyList<ZipArchiveEntry> entries;
            try
            {
                entries = zip.Entries;
            }
            catch (InvalidDataException)
            {
                throw new UnreadableDocumentException(damaged);
            }
            if (entries.Count > limits.MaxEntries)
                throw new UnreadableDocumentException($"holds {entries.Count} entries, more than the {limits.MaxEntries} this reader opens");

            // Added up entry by entry against what is left, so no stated size,
            // however large, can overflow the sum.
            long statedBytes = 0;
            foreach (var entry in entries)
            {
                if (entry.Length < 0 || entry.Length > limits.MaxUncompressedBytes - statedBytes)
                    throw new UnreadableDocumentException(
                        $"says it expands to more than {Sizes.Of(limits.MaxUncompressedBytes)}, the most this reader opens");
                statedBytes += entry.Length;
            }

            return new ZipParts(zip, entries, limits, clock, damaged);
        }
        catch
        {
            zip.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Whether the file carries a macro project anywhere. Never opened, only
    /// looked for: one makes the file macro-enabled whatever its name says.
    /// </summary>
    public bool HoldsMacroProject => Entries.Any(e => e.FullName.EndsWith("vbaProject.bin", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The name of the file's main part, from the package's own relationships,
    /// or <paramref name="fallback"/> when they name none. A name inside this
    /// file, never a path or an address outside it: one that climbs, or holds a
    /// colon or a backslash, refuses the file.
    /// </summary>
    public string MainPart(string fallback)
    {
        var target = Relationships("")
            .Where(r => r.Type.EndsWith(OfficeDocumentRelationship, StringComparison.Ordinal) && !r.External)
            .Select(r => r.Target)
            .FirstOrDefault(t => t.Length > 0);
        if (target is null) return fallback;

        var name = target.TrimStart('/');
        if (name.Contains("..", StringComparison.Ordinal) || name.Contains(':') || name.Contains('\\'))
            throw new UnreadableDocumentException(_damaged);
        return name;
    }

    /// <summary>Whether the content types declare <paramref name="mainPart"/> a macro-enabled document.</summary>
    public bool DeclaresMacroEnabled(string mainPart) =>
        Xml("[Content_Types].xml")?.Root?.Elements()
            .Where(e => e.Name.LocalName == "Override"
                        && ((string?)e.Attribute("PartName"))?.TrimStart('/').Equals(mainPart, StringComparison.OrdinalIgnoreCase) == true)
            .Any(e => ((string?)e.Attribute("ContentType"))?.Contains("macroEnabled", StringComparison.OrdinalIgnoreCase) == true) == true;

    /// <summary>One relationship a part states: its id, its type, where it points, and whether that is outside the file.</summary>
    internal sealed record Relationship(string Id, string Type, string Target, bool External);

    /// <summary>
    /// The relationships <paramref name="part"/> states, from its <c>_rels</c>
    /// part; empty when it states none. <c>""</c> is the package itself. Only
    /// read: nothing a relationship points at is opened here.
    /// </summary>
    public IReadOnlyList<Relationship> Relationships(string part)
    {
        var slash = part.LastIndexOf('/');
        var rels = slash < 0 ? $"_rels/{part}.rels" : $"{part[..(slash + 1)]}_rels/{part[(slash + 1)..]}.rels";
        return Xml(rels)?.Root?.Elements()
            .Where(e => e.Name.LocalName == "Relationship")
            .Select(e => new Relationship(
                (string?)e.Attribute("Id") ?? "",
                (string?)e.Attribute("Type") ?? "",
                (string?)e.Attribute("Target") ?? "",
                (string?)e.Attribute("TargetMode") is not (null or "Internal")))
            .ToList() ?? [];
    }

    /// <summary>
    /// The part a relationship of <paramref name="source"/> points at, as a
    /// name inside this file: relative to the folder of the source, or from the
    /// top of the file when it starts with a slash. A name that climbs, names a
    /// folder, or holds a colon or a backslash once unescaped, refuses the file.
    /// </summary>
    public string Resolve(string source, string target)
    {
        var name = Uri.UnescapeDataString(target);
        if (name.StartsWith('/')) name = name.TrimStart('/');
        else
        {
            var slash = source.LastIndexOf('/');
            name = slash < 0 ? name : source[..(slash + 1)] + name;
        }
        if (name.Length == 0 || name.EndsWith('/') || name.Contains("..", StringComparison.Ordinal) || name.Contains(':') || name.Contains('\\'))
            throw new UnreadableDocumentException(_damaged);
        return name;
    }

    /// <summary>
    /// The XML of one part, loaded whole once it has been measured; null when
    /// the file has no such part.
    /// </summary>
    public XDocument? Xml(string name)
    {
        using var reader = Reader(name);
        if (reader is null) return null;
        try
        {
            return XDocument.Load(reader);
        }
        catch (XmlException)
        {
            throw new UnreadableDocumentException(_damaged);
        }
    }

    /// <summary>
    /// A reader over one part's XML, for a part too large to load whole; null
    /// when the file has no such part. The part is parsed once before it is
    /// returned, by a reader that keeps nothing, so a part that is not well
    /// formed or nests deeper than the limit is refused here, and the reader
    /// returned meets neither.
    /// </summary>
    public XmlReader? Reader(string name)
    {
        var data = Bytes(name);
        if (data is null) return null;

        try
        {
            using var scan = XmlReader.Create(new MemoryStream(data, writable: false), Settings());
            for (var nodes = 1L; scan.Read(); nodes++)
            {
                if (scan.Depth > _limits.MaxDepth)
                    throw new UnreadableDocumentException(
                        $"the part {name} nests its XML more than {_limits.MaxDepth} deep, the most this reader follows");
                if (nodes % 4096 == 0) _clock.Check();
            }
        }
        catch (XmlException)
        {
            throw new UnreadableDocumentException(_damaged);
        }

        return XmlReader.Create(new MemoryStream(data, writable: false), Settings());
    }

    /// <summary>
    /// The bytes of one part, for a part that is not XML, such as a binary
    /// workbook's records; null when the file has no such part. Read through
    /// the same count, ratio and refusals as every part a reader reads.
    /// </summary>
    public byte[]? Bytes(string name)
    {
        _clock.Check();
        var found = Entries.Where(e => e.FullName.Equals(name, StringComparison.OrdinalIgnoreCase)).Take(2).ToList();
        if (found.Count == 0) return null;
        if (found.Count > 1) throw new UnreadableDocumentException($"holds two parts named {name}");

        byte[] data;
        try
        {
            data = ReadCounted(found[0]);
        }
        catch (InvalidDataException)
        {
            throw new UnreadableDocumentException(_damaged);
        }

        if (data.AsSpan().StartsWith(ZipLocalHeader))
            throw new UnreadableDocumentException($"holds an archive where the part {name} should be");
        return data;
    }

    private static XmlReaderSettings Settings() => new()
    {
        // No document type definition, so no entity, and no resolver, so
        // nothing a part names is ever fetched.
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        MaxCharactersFromEntities = 0,
        IgnoreComments = true,
        IgnoreProcessingInstructions = true,
    };

    private byte[] ReadCounted(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        using var copy = new MemoryStream();
        var chunk = new byte[64 * 1024];
        int got;
        while ((got = stream.Read(chunk, 0, chunk.Length)) > 0)
        {
            _clock.Check();
            copy.Write(chunk, 0, got);
            _read += got;
            if (_read > _limits.MaxReadBytes)
                throw new UnreadableDocumentException(
                    $"its text parts expand past {Sizes.Of(_limits.MaxReadBytes)}, the most this reader reads");
            if (copy.Length > _limits.RatioFloorBytes && copy.Length > Math.Max(1, entry.CompressedLength) * _limits.MaxCompressionRatio)
                throw new UnreadableDocumentException(
                    $"the part {entry.FullName} expands more than {_limits.MaxCompressionRatio} times its size in the file");
        }
        return copy.ToArray();
    }

    /// <summary>
    /// The number of entries the zip's end record states, or null when there is
    /// no plain end record to read (a damaged file, or one written with the
    /// 64-bit layout, whose count is then checked once the archive is open).
    /// </summary>
    internal static long? StatedEntryCount(byte[] bytes)
    {
        const int endRecord = 22;
        var earliest = Math.Max(0, bytes.Length - endRecord - ushort.MaxValue);
        for (var at = bytes.Length - endRecord; at >= earliest; at--)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at)) != 0x06054b50) continue;
            var total = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(at + 10));
            return total == ushort.MaxValue ? null : total;
        }
        return null;
    }

    public void Dispose() => _zip.Dispose();
}

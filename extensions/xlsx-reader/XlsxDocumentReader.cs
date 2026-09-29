using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Premagentic.Core.Extensions;
using Premagentic.Core.Ingestion.Readers;
using Premagentic.Extensions.Shared;

namespace Premagentic.Extensions.Xlsx;

/// <summary>Registers the Excel reader.</summary>
public sealed class XlsxReaderExtension : IExtension
{
    public string Name => "xlsx-reader";

    public void Register(ExtensionRegistrations registrations) => registrations.AddReader(new XlsxDocumentReader());
}

/// <summary>
/// How much of a workbook this reader will take on. A .xlsx is a zip, so the
/// first limits are about the container, as for a Word file; the rest are
/// about the workbook inside it. A file over any of them is reported
/// unreadable with the limit it crossed, never read in part.
/// </summary>
/// <param name="MaxFileBytes">The largest file read at all.</param>
/// <param name="MaxEntries">The most entries the zip may hold.</param>
/// <param name="MaxUncompressedBytes">The most bytes the zip may say it expands to, over every entry.</param>
/// <param name="MaxReadBytes">
/// The most bytes the parts this reader reads (the workbook, its sheets, its
/// shared strings and styles) may really expand to together, counted as they
/// are read.
/// </param>
/// <param name="MaxCompressionRatio">
/// How many times its compressed size a part this reader reads may expand to,
/// once it is past <paramref name="RatioFloorBytes"/>.
/// </param>
/// <param name="RatioFloorBytes">How far a part may expand before its ratio is held to the limit.</param>
/// <param name="MaxSheets">The most sheets the workbook may list.</param>
/// <param name="MaxCells">
/// The most cells read, over every sheet, empty cells included, since a sheet
/// of empty cells gives no text for the text limit to count. The shared
/// strings are held to the same number: a table longer than the cells that
/// could use it is not a workbook anyone saved.
/// </param>
/// <param name="MaxTextChars">The most characters of text a file may give.</param>
/// <param name="TimeBudget">How long one file may take, checked at every row.</param>
/// <param name="MaxDepth">How deep the XML of a part this reader reads may nest, measured before it is read.</param>
public sealed record XlsxReaderLimits(
    long MaxFileBytes, int MaxEntries, long MaxUncompressedBytes, long MaxReadBytes, int MaxCompressionRatio, long RatioFloorBytes,
    int MaxSheets, int MaxCells, int MaxTextChars, TimeSpan TimeBudget, int MaxDepth)
{
    public static XlsxReaderLimits Default { get; } = new(
        MaxFileBytes: 200L * 1024 * 1024,
        MaxEntries: 5000,
        MaxUncompressedBytes: 1024L * 1024 * 1024,
        MaxReadBytes: 64L * 1024 * 1024,
        MaxCompressionRatio: 100,
        RatioFloorBytes: 1024 * 1024,
        MaxSheets: 256,
        MaxCells: 1_000_000,
        MaxTextChars: 10_000_000,
        TimeBudget: TimeSpan.FromSeconds(60),
        MaxDepth: 256);

    internal ZipLimits Zip => new(MaxEntries, MaxUncompressedBytes, MaxReadBytes, MaxCompressionRatio, RatioFloorBytes, MaxDepth);
}

/// <summary>
/// Reads an Excel workbook (.xlsx, or the binary .xlsb) with the zip and XML
/// readers of the base library and nothing else. The workbook's file name is
/// the first heading and each sheet is a heading under it, so the heading path
/// a passage is cited by names the sheet. Each row is a paragraph, its cells
/// left to right with a tab between them. A binary workbook reads to the same
/// text as the same workbook saved as .xlsx.
/// <para>
/// A cell is read as its value was last saved: a shared or inline string as
/// its text, a number as Excel's General format shows it (or as a date or a
/// time when the cell is styled as one), TRUE or FALSE, an error as written. A
/// formula is never evaluated: its saved result is the text, and a formula
/// saved with no result is empty. Hidden sheets and hidden rows are read,
/// because who may see a workbook is decided by the folder rules, not by how
/// its sheets are shown.
/// </para>
/// <para>
/// Comments, charts, pictures, pivot caches and defined names are not read.
/// Nothing is followed: an external link, a data connection, a web query and a
/// hyperlink are never opened, and no part is parsed with a document type
/// definition. A macro-enabled file (.xlsm, or any file carrying a macro
/// project or an Excel 4 macro sheet) is skipped with the reason
/// <c>macro-enabled</c>, and a legacy .xls with <c>legacy .xls</c>.
/// </para>
/// </summary>
public sealed partial class XlsxDocumentReader(XlsxReaderLimits limits) : IDocumentReader
{
    public XlsxDocumentReader() : this(XlsxReaderLimits.Default)
    {
    }

    public const string ReaderName = "xlsx";

    public string Name => ReaderName;

    public IReadOnlyList<string> Extensions { get; } = [".xlsx", ".xlsm", ".xlsb", ".xls"];

    private const string S = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private const string SStrict = "http://purl.oclc.org/ooxml/spreadsheetml/main";
    private const string R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private const string RStrict = "http://purl.oclc.org/ooxml/officeDocument/relationships";
    private const string Damaged = "damaged, or not an Excel file";

    private static readonly byte[] CompoundFileSignature = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1];

    public async Task<ReadDocument> ReadAsync(Stream content, string path, CancellationToken ct)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension == ".xls") return ReadDocument.Skipped("legacy .xls");
        if (extension == ".xlsm") return ReadDocument.Skipped("macro-enabled");

        var bytes = await ReaderBytes.AllAsync(content, ct);
        if (bytes.Length == 0) return new ReadDocument("", null, null);
        if (bytes.Length > limits.MaxFileBytes)
            throw new UnreadableDocumentException($"larger than {Sizes.Of(limits.MaxFileBytes)}, the most this reader reads");

        // A password-protected .xlsx is not a zip at all: Excel wraps the
        // encrypted package in the older compound file format. So is a .xls
        // saved under a .xlsx name.
        if (bytes.AsSpan().StartsWith(CompoundFileSignature))
            return bytes.AsSpan().IndexOf(Encoding.Unicode.GetBytes("EncryptedPackage")) >= 0
                ? throw new UnreadableDocumentException("password protected")
                : ReadDocument.Skipped("legacy .xls");

        var fileName = path[(path.LastIndexOfAny(['/', '\\']) + 1)..];
        return await Task.Run(() => Read(bytes, fileName, new ReadClock(limits.TimeBudget, ct)), ct);
    }

    private ReadDocument Read(byte[] bytes, string fileName, ReadClock clock)
    {
        using var parts = ZipParts.Open(bytes, limits.Zip, clock, Damaged);
        if (parts.HoldsMacroProject) return ReadDocument.Skipped("macro-enabled");

        var main = parts.MainPart("xl/workbook.xml");
        // A binary workbook, whatever its name says: its main part is Excel's
        // binary records, not XML, and its content type calls it macro-enabled
        // whether or not it holds a macro, so it is told apart before that
        // check. One that does hold a macro project was skipped above.
        if (main.EndsWith(".bin", StringComparison.OrdinalIgnoreCase)) return ReadBinary(parts, main, fileName, limits, clock);
        if (parts.DeclaresMacroEnabled(main)) return ReadDocument.Skipped("macro-enabled");

        var workbook = parts.Xml(main)?.Root;
        if (workbook is null || !Is(workbook, "workbook")) throw new UnreadableDocumentException(Damaged);

        var relationships = parts.Relationships(main);
        if (HoldsMacroSheet(relationships)) return ReadDocument.Skipped("macro-enabled");

        var sheets = Child(workbook, "sheets")?.Elements().Where(e => Is(e, "sheet")).ToList() ?? [];
        if (sheets.Count > limits.MaxSheets)
            throw new UnreadableDocumentException($"holds {sheets.Count} sheets, more than the {limits.MaxSheets} this reader reads");

        var byId = new Dictionary<string, ZipParts.Relationship>(StringComparer.Ordinal);
        foreach (var r in relationships) byId.TryAdd(r.Id, r);

        var workbookPr = Child(workbook, "workbookPr")?.Attribute("date1904")?.Value;
        var cells = new Cells(limits.MaxCells, clock);
        var values = new Values(
            SharedStrings(parts, main, relationships, cells, clock),
            Styles(parts, main, relationships),
            Date1904: workbookPr is "1" or "true");
        var writer = new Writer(limits.MaxTextChars);

        for (var i = 0; i < sheets.Count; i++)
        {
            var id = sheets[i].Attributes().FirstOrDefault(a => a.Name.LocalName == "id" && a.Name.NamespaceName is R or RStrict)?.Value;
            if (id is null || !byId.TryGetValue(id, out var target) || target.External) throw new UnreadableDocumentException(Damaged);
            // A chart sheet or a dialog sheet holds no cells.
            if (!target.Type.EndsWith("/worksheet", StringComparison.Ordinal)) continue;

            var name = (string?)sheets[i].Attribute("name") is { } n && n.Trim().Length > 0 ? n : $"Sheet {i + 1}";
            using var sheet = parts.Reader(parts.Resolve(main, target.Target)) ?? throw new UnreadableDocumentException(Damaged);
            Sheet(sheet, writer, cells, values, heading: () =>
            {
                if (writer.IsEmpty) writer.Heading(1, fileName);
                writer.Heading(2, name);
            });
        }

        var title = Title(parts.Xml("docProps/core.xml"));
        return writer.IsEmpty ? new ReadDocument("", title, null) : new ReadDocument(writer.Text, title, null);
    }

    // ---- The sheets ----

    /// <summary>
    /// One sheet's rows, streamed: each row with any text is a paragraph, and
    /// the sheet's heading is written before its first one, so a sheet with no
    /// text adds nothing. Only rows are looked at; what a sheet says about its
    /// size, its columns, its links and its layout is not read.
    /// </summary>
    private static void Sheet(XmlReader reader, Writer writer, Cells cells, Values values, Action heading)
    {
        var headed = false;
        try
        {
            while (reader.Read())
            {
                if (reader.NodeType != XmlNodeType.Element || !Is(reader, "row")) continue;
                var line = Row(reader, cells, values);
                if (line.Length == 0) continue;
                if (!headed)
                {
                    heading();
                    headed = true;
                }
                writer.Row(line);
            }
        }
        catch (XmlException)
        {
            throw new UnreadableDocumentException(Damaged);
        }
    }

    /// <summary>A row's cells with text, left to right, a tab between each. Leaves the reader on the row's last node.</summary>
    private static string Row(XmlReader reader, Cells cells, Values values)
    {
        cells.Row();
        if (reader.IsEmptyElement) return "";
        var depth = reader.Depth;
        var texts = new List<string>();
        reader.Read();
        while (reader.Depth > depth)
        {
            if (reader.NodeType == XmlNodeType.Element && Is(reader, "c"))
            {
                cells.Count();
                var text = Cell(reader, values);
                if (text.Length > 0) texts.Add(text);
                continue;
            }
            reader.Read();
        }
        return string.Join('\t', texts);
    }

    /// <summary>
    /// One cell's saved value as text. A formula is skipped unread; only the
    /// value saved with it is taken. Leaves the reader past the cell.
    /// </summary>
    private static string Cell(XmlReader reader, Values values)
    {
        var type = reader.GetAttribute("t");
        var style = reader.GetAttribute("s");
        if (reader.IsEmptyElement)
        {
            reader.Read();
            return "";
        }

        var depth = reader.Depth;
        string? value = null, inline = null;
        reader.Read();
        while (reader.Depth > depth)
        {
            if (reader.NodeType == XmlNodeType.Element && Is(reader, "v")) value = reader.ReadElementContentAsString();
            else if (reader.NodeType == XmlNodeType.Element && Is(reader, "is"))
            {
                inline = InlineText(reader);
                reader.Read();
            }
            else if (reader.NodeType == XmlNodeType.Element) reader.Skip();
            else reader.Read();
        }
        reader.Read();
        return CellText(values.Text(type, style, value, inline));
    }

    /// <summary>
    /// The text of a string item (a shared string, or an inline one): its text
    /// and the text of its runs, without the phonetic guide some languages add.
    /// Leaves the reader on the item's last node.
    /// </summary>
    private static string InlineText(XmlReader reader)
    {
        if (reader.IsEmptyElement) return "";
        var depth = reader.Depth;
        var text = new StringBuilder();
        reader.Read();
        while (reader.Depth > depth)
        {
            if (reader.NodeType == XmlNodeType.Element && Is(reader, "rPh")) reader.Skip();
            else if (reader.NodeType == XmlNodeType.Element && Is(reader, "t")) text.Append(reader.ReadElementContentAsString());
            else reader.Read();
        }
        return text.ToString();
    }

    private static readonly Regex Escaped = new("_x([0-9A-Fa-f]{4})_", RegexOptions.CultureInvariant);

    /// <summary>
    /// A cell's text on one line: the escapes the file format writes for
    /// characters XML cannot hold (<c>_x000D_</c>) turned back into them, and
    /// tabs, line breaks and every other control character made spaces, so a
    /// tab in a row always means the next cell and a row is always one line.
    /// An escape can name any code unit, so it can also name half of a
    /// surrogate pair with no other half, which no encoding can write; each
    /// such half becomes U+FFFD, and a pair written as two escapes is kept.
    /// </summary>
    private static string CellText(string text)
    {
        if (!text.Contains("_x", StringComparison.Ordinal))
            return text.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ').Trim();

        return Clean(Escaped.Replace(text, m => ((char)int.Parse(m.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture)).ToString()));
    }

    /// <summary>
    /// Text on one line with every control character a space and every half of
    /// a surrogate pair with no other half U+FFFD, trimmed. A binary workbook
    /// holds its text as UTF-16 with no escapes, so its text comes here as it
    /// is: any code unit can be in it, and <c>_x000D_</c> in it is what was
    /// typed.
    /// </summary>
    private static string Clean(string text)
    {
        var clean = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                clean.Append(c).Append(text[++i]);
                continue;
            }
            clean.Append(char.IsSurrogate(c) ? '\uFFFD' : c < ' ' ? ' ' : c);
        }
        return clean.ToString().Trim();
    }

    // ---- The workbook ----

    /// <summary>
    /// The shared string table, streamed, each entry held to the cell limit;
    /// empty when the workbook has none.
    /// </summary>
    private static List<string> SharedStrings(ZipParts parts, string main, IReadOnlyList<ZipParts.Relationship> relationships, Cells cells, ReadClock clock)
    {
        var strings = new List<string>();
        var target = relationships.FirstOrDefault(r => r.Type.EndsWith("/sharedStrings", StringComparison.Ordinal) && !r.External);
        if (target is null) return strings;

        using var reader = parts.Reader(parts.Resolve(main, target.Target));
        if (reader is null) return strings;
        try
        {
            while (reader.Read())
            {
                if (reader.NodeType != XmlNodeType.Element || !Is(reader, "si")) continue;
                if (strings.Count >= cells.Max)
                    throw new UnreadableDocumentException($"holds more than {cells.Max} shared strings, more than the cells this reader reads");
                if (strings.Count % 1024 == 0) clock.Check();
                strings.Add(InlineText(reader));
            }
        }
        catch (XmlException)
        {
            throw new UnreadableDocumentException(Damaged);
        }
        return strings;
    }

    /// <summary>What each cell style says about a number: whether it is shown as a date, a time, both, or a number.</summary>
    private static Shown[] Styles(ZipParts parts, string main, IReadOnlyList<ZipParts.Relationship> relationships)
    {
        var target = relationships.FirstOrDefault(r => r.Type.EndsWith("/styles", StringComparison.Ordinal) && !r.External);
        var styles = target is null ? null : parts.Xml(parts.Resolve(main, target.Target))?.Root;
        if (styles is null) return [];

        var custom = new Dictionary<int, string>();
        foreach (var format in Child(styles, "numFmts")?.Elements().Where(e => Is(e, "numFmt")) ?? [])
            if (int.TryParse((string?)format.Attribute("numFmtId"), CultureInfo.InvariantCulture, out var id))
                custom[id] = (string?)format.Attribute("formatCode") ?? "";

        return Child(styles, "cellXfs")?.Elements().Where(e => Is(e, "xf"))
            .Select(xf => int.TryParse((string?)xf.Attribute("numFmtId"), CultureInfo.InvariantCulture, out var id)
                ? custom.TryGetValue(id, out var code) ? ShownBy(code) : BuiltIn(id)
                : Shown.Number)
            .ToArray() ?? [];
    }

    private enum Shown { Number, Date, Time, DateAndTime }

    /// <summary>The built-in formats that show a date or a time (the East Asian ones among them). Elapsed time, <c>[h]:mm:ss</c>, stays a number.</summary>
    private static Shown BuiltIn(int id) => id switch
    {
        >= 14 and <= 17 or >= 27 and <= 31 or 34 or 35 or 36 or >= 50 and <= 58 => Shown.Date,
        >= 18 and <= 21 or 32 or 33 or 45 or 47 => Shown.Time,
        22 => Shown.DateAndTime,
        _ => Shown.Number,
    };

    /// <summary>
    /// What a custom format code shows, read from its first section with the
    /// quoted text, bracketed colors and locales, and escaped characters left
    /// out: a year, a day, or a month with no hour beside it is a date; an hour
    /// or a second is a time. Elapsed time (<c>[h]</c>) stays a number.
    /// </summary>
    private static Shown ShownBy(string code)
    {
        var plain = new StringBuilder();
        for (var i = 0; i < code.Length; i++)
        {
            var c = code[i];
            if (c == ';') break;
            if (c == '"')
            {
                var close = code.IndexOf('"', i + 1);
                i = close < 0 ? code.Length : close;
            }
            else if (c == '[')
            {
                var close = code.IndexOf(']', i + 1);
                var inside = close < 0 ? "" : code[(i + 1)..close].ToLowerInvariant();
                if (inside.Length > 0 && inside.All(ch => ch is 'h' or 'm' or 's')) return Shown.Number;
                i = close < 0 ? code.Length : close;
            }
            else if (c is '\\' or '_' or '*') i++;
            else plain.Append(char.ToLowerInvariant(c));
        }

        var text = plain.ToString();
        var time = text.Contains('h') || text.Contains('s');
        var date = text.Contains('y') || text.Contains('d') || (text.Contains('m') && !time);
        return date && time ? Shown.DateAndTime : date ? Shown.Date : time ? Shown.Time : Shown.Number;
    }

    /// <summary>How a saved value becomes text, given the workbook's shared strings, its styles and its date system.</summary>
    private sealed record Values(List<string> Shared, Shown[] Styles, bool Date1904)
    {
        public string Text(string? type, string? style, string? value, string? inline)
        {
            switch (type)
            {
                case "s":
                    return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var index) && index < Shared.Count
                        ? Shared[index]
                        : "";
                case "inlineStr":
                    return inline ?? "";
                case "b":
                    return value?.Trim() switch { "1" => "TRUE", "0" => "FALSE", _ => "" };
                case "str" or "e" or "d":
                    return value ?? "";
            }
            if (value is null) return "";
            if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)) return value;

            return Number(number, int.TryParse(style, NumberStyles.None, CultureInfo.InvariantCulture, out var s) ? s : -1);
        }

        /// <summary>A number as the cell style at <paramref name="style"/> shows it: a date or a time, or as General.</summary>
        public string Number(double number, int style)
        {
            var shown = style >= 0 && style < Styles.Length ? Styles[style] : Shown.Number;
            return (shown == Shown.Number ? null : Serial(number, shown, Date1904))
                   ?? number.ToString("G15", CultureInfo.InvariantCulture);
        }
    }

    /// <summary>
    /// A date or time from the serial number a workbook stores it as, in ISO
    /// form; null when the number is not one a date can be (below zero, past the
    /// year 9999, or a time of day of a whole day or more), which is then read
    /// as a number.
    /// </summary>
    private static string? Serial(double number, Shown shown, bool date1904)
    {
        // 31 December 9999, the last day a date can be, in each system: the
        // 1904 system starts 1,462 days later, so its serials end sooner.
        var lastDay = date1904 ? 2957003 : 2958465;
        if (double.IsNaN(number) || number < 0 || number >= lastDay + 1) return null;
        var days = (int)Math.Floor(number);
        var seconds = (int)Math.Round((number - days) * 86400);
        if (seconds == 86400)
        {
            days++;
            seconds = 0;
        }
        // Checked again once rounded: the last second of the last day rounds
        // into a day no date can be.
        if (days > lastDay) return null;
        var clock = seconds % 60 == 0
            ? $"{seconds / 3600:00}:{seconds / 60 % 60:00}"
            : $"{seconds / 3600:00}:{seconds / 60 % 60:00}:{seconds % 60:00}";
        if (shown == Shown.Time) return number < 1 ? clock : null;
        if (days == 0 && !date1904) return null;

        // The 1900 system counts from 1 January 1900 as day 1 and keeps the
        // 29 February 1900 that never was, as day 60, so later days are one
        // behind a plain count.
        var date = date1904 ? new DateTime(1904, 1, 1).AddDays(days).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : days == 60 ? "1900-02-29"
            : new DateTime(1899, 12, days < 60 ? 31 : 30).AddDays(days).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        return shown == Shown.DateAndTime ? $"{date} {clock}" : date;
    }

    /// <summary>
    /// Whether the workbook carries an Excel 4 macro sheet: a macro whatever
    /// the file is called, and the one kind a workbook can carry without a
    /// macro project.
    /// </summary>
    private static bool HoldsMacroSheet(IReadOnlyList<ZipParts.Relationship> relationships) =>
        relationships.Any(r => r.Type.EndsWith("/xlMacrosheet", StringComparison.Ordinal)
                               || r.Type.EndsWith("/xlIntlMacrosheet", StringComparison.Ordinal));

    private static string? Title(XDocument? core)
    {
        var title = core?.Root?.Elements().FirstOrDefault(e => e.Name.LocalName == "title")?.Value;
        if (string.IsNullOrWhiteSpace(title)) return null;
        var line = string.Join(' ', title.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return line.Length <= 300 ? line : line[..300];
    }

    // ---- Keeping count ----

    /// <summary>The cells read so far, over every sheet, held to the limit, with the clock checked as rows and cells go by.</summary>
    private sealed class Cells(int max, ReadClock clock)
    {
        private int _count;

        public int Max => max;

        public void Row() => clock.Check();

        public void Count()
        {
            if (++_count > max) throw new UnreadableDocumentException($"holds more than {max} cells, the most this reader reads");
            if (_count % 1024 == 0) clock.Check();
        }
    }

    /// <summary>Builds the Markdown, holding it to the text limit as it goes.</summary>
    private sealed class Writer(int maxChars)
    {
        private readonly StringBuilder _text = new();

        public bool IsEmpty => _text.Length == 0;

        public string Text => _text.ToString().TrimEnd('\n') + "\n";

        public void Heading(int level, string text) => Append(ReaderMarkdown.Heading(level, text));

        public void Row(string line)
        {
            var block = ReaderMarkdown.Block(line);
            if (block.Trim().Length > 0) Append(block);
        }

        private void Append(string block)
        {
            // Checked before the text is added, so one long cell cannot carry
            // the text past the limit before anything notices.
            if (_text.Length + block.Length + 2 > maxChars)
                throw new UnreadableDocumentException($"gives more than {maxChars} characters of text, the most this reader keeps");
            _text.Append(block).Append("\n\n");
        }
    }

    // ---- Small things ----

    private static bool Is(XElement e, string localName) => e.Name.NamespaceName is S or SStrict && e.Name.LocalName == localName;

    private static bool Is(XmlReader r, string localName) => r.NamespaceURI is S or SStrict && r.LocalName == localName;

    private static XElement? Child(XElement e, string localName) => e.Elements().FirstOrDefault(c => Is(c, localName));
}

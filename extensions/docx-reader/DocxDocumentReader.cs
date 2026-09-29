using System.Text;
using System.Xml.Linq;
using Premagentic.Core.Extensions;
using Premagentic.Core.Ingestion.Readers;
using Premagentic.Extensions.Shared;

namespace Premagentic.Extensions.Docx;

/// <summary>Registers the Word reader.</summary>
public sealed class DocxReaderExtension : IExtension
{
    public string Name => "docx-reader";

    public void Register(ExtensionRegistrations registrations) => registrations.AddReader(new DocxDocumentReader());
}

/// <summary>
/// How much of a Word file this reader will take on. A .docx is a zip, and a
/// zip can be built to expand without end, so every limit here is about the
/// container. A file over any of them is reported unreadable with the limit it
/// crossed, never read in part.
/// </summary>
/// <param name="MaxFileBytes">The largest file read at all.</param>
/// <param name="MaxEntries">The most entries the zip may hold.</param>
/// <param name="MaxUncompressedBytes">
/// The most bytes the zip may say it expands to, over every entry, pictures
/// and embedded files included, though those are never opened.
/// </param>
/// <param name="MaxReadBytes">
/// The most bytes the parts this reader does read (the document, its styles,
/// its notes) may really expand to together, counted as they are read. Checked
/// apart from what the zip says, because the sizes a zip states are written by
/// whoever made it.
/// </param>
/// <param name="MaxCompressionRatio">
/// How many times its compressed size a part this reader reads may expand to,
/// once it is past <paramref name="RatioFloorBytes"/>. Text compresses well, and
/// not a thousand times.
/// </param>
/// <param name="RatioFloorBytes">How far a part may expand before its ratio is held to the limit.</param>
/// <param name="MaxTextChars">The most characters of text a file may give.</param>
/// <param name="TimeBudget">How long one file may take, checked at every paragraph.</param>
/// <param name="MaxDepth">
/// How deep the XML of a part this reader reads may nest. The document is
/// walked by recursion, and a stack overflow ends the process that has it,
/// which no handler can catch, so the depth is measured before anything is
/// walked. Word's own documents stay well inside it.
/// </param>
public sealed record DocxReaderLimits(
    long MaxFileBytes, int MaxEntries, long MaxUncompressedBytes, long MaxReadBytes, int MaxCompressionRatio, long RatioFloorBytes,
    int MaxTextChars, TimeSpan TimeBudget, int MaxDepth)
{
    public static DocxReaderLimits Default { get; } = new(
        MaxFileBytes: 200L * 1024 * 1024,
        MaxEntries: 5000,
        MaxUncompressedBytes: 1024L * 1024 * 1024,
        MaxReadBytes: 64L * 1024 * 1024,
        MaxCompressionRatio: 100,
        RatioFloorBytes: 1024 * 1024,
        MaxTextChars: 10_000_000,
        TimeBudget: TimeSpan.FromSeconds(60),
        MaxDepth: 256);

    internal ZipLimits Zip => new(MaxEntries, MaxUncompressedBytes, MaxReadBytes, MaxCompressionRatio, RatioFloorBytes, MaxDepth);
}

/// <summary>
/// Reads a Word document (.docx) with the zip and XML readers of the base
/// library and nothing else. Headings become Markdown headings, so the heading
/// path a passage is cited by is the document's own; paragraphs, list items
/// and the text of table cells follow in document order, and footnotes and
/// endnotes after the body. A text box is read once, after the paragraph it
/// is anchored in, or on the line of the table cell it is anchored in.
/// <para>
/// Tracked changes are read as they would print once accepted: inserted text
/// is read and deleted text is not. Comments, headers and footers, pictures
/// and every embedded object are not read. Nothing is followed: a hyperlink's
/// text is read and its address is not, a field's result is read and its
/// instruction is not, and a relationship to anything outside the file (an
/// image, a template, a web page) is never resolved. No part is parsed with a
/// document type definition, so no entity can name a file or an address.
/// </para>
/// <para>
/// A macro-enabled file (.docm, or any file carrying a macro project) is
/// skipped with the reason <c>macro-enabled</c> and never opened further. A
/// legacy .doc is skipped with the reason <c>legacy .doc</c>.
/// </para>
/// </summary>
public sealed class DocxDocumentReader(DocxReaderLimits limits) : IDocumentReader
{
    public DocxDocumentReader() : this(DocxReaderLimits.Default)
    {
    }

    public const string ReaderName = "docx";

    public string Name => ReaderName;

    public IReadOnlyList<string> Extensions { get; } = [".docx", ".docm", ".doc"];

    private const string W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private const string WStrict = "http://purl.oclc.org/ooxml/wordprocessingml/main";
    private const string Mc = "http://schemas.openxmlformats.org/markup-compatibility/2006";
    private const string Damaged = "damaged, or not a Word file";

    private static readonly byte[] CompoundFileSignature = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1];

    public async Task<ReadDocument> ReadAsync(Stream content, string path, CancellationToken ct)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension == ".doc") return ReadDocument.Skipped("legacy .doc");
        if (extension == ".docm") return ReadDocument.Skipped("macro-enabled");

        var bytes = await ReaderBytes.AllAsync(content, ct);
        if (bytes.Length == 0) return new ReadDocument("", null, null);
        if (bytes.Length > limits.MaxFileBytes)
            throw new UnreadableDocumentException($"larger than {Size(limits.MaxFileBytes)}, the most this reader reads");

        // A password-protected .docx is not a zip at all: Word wraps the
        // encrypted package in the older compound file format. So is a .doc
        // saved under a .docx name.
        if (bytes.AsSpan().StartsWith(CompoundFileSignature))
            return Contains(bytes, Encoding.Unicode.GetBytes("EncryptedPackage"))
                ? throw new UnreadableDocumentException("password protected")
                : ReadDocument.Skipped("legacy .doc");

        return await Task.Run(() => Read(bytes, new ReadClock(limits.TimeBudget, ct)), ct);
    }

    private ReadDocument Read(byte[] bytes, ReadClock clock)
    {
        using (var parts = ZipParts.Open(bytes, limits.Zip, clock, Damaged))
        {
            if (parts.HoldsMacroProject) return ReadDocument.Skipped("macro-enabled");

            var mainPart = parts.MainPart("word/document.xml");
            if (parts.DeclaresMacroEnabled(mainPart)) return ReadDocument.Skipped("macro-enabled");

            var document = parts.Xml(mainPart)
                ?? throw new UnreadableDocumentException(Damaged);
            var styles = new Headings(parts.Xml(Sibling(mainPart, "styles.xml")));
            var writer = new Writer(limits.MaxTextChars, clock);

            var body = document.Root?.Elements().FirstOrDefault(e => Is(e, "body"))
                ?? throw new UnreadableDocumentException(Damaged);
            writer.Blocks(body.Elements(), styles);

            foreach (var (file, heading, note) in new[] { ("footnotes.xml", "Footnotes", "footnote"), ("endnotes.xml", "Endnotes", "endnote") })
            {
                var notes = parts.Xml(Sibling(mainPart, file))?.Root?.Elements()
                    .Where(e => Is(e, note) && Attribute(e, "type") is null or "normal")
                    .ToList();
                if (notes is not { Count: > 0 }) continue;
                writer.Heading(1, heading);
                foreach (var n in notes) writer.Blocks(n.Elements(), styles);
            }

            var title = Title(parts.Xml("docProps/core.xml"));
            return writer.IsEmpty
                ? new ReadDocument("", title, null)
                : new ReadDocument(writer.Text, title, null);
        }
    }

    // ---- The container ----

    private static string Sibling(string part, string file)
    {
        var slash = part.LastIndexOf('/');
        return slash < 0 ? file : part[..(slash + 1)] + file;
    }

    private static string? Title(XDocument? core)
    {
        var title = core?.Root?.Elements().FirstOrDefault(e => e.Name.LocalName == "title")?.Value;
        if (string.IsNullOrWhiteSpace(title)) return null;
        var line = string.Join(' ', title.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return line.Length <= 300 ? line : line[..300];
    }

    // ---- The document ----

    /// <summary>The heading level each paragraph style gives, from styles.xml.</summary>
    private sealed class Headings
    {
        private readonly Dictionary<string, (string? BasedOn, int? Level)> _styles = new(StringComparer.Ordinal);

        public Headings(XDocument? styles)
        {
            foreach (var style in styles?.Root?.Elements().Where(e => Is(e, "style") && Attribute(e, "type") == "paragraph") ?? [])
            {
                if (Attribute(style, "styleId") is not { } id) continue;
                var name = Child(style, "name") is { } n ? Attribute(n, "val") : null;
                var basedOn = Child(style, "basedOn") is { } b ? Attribute(b, "val") : null;
                int? level = OutlineLevel(Child(style, "pPr"));
                if (level is null && name is not null && name.StartsWith("heading ", StringComparison.OrdinalIgnoreCase)
                    && int.TryParse(name.AsSpan(8), out var n2) && n2 is >= 1 and <= 9)
                    level = n2;
                _styles[id] = (basedOn, level);
            }
        }

        /// <summary>The level for a style, following what it is based on; null when it is not a heading.</summary>
        public int? Level(string? styleId)
        {
            // A chain that loops or runs long is not a heading.
            for (var depth = 0; styleId is not null && depth < 20; depth++)
            {
                if (!_styles.TryGetValue(styleId, out var style)) return null;
                if (style.Level is { } level) return level;
                styleId = style.BasedOn;
            }
            return null;
        }

        /// <summary>An outline level in paragraph properties: 0 to 8 are headings 1 to 9, and 9 is body text.</summary>
        public static int? OutlineLevel(XElement? pPr) =>
            pPr is not null && Child(pPr, "outlineLvl") is { } o && int.TryParse(Attribute(o, "val"), out var v) && v is >= 0 and <= 8
                ? v + 1
                : null;
    }

    /// <summary>Builds the Markdown, holding it to the text limit and the clock as it goes.</summary>
    private sealed class Writer(int maxChars, ReadClock clock)
    {
        private readonly StringBuilder _text = new();

        public bool IsEmpty => _text.Length == 0;

        public string Text => _text.ToString().TrimEnd('\n') + "\n";

        public void Heading(int level, string text)
        {
            if (text.Trim().Length == 0) return;
            Append(ReaderMarkdown.Heading(level, text));
        }

        private void Paragraph(string text)
        {
            var block = ReaderMarkdown.Block(text);
            if (block.Trim().Length > 0) Append(block);
        }

        private void Append(string block)
        {
            _text.Append(block).Append("\n\n");
            if (_text.Length > maxChars)
                throw new UnreadableDocumentException($"gives more than {maxChars} characters of text, the most this reader keeps");
        }

        /// <summary>
        /// Paragraphs, tables and content controls, in the order they come,
        /// each paragraph followed by the text boxes anchored in it.
        /// </summary>
        public void Blocks(IEnumerable<XElement> elements, Headings headings)
        {
            foreach (var element in elements)
            {
                clock.Check();
                if (Is(element, "p"))
                {
                    var pPr = Child(element, "pPr");
                    var level = Headings.OutlineLevel(pPr)
                                ?? headings.Level(pPr is not null && Child(pPr, "pStyle") is { } s ? Attribute(s, "val") : null);
                    var boxes = new List<XElement>();
                    var text = Runs(element, boxes);
                    if (level is { } l) Heading(Math.Min(l, 6), text);
                    else if (pPr is not null && Child(pPr, "numPr") is not null) Paragraph("- " + text);
                    else Paragraph(text);
                    // A text box floats beside the text; its place in the file
                    // is the paragraph it is anchored in, so it is read next.
                    foreach (var box in boxes)
                    {
                        clock.Check();
                        Blocks(box.Elements(), headings);
                    }
                }
                else if (Is(element, "tbl"))
                {
                    foreach (var row in element.Elements().Where(e => Is(e, "tr")))
                    {
                        clock.Check();
                        var cells = row.Elements().Where(e => Is(e, "tc")).Select(Cell).Where(c => c.Length > 0).ToList();
                        if (cells.Count > 0) Paragraph(string.Join(" | ", cells));
                    }
                }
                else if (Is(element, "sdt"))
                {
                    if (Child(element, "sdtContent") is { } inner) Blocks(inner.Elements(), headings);
                }
                else if (Is(element, "customXml") || Is(element, "ins") || Is(element, "moveTo"))
                {
                    Blocks(element.Elements(), headings);
                }
                // Anything else, deleted and moved-away blocks among them, is
                // not document text.
            }
        }

        /// <summary>
        /// A table cell's text on one line: its paragraphs, the text boxes
        /// anchored in them, and any table inside it, run together. A text box
        /// holds what a cell holds, so it is read the same way.
        /// </summary>
        private string Cell(XElement cell)
        {
            clock.Check();
            var parts = new List<string>();
            foreach (var element in cell.Elements())
            {
                if (Is(element, "p")) Add(element);
                else if (Is(element, "tbl"))
                    // One level at a time: the cells of this table's rows, each
                    // of which takes in any table inside it. Taking every cell
                    // below would read an inner cell once for each table around
                    // it, which doubles with every level.
                    parts.AddRange(element.Elements().Where(r => Is(r, "tr"))
                        .SelectMany(r => r.Elements().Where(c => Is(c, "tc")))
                        .Select(Cell));
                else if (Is(element, "sdt") && Child(element, "sdtContent") is { } inner)
                    foreach (var p in inner.Elements().Where(e => Is(e, "p"))) Add(p);
            }
            return string.Join(' ', parts.Select(p => p.Replace('\n', ' ').Trim()).Where(p => p.Length > 0));

            void Add(XElement paragraph)
            {
                var boxes = new List<XElement>();
                parts.Add(Runs(paragraph, boxes));
                parts.AddRange(boxes.Select(Cell));
            }
        }

        /// <summary>
        /// The text of one paragraph as it prints: runs, inserted runs, the
        /// text of hyperlinks and the results of fields. Deleted text, field
        /// instructions and everything drawn are left out; the text boxes
        /// among what is drawn are added to <paramref name="boxes"/>, for the
        /// caller to read after the paragraph.
        /// </summary>
        private static string Runs(XElement paragraph, List<XElement> boxes)
        {
            var text = new StringBuilder();
            // How many fields deep, and whether each is still in its instruction.
            var fields = new Stack<bool>();
            Walk(paragraph);
            return text.ToString();

            void Walk(XElement parent)
            {
                foreach (var e in parent.Elements())
                {
                    if (e.Name.NamespaceName == Mc && e.Name.LocalName == "AlternateContent")
                    {
                        // One of the choices, never both: they say the same thing twice.
                        if (e.Elements().FirstOrDefault(c => c.Name.NamespaceName == Mc) is { } first) Walk(first);
                        continue;
                    }
                    if (!IsW(e)) continue;
                    switch (e.Name.LocalName)
                    {
                        case "pPr" or "rPr" or "del" or "moveFrom" or "instrText" or "delText" or "delInstrText"
                            or "object" or "commentReference" or "footnoteReference" or "endnoteReference":
                            break;
                        case "drawing" or "pict":
                            if (!fields.Any(i => i)) TextBoxes(e, boxes);
                            break;
                        case "fldChar":
                            switch (Attribute(e, "fldCharType"))
                            {
                                case "begin": fields.Push(true); break;
                                case "separate" when fields.Count > 0: fields.Pop(); fields.Push(false); break;
                                case "end" when fields.Count > 0: fields.Pop(); break;
                            }
                            break;
                        case "t":
                            if (!fields.Any(inInstruction => inInstruction)) text.Append(e.Value);
                            break;
                        case "tab":
                            if (!fields.Any(i => i)) text.Append('\t');
                            break;
                        case "br" or "cr":
                            if (!fields.Any(i => i)) text.Append('\n');
                            break;
                        case "noBreakHyphen":
                            if (!fields.Any(i => i)) text.Append('-');
                            break;
                        default:
                            // Runs, hyperlinks, simple fields, inserted and
                            // moved-here runs, smart tags and inline content
                            // controls hold runs of their own.
                            Walk(e);
                            break;
                    }
                }
            }
        }
    }

    /// <summary>
    /// The text boxes in something drawn, in document order. Only the
    /// outermost: a text box inside a text box is found when the paragraphs of
    /// the box around it are read. And only from the first choice of any
    /// alternate content, since Word writes each text box twice, as a drawing
    /// and as the older shape, and both hold the same text.
    /// </summary>
    private static void TextBoxes(XElement drawn, List<XElement> boxes)
    {
        foreach (var e in drawn.Elements())
        {
            if (e.Name.NamespaceName == Mc && e.Name.LocalName == "AlternateContent")
            {
                if (e.Elements().FirstOrDefault(c => c.Name.NamespaceName == Mc) is { } first) TextBoxes(first, boxes);
            }
            else if (Is(e, "txbxContent")) boxes.Add(e);
            else TextBoxes(e, boxes);
        }
    }

    // ---- Small things ----

    private static bool IsW(XElement e) => e.Name.NamespaceName is W or WStrict;

    private static bool Is(XElement e, string localName) => IsW(e) && e.Name.LocalName == localName;

    private static XElement? Child(XElement e, string localName) => e.Elements().FirstOrDefault(c => Is(c, localName));

    private static string? Attribute(XElement e, string localName) =>
        e.Attributes().FirstOrDefault(a => a.Name.LocalName == localName && (a.Name.NamespaceName is W or WStrict or ""))?.Value;

    private static bool Contains(byte[] haystack, byte[] needle) => haystack.AsSpan().IndexOf(needle) >= 0;

    private static string Size(long bytes) => Sizes.Of(bytes);
}

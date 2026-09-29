using System.IO.Compression;
using System.Security;
using System.Text;

namespace Premagentic.Readers.Tests;

/// <summary>
/// Invented workbooks, built here as zips of hand-written XML, so no real
/// spreadsheet is a fixture and a hostile one is made in memory at whatever
/// size the test needs. Only the parts a reader looks at are written; Excel
/// itself would want a few more, and the reader must not depend on them.
/// </summary>
internal static class XlsxFixtures
{
    internal const string MainType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml";
    internal const string MacroMainType = "application/vnd.ms-excel.sheet.macroEnabled.main+xml";
    internal const string S = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    internal const string SStrict = "http://purl.oclc.org/ooxml/spreadsheetml/main";
    internal const string R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private const string RStrict = "http://purl.oclc.org/ooxml/officeDocument/relationships";
    private const string PackageRelationships = "http://schemas.openxmlformats.org/package/2006/relationships";
    private const string Xml = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>";

    internal static string X(string text) => SecurityElement.Escape(text);

    // ---- Cells and rows ----

    internal static string Row(params string[] cells) => $"<row>{string.Concat(cells)}</row>";

    internal static string HiddenRow(params string[] cells) => $"<row hidden=\"1\">{string.Concat(cells)}</row>";

    /// <summary>A cell holding its own string.</summary>
    internal static string Str(string text) => $"<c t=\"inlineStr\"><is><t xml:space=\"preserve\">{X(text)}</t></is></c>";

    /// <summary>A cell pointing at an entry of the shared string table.</summary>
    internal static string Shared(int index) => $"<c t=\"s\"><v>{index}</v></c>";

    internal static string Num(string value, int? style = null) => $"<c{(style is { } s ? $" s=\"{s}\"" : "")}><v>{value}</v></c>";

    /// <summary>A formula with the value saved beside it, or none.</summary>
    internal static string Formula(string formula, string? saved, string? type = null) =>
        $"<c{(type is null ? "" : $" t=\"{type}\"")}><f>{X(formula)}</f>{(saved is null ? "" : $"<v>{X(saved)}</v>")}</c>";

    internal static string Bool(bool value) => $"<c t=\"b\"><v>{(value ? 1 : 0)}</v></c>";

    internal static string Error(string value) => $"<c t=\"e\"><v>{X(value)}</v></c>";

    /// <summary>A shared string entry of plain text.</summary>
    internal static string T(string text) => $"<t xml:space=\"preserve\">{X(text)}</t>";

    // ---- Parts ----

    /// <summary>A sheet: its name, its rows, how it is shown, what kind it is, and what follows its rows.</summary>
    internal sealed record Sheet(string Name, string Rows, string? State = null, string Kind = "worksheet", string Tail = "");

    internal static Sheet Ws(string name, params string[] rows) => new(name, string.Concat(rows));

    internal static string SheetXml(string rows, string tail = "", string ns = S) =>
        $"{Xml}<worksheet xmlns=\"{ns}\" xmlns:r=\"{R}\"><dimension ref=\"A1\"/><sheetData>{rows}</sheetData>{tail}</worksheet>";

    internal static string ContentTypes(string mainType = MainType) =>
        $"{Xml}<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
        "<Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
        "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>" +
        $"<Override PartName=\"/xl/workbook.xml\" ContentType=\"{mainType}\"/></Types>";

    internal static string RootRelationships(string target = "xl/workbook.xml") =>
        $"{Xml}<Relationships xmlns=\"{PackageRelationships}\">" +
        $"<Relationship Id=\"rId1\" Type=\"{R}/officeDocument\" Target=\"{X(target)}\"/></Relationships>";

    internal static string Relationships(params (string Id, string Type, string Target, bool External)[] rels) =>
        $"{Xml}<Relationships xmlns=\"{PackageRelationships}\">" +
        string.Concat(rels.Select(r =>
            $"<Relationship Id=\"{r.Id}\" Type=\"{X(r.Type)}\" Target=\"{X(r.Target)}\"{(r.External ? " TargetMode=\"External\"" : "")}/>")) +
        "</Relationships>";

    internal static string CoreProperties(string title) =>
        $"{Xml}<cp:coreProperties xmlns:cp=\"http://schemas.openxmlformats.org/package/2006/metadata/core-properties\" " +
        $"xmlns:dc=\"http://purl.org/dc/elements/1.1/\"><dc:title>{X(title)}</dc:title></cp:coreProperties>";

    /// <summary>
    /// Cell styles for numbers: 0 General, 1 a built-in date, 2 a custom date
    /// and time, 3 a built-in time, 4 elapsed hours, 5 a number with a word in
    /// quotes, 6 a custom long date behind a locale, 7 two decimals.
    /// </summary>
    internal const string NumberStyles =
        Xml + "<styleSheet xmlns=\"" + S + "\"><numFmts count=\"4\">" +
        "<numFmt numFmtId=\"164\" formatCode=\"yyyy-mm-dd hh:mm\"/>" +
        "<numFmt numFmtId=\"165\" formatCode=\"[h]:mm:ss\"/>" +
        "<numFmt numFmtId=\"166\" formatCode=\"0.00 &quot;days&quot;\"/>" +
        "<numFmt numFmtId=\"167\" formatCode=\"[$-409]mmmm d, yyyy;@\"/>" +
        "</numFmts><cellXfs count=\"8\">" +
        "<xf numFmtId=\"0\"/><xf numFmtId=\"14\"/><xf numFmtId=\"164\"/><xf numFmtId=\"20\"/>" +
        "<xf numFmtId=\"165\"/><xf numFmtId=\"166\"/><xf numFmtId=\"167\"/><xf numFmtId=\"2\"/>" +
        "</cellXfs></styleSheet>";

    // ---- Workbooks ----

    /// <summary>
    /// A whole workbook: its sheets in order, a shared string table of these
    /// entries when given, styles when given, a title when given, and any parts
    /// added or put in place of the usual ones last.
    /// </summary>
    internal static byte[] Workbook(
        IReadOnlyList<Sheet> sheets, string[]? shared = null, string? styles = null, string? title = null,
        string mainType = MainType, string workbookPr = "", string workbookTail = "",
        (string Id, string Type, string Target, bool External)[]? extraRels = null,
        IReadOnlyDictionary<string, string>? parts = null, bool strict = false)
    {
        var ns = strict ? SStrict : S;
        var rns = strict ? RStrict : R;
        var all = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["[Content_Types].xml"] = Encoding.UTF8.GetBytes(ContentTypes(mainType)),
            ["_rels/.rels"] = Encoding.UTF8.GetBytes(RootRelationships()),
            ["xl/workbook.xml"] = Encoding.UTF8.GetBytes(
                $"{Xml}<workbook xmlns=\"{ns}\" xmlns:r=\"{rns}\">{workbookPr}<sheets>" +
                string.Concat(sheets.Select((s, i) =>
                    $"<sheet name=\"{X(s.Name)}\" sheetId=\"{i + 1}\"{(s.State is null ? "" : $" state=\"{s.State}\"")} r:id=\"rId{i + 1}\"/>")) +
                $"</sheets>{workbookTail}</workbook>"),
        };

        var rels = new List<(string, string, string, bool)>();
        for (var i = 0; i < sheets.Count; i++)
        {
            var folder = sheets[i].Kind == "worksheet" ? "worksheets" : sheets[i].Kind + "s";
            rels.Add(($"rId{i + 1}", $"{rns}/{sheets[i].Kind}", $"{folder}/sheet{i + 1}.xml", false));
            all[$"xl/{folder}/sheet{i + 1}.xml"] = Encoding.UTF8.GetBytes(sheets[i].Kind == "worksheet"
                ? SheetXml(sheets[i].Rows, sheets[i].Tail, ns)
                : $"{Xml}<{sheets[i].Kind} xmlns=\"{ns}\"/>");
        }
        if (shared is not null)
        {
            rels.Add(("rIdStrings", $"{rns}/sharedStrings", "sharedStrings.xml", false));
            all["xl/sharedStrings.xml"] = Encoding.UTF8.GetBytes(
                $"{Xml}<sst xmlns=\"{ns}\" count=\"{shared.Length}\" uniqueCount=\"{shared.Length}\">" +
                string.Concat(shared.Select(s => $"<si>{s}</si>")) + "</sst>");
        }
        if (styles is not null)
        {
            rels.Add(("rIdStyles", $"{rns}/styles", "styles.xml", false));
            all["xl/styles.xml"] = Encoding.UTF8.GetBytes(styles);
        }
        rels.AddRange(extraRels ?? []);
        all["xl/_rels/workbook.xml.rels"] = Encoding.UTF8.GetBytes(Relationships([.. rels]));
        if (title is not null) all["docProps/core.xml"] = Encoding.UTF8.GetBytes(CoreProperties(title));

        foreach (var (name, text) in parts ?? new Dictionary<string, string>()) all[name] = Encoding.UTF8.GetBytes(text);
        return DocxFixtures.Zip(all.Select(p => (p.Key, p.Value)));
    }

    internal static byte[] Workbook(params Sheet[] sheets) => Workbook((IReadOnlyList<Sheet>)sheets);

    /// <summary>
    /// A workbook of one sheet with this many rows of one empty cell each,
    /// written straight into the zip so the test holds none of it as text.
    /// </summary>
    internal static byte[] RowsOfOneEmptyCell(int rows)
    {
        using var file = new MemoryStream();
        using (var zip = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, text) in new[]
                     {
                         ("[Content_Types].xml", ContentTypes()),
                         ("_rels/.rels", RootRelationships()),
                         ("xl/workbook.xml", $"{Xml}<workbook xmlns=\"{S}\" xmlns:r=\"{R}\"><sheets><sheet name=\"Empty\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>"),
                         ("xl/_rels/workbook.xml.rels", Relationships(("rId1", $"{R}/worksheet", "worksheets/sheet1.xml", false))),
                     })
                using (var s = zip.CreateEntry(name).Open()) s.Write(Encoding.UTF8.GetBytes(text));

            using var sheet = new StreamWriter(zip.CreateEntry("xl/worksheets/sheet1.xml", CompressionLevel.Fastest).Open(), new UTF8Encoding(false));
            // The dimension says the sheet is every cell Excel allows, which a
            // reader that sized anything by it would believe.
            sheet.Write($"{Xml}<worksheet xmlns=\"{S}\"><dimension ref=\"A1:XFD1048576\"/><sheetData>");
            for (var r = 1; r <= rows; r++) sheet.Write($"<row r=\"{r}\"><c r=\"A{r}\"/></row>");
            sheet.Write("</sheetData></worksheet>");
        }
        return file.ToArray();
    }

    // ---- Binary workbooks ----

    /// <summary>
    /// Record types, from MS-XLSB section 2.3, written here from the
    /// specification and not taken from the reader, so a wrong number in one
    /// is not repeated in the other.
    /// </summary>
    internal static class Brt
    {
        internal const int RowHdr = 0, CellBlank = 1, CellRk = 2, CellError = 3, CellBool = 4, CellReal = 5, CellSt = 6,
            CellIsst = 7, FmlaString = 8, FmlaNum = 9, FmlaBool = 10, FmlaError = 11, SstItem = 19, Fmt = 44, Xf = 47,
            BeginSheet = 129, EndSheet = 130, BeginBook = 131, EndBook = 132, BeginBundleShs = 143, EndBundleShs = 144,
            BeginSheetData = 145, EndSheetData = 146, WsProp = 147, WbProp = 153, BundleSh = 156, BeginSst = 159,
            EndSst = 160, BeginStyleSheet = 278, EndStyleSheet = 279, BeginFmts = 615, EndFmts = 616,
            BeginCellXFs = 617, EndCellXFs = 618, BeginCellStyleXFs = 626, EndCellStyleXFs = 627;
    }

    /// <summary>
    /// One record: its type in one or two bytes and its size in one to four,
    /// seven bits a byte with the low bits first and the high bit saying
    /// another byte follows, then its body.
    /// </summary>
    internal static byte[] Record(int type, params byte[][] fields)
    {
        var body = fields.SelectMany(f => f).ToArray();
        var head = new List<byte> { (byte)((type & 0x7F) | (type > 0x7F ? 0x80 : 0)) };
        if (type > 0x7F) head.Add((byte)(type >> 7));
        var size = body.Length;
        do
        {
            var low = (byte)(size & 0x7F);
            size >>= 7;
            head.Add((byte)(low | (size > 0 ? 0x80 : 0)));
        } while (size > 0);
        return [.. head, .. body];
    }

    internal static byte[] U8(byte value) => [value];

    internal static byte[] U16(int value) => BitConverter.GetBytes((ushort)value);

    internal static byte[] U32(uint value) => BitConverter.GetBytes(value);

    internal static byte[] F64(double value) => BitConverter.GetBytes(value);

    /// <summary>
    /// A string as the format writes it: its count of UTF-16 code units, then
    /// the code units as they are, so half of a surrogate pair is written as
    /// itself and not replaced as an encoder would.
    /// </summary>
    internal static byte[] W(string text) => [.. U32((uint)text.Length), .. text.SelectMany(c => U16(c))];

    /// <summary>The string that stands for none: a count of all ones and nothing after it.</summary>
    internal static readonly byte[] NoString = U32(uint.MaxValue);

    /// <summary>What every cell record begins with: its column, then its style in the low three bytes of four.</summary>
    internal static byte[] CellAt(int column, int style) => [.. U32((uint)column), .. U32((uint)style & 0xFFFFFF)];

    /// <summary>
    /// A formula as a cell record carries it after the saved value: its flags,
    /// then invented parsed tokens and their extra data, which the reader must
    /// never take for text.
    /// </summary>
    private static readonly byte[] FormulaTail = [.. U16(0), .. U32(3), 0x1E, 0x2A, 0x00, .. U32(2), 0x53, 0x55];

    internal static byte[] BBlank(int style = 0) => Record(Brt.CellBlank, CellAt(0, style));

    /// <summary>A number stored as an RK: its raw four bytes, as the format lays them out.</summary>
    internal static byte[] BRk(uint rk, int style = 0) => Record(Brt.CellRk, CellAt(0, style), U32(rk));

    /// <summary>An RK holding a whole number: the number in the top 30 bits, the integer bit set.</summary>
    internal static uint RkInt(int value, bool times100 = false) => ((uint)(value << 2)) | 2u | (times100 ? 1u : 0u);

    /// <summary>An RK holding the top 30 bits of a double whose other 34 are zero.</summary>
    internal static uint RkDouble(double value, bool times100 = false) =>
        (uint)(BitConverter.DoubleToUInt64Bits(value) >> 32) & 0xFFFFFFFC | (times100 ? 1u : 0u);

    internal static byte[] BNum(double value, int style = 0) => Record(Brt.CellReal, CellAt(0, style), F64(value));

    internal static byte[] BStr(string text) => Record(Brt.CellSt, CellAt(0, 0), W(text));

    internal static byte[] BShared(uint index) => Record(Brt.CellIsst, CellAt(0, 0), U32(index));

    internal static byte[] BBool(bool value) => Record(Brt.CellBool, CellAt(0, 0), U8(value ? (byte)1 : (byte)0));

    internal static byte[] BError(byte code) => Record(Brt.CellError, CellAt(0, 0), U8(code));

    internal static byte[] BFormulaText(string saved) => Record(Brt.FmlaString, CellAt(0, 0), W(saved), FormulaTail);

    internal static byte[] BFormulaNum(double saved, int style = 0) => Record(Brt.FmlaNum, CellAt(0, style), F64(saved), FormulaTail);

    internal static byte[] BFormulaBool(bool saved) => Record(Brt.FmlaBool, CellAt(0, 0), U8(saved ? (byte)1 : (byte)0), FormulaTail);

    internal static byte[] BFormulaError(byte saved) => Record(Brt.FmlaError, CellAt(0, 0), U8(saved), FormulaTail);

    /// <summary>A row's cells, in the order they are written.</summary>
    internal static byte[][] BRow(params byte[][] cells) => cells;

    /// <summary>
    /// A row header: the row's index, then its style, height, flags and a
    /// span count of zero, seventeen bytes in all, none of which the reader
    /// needs.
    /// </summary>
    internal static byte[] RowHeader(int row) => Record(Brt.RowHdr, U32((uint)row), U32(0), U16(300), U16(0), U8(0), U32(0));

    /// <summary>A worksheet part: its properties, then its rows, each under its header, between the start and end of its data.</summary>
    internal static byte[] SheetBin(params byte[][][] rows) =>
    [
        .. Record(Brt.BeginSheet),
        .. Record(Brt.WsProp, new byte[27]),
        .. Record(Brt.BeginSheetData),
        .. rows.SelectMany((cells, i) => RowHeader(i).Concat(cells.SelectMany(c => c))),
        .. Record(Brt.EndSheetData),
        .. Record(Brt.EndSheet),
    ];

    /// <summary>
    /// A sheet of a binary workbook: its name, its part's bytes, how it is
    /// shown (0 shown, 1 hidden, 2 very hidden), what kind it is, and whether
    /// it names no relationship at all.
    /// </summary>
    internal sealed record BinarySheet(string Name, byte[] Part, uint State = 0, string Kind = "worksheet", bool NoRelationship = false);

    internal static BinarySheet Bs(string name, params byte[][][] rows) => new(name, SheetBin(rows));

    /// <summary>
    /// Cell styles for numbers, as a binary workbook writes them: one cell
    /// style format first, which a cell's style must not count among, then
    /// the same eight cell formats as <see cref="NumberStyles"/>, in order.
    /// </summary>
    internal static byte[] BinaryNumberStyles() =>
    [
        .. Record(Brt.BeginStyleSheet),
        .. Record(Brt.BeginFmts),
        .. Record(Brt.Fmt, U16(164), W("yyyy-mm-dd hh:mm")),
        .. Record(Brt.Fmt, U16(165), W("[h]:mm:ss")),
        .. Record(Brt.Fmt, U16(166), W("0.00 \"days\"")),
        .. Record(Brt.Fmt, U16(167), W("[$-409]mmmm d, yyyy;@")),
        .. Record(Brt.EndFmts),
        .. Record(Brt.BeginCellStyleXFs),
        .. Xf(0xFFFF, 14),
        .. Record(Brt.EndCellStyleXFs),
        .. Record(Brt.BeginCellXFs),
        .. new[] { 0, 14, 164, 20, 165, 166, 167, 2 }.SelectMany(format => Xf(0, format)),
        .. Record(Brt.EndCellXFs),
        .. Record(Brt.EndStyleSheet),
    ];

    /// <summary>A format record: its parent, its number format, then font, fill, border, rotation, indent and flags, sixteen bytes in all.</summary>
    internal static byte[] Xf(int parent, int format) => Record(Brt.Xf, U16(parent), U16(format), U16(0), U16(0), U16(0), U8(0), U8(0), U32(0));

    /// <summary>A shared string table of these entries, each a plain string with no runs and no phonetic guide.</summary>
    internal static byte[] SharedStringsBin(params string[] entries) =>
    [
        .. Record(Brt.BeginSst, U32((uint)entries.Length), U32((uint)entries.Length)),
        .. entries.SelectMany(s => Record(Brt.SstItem, U8(0), W(s))),
        .. Record(Brt.EndSst),
    ];

    /// <summary>
    /// A whole binary workbook, laid out as a .xlsb is: the package's XML parts
    /// and relationships, and the workbook, its sheets, its shared strings and
    /// its styles as records. Any parts given last are added or put in place
    /// of the usual ones.
    /// </summary>
    internal static byte[] BinaryWorkbook(
        IReadOnlyList<BinarySheet> sheets, string[]? shared = null, byte[]? styles = null, string? title = null, bool date1904 = false,
        (string Id, string Type, string Target, bool External)[]? extraRels = null, IReadOnlyDictionary<string, byte[]>? parts = null)
    {
        const string binaryMain = "application/vnd.ms-excel.sheet.binary.macroEnabled.main";
        var all = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["[Content_Types].xml"] = Encoding.UTF8.GetBytes(
                $"{Xml}<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
                $"<Default Extension=\"bin\" ContentType=\"{binaryMain}\"/>" +
                "<Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
                "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>" +
                "<Override PartName=\"/xl/styles.bin\" ContentType=\"application/vnd.ms-excel.styles\"/>" +
                "<Override PartName=\"/xl/sharedStrings.bin\" ContentType=\"application/vnd.ms-excel.sharedStrings\"/>" +
                "</Types>"),
            ["_rels/.rels"] = Encoding.UTF8.GetBytes(RootRelationships("xl/workbook.bin")),
            ["xl/workbook.bin"] =
            [
                .. Record(Brt.BeginBook),
                .. Record(Brt.WbProp, U32(date1904 ? 1u : 0u), U32(0), W("")),
                .. Record(Brt.BeginBundleShs),
                .. sheets.SelectMany((s, i) => Record(Brt.BundleSh, U32(s.State), U32((uint)i + 1), s.NoRelationship ? NoString : W($"rId{i + 1}"), W(s.Name))),
                .. Record(Brt.EndBundleShs),
                .. Record(Brt.EndBook),
            ],
        };

        var rels = new List<(string, string, string, bool)>();
        for (var i = 0; i < sheets.Count; i++)
        {
            if (sheets[i].NoRelationship) continue;
            var folder = sheets[i].Kind == "worksheet" ? "worksheets" : sheets[i].Kind + "s";
            rels.Add(($"rId{i + 1}", $"{R}/{sheets[i].Kind}", $"{folder}/sheet{i + 1}.bin", false));
            all[$"xl/{folder}/sheet{i + 1}.bin"] = sheets[i].Part;
        }
        if (shared is not null)
        {
            rels.Add(("rIdStrings", $"{R}/sharedStrings", "sharedStrings.bin", false));
            all["xl/sharedStrings.bin"] = SharedStringsBin(shared);
        }
        if (styles is not null)
        {
            rels.Add(("rIdStyles", $"{R}/styles", "styles.bin", false));
            all["xl/styles.bin"] = styles;
        }
        rels.AddRange(extraRels ?? []);
        all["xl/_rels/workbook.bin.rels"] = Encoding.UTF8.GetBytes(Relationships([.. rels]));
        if (title is not null) all["docProps/core.xml"] = Encoding.UTF8.GetBytes(CoreProperties(title));

        foreach (var (name, bytes) in parts ?? new Dictionary<string, byte[]>()) all[name] = bytes;
        return DocxFixtures.Zip(all.Select(p => (p.Key, p.Value)));
    }

    internal static byte[] BinaryWorkbook(params BinarySheet[] sheets) => BinaryWorkbook((IReadOnlyList<BinarySheet>)sheets);

    /// <summary>What Excel writes for a file with a password: a compound file holding an encrypted package.</summary>
    internal static byte[] Encrypted() => DocxFixtures.Encrypted();

    /// <summary>A compound file with no encrypted package in it: a legacy .xls under a .xlsx name.</summary>
    internal static byte[] LegacyUnderANewName() =>
        [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1, .. new byte[64], .. Encoding.Unicode.GetBytes("Workbook"), .. new byte[64]];

    /// <summary>
    /// A workbook that points outside itself in every way a workbook can: a
    /// link to another workbook, a web query and a database connection set to
    /// refresh on open, a query table, a hyperlink, a picture by address, and
    /// formulas that fetch from an address and read the linked workbook, with
    /// the values saved beside them. Each address is on the loopback at this port.
    /// </summary>
    internal static byte[] PointingAt(int port)
    {
        var host = $"http://127.0.0.1:{port}";
        var sheet = new Sheet("Links",
            Row(Str("Rate"), Formula($"WEBSERVICE(\"{host}/rate\")", "Saved web result", "str")) +
            Row(Str("Linked"), Formula("[1]Remote!A1", "Saved linked value", "str")),
            Tail: "<hyperlinks><hyperlink ref=\"A1\" r:id=\"rIdLink\"/></hyperlinks><drawing r:id=\"rIdDrawing\"/>");
        return Workbook(
            [sheet],
            workbookTail: "<externalReferences><externalReference r:id=\"rIdExternal\"/></externalReferences>",
            extraRels:
            [
                ("rIdExternal", $"{R}/externalLink", "externalLinks/externalLink1.xml", false),
                ("rIdConnections", $"{R}/connections", "connections.xml", false),
            ],
            parts: new Dictionary<string, string>
            {
                ["xl/externalLinks/externalLink1.xml"] =
                    $"{Xml}<externalLink xmlns=\"{S}\" xmlns:r=\"{R}\"><externalBook r:id=\"rId1\"><sheetNames><sheetName val=\"Remote\"/></sheetNames></externalBook></externalLink>",
                ["xl/externalLinks/_rels/externalLink1.xml.rels"] =
                    Relationships(("rId1", $"{R}/externalLinkPath", $"{host}/book.xlsx", true)),
                ["xl/connections.xml"] =
                    $"{Xml}<connections xmlns=\"{S}\">" +
                    $"<connection id=\"1\" name=\"Web\" type=\"4\" refreshOnLoad=\"1\"><webPr url=\"{host}/query\"/></connection>" +
                    $"<connection id=\"2\" name=\"Db\" type=\"1\" refreshOnLoad=\"1\"><dbPr connection=\"Provider=SQLOLEDB;Data Source=127.0.0.1,{port}\" command=\"SELECT 1\"/></connection>" +
                    "</connections>",
                ["xl/worksheets/_rels/sheet1.xml.rels"] = Relationships(
                    ("rIdLink", $"{R}/hyperlink", $"{host}/link", true),
                    ("rIdQuery", $"{R}/queryTable", "../queryTables/queryTable1.xml", false),
                    ("rIdDrawing", $"{R}/drawing", "../drawings/drawing1.xml", false)),
                ["xl/queryTables/queryTable1.xml"] = $"{Xml}<queryTable xmlns=\"{S}\" name=\"Web\" connectionId=\"1\" refreshOnLoad=\"1\"/>",
                ["xl/drawings/drawing1.xml"] = $"{Xml}<xdr:wsDr xmlns:xdr=\"http://schemas.openxmlformats.org/drawingml/2006/spreadsheetDrawing\"/>",
                ["xl/drawings/_rels/drawing1.xml.rels"] = Relationships(("rIdImage", $"{R}/image", $"{host}/picture.png", true)),
            });
    }
}

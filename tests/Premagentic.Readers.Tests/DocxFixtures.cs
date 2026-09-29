using System.Buffers.Binary;
using System.IO.Compression;
using System.Security;
using System.Text;

namespace Premagentic.Readers.Tests;

/// <summary>
/// Invented Word files, built here as zips of hand-written XML, so no real
/// document is a fixture and a hostile one is made in memory at whatever size
/// the test needs. Only the parts a reader looks at are written; Word itself
/// would want a few more, and the reader must not depend on them.
/// </summary>
internal static class DocxFixtures
{
    internal const string MainType = "application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml";
    internal const string MacroMainType = "application/vnd.ms-word.document.macroEnabled.main+xml";

    private const string Namespaces =
        "xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\" " +
        "xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\" " +
        "xmlns:mc=\"http://schemas.openxmlformats.org/markup-compatibility/2006\"";

    internal static string X(string text) => SecurityElement.Escape(text);

    // ---- Body pieces ----

    internal static string R(string text) => $"<w:r><w:t xml:space=\"preserve\">{X(text)}</w:t></w:r>";

    internal static string P(string text) => $"<w:p>{R(text)}</w:p>";

    internal static string PRuns(params string[] runs) => $"<w:p>{string.Concat(runs)}</w:p>";

    internal static string Styled(string style, string text) =>
        $"<w:p><w:pPr><w:pStyle w:val=\"{style}\"/></w:pPr>{R(text)}</w:p>";

    internal static string Heading(int level, string text) => Styled($"Heading{level}", text);

    internal static string ListItem(string text) =>
        $"<w:p><w:pPr><w:numPr><w:ilvl w:val=\"0\"/><w:numId w:val=\"1\"/></w:numPr></w:pPr>{R(text)}</w:p>";

    internal static string Table(params string[][] rows) =>
        "<w:tbl>" + string.Concat(rows.Select(row =>
            "<w:tr>" + string.Concat(row.Select(cell => $"<w:tc>{P(cell)}</w:tc>")) + "</w:tr>")) + "</w:tbl>";

    internal static string Inserted(string text) => $"<w:ins w:id=\"1\" w:author=\"A\">{R(text)}</w:ins>";

    internal static string Deleted(string text) =>
        $"<w:del w:id=\"2\" w:author=\"A\"><w:r><w:delText xml:space=\"preserve\">{X(text)}</w:delText></w:r></w:del>";

    /// <summary>A complex field: its instruction, then the result that prints.</summary>
    internal static string Field(string instruction, string result) =>
        "<w:r><w:fldChar w:fldCharType=\"begin\"/></w:r>" +
        $"<w:r><w:instrText xml:space=\"preserve\">{X(instruction)}</w:instrText></w:r>" +
        "<w:r><w:fldChar w:fldCharType=\"separate\"/></w:r>" +
        R(result) +
        "<w:r><w:fldChar w:fldCharType=\"end\"/></w:r>";

    internal static string Hyperlink(string relationshipId, string text) =>
        $"<w:hyperlink r:id=\"{relationshipId}\">{R(text)}</w:hyperlink>";

    private const string DrawingNamespaces =
        "xmlns:wp=\"http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing\" " +
        "xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" " +
        "xmlns:wps=\"http://schemas.microsoft.com/office/word/2010/wordprocessingShape\"";

    private const string VmlNamespace = "xmlns:v=\"urn:schemas-microsoft-com:vml\"";

    /// <summary>
    /// A run holding a text box of these blocks, as Word writes one: a drawing
    /// anchored in the paragraph, and the older shape as the fallback, each
    /// holding the same blocks.
    /// </summary>
    internal static string TextBoxOf(string blocks) =>
        "<w:r><mc:AlternateContent><mc:Choice Requires=\"wps\"><w:drawing>" +
        $"<wp:anchor {DrawingNamespaces}><a:graphic><a:graphicData uri=\"http://schemas.microsoft.com/office/word/2010/wordprocessingShape\">" +
        $"<wps:wsp><wps:txbx><w:txbxContent>{blocks}</w:txbxContent></wps:txbx></wps:wsp>" +
        "</a:graphicData></a:graphic></wp:anchor></w:drawing></mc:Choice>" +
        $"<mc:Fallback>{OldTextBoxRun(blocks)[5..^6]}</mc:Fallback></mc:AlternateContent></w:r>";

    /// <summary>
    /// A run holding a group of shapes drawn together, as one drawing: the
    /// first shape's text box inside alternate content within the group,
    /// written in both choices as a producer may write it, then a second
    /// shape's text box on its own.
    /// </summary>
    internal static string GroupOf(string first, string second) =>
        "<w:r><w:drawing>" +
        $"<wp:anchor {DrawingNamespaces} xmlns:wpg=\"http://schemas.microsoft.com/office/word/2010/wordprocessingGroup\">" +
        "<a:graphic><a:graphicData uri=\"http://schemas.microsoft.com/office/word/2010/wordprocessingGroup\"><wpg:wgp>" +
        $"<mc:AlternateContent><mc:Choice Requires=\"wps\"><wps:wsp><wps:txbx><w:txbxContent>{first}</w:txbxContent></wps:txbx></wps:wsp></mc:Choice>" +
        $"<mc:Fallback><wps:wsp><wps:txbx><w:txbxContent>{first}</w:txbxContent></wps:txbx></wps:wsp></mc:Fallback></mc:AlternateContent>" +
        $"<wps:wsp><wps:txbx><w:txbxContent>{second}</w:txbxContent></wps:txbx></wps:wsp>" +
        "</wpg:wgp></a:graphicData></a:graphic></wp:anchor></w:drawing></w:r>";

    /// <summary>A run holding a text box of one paragraph, in both forms.</summary>
    internal static string TextBox(string text) => TextBoxOf(P(text));

    /// <summary>A run holding a text box in the older shape only, as older files have it.</summary>
    internal static string OldTextBoxRun(string blocks) =>
        $"<w:r><w:pict><v:shape {VmlNamespace}><v:textbox><w:txbxContent>{blocks}</w:txbxContent></v:textbox></v:shape></w:pict></w:r>";

    internal static string Document(string body) =>
        $"<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><w:document {Namespaces}><w:body>{body}<w:sectPr/></w:body></w:document>";

    /// <summary>Styles for Heading1 to Heading3 by name, a style based on Heading2, and a style with its own outline level.</summary>
    internal const string Styles =
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?><w:styles xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\">" +
        "<w:style w:type=\"paragraph\" w:styleId=\"Normal\"><w:name w:val=\"Normal\"/></w:style>" +
        "<w:style w:type=\"paragraph\" w:styleId=\"Heading1\"><w:name w:val=\"heading 1\"/><w:basedOn w:val=\"Normal\"/></w:style>" +
        "<w:style w:type=\"paragraph\" w:styleId=\"Heading2\"><w:name w:val=\"heading 2\"/><w:basedOn w:val=\"Normal\"/></w:style>" +
        "<w:style w:type=\"paragraph\" w:styleId=\"Heading3\"><w:name w:val=\"heading 3\"/><w:basedOn w:val=\"Normal\"/></w:style>" +
        "<w:style w:type=\"paragraph\" w:styleId=\"SectionTitle\"><w:name w:val=\"Section Title\"/><w:basedOn w:val=\"Heading2\"/></w:style>" +
        "<w:style w:type=\"paragraph\" w:styleId=\"Outlined\"><w:name w:val=\"Outlined\"/><w:pPr><w:outlineLvl w:val=\"0\"/></w:pPr></w:style>" +
        "<w:style w:type=\"paragraph\" w:styleId=\"LoopA\"><w:name w:val=\"Loop A\"/><w:basedOn w:val=\"LoopB\"/></w:style>" +
        "<w:style w:type=\"paragraph\" w:styleId=\"LoopB\"><w:name w:val=\"Loop B\"/><w:basedOn w:val=\"LoopA\"/></w:style>" +
        "</w:styles>";

    internal static string Notes(string kind, params string[] texts) =>
        $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><w:{kind}s {Namespaces}>" +
        $"<w:{kind} w:type=\"separator\" w:id=\"-1\"><w:p><w:r><w:separator/></w:r></w:p></w:{kind}>" +
        string.Concat(texts.Select((t, i) => $"<w:{kind} w:id=\"{i + 1}\">{P(t)}</w:{kind}>")) +
        $"</w:{kind}s>";

    internal static string CoreProperties(string title) =>
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?><cp:coreProperties " +
        "xmlns:cp=\"http://schemas.openxmlformats.org/package/2006/metadata/core-properties\" " +
        $"xmlns:dc=\"http://purl.org/dc/elements/1.1/\"><dc:title>{X(title)}</dc:title></cp:coreProperties>";

    internal static string ContentTypes(string mainType = MainType) =>
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
        "<Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
        "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>" +
        $"<Override PartName=\"/word/document.xml\" ContentType=\"{mainType}\"/></Types>";

    internal static string RootRelationships(string target = "word/document.xml") =>
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
        $"<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"{X(target)}\"/>" +
        "</Relationships>";

    // ---- Packages ----

    /// <summary>A whole file: the parts named, and the usual ones for any not given.</summary>
    internal static byte[] Package(string body, IReadOnlyDictionary<string, byte[]>? parts = null, string mainType = MainType)
    {
        var all = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["[Content_Types].xml"] = Encoding.UTF8.GetBytes(ContentTypes(mainType)),
            ["_rels/.rels"] = Encoding.UTF8.GetBytes(RootRelationships()),
            ["word/document.xml"] = Encoding.UTF8.GetBytes(Document(body)),
            ["word/styles.xml"] = Encoding.UTF8.GetBytes(Styles),
        };
        foreach (var (name, data) in parts ?? new Dictionary<string, byte[]>()) all[name] = data;
        return Zip(all.Select(p => (p.Key, p.Value)));
    }

    /// <summary>A whole file with these parts added or put in place of the usual ones.</summary>
    internal static byte[] With(string body, params (string Name, string Text)[] parts) =>
        Package(body, parts.ToDictionary(p => p.Name, p => Encoding.UTF8.GetBytes(p.Text)));

    internal static byte[] Zip(IEnumerable<(string Name, byte[] Data)> entries, CompressionLevel level = CompressionLevel.Optimal)
    {
        using var file = new MemoryStream();
        using (var zip = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (name, data) in entries)
            {
                var entry = zip.CreateEntry(name, level);
                entry.LastWriteTime = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
                using var stream = entry.Open();
                stream.Write(data);
            }
        return file.ToArray();
    }

    /// <summary>
    /// A document part of this many bytes, nearly all of it spaces between two
    /// paragraphs, which compresses to a thousandth of its size.
    /// </summary>
    internal static byte[] InflatingTo(long bytes)
    {
        var head = Encoding.UTF8.GetBytes($"<?xml version=\"1.0\" encoding=\"UTF-8\"?><w:document {Namespaces}><w:body>{P("First.")}");
        var tail = Encoding.UTF8.GetBytes($"{P("Last.")}</w:body></w:document>");
        using var file = new MemoryStream();
        using (var zip = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, text) in new[] { ("[Content_Types].xml", ContentTypes()), ("_rels/.rels", RootRelationships()) })
                using (var s = zip.CreateEntry(name).Open()) s.Write(Encoding.UTF8.GetBytes(text));
            using var stream = zip.CreateEntry("word/document.xml", CompressionLevel.SmallestSize).Open();
            stream.Write(head);
            var spaces = new byte[64 * 1024];
            Array.Fill(spaces, (byte)' ');
            for (long left = bytes - head.Length - tail.Length; left > 0; left -= spaces.Length)
                stream.Write(spaces, 0, (int)Math.Min(spaces.Length, left));
            stream.Write(tail);
        }
        return file.ToArray();
    }

    /// <summary>A file whose zip records say one entry expands to nearly four gigabytes.</summary>
    internal static byte[] StatingAHugeEntry()
    {
        var file = With(P("Small."), ("word/media/big.bin", "tiny"));
        // Every central directory record for the picture: its uncompressed size field.
        var name = Encoding.UTF8.GetBytes("word/media/big.bin");
        for (var at = 0; at + 46 < file.Length; at++)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(at)) != 0x02014b50) continue;
            var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(at + 28));
            if (!file.AsSpan(at + 46, nameLength).SequenceEqual(name)) continue;
            BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(at + 24), 0xF0000000);
        }
        return file;
    }

    /// <summary>A file of this many entries, all of them tiny.</summary>
    internal static byte[] WithEntries(int count) =>
        Zip(Enumerable.Range(0, count).Select(i => ($"extra/{i}.xml", "<x/>"u8.ToArray()))
            .Prepend(("word/document.xml", Encoding.UTF8.GetBytes(Document(P("Body.")))))
            .Prepend(("_rels/.rels", Encoding.UTF8.GetBytes(RootRelationships())))
            .Prepend(("[Content_Types].xml", Encoding.UTF8.GetBytes(ContentTypes()))), CompressionLevel.NoCompression);

    /// <summary>Bytes that are not a zip but end in a zip end record stating this many entries.</summary>
    internal static byte[] EndRecordStating(ushort entries)
    {
        var record = new byte[22];
        BinaryPrimitives.WriteUInt32LittleEndian(record, 0x06054b50);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(8), entries);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(10), entries);
        return [.. "not a zip at all, only an end record"u8.ToArray(), .. record];
    }

    /// <summary>What Word writes for a file with a password: a compound file holding an encrypted package.</summary>
    internal static byte[] Encrypted() =>
        [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1, .. new byte[64], .. Encoding.Unicode.GetBytes("EncryptedPackage"), .. new byte[64]];

    /// <summary>A compound file with no encrypted package in it: a legacy .doc under a .docx name.</summary>
    internal static byte[] LegacyUnderANewName() =>
        [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1, .. new byte[64], .. Encoding.Unicode.GetBytes("WordDocument"), .. new byte[64]];

    internal static byte[] Truncated()
    {
        var whole = Package(P("Some words that will never be found."));
        return whole[..(whole.Length / 2)];
    }

    /// <summary>
    /// A file whose parts point outside it in every way Word knows: an external
    /// picture, a linked template, a hyperlink, and fields that include a file
    /// and a picture by address. Each address is on the loopback at this port.
    /// </summary>
    internal static byte[] PointingAt(int port, string linkText)
    {
        var host = $"http://127.0.0.1:{port}";
        var body =
            PRuns(R("See "), Hyperlink("rIdLink", linkText), R(".")) +
            PRuns(Field($"INCLUDETEXT \"{host}/include.docx\"", "Included text result.")) +
            PRuns(Field($"INCLUDEPICTURE \"{host}/picture.png\" \\d", "")) +
            PRuns(Field($"HYPERLINK \"{host}/field-link\"", "Field link text")) +
            "<w:p><w:r><w:drawing><a:blip xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" r:link=\"rIdImage\"/></w:drawing></w:r></w:p>";
        var documentRels =
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
            $"<Relationship Id=\"rIdLink\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/hyperlink\" Target=\"{host}/link\" TargetMode=\"External\"/>" +
            $"<Relationship Id=\"rIdImage\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/image\" Target=\"{host}/image.png\" TargetMode=\"External\"/>" +
            "</Relationships>";
        var settings =
            $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><w:settings {Namespaces}><w:attachedTemplate r:id=\"rIdTemplate\"/></w:settings>";
        var settingsRels =
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
            $"<Relationship Id=\"rIdTemplate\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/attachedTemplate\" Target=\"{host}/template.dotx\" TargetMode=\"External\"/>" +
            "</Relationships>";
        return With(body,
            ("word/_rels/document.xml.rels", documentRels),
            ("word/settings.xml", settings),
            ("word/_rels/settings.xml.rels", settingsRels));
    }

    /// <summary>A document part with a document type definition whose entity names an address on the loopback.</summary>
    internal static byte[] WithExternalEntity(int port) =>
        With("", ("word/document.xml",
            "<?xml version=\"1.0\"?><!DOCTYPE w:document [<!ENTITY ext SYSTEM \"http://127.0.0.1:" + port + "/entity\">]>" +
            $"<w:document {Namespaces}><w:body><w:p><w:r><w:t>&ext;</w:t></w:r></w:p></w:body></w:document>"));
}

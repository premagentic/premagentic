using System.IO.Compression;
using System.Text;

namespace Premagentic.Readers.Tests;

/// <summary>
/// Invented PDF files, built here byte by byte so no real document is ever a
/// fixture and a hostile one can be made to any size without being committed.
/// The builder writes a correct cross-reference table, so what a fixture tests
/// is what its objects say and not that a reader could not find them.
/// </summary>
internal static class PdfFixtures
{
    private static readonly Encoding Latin1 = Encoding.Latin1;

    /// <summary>The pieces of one file: numbered objects and where the catalog is.</summary>
    internal sealed class Builder
    {
        private readonly SortedDictionary<int, byte[]> _objects = [];

        public Builder Add(int id, string body) => Add(id, Latin1.GetBytes(body));

        public Builder Add(int id, byte[] body)
        {
            _objects[id] = body;
            return this;
        }

        /// <summary>An object that is a stream: its dictionary entries, then the stream's bytes.</summary>
        public Builder AddStream(int id, string dictionaryEntries, byte[] data)
        {
            using var body = new MemoryStream();
            body.Write(Latin1.GetBytes($"<< {dictionaryEntries} /Length {data.Length} >>\nstream\n"));
            body.Write(data);
            body.Write(Latin1.GetBytes("\nendstream"));
            return Add(id, body.ToArray());
        }

        public byte[] Build(int root, string trailerEntries = "")
        {
            using var file = new MemoryStream();
            file.Write(Latin1.GetBytes("%PDF-1.7\n%"));
            file.Write([0xE2, 0xE3, 0xCF, 0xD3, (byte)'\n']);

            var offsets = new Dictionary<int, long>();
            foreach (var (id, body) in _objects)
            {
                offsets[id] = file.Position;
                file.Write(Latin1.GetBytes($"{id} 0 obj\n"));
                file.Write(body);
                file.Write(Latin1.GetBytes("\nendobj\n"));
            }

            var size = _objects.Keys.Max() + 1;
            var xref = file.Position;
            var table = new StringBuilder($"xref\n0 {size}\n0000000000 65535 f \n");
            for (var id = 1; id < size; id++)
                table.Append(offsets.TryGetValue(id, out var at)
                    ? $"{at:D10} 00000 n \n"
                    : "0000000000 65535 f \n");
            table.Append($"trailer\n<< /Size {size} /Root {root} 0 R {trailerEntries} >>\nstartxref\n{xref}\n%%EOF\n");
            file.Write(Latin1.GetBytes(table.ToString()));
            return file.ToArray();
        }
    }

    private static string Escape(string text) =>
        text.Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)");

    /// <summary>The content stream for one page of lines in Helvetica, one line under the next.</summary>
    private static byte[] TextStream(IEnumerable<string> lines)
    {
        var content = new StringBuilder("BT /F1 12 Tf 72 720 Td 16 TL\n");
        foreach (var line in lines) content.Append($"({Escape(line)}) Tj T*\n");
        content.Append("ET\n");
        return Latin1.GetBytes(content.ToString());
    }

    /// <summary>
    /// A file of pages of text. Object 1 is the catalog, 2 the page tree and 3
    /// the font; page n is object 10 + 2n with its content at 11 + 2n.
    /// </summary>
    internal static Builder PagesBuilder(string[][] pages, string? title = null, string catalogExtra = "")
    {
        var builder = new Builder();
        builder.Add(1, $"<< /Type /Catalog /Pages 2 0 R {catalogExtra} >>");
        var kids = string.Join(' ', Enumerable.Range(0, pages.Length).Select(n => $"{10 + 2 * n} 0 R"));
        builder.Add(2, $"<< /Type /Pages /Kids [{kids}] /Count {pages.Length} >>");
        builder.Add(3, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");
        for (var n = 0; n < pages.Length; n++)
        {
            builder.Add(10 + 2 * n,
                $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 3 0 R >> >> /Contents {11 + 2 * n} 0 R >>");
            builder.AddStream(11 + 2 * n, "", TextStream(pages[n]));
        }
        if (title is not null) builder.Add(4, $"<< /Title ({Escape(title)}) >>");
        return builder;
    }

    /// <summary>A file of pages of text, one string per line.</summary>
    internal static byte[] Text(params string[][] pages) => PagesBuilder(pages).Build(1);

    internal static byte[] TextWithTitle(string title, params string[][] pages) =>
        PagesBuilder(pages, title).Build(1, "/Info 4 0 R");

    /// <summary>
    /// A file whose pages are each one picture and no text at all, which is what
    /// a scan is.
    /// </summary>
    internal static byte[] Scanned(int pages)
    {
        var builder = new Builder();
        builder.Add(1, "<< /Type /Catalog /Pages 2 0 R >>");
        builder.Add(2, $"<< /Type /Pages /Kids [{string.Join(' ', Enumerable.Range(0, pages).Select(n => $"{10 + 2 * n} 0 R"))}] /Count {pages} >>");
        builder.AddStream(3, "/Type /XObject /Subtype /Image /Width 1 /Height 1 /ColorSpace /DeviceGray /BitsPerComponent 8", [0x80]);
        for (var n = 0; n < pages; n++)
        {
            builder.Add(10 + 2 * n,
                $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /XObject << /Im1 3 0 R >> >> /Contents {11 + 2 * n} 0 R >>");
            builder.AddStream(11 + 2 * n, "", Latin1.GetBytes("q 612 0 0 792 0 0 cm /Im1 Do Q\n"));
        }
        return builder.Build(1);
    }

    /// <summary>A page tree in which a node lists itself as one of its own children.</summary>
    internal static byte[] PageTreeThatLoops()
    {
        var builder = PagesBuilder([["The real page."]]);
        builder.Add(2, "<< /Type /Pages /Kids [10 0 R 2 0 R] /Count 2 >>");
        return builder.Build(1);
    }

    /// <summary>Two page-tree nodes that each list the other as a child.</summary>
    internal static byte[] PageTreeThatLoopsThroughTwoNodes()
    {
        var builder = PagesBuilder([["The real page."]]);
        builder.Add(2, "<< /Type /Pages /Kids [10 0 R 5 0 R] /Count 2 >>");
        builder.Add(5, "<< /Type /Pages /Parent 2 0 R /Kids [2 0 R] /Count 1 >>");
        return builder.Build(1);
    }

    /// <summary>A page tree nested this deep, one node inside the next, ending in one page.</summary>
    internal static byte[] PageTreeNestedDeep(int depth)
    {
        var builder = PagesBuilder([["The real page."]]);
        // Nodes 1000 upward; the last one holds the page. Node 2 leads to the first.
        builder.Add(2, "<< /Type /Pages /Kids [1000 0 R] /Count 1 >>");
        for (var n = 0; n < depth; n++)
            builder.Add(1000 + n, n == depth - 1
                ? "<< /Type /Pages /Kids [10 0 R] /Count 1 >>"
                : $"<< /Type /Pages /Kids [{1001 + n} 0 R] /Count 1 >>");
        return builder.Build(1);
    }

    /// <summary>A page whose content nests its text in this many arrays, one inside the next.</summary>
    internal static byte[] ContentNested(int depth)
    {
        var builder = PagesBuilder([["Page text."]]);
        builder.AddStream(11, "", Latin1.GetBytes(
            "BT /F1 12 Tf 72 720 Td " + new string('[', depth) + "(Deep.)" + new string(']', depth) + " TJ ET\n"));
        return builder.Build(1);
    }

    /// <summary>A page tree that says it holds this many pages and lists one.</summary>
    internal static byte[] DeclaringPages(int declared)
    {
        var builder = PagesBuilder([["The one real page."]]);
        builder.Add(2, $"<< /Type /Pages /Kids [10 0 R] /Count {declared} >>");
        return builder.Build(1);
    }

    /// <summary>A page tree that lists the same page this many times.</summary>
    internal static byte[] ListingOnePageManyTimes(int times)
    {
        var builder = PagesBuilder([["The one real page."]]);
        builder.Add(2, $"<< /Type /Pages /Kids [{string.Join(' ', Enumerable.Repeat("10 0 R", times))}] /Count {times} >>");
        return builder.Build(1);
    }

    /// <summary>
    /// A page of text whose content stream is Flate compressed and inflates to
    /// this many bytes of NUL, which a content stream reads as white space.
    /// </summary>
    internal static byte[] InflatingTo(long inflatedBytes)
    {
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            var block = new byte[64 * 1024];
            for (long written = 0; written < inflatedBytes; written += block.Length)
                zlib.Write(block, 0, (int)Math.Min(block.Length, inflatedBytes - written));
        }

        var builder = PagesBuilder([["Page text."]]);
        builder.AddStream(11, "/Filter /FlateDecode", compressed.ToArray());
        return builder.Build(1);
    }

    /// <summary>
    /// A page whose content stream is run-length encoded and expands to about
    /// this many bytes of spaces: a filter other than Flate, which is only
    /// counted once it has decoded.
    /// </summary>
    internal static byte[] ExpandingByRunLength(long expandedBytes)
    {
        // Each pair is "repeat the next byte 128 times"; 0x80 ends the data.
        var pairs = (int)(expandedBytes / 128);
        var data = new byte[pairs * 2 + 1];
        for (var i = 0; i < pairs; i++)
        {
            data[2 * i] = 0x81;
            data[2 * i + 1] = (byte)' ';
        }
        data[^1] = 0x80;

        var builder = PagesBuilder([["Page text."]]);
        builder.AddStream(11, "/Filter /RunLengthDecode", data);
        return builder.Build(1);
    }

    /// <summary>
    /// A file whose pages carry every kind of reference that could send a reader
    /// somewhere: link annotations, a launch action, a remote go-to, a form
    /// submission, an open action and a script that names the address, and an
    /// embedded file with words of its own. Each address is on the loopback at
    /// this port, where a test listens.
    /// </summary>
    internal static byte[] PointingAt(int port, string visibleLinkText, string embeddedFileText)
    {
        var host = $"http://127.0.0.1:{port}";
        var builder = PagesBuilder([[visibleLinkText, "The rest of the page."]],
            catalogExtra: $"/OpenAction << /S /URI /URI ({host}/open-action) >> " +
                          "/Names << /EmbeddedFiles << /Names [(note.txt) 20 0 R] >> >>");
        builder.Add(10,
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 3 0 R >> >> /Contents 11 0 R " +
            "/Annots [21 0 R 22 0 R 23 0 R 24 0 R 25 0 R] >>");
        builder.Add(20, "<< /Type /Filespec /F (note.txt) /UF (note.txt) /EF << /F 26 0 R >> >>");
        builder.Add(21, $"<< /Type /Annot /Subtype /Link /Rect [72 700 300 720] /A << /S /URI /URI ({host}/link) >> >>");
        builder.Add(22, "<< /Type /Annot /Subtype /Link /Rect [72 680 300 700] /A << /S /Launch /F (notepad.exe) >> >>");
        builder.Add(23, $"<< /Type /Annot /Subtype /Link /Rect [72 660 300 680] /A << /S /GoToR /F (<< /FS /URL /F ({host}/remote.pdf) >>) /D [0 /Fit] >> >>");
        builder.Add(24, $"<< /Type /Annot /Subtype /Link /Rect [72 640 300 660] /A << /S /SubmitForm /F ({host}/submit) >> >>");
        builder.Add(25, $"<< /Type /Annot /Subtype /Link /Rect [72 620 300 640] /A << /S /JavaScript /JS (app.launchURL\\(\"{host}/script\"\\)) >> >>");
        builder.AddStream(26, "/Type /EmbeddedFile", Latin1.GetBytes(embeddedFileText));
        return builder.Build(1);
    }

    /// <summary>A file that asks for a password no one gave: the standard handler with values that cannot be the right ones.</summary>
    internal static byte[] Encrypted()
    {
        var builder = PagesBuilder([["Hidden text."]]);
        var o = new string('A', 64);
        var u = new string('B', 64);
        builder.Add(5, $"<< /Filter /Standard /V 1 /R 2 /O <{o}> /U <{u}> /P -4 >>");
        return builder.Build(1, "/Encrypt 5 0 R /ID [<00112233445566778899AABBCCDDEEFF> <00112233445566778899AABBCCDDEEFF>]");
    }

    /// <summary>What a damaged download looks like: the start of a file and then nothing that parses.</summary>
    internal static byte[] Truncated()
    {
        var whole = Text(["Some words that will never be found."]);
        return whole[..(whole.Length / 3)];
    }

    /// <summary>Bytes that were never a PDF, under a name that says they are.</summary>
    internal static byte[] NotAPdf() => Latin1.GetBytes("This is a note, not a PDF file at all.\n");
}

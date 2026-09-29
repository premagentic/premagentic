extern alias docx;

using docx::Premagentic.Extensions.Shared;
using Premagentic.Core.Ingestion.Readers;
using Premagentic.Extensions.Pdf;
using Premagentic.Extensions.Xlsx;

namespace Premagentic.Readers.Tests;

/// <summary>
/// How a first-party reader takes the file it was handed: the pipeline's own
/// array when the stream shows the whole of it from the start, a copy
/// otherwise. The shared source is compiled into each reader; these tests take
/// the copy in the Word reader's assembly, which is the same source.
/// </summary>
[Collection(SerialReaders.Name)]
public sealed class ReaderBytesTests
{
    private static byte[] Invented(int length)
    {
        var bytes = new byte[length];
        for (var i = 0; i < length; i++) bytes[i] = (byte)(i * 11 + 5);
        return bytes;
    }

    [Fact]
    public async Task A_read_only_stream_showing_a_whole_array_from_the_start_gives_that_array_and_no_copy()
    {
        var file = Invented(10_000);

        var taken = await ReaderBytes.AllAsync(new MemoryStream(file, 0, file.Length, writable: false, publiclyVisible: true), CancellationToken.None);

        Assert.Same(file, taken);
    }

    [Fact]
    public async Task A_stream_that_hides_its_buffer_is_copied()
    {
        var file = Invented(10_000);

        var taken = await ReaderBytes.AllAsync(new MemoryStream(file, writable: false), CancellationToken.None);

        Assert.NotSame(file, taken);
        Assert.Equal(file, taken);
    }

    [Fact]
    public async Task A_stream_that_shows_only_part_of_an_array_is_copied_as_that_part()
    {
        var file = Invented(10_000);

        var taken = await ReaderBytes.AllAsync(new MemoryStream(file, 100, 5_000, writable: false, publiclyVisible: true), CancellationToken.None);

        Assert.Equal(file[100..5_100], taken);
    }

    [Fact]
    public async Task A_stream_that_is_not_at_its_start_is_copied_from_where_it_stands()
    {
        var file = Invented(10_000);
        var stream = new MemoryStream(file, 0, file.Length, writable: false, publiclyVisible: true) { Position = 40 };

        var taken = await ReaderBytes.AllAsync(stream, CancellationToken.None);

        Assert.Equal(file[40..], taken);
    }

    /// <summary>A file of each format, and the reader for it.</summary>
    public static TheoryData<string> Formats() => ["pdf", "docx", "xlsx"];

    [Theory]
    [MemberData(nameof(Formats))]
    public async Task Each_reader_reads_a_file_from_a_visible_buffer_as_it_reads_one_it_must_copy(string format)
    {
        var (reader, file, path) = format switch
        {
            "pdf" => ((IDocumentReader)new PdfDocumentReader(), PdfFixtures.Text(["The dock opens at seven."], ["Returns go to bay four."]), "dock.pdf"),
            "docx" => (new docx::Premagentic.Extensions.Docx.DocxDocumentReader(),
                DocxFixtures.Package(DocxFixtures.Heading(1, "Leave") + DocxFixtures.P("Annual leave is booked two weeks ahead.")), "leave.docx"),
            _ => (new XlsxDocumentReader(), XlsxFixtures.Workbook(XlsxFixtures.Ws("Dock", XlsxFixtures.Row(XlsxFixtures.Str("Pallets"), XlsxFixtures.Num("14")))), "stock.xlsx"),
        };

        var shown = await reader.ReadAsync(new MemoryStream(file, 0, file.Length, writable: false, publiclyVisible: true), path, CancellationToken.None);
        var hidden = await reader.ReadAsync(new MemoryStream(file, writable: false), path, CancellationToken.None);

        Assert.NotEqual("", shown.Text);
        Assert.Equal(hidden.Text, shown.Text);
        Assert.Equal(hidden.Title, shown.Title);
    }
}

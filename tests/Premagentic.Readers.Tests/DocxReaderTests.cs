using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Premagentic.Conformance;
using Premagentic.Core.Ingestion;
using Premagentic.Core.Ingestion.Readers;
using Premagentic.Extensions.Docx;
using static Premagentic.Readers.Tests.DocxFixtures;

namespace Premagentic.Readers.Tests;

/// <summary>The Word reader held to the reader contract every reader keeps.</summary>
public sealed class DocxReaderConformance : DocumentReaderConformance
{
    protected override IDocumentReader Reader { get; } = new DocxDocumentReader();

    protected override IReadOnlyList<ReaderSample> Samples { get; } =
    [
        new("handbook/leave.docx",
            Package(Heading(1, "Leave") + P("Annual leave is booked two weeks ahead.") + ListItem("Sick leave needs a note after three days.")),
            ["Leave", "Annual leave is booked two weeks ahead.", "Sick leave needs a note after three days."]),
    ];
}

/// <summary>
/// What the Word reader reads and what it refuses. Every file is generated
/// here; none is a real document, and the hostile ones are built in memory.
/// </summary>
[Collection(SerialReaders.Name)]
public sealed class DocxReaderTests
{
    private static readonly DocxReaderLimits Small = DocxReaderLimits.Default with
    {
        MaxEntries = 20,
        MaxReadBytes = 8L * 1024 * 1024,
        MaxTextChars = 2000,
    };

    private static async Task<ReadDocument> ReadAsync(byte[] file, DocxReaderLimits? limits = null, string path = "invented.docx")
    {
        var reader = new DocxDocumentReader(limits ?? DocxReaderLimits.Default);
        using var content = new MemoryStream(file, writable: false);
        return await reader.ReadAsync(content, path, CancellationToken.None);
    }

    private static async Task<string> RefusalAsync(byte[] file, DocxReaderLimits? limits = null) =>
        (await Assert.ThrowsAsync<UnreadableDocumentException>(() => ReadAsync(file, limits))).Message;

    private static IReadOnlyList<string> HeadingPaths(ReadDocument read) =>
        MarkdownChunker.Chunk(read.Text).Select(c => c.HeadingPath).ToList();

    // ---- What it reads ----

    [Fact]
    public async Task Headings_become_the_heading_path_a_passage_is_cited_by()
    {
        var read = await ReadAsync(Package(
            Heading(1, "Leave") + P("Booked two weeks ahead.") +
            Heading(2, "Sick leave") + P("A note after three days.") +
            Heading(1, "Travel") + P("Economy under six hours.")));

        Assert.Equal(["Leave", "Leave > Sick leave", "Travel"], HeadingPaths(read));
    }

    [Fact]
    public async Task A_heading_is_found_through_the_style_it_is_based_on_and_through_an_outline_level()
    {
        var read = await ReadAsync(Package(
            Heading(1, "Policies") + Styled("SectionTitle", "Based on heading two") + P("Under it.") +
            Styled("Outlined", "Outline level one") + P("Under that.") +
            "<w:p><w:pPr><w:outlineLvl w:val=\"1\"/></w:pPr>" + R("Direct level two") + "</w:p>" + P("Last.")));

        Assert.Equal(
            ["Policies > Based on heading two", "Outline level one", "Outline level one > Direct level two"],
            HeadingPaths(read));
    }

    [Fact]
    public async Task A_style_chain_that_loops_is_not_a_heading_and_does_not_hang()
    {
        var read = await ReadAsync(Package(Styled("LoopA", "Looping style") + P("Body.")));

        Assert.Equal("Looping style\n\nBody.\n", read.Text);
    }

    [Fact]
    public async Task Paragraphs_list_items_and_table_cells_come_in_document_order()
    {
        var read = await ReadAsync(Package(
            P("Before the list.") + ListItem("First item.") + ListItem("Second item.") +
            Table(["Bay", "Opens"], ["Four", "Seven"]) + P("After the table.")));

        Assert.Equal(
            "Before the list.\n\n- First item.\n\n- Second item.\n\nBay | Opens\n\nFour | Seven\n\nAfter the table.\n",
            read.Text);
    }

    [Fact]
    public async Task Inserted_text_is_read_and_deleted_text_is_not()
    {
        var read = await ReadAsync(Package(
            PRuns(R("The rate is "), Deleted("forty"), Inserted("fifty"), R(" a day.")) +
            "<w:p><w:moveFrom w:id=\"3\" w:author=\"A\">" + R("Moved away.") + "</w:moveFrom></w:p>" +
            "<w:p><w:moveTo w:id=\"4\" w:author=\"A\">" + R("Moved here.") + "</w:moveTo></w:p>"));

        Assert.Equal("The rate is fifty a day.\n\nMoved here.\n", read.Text);
    }

    [Fact]
    public async Task Deleted_text_is_not_read_even_when_a_producer_writes_it_as_ordinary_text()
    {
        // Word writes deleted text as delText, which is never text here. Other
        // producers have written ordinary text inside a deletion; the deletion
        // itself is what keeps it out.
        var read = await ReadAsync(Package(
            PRuns(R("Keep this. "), "<w:del w:id=\"5\" w:author=\"A\">" + R("Drop this.") + "</w:del>")));

        Assert.Equal("Keep this.\n", read.Text);
    }

    [Fact]
    public async Task Alternate_content_is_read_once_from_its_first_choice()
    {
        var read = await ReadAsync(Package(PRuns(
            R("Symbol: "),
            "<w:r><mc:AlternateContent><mc:Choice Requires=\"w14\"><w:t>star</w:t></mc:Choice>" +
            "<mc:Fallback><w:t>star</w:t></mc:Fallback></mc:AlternateContent></w:r>")));

        Assert.Equal("Symbol: star\n", read.Text);
    }

    [Fact]
    public async Task A_fields_result_is_read_and_its_instruction_is_not()
    {
        var read = await ReadAsync(Package(PRuns(R("Total: "), Field("= 2 + 2 \\* MERGEFORMAT", "4"), R("."))));

        Assert.Equal("Total: 4.\n", read.Text);
    }

    [Fact]
    public async Task Footnotes_and_endnotes_follow_the_body_and_their_separators_are_not_read()
    {
        var read = await ReadAsync(With(
            Heading(1, "Leave") + PRuns(R("Booked ahead."), "<w:r><w:footnoteReference w:id=\"1\"/></w:r>"),
            ("word/footnotes.xml", Notes("footnote", "Two weeks for annual leave.")),
            ("word/endnotes.xml", Notes("endnote", "Revised every January."))));

        Assert.Equal(["Leave", "Footnotes", "Endnotes"], HeadingPaths(read));
        Assert.Contains("Two weeks for annual leave.", read.Text);
        Assert.Contains("Revised every January.", read.Text);
    }

    [Fact]
    public async Task Comments_and_drawings_are_not_read()
    {
        var read = await ReadAsync(With(
            PRuns(R("Visible."), "<w:r><w:commentReference w:id=\"0\"/></w:r>",
                "<w:r><w:drawing><w:t>A picture's alternative text.</w:t></w:drawing></w:r>",
                "<w:r><w:pict><w:t>An old shape's text.</w:t></w:pict></w:r>"),
            ("word/comments.xml",
                "<?xml version=\"1.0\"?><w:comments xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\">" +
                "<w:comment w:id=\"0\"><w:p><w:r><w:t>A reviewer's remark.</w:t></w:r></w:p></w:comment></w:comments>")));

        Assert.Equal("Visible.\n", read.Text);
    }

    // ---- Text boxes ----

    [Fact]
    public async Task A_text_box_is_read_once_after_the_paragraph_it_is_anchored_in()
    {
        var read = await ReadAsync(Package(
            P("Before.") + PRuns(R("The anchor line."), TextBox("Boxed words."), R(" Its end.")) + P("After.")));

        Assert.Equal("Before.\n\nThe anchor line. Its end.\n\nBoxed words.\n\nAfter.\n", read.Text);
    }

    [Fact]
    public async Task A_text_box_in_a_text_box_and_one_in_a_table_cell_are_read_in_document_order()
    {
        var inner = TextBox("The inner box.");
        var outer = TextBoxOf(P("The outer box.") + PRuns(R("The inner anchor."), inner) + ListItem("A listed line in the box."));
        var table = "<w:tbl><w:tr>" +
                    $"<w:tc>{PRuns(R("A cell."), TextBoxOf(P("The cell's box.") + P("Its second line.")))}</w:tc>" +
                    $"<w:tc>{P("The next cell.")}</w:tc>" +
                    "</w:tr></w:tbl>";

        var read = await ReadAsync(Package(Heading(1, "Notice") + PRuns(R("The outer anchor."), outer) + table + P("The end.")));

        Assert.Equal(
            "# Notice\n\nThe outer anchor.\n\nThe outer box.\n\nThe inner anchor.\n\nThe inner box.\n\n- A listed line in the box.\n\n" +
            "A cell. The cell's box. Its second line. | The next cell.\n\nThe end.\n",
            read.Text);
    }

    [Fact]
    public async Task A_document_whose_body_is_all_text_boxes_is_not_empty()
    {
        var read = await ReadAsync(Package(PRuns(TextBoxOf(Heading(1, "Flyer") + P("Doors open at nine."))) + PRuns(TextBox("Bring a badge."))));

        Assert.Equal("# Flyer\n\nDoors open at nine.\n\nBring a badge.\n", read.Text);
        Assert.Equal(["Flyer"], HeadingPaths(read).Distinct());
    }

    [Fact]
    public async Task A_group_of_shapes_reads_each_text_box_once_in_order_whatever_alternate_content_it_holds()
    {
        // Alternate content inside the drawing, not around it, is searched by
        // its first choice only, as alternate content around a run is.
        var read = await ReadAsync(Package(PRuns(R("Anchor."), GroupOf(P("First shape."), P("Second shape.")))));

        Assert.Equal("Anchor.\n\nFirst shape.\n\nSecond shape.\n", read.Text);
    }

    [Fact]
    public async Task A_text_box_in_the_older_shape_alone_is_read()
    {
        var read = await ReadAsync(Package(PRuns(R("Anchor."), OldTextBoxRun(P("Old words.")))));

        Assert.Equal("Anchor.\n\nOld words.\n", read.Text);
    }

    [Fact]
    public async Task A_text_box_inside_a_fields_instruction_is_not_read()
    {
        var read = await ReadAsync(Package(PRuns(
            "<w:r><w:fldChar w:fldCharType=\"begin\"/></w:r>", TextBox("Instruction words."),
            "<w:r><w:fldChar w:fldCharType=\"separate\"/></w:r>", R("Result."), "<w:r><w:fldChar w:fldCharType=\"end\"/></w:r>")));

        Assert.Equal("Result.\n", read.Text);
    }

    [Fact]
    public async Task Text_boxes_nested_in_text_boxes_are_read_once_each()
    {
        // Each level holds the box inside it twice, as Word writes it, once in
        // each form. Reading both forms would read the innermost box once for
        // each path down to it, which doubles with every level.
        var blocks = P("Innermost.");
        for (var i = 0; i < 10; i++) blocks = PRuns(TextBoxOf(blocks));

        var read = await ReadAsync(Package(blocks));

        Assert.Equal("Innermost.\n", read.Text);
    }

    [Fact]
    public async Task Text_boxes_nested_past_the_depth_limit_are_refused_before_they_are_walked()
    {
        // Built in the older shape alone, so the file grows with the depth and
        // not with twice the depth.
        var blocks = P("Deep.");
        for (var i = 0; i < 50; i++) blocks = PRuns(OldTextBoxRun(blocks));

        Assert.StartsWith("the part word/document.xml nests its XML more than 256 deep", await RefusalAsync(Package(blocks)));
    }

    [Fact]
    public async Task Many_text_boxes_are_held_to_the_text_limit_and_the_time_budget()
    {
        var boxes = PRuns([.. Enumerable.Range(1, 200).Select(n => TextBox($"Box {n} of an invented flyer."))]);

        Assert.Equal("gives more than 2000 characters of text, the most this reader keeps", await RefusalAsync(Package(boxes), Small));
        Assert.Equal("took longer than 0 seconds to read",
            await RefusalAsync(Package(PRuns(TextBox("One."))), Small with { TimeBudget = TimeSpan.Zero }));
    }

    [Fact]
    public async Task A_line_of_the_document_cannot_pose_as_a_heading_or_open_a_fence()
    {
        var read = await ReadAsync(Package(Heading(1, "Real") + P("# Posing") + P("```") + Heading(2, "Still real") + P("Under it.")));

        Assert.Equal(["Real", "Real > Still real"], HeadingPaths(read));
        Assert.Contains("\\# Posing", read.Text);
    }

    [Fact]
    public async Task The_title_the_file_states_is_the_title()
    {
        var read = await ReadAsync(With(P("Body."), ("docProps/core.xml", CoreProperties("Dock rota"))));

        Assert.Equal("Dock rota", read.Title);
    }

    [Fact]
    public async Task A_document_with_no_text_is_empty_and_not_skipped()
    {
        var read = await ReadAsync(Package("<w:p/>"));

        Assert.Equal("", read.Text);
        Assert.Null(read.SkipReason);
    }

    // ---- What it skips ----

    [Theory]
    [InlineData("old/minutes.doc", "legacy .doc")]
    [InlineData("forms/request.docm", "macro-enabled")]
    public async Task A_format_it_recognizes_and_does_not_read_is_skipped_with_its_reason(string path, string reason)
    {
        // The bytes are never looked at: the name decides.
        var read = await ReadAsync([1, 2, 3], path: path);

        Assert.Equal(reason, read.SkipReason);
    }

    [Fact]
    public async Task A_docx_carrying_a_macro_project_is_skipped_whatever_its_name_says()
    {
        var withProject = Package(P("Body."), new Dictionary<string, byte[]> { ["word/vbaProject.bin"] = [0xD0, 0xCF, 0x11, 0xE0] });
        var withType = Package(P("Body."), mainType: MacroMainType);

        Assert.Equal("macro-enabled", (await ReadAsync(withProject)).SkipReason);
        Assert.Equal("macro-enabled", (await ReadAsync(withType)).SkipReason);
    }

    [Fact]
    public async Task A_compound_file_under_a_docx_name_is_password_protected_or_a_legacy_doc()
    {
        Assert.Equal("password protected", await RefusalAsync(Encrypted()));
        Assert.Equal("legacy .doc", (await ReadAsync(LegacyUnderANewName())).SkipReason);
    }

    // ---- The container ----

    [Fact]
    public async Task A_part_that_expands_past_the_read_limit_is_refused_before_it_is_held()
    {
        // 64 MB of document that compresses to well under one. Measured with
        // the ratio check out of the way, so this is the size limit alone.
        var bomb = InflatingTo(64L * 1024 * 1024);
        Assert.True(bomb.Length < 1024 * 1024, $"The fixture is {bomb.Length} bytes.");

        var before = GC.GetTotalAllocatedBytes(precise: true);
        var reason = await RefusalAsync(bomb, Small with { MaxCompressionRatio = int.MaxValue });
        var allocated = GC.GetTotalAllocatedBytes(precise: true) - before;

        Assert.Equal("its text parts expand past 8 MB, the most this reader reads", reason);
        Assert.True(allocated < 48L * 1024 * 1024, $"Reading it allocated {allocated / (1024 * 1024)} MB.");
    }

    [Fact]
    public async Task A_part_that_expands_more_than_the_ratio_allows_is_refused()
    {
        var reason = await RefusalAsync(InflatingTo(16L * 1024 * 1024), DocxReaderLimits.Default);

        Assert.Equal("the part word/document.xml expands more than 100 times its size in the file", reason);
    }

    [Fact]
    public async Task A_zip_that_says_it_expands_past_the_limit_is_refused_without_reading_it()
    {
        Assert.Equal("says it expands to more than 1024 MB, the most this reader opens",
            await RefusalAsync(StatingAHugeEntry()));
    }

    [Fact]
    public async Task A_zip_whose_end_record_states_too_many_entries_is_refused_before_it_is_opened()
    {
        // Not a zip at all past its last 22 bytes: opening it would call it
        // damaged, so this reason can only come from the count read first.
        Assert.Equal("holds 60000 entries, more than the 20 this reader opens", await RefusalAsync(EndRecordStating(60000), Small));
    }

    [Fact]
    public async Task A_zip_with_too_many_entries_is_refused_when_opened()
    {
        Assert.Equal("holds 53 entries, more than the 20 this reader opens", await RefusalAsync(WithEntries(50), Small));
    }

    [Fact]
    public async Task A_zip_in_the_64_bit_layout_is_counted_once_opened()
    {
        // Over 65535 entries is written in the 64-bit layout, whose end record
        // states no plain count, so the count after opening is the only one.
        Assert.Equal("holds 70003 entries, more than the 20 this reader opens", await RefusalAsync(WithEntries(70000), Small));
    }

    [Fact]
    public async Task A_part_that_is_itself_an_archive_is_refused()
    {
        var inner = Zip([("word/document.xml", Encoding.UTF8.GetBytes(Document(P("Hidden."))))]);

        Assert.Equal("holds an archive where the part word/document.xml should be",
            await RefusalAsync(Package("", new Dictionary<string, byte[]> { ["word/document.xml"] = inner })));
    }

    [Fact]
    public async Task Two_parts_with_one_name_are_refused()
    {
        var file = Zip([
            ("[Content_Types].xml", Encoding.UTF8.GetBytes(ContentTypes())),
            ("_rels/.rels", Encoding.UTF8.GetBytes(RootRelationships())),
            ("word/document.xml", Encoding.UTF8.GetBytes(Document(P("One story.")))),
            ("Word/Document.xml", Encoding.UTF8.GetBytes(Document(P("Another story.")))),
        ]);

        Assert.Equal("holds two parts named word/document.xml", await RefusalAsync(file));
    }

    [Fact]
    public async Task A_main_part_named_outside_the_file_is_refused_even_when_an_entry_has_that_name()
    {
        // A zip may hold an entry whose name climbs out of it. Nothing here is
        // written to disk, but a name that climbs is refused all the same, so
        // no later change can turn it into a path.
        var file = Zip([
            ("[Content_Types].xml", Encoding.UTF8.GetBytes(ContentTypes())),
            ("_rels/.rels", Encoding.UTF8.GetBytes(RootRelationships("../outside/document.xml"))),
            ("../outside/document.xml", Encoding.UTF8.GetBytes(Document(P("Outside."))))]);

        Assert.Equal("damaged, or not a Word file", await RefusalAsync(file));
    }

    [Fact]
    public async Task A_damaged_zip_or_a_damaged_part_is_unreadable_with_that_reason()
    {
        Assert.Equal("damaged, or not a Word file", await RefusalAsync(Truncated()));
        Assert.Equal("damaged, or not a Word file", await RefusalAsync("plain text, not a zip"u8.ToArray()));
        Assert.Equal("damaged, or not a Word file",
            await RefusalAsync(With("", ("word/document.xml", "<w:document><w:body><w:p>unclosed"))));
    }

    private static string Nested(string open, string close, int depth, string inside) =>
        string.Concat(Enumerable.Repeat(open, depth)) + inside + string.Concat(Enumerable.Repeat(close, depth));

    [Fact]
    public async Task A_part_nested_deeper_than_the_limit_is_refused_before_it_is_walked()
    {
        var file = Package(Nested("<w:ins>", "</w:ins>", 60, P("Deep.")));

        Assert.Equal("the part word/document.xml nests its XML more than 40 deep, the most this reader follows",
            await RefusalAsync(file, Small with { MaxDepth = 40 }));
    }

    [Theory]
    [InlineData("<w:ins>", "</w:ins>")]
    [InlineData("<w:customXml>", "</w:customXml>")]
    [InlineData("<w:sdt><w:sdtContent>", "</w:sdtContent></w:sdt>")]
    public async Task A_part_nested_tens_of_thousands_deep_is_refused_at_the_default_limit(string open, string close)
    {
        // Deep enough to overflow the stack of a recursive walk, and small
        // enough to pass the compression ratio check, which starts past 1 MB.
        // Measured before anything is walked, so this process survives it.
        var file = Package(Nested(open, close, 60_000 / (open.Length / 7 + 1), P("Deep.")));

        Assert.StartsWith("the part word/document.xml nests its XML more than 256 deep", await RefusalAsync(file));
    }

    [Fact]
    public async Task A_run_nested_tens_of_thousands_deep_is_refused_at_the_default_limit()
    {
        var file = Package("<w:p>" + Nested("<w:hyperlink>", "</w:hyperlink>", 30_000, R("Deep.")) + "</w:p>");

        Assert.StartsWith("the part word/document.xml nests its XML more than 256 deep", await RefusalAsync(file));
    }

    [Fact]
    public async Task Tables_nested_in_cells_are_read_once_each()
    {
        // Each inner cell once, however many tables are around it. Taking every
        // cell below a table would read the innermost one once for each path
        // down to it, which doubles with every level.
        var file = Package(Nested("<w:tbl><w:tr><w:tc>", "</w:tc></w:tr></w:tbl>", 18, P("Innermost.")));

        var read = await ReadAsync(file);

        Assert.Equal("Innermost.\n", read.Text);
    }

    [Fact]
    public async Task A_file_that_gives_more_text_than_the_limit_is_refused()
    {
        var body = string.Concat(Enumerable.Range(1, 60).Select(n => P($"Line {n} of a long invented document.")));

        Assert.Equal("gives more than 2000 characters of text, the most this reader keeps", await RefusalAsync(Package(body), Small));
    }

    [Fact]
    public async Task A_file_larger_than_the_limit_is_refused()
    {
        Assert.Equal("larger than 100 bytes, the most this reader reads",
            await RefusalAsync(Package(P("Body.")), Small with { MaxFileBytes = 100 }));
    }

    [Fact]
    public async Task A_file_that_runs_past_its_time_budget_is_stopped()
    {
        Assert.Equal("took longer than 0 seconds to read",
            await RefusalAsync(Package(P("Quick.")), Small with { TimeBudget = TimeSpan.Zero }));
    }

    // ---- Nothing reaches out ----

    [Fact]
    public async Task No_link_picture_template_or_field_is_followed_and_the_link_text_is_read()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var read = await ReadAsync(PointingAt(port, "the leave policy"));

        Assert.Contains("See the leave policy.", read.Text);
        Assert.Contains("Included text result.", read.Text);
        Assert.Contains("Field link text", read.Text);
        Assert.DoesNotContain("127.0.0.1", read.Text);
        Assert.DoesNotContain("INCLUDE", read.Text);
        await Task.Delay(500);
        Assert.False(listener.Pending(), "Something connected to an address the file points at.");

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        await Task.Delay(200);
        Assert.True(listener.Pending(), "The listener cannot see a connection, so its silence above proves nothing.");
    }

    [Fact]
    public async Task A_part_with_an_external_entity_is_refused_and_the_entity_is_never_fetched()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        Assert.Equal("damaged, or not a Word file", await RefusalAsync(WithExternalEntity(port)));
        await Task.Delay(500);
        Assert.False(listener.Pending(), "The entity's address was fetched.");
    }
}

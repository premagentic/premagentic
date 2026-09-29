using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Premagentic.Conformance;
using Premagentic.Core.Ingestion;
using Premagentic.Core.Ingestion.Readers;
using Premagentic.Extensions.Xlsx;
using static Premagentic.Readers.Tests.XlsxFixtures;

namespace Premagentic.Readers.Tests;

/// <summary>The Excel reader held to the reader contract every reader keeps.</summary>
public sealed class XlsxReaderConformance : DocumentReaderConformance
{
    protected override IDocumentReader Reader { get; } = new XlsxDocumentReader();

    protected override IReadOnlyList<ReaderSample> Samples { get; } =
    [
        new("stores/stock-count.xlsx",
            Workbook(Ws("Dock", Row(Str("Pallets"), Num("14")), Row(Str("Returns bay"), Str("Door 3")))),
            ["stock-count.xlsx", "Dock", "Pallets\t14", "Returns bay\tDoor 3"]),
        new("stores/stock-count.xlsb",
            BinaryWorkbook(Bs("Dock", BRow(BStr("Pallets"), BRk(RkInt(14))), BRow(BStr("Returns bay"), BStr("Door 3")))),
            ["stock-count.xlsb", "Dock", "Pallets\t14", "Returns bay\tDoor 3"]),
    ];
}

/// <summary>
/// What the Excel reader reads and what it refuses. Every workbook is generated
/// here; none is a real spreadsheet, and the hostile ones are built in memory.
/// </summary>
[Collection(SerialReaders.Name)]
public sealed class XlsxReaderTests
{
    private static readonly XlsxReaderLimits Small = XlsxReaderLimits.Default with
    {
        MaxEntries = 20,
        MaxReadBytes = 8L * 1024 * 1024,
        MaxSheets = 3,
        MaxCells = 50,
        MaxTextChars = 2000,
    };

    private static async Task<ReadDocument> ReadAsync(byte[] file, XlsxReaderLimits? limits = null, string path = "office/budget.xlsx")
    {
        var reader = new XlsxDocumentReader(limits ?? XlsxReaderLimits.Default);
        using var content = new MemoryStream(file, writable: false);
        return await reader.ReadAsync(content, path, CancellationToken.None);
    }

    private static async Task<string> RefusalAsync(byte[] file, XlsxReaderLimits? limits = null) =>
        (await Assert.ThrowsAsync<UnreadableDocumentException>(() => ReadAsync(file, limits))).Message;

    private static IReadOnlyList<string> HeadingPaths(ReadDocument read) =>
        MarkdownChunker.Chunk(read.Text).Select(c => c.HeadingPath).Distinct().ToList();

    // ---- What it reads ----

    [Fact]
    public async Task Each_sheet_is_a_heading_under_the_file_name_and_each_row_a_paragraph_of_its_cells_with_a_tab_between()
    {
        var file = Workbook(
            Ws("Q1", Row(Str("Item"), Str("Cost")), Row(Str("Paper"), Num("12.5"))),
            Ws("Q2", Row(Str("Toner"), Num("40"))));

        var read = await ReadAsync(file);

        Assert.Equal("# budget.xlsx\n\n## Q1\n\nItem\tCost\n\nPaper\t12.5\n\n## Q2\n\nToner\t40\n", read.Text);
        Assert.Equal(["budget.xlsx > Q1", "budget.xlsx > Q2"], HeadingPaths(read));
    }

    [Fact]
    public async Task Rows_come_in_order_cells_left_to_right_and_an_empty_cell_adds_no_tab()
    {
        var file = Workbook(Ws("Plan",
            Row(Str("Monday"), "<c/>", "<c r=\"C1\"></c>", Str("Deliveries")),
            Row("<c/>", Str("Tuesday")),
            Row("<c/>", "<c/>")));

        var read = await ReadAsync(file);

        Assert.Equal("# budget.xlsx\n\n## Plan\n\nMonday\tDeliveries\n\nTuesday\n", read.Text);
    }

    [Fact]
    public async Task Shared_strings_and_rich_text_are_read_and_a_phonetic_guide_is_not()
    {
        var file = Workbook(
            [Ws("Names", Row(Shared(0), Shared(1)), Row(Shared(2)))],
            shared:
            [
                T("Warehouse"),
                "<r><t>North </t></r><r><rPr><b/></rPr><t>gate</t></r>",
                T("Kanji") + "<rPh sb=\"0\" eb=\"5\"><t>PHONETIC</t></rPh><phoneticPr fontId=\"1\"/>",
            ]);

        var read = await ReadAsync(file);

        Assert.Contains("Warehouse\tNorth gate", read.Text);
        Assert.Contains("Kanji", read.Text);
        Assert.DoesNotContain("PHONETIC", read.Text);
    }

    [Fact]
    public async Task A_shared_string_index_outside_the_table_is_an_empty_cell()
    {
        var read = await ReadAsync(Workbook([Ws("S", Row(Shared(7), Str("After")))], shared: [T("Only")]));

        Assert.Contains("\n\nAfter\n", read.Text);
    }

    [Fact]
    public async Task A_formula_is_never_evaluated_its_saved_value_is_the_text_and_one_saved_with_none_is_empty()
    {
        var file = Workbook(Ws("Sums",
            Row(Str("Total"), Formula("SUM(B2:B9)", "118")),
            Row(Str("Label"), Formula("CONCAT(\"Bay \",\"four\")", "Bay four", "str")),
            Row(Str("Unsaved"), Formula("NOW()", null))));

        var read = await ReadAsync(file);

        Assert.Contains("Total\t118", read.Text);
        Assert.Contains("Label\tBay four", read.Text);
        Assert.Contains("\n\nUnsaved\n", read.Text);
        Assert.DoesNotContain("SUM", read.Text);
        Assert.DoesNotContain("CONCAT", read.Text);
        Assert.DoesNotContain("NOW", read.Text);
    }

    [Fact]
    public async Task Numbers_read_as_General_shows_them_and_booleans_and_errors_as_written()
    {
        var file = Workbook(Ws("Values",
            Row(Num("0.30000000000000004"), Num("1234.5"), Num("1E+20"), Num("-7")),
            Row(Bool(true), Bool(false), Error("#DIV/0!"))));

        var read = await ReadAsync(file);

        Assert.Contains("0.3\t1234.5\t1E+20\t-7", read.Text);
        Assert.Contains("TRUE\tFALSE\t#DIV/0!", read.Text);
    }

    [Fact]
    public async Task A_number_styled_as_a_date_or_a_time_reads_as_one()
    {
        var file = Workbook(
            [Ws("Dates", Row(
                Num("46082", 1), Num("46082.75", 2), Num("0.5625", 3), Num("1.5", 4),
                Num("3", 5), Num("46082", 6), Num("1234.5", 7), Num("46082", 0)))],
            styles: NumberStyles);

        var read = await ReadAsync(file);

        Assert.Contains("2026-03-01\t2026-03-01 18:00\t13:30\t1.5\t3\t2026-03-01\t1234.5\t46082", read.Text);
    }

    [Fact]
    public async Task The_1900_date_system_keeps_the_day_that_never_was_and_the_1904_system_counts_from_1904()
    {
        var days1900 = Workbook([Ws("D", Row(Num("59", 1), Num("60", 1), Num("61", 1)))], styles: NumberStyles);
        var days1904 = Workbook([Ws("D", Row(Num("0", 1), Num("1", 1)))], styles: NumberStyles, workbookPr: "<workbookPr date1904=\"1\"/>");

        Assert.Contains("1900-02-28\t1900-02-29\t1900-03-01", (await ReadAsync(days1900)).Text);
        Assert.Contains("1904-01-01\t1904-01-02", (await ReadAsync(days1904)).Text);
    }

    [Fact]
    public async Task A_date_style_on_a_number_no_date_can_be_leaves_the_number()
    {
        var file = Workbook([Ws("D", Row(Num("-3", 1), Num("3000000", 1), Num("2.5", 3)))], styles: NumberStyles);

        Assert.Contains("-3\t3000000\t2.5", (await ReadAsync(file)).Text);
    }

    [Fact]
    public async Task A_serial_past_the_last_day_of_either_date_system_reads_as_a_number()
    {
        // The 1904 system's last day is 1,462 serials before the 1900
        // system's, and the last second of the last day rounds into the next.
        var days1904 = Workbook([Ws("D", Row(Num("2957003", 1), Num("2958000", 1)))], styles: NumberStyles, workbookPr: "<workbookPr date1904=\"1\"/>");
        var days1900 = Workbook([Ws("D", Row(Num("2958465", 1), Num("2958465.99999999", 1)))], styles: NumberStyles);

        Assert.Contains("9999-12-31\t2958000", (await ReadAsync(days1904)).Text);
        Assert.Contains("9999-12-31\t2958465.99999999", (await ReadAsync(days1900)).Text);
    }

    [Fact]
    public async Task An_escape_for_a_control_or_for_half_a_surrogate_pair_reads_as_a_space_or_a_replacement_character()
    {
        // Escapes a file may carry for characters XML cannot: a NUL, a bell,
        // a high surrogate with no low one after it, and a whole pair.
        var file = Workbook(Ws("T", Row(Str("_x0000_nul_x0007_bell_xD800_lone and a pair _xD83D__xDE00_"))));

        var read = await ReadAsync(file);

        Assert.Contains("nul bell�lone and a pair \U0001F600", read.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("\0", read.Text, StringComparison.Ordinal);
        // What the database's UTF-8 encoding would refuse, refused here first.
        _ = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetBytes(read.Text);
    }

    [Fact]
    public async Task Hidden_sheets_and_hidden_rows_are_read()
    {
        var file = Workbook(
            new Sheet("Shown", Row(Str("Visible row"))),
            new Sheet("Tucked away", HiddenRow(Str("Hidden row")), State: "hidden"),
            new Sheet("Very hidden", Row(Str("Very hidden row")), State: "veryHidden"));

        var read = await ReadAsync(file);

        Assert.Contains("Hidden row", read.Text);
        Assert.Contains("Very hidden row", read.Text);
        Assert.Contains("budget.xlsx > Tucked away", HeadingPaths(read));
    }

    [Fact]
    public async Task A_chart_sheet_a_sheet_with_no_text_comments_and_defined_names_add_nothing()
    {
        var file = Workbook(
            [new Sheet("Chart", "", Kind: "chartsheet"), Ws("Blank", Row("<c/>")), Ws("Data", Row(Str("Kept")))],
            workbookTail: "<definedNames><definedName name=\"Secret\">\"Defined name text\"</definedName></definedNames>",
            extraRels: [("rIdComments", $"{R}/comments", "comments1.xml", false)],
            parts: new Dictionary<string, string>
            {
                ["xl/comments1.xml"] = $"<comments xmlns=\"{S}\"><commentList><comment ref=\"A1\"><text><t>Comment text</t></text></comment></commentList></comments>",
            });

        var read = await ReadAsync(file);

        Assert.Equal("# budget.xlsx\n\n## Data\n\nKept\n", read.Text);
    }

    [Fact]
    public async Task A_cells_tabs_and_line_breaks_become_spaces_and_its_escapes_are_turned_back()
    {
        var file = Workbook(Ws("T", Row(Str("one\ttwo"), Str("line\nbreak"), Str("carriage_x000D_return"))));

        var read = await ReadAsync(file);

        Assert.Contains("one two\tline break\tcarriage return", read.Text);
    }

    [Fact]
    public async Task A_cell_cannot_pose_as_a_heading_or_open_a_fence_and_nor_can_a_sheet_name()
    {
        var file = Workbook(Ws("# Not a sheet heading",
            Row(Str("# Not a heading")),
            Row(Str("```")),
            Row(Str("After the fence"))));

        var read = await ReadAsync(file);

        Assert.Equal(["budget.xlsx > \\# Not a sheet heading"], HeadingPaths(read));
        Assert.Contains("\\# Not a heading", read.Text);
        Assert.Contains("\\```", read.Text);
    }

    [Fact]
    public async Task The_title_the_file_states_is_the_title()
    {
        Assert.Equal("Stock count", (await ReadAsync(Workbook([Ws("S", Row(Str("x")))], title: "Stock count"))).Title);
        Assert.Null((await ReadAsync(Workbook(Ws("S", Row(Str("x")))))).Title);
    }

    [Fact]
    public async Task A_workbook_with_no_text_is_empty_and_not_skipped()
    {
        var read = await ReadAsync(Workbook(Ws("Blank", Row("<c/>"))));

        Assert.Equal("", read.Text);
        Assert.Null(read.SkipReason);
    }

    [Fact]
    public async Task A_workbook_in_the_strict_namespaces_is_read()
    {
        var read = await ReadAsync(Workbook([Ws("Strict", Row(Str("Read in strict")))], strict: true));

        Assert.Contains("Read in strict", read.Text);
    }

    [Fact]
    public async Task A_sheet_found_by_an_absolute_target_or_an_escaped_one_is_read()
    {
        var file = Workbook([Ws("Absolute", Row(Str("Found")))], parts: new Dictionary<string, string>
        {
            ["xl/_rels/workbook.xml.rels"] = Relationships(("rId1", $"{R}/worksheet", "/xl/worksheets/sheet%201.xml", false)),
            ["xl/worksheets/sheet 1.xml"] = SheetXml(Row(Str("Found at an escaped name"))),
        });

        Assert.Contains("Found at an escaped name", (await ReadAsync(file)).Text);
    }

    [Fact]
    public async Task The_sample_workbook_the_clean_install_proof_ingests_reads_as_the_proof_expects()
    {
        var root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "Premagentic.slnx"))) root = Path.GetDirectoryName(root)!;
        var file = await File.ReadAllBytesAsync(Path.Combine(root, "sample-docs", "spreadsheets", "equipment-register.xlsx"));

        var read = await ReadAsync(file, path: "spreadsheets/equipment-register.xlsx");

        Assert.Equal("Equipment register", read.Title);
        Assert.Equal(
            "# equipment-register.xlsx\n\n## Forklifts\n\n" +
            "Unit\tLast service\tCharging bay\n\nForklift 1\t2026-03-01\tBay C\n\n" +
            "Forklift 2\t2026-04-01\tBay C\n\nTotal units\t2\n\n" +
            "## Keys\n\nThe spare key to the cage is kept in the site office safe, second shelf.\n",
            read.Text);
    }

    // ---- What it skips ----

    [Theory]
    [InlineData("finance/old-ledger.xls", "legacy .xls")]
    [InlineData("finance/tracker.xlsm", "macro-enabled")]
    [InlineData("FINANCE/TRACKER.XLSM", "macro-enabled")]
    public async Task A_format_it_recognizes_and_does_not_read_is_skipped_with_its_reason(string path, string reason)
    {
        // Never opened: these bytes would read as a workbook.
        var read = await ReadAsync(Workbook(Ws("S", Row(Str("Never read")))), path: path);

        Assert.Equal(reason, read.SkipReason);
        Assert.Equal("", read.Text);
    }

    [Fact]
    public async Task A_workbook_saved_as_xlsx_under_a_xlsb_name_is_read()
    {
        // What decides how a workbook is read is its main part, not its name.
        var read = await ReadAsync(Workbook(Ws("S", Row(Str("Read as XML")))), path: "finance/rates.xlsb");

        Assert.Null(read.SkipReason);
        Assert.Contains("Read as XML", read.Text);
    }

    [Fact]
    public async Task A_workbook_carrying_a_macro_project_or_declared_macro_enabled_is_skipped_whatever_its_name_says()
    {
        var withProject = Workbook([Ws("S", Row(Str("x")))], parts: new Dictionary<string, string> { ["xl/vbaProject.bin"] = "not a real project" });
        var declared = Workbook([Ws("S", Row(Str("x")))], mainType: MacroMainType);

        Assert.Equal("macro-enabled", (await ReadAsync(withProject)).SkipReason);
        Assert.Equal("macro-enabled", (await ReadAsync(declared)).SkipReason);
    }

    [Theory]
    [InlineData("http://schemas.microsoft.com/office/2006/relationships/xlMacrosheet")]
    [InlineData("http://schemas.microsoft.com/office/2006/relationships/xlIntlMacrosheet")]
    public async Task A_workbook_with_an_excel_4_macro_sheet_is_skipped_as_macro_enabled(string type)
    {
        var file = Workbook([Ws("S", Row(Str("x")))], extraRels: [("rIdMacro", type, "macrosheets/sheet1.xml", false)],
            parts: new Dictionary<string, string> { ["xl/macrosheets/sheet1.xml"] = $"<xm:macrosheet xmlns:xm=\"http://schemas.microsoft.com/office/excel/2006/main\"/>" });

        Assert.Equal("macro-enabled", (await ReadAsync(file)).SkipReason);
    }

    [Fact]
    public async Task A_compound_file_under_a_xlsx_name_is_password_protected_or_a_legacy_xls()
    {
        Assert.Equal("password protected", await RefusalAsync(Encrypted()));
        Assert.Equal("legacy .xls", (await ReadAsync(LegacyUnderANewName())).SkipReason);
    }

    // ---- What it refuses ----

    [Fact]
    public async Task A_damaged_file_or_a_damaged_part_is_unreadable_with_that_reason()
    {
        var whole = Workbook(Ws("S", Row(Str("Some words that will never be found."))));

        Assert.Equal("damaged, or not an Excel file", await RefusalAsync(whole[..(whole.Length / 2)]));
        Assert.Equal("damaged, or not an Excel file", await RefusalAsync("plain text, not a zip"u8.ToArray()));
        Assert.Equal("damaged, or not an Excel file", await RefusalAsync(Workbook([Ws("S")], parts: new Dictionary<string, string>
        {
            ["xl/worksheets/sheet1.xml"] = $"<worksheet xmlns=\"{S}\"><sheetData><row><c><v>unclosed</c></row></sheetData></worksheet>",
        })));
    }

    [Fact]
    public async Task A_workbook_whose_main_part_is_not_a_workbook_or_whose_sheet_is_missing_is_damaged()
    {
        Assert.Equal("damaged, or not an Excel file", await RefusalAsync(Workbook([Ws("S")], parts: new Dictionary<string, string>
        {
            ["xl/workbook.xml"] = $"<document xmlns=\"{S}\"/>",
        })));
        Assert.Equal("damaged, or not an Excel file", await RefusalAsync(Workbook([Ws("S")], parts: new Dictionary<string, string>
        {
            ["xl/_rels/workbook.xml.rels"] = Relationships(("rId1", $"{R}/worksheet", "worksheets/missing.xml", false)),
        })));
        Assert.Equal("damaged, or not an Excel file", await RefusalAsync(Workbook([Ws("S")], parts: new Dictionary<string, string>
        {
            ["xl/_rels/workbook.xml.rels"] = Relationships(("rIdOther", $"{R}/worksheet", "worksheets/sheet1.xml", false)),
        })));
    }

    [Theory]
    [InlineData("../outside/sheet1.xml", false)]
    [InlineData("worksheets/../../outside.xml", false)]
    [InlineData("http://127.0.0.1:9/sheet.xml", true)]
    public async Task A_sheet_named_outside_the_file_is_refused_and_never_followed(string target, bool external)
    {
        var file = Workbook([Ws("S", Row(Str("Inside")))], parts: new Dictionary<string, string>
        {
            ["xl/_rels/workbook.xml.rels"] = Relationships(("rId1", $"{R}/worksheet", target, external)),
            ["outside/sheet1.xml"] = SheetXml(Row(Str("Outside"))),
        });

        Assert.Equal("damaged, or not an Excel file", await RefusalAsync(file));
    }

    [Fact]
    public async Task A_workbook_with_more_sheets_than_the_limit_is_refused()
    {
        var file = Workbook(Enumerable.Range(1, 4).Select(i => Ws($"S{i}", Row(Str("x")))).ToArray());

        Assert.Equal("holds 4 sheets, more than the 3 this reader reads", await RefusalAsync(file, Small));
        Assert.NotNull(await ReadAsync(Workbook(Enumerable.Range(1, 3).Select(i => Ws($"S{i}", Row(Str("x")))).ToArray()), Small));
    }

    [Fact]
    public async Task The_cell_limit_counts_empty_cells_over_every_sheet()
    {
        // 30 cells on each of two sheets, 20 of them empty: 60 over the 50 allowed.
        var sheet = string.Concat(Enumerable.Range(0, 10).Select(_ => Row(Str("a"), "<c/>", "<c/>")));

        Assert.Equal("holds more than 50 cells, the most this reader reads",
            await RefusalAsync(Workbook(Ws("One", sheet), Ws("Two", sheet)), Small));
        Assert.NotNull(await ReadAsync(Workbook(Ws("One", sheet)), Small));
    }

    [Fact]
    public async Task A_sheet_of_every_row_excel_allows_with_one_empty_cell_each_is_refused_at_the_default_cell_limit()
    {
        // No text at all, so no text limit can stop it, and a dimension that
        // claims every cell Excel allows. Only the cell count does.
        var file = RowsOfOneEmptyCell(1_048_576);

        Assert.Equal("holds more than 1000000 cells, the most this reader reads", await RefusalAsync(file));
    }

    [Fact]
    public async Task The_same_sheet_is_stopped_by_the_time_budget_when_the_cell_limit_is_out_of_the_way()
    {
        var file = RowsOfOneEmptyCell(1_048_576);
        var budget = XlsxReaderLimits.Default with { MaxCells = int.MaxValue, TimeBudget = TimeSpan.FromMilliseconds(100) };

        var clock = Stopwatch.StartNew();
        var reason = await RefusalAsync(file, budget);

        Assert.Equal("took longer than 0.1 seconds to read", reason);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), $"Stopped after {clock.Elapsed.TotalSeconds:0.0} s.");
    }

    [Fact]
    public async Task More_shared_strings_than_the_cell_limit_is_refused()
    {
        var file = Workbook([Ws("S", Row(Shared(0)))], shared: Enumerable.Range(0, 51).Select(i => T($"s{i}")).ToArray());

        Assert.Equal("holds more than 50 shared strings, more than the cells this reader reads", await RefusalAsync(file, Small));
    }

    [Fact]
    public async Task One_long_shared_string_used_by_many_cells_is_stopped_at_the_text_limit()
    {
        // A few kilobytes in the file that would read as megabytes of text.
        var file = Workbook([Ws("S", string.Concat(Enumerable.Range(0, 40).Select(_ => Row(Shared(0)))))],
            shared: [T(new string('a', 100))]);

        Assert.Equal("gives more than 2000 characters of text, the most this reader keeps", await RefusalAsync(file, Small));
    }

    [Fact]
    public async Task A_file_larger_than_the_limit_is_refused()
    {
        Assert.Equal("larger than 100 bytes, the most this reader reads",
            await RefusalAsync(Workbook(Ws("S", Row(Str("x")))), Small with { MaxFileBytes = 100 }));
    }

    [Fact]
    public async Task The_readers_container_limits_are_the_ones_it_was_given()
    {
        var extras = Enumerable.Range(0, 30).ToDictionary(i => $"extra/{i}.xml", _ => "<x/>");

        Assert.Equal("holds 35 entries, more than the 20 this reader opens",
            await RefusalAsync(Workbook([Ws("S", Row(Str("x")))], parts: extras), Small));
    }

    [Fact]
    public async Task A_sheet_that_expands_more_than_the_ratio_allows_is_refused()
    {
        var file = Workbook(Ws("S", Row(Str("Start")) + new string(' ', 16 * 1024 * 1024) + Row(Str("End"))));

        Assert.Equal("the part xl/worksheets/sheet1.xml expands more than 100 times its size in the file", await RefusalAsync(file));
    }

    [Fact]
    public async Task A_file_that_runs_past_its_time_budget_is_stopped()
    {
        Assert.Equal("took longer than 0 seconds to read",
            await RefusalAsync(Workbook(Ws("S", Row(Str("Quick.")))), Small with { TimeBudget = TimeSpan.Zero }));
    }

    private static string Nested(string open, string close, int depth, string inside) =>
        string.Concat(Enumerable.Repeat(open, depth)) + inside + string.Concat(Enumerable.Repeat(close, depth));

    [Fact]
    public async Task Rich_text_nested_tens_of_thousands_deep_is_refused_at_the_default_limit()
    {
        var file = Workbook([Ws("S", Row(Shared(0)))], shared: [Nested("<r>", "</r>", 30_000, T("Deep."))]);

        Assert.StartsWith("the part xl/sharedStrings.xml nests its XML more than 256 deep", await RefusalAsync(file));
    }

    [Fact]
    public async Task A_cell_nested_tens_of_thousands_deep_is_refused_at_the_default_limit()
    {
        var file = Workbook(Ws("S", Row("<c t=\"inlineStr\"><is>" + Nested("<r>", "</r>", 30_000, T("Deep.")) + "</is></c>")));

        Assert.StartsWith("the part xl/worksheets/sheet1.xml nests its XML more than 256 deep", await RefusalAsync(file));
    }

    // ---- Nothing reaches out ----

    [Fact]
    public async Task No_link_connection_query_or_picture_is_followed_and_the_saved_values_are_read()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var read = await ReadAsync(PointingAt(port));

        Assert.Contains("Rate\tSaved web result", read.Text);
        Assert.Contains("Linked\tSaved linked value", read.Text);
        Assert.DoesNotContain("127.0.0.1", read.Text);
        Assert.DoesNotContain("WEBSERVICE", read.Text);
        await Task.Delay(500);
        Assert.False(listener.Pending(), "Something connected to an address the workbook points at.");

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
        var file = Workbook([Ws("S")], parts: new Dictionary<string, string>
        {
            ["xl/worksheets/sheet1.xml"] =
                $"<?xml version=\"1.0\"?><!DOCTYPE worksheet [<!ENTITY ext SYSTEM \"http://127.0.0.1:{port}/entity\">]>" +
                $"<worksheet xmlns=\"{S}\"><sheetData><row><c t=\"inlineStr\"><is><t>&ext;</t></is></c></row></sheetData></worksheet>",
        });

        Assert.Equal("damaged, or not an Excel file", await RefusalAsync(file));
        await Task.Delay(500);
        Assert.False(listener.Pending(), "The entity's address was fetched.");
    }
}

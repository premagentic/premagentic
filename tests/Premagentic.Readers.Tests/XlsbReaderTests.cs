using System.Net;
using System.Net.Sockets;
using System.Text;
using Premagentic.Core.Ingestion;
using Premagentic.Core.Ingestion.Readers;
using Premagentic.Extensions.Xlsx;
using static Premagentic.Readers.Tests.XlsxFixtures;

namespace Premagentic.Readers.Tests;

/// <summary>
/// What the Excel reader reads from a binary workbook (.xlsb) and what it
/// refuses. Every workbook is written here record by record from the format's
/// specification; none is a file Excel saved, and the hostile ones are built
/// in memory.
/// </summary>
[Collection(SerialReaders.Name)]
public sealed class XlsbReaderTests
{
    private static readonly XlsxReaderLimits Small = XlsxReaderLimits.Default with
    {
        MaxEntries = 20,
        MaxReadBytes = 8L * 1024 * 1024,
        MaxSheets = 3,
        MaxCells = 50,
        MaxTextChars = 2000,
    };

    private const string Damaged = "damaged, or not an Excel file";

    private static async Task<ReadDocument> ReadAsync(byte[] file, XlsxReaderLimits? limits = null, string path = "office/budget.xlsb")
    {
        var reader = new XlsxDocumentReader(limits ?? XlsxReaderLimits.Default);
        using var content = new MemoryStream(file, writable: false);
        return await reader.ReadAsync(content, path, CancellationToken.None);
    }

    private static async Task<string> RefusalAsync(byte[] file, XlsxReaderLimits? limits = null) =>
        (await Assert.ThrowsAsync<UnreadableDocumentException>(() => ReadAsync(file, limits))).Message;

    private static IReadOnlyList<string> HeadingPaths(ReadDocument read) =>
        MarkdownChunker.Chunk(read.Text).Select(c => c.HeadingPath).Distinct().ToList();

    /// <summary>A workbook of one sheet whose part is these bytes, for a sheet no well-formed builder would write.</summary>
    private static byte[] WithSheetPart(byte[] part, string[]? shared = null) =>
        BinaryWorkbook([Bs("S")], shared: shared, parts: new Dictionary<string, byte[]> { ["xl/worksheets/sheet1.bin"] = part });

    /// <summary>The start of a sheet's data, as far as a first row's header, for a sheet that goes on with whatever follows.</summary>
    private static byte[] SheetOpening() => [.. Record(Brt.BeginSheet), .. Record(Brt.BeginSheetData), .. RowHeader(0)];

    // ---- What it reads ----

    [Fact]
    public async Task Each_sheet_is_a_heading_under_the_file_name_and_each_row_a_paragraph_of_its_cells_with_a_tab_between()
    {
        var file = BinaryWorkbook(
            Bs("Q1", BRow(BStr("Item"), BStr("Cost")), BRow(BStr("Paper"), BNum(12.5))),
            Bs("Q2", BRow(BStr("Toner"), BRk(RkInt(40)))));

        var read = await ReadAsync(file);

        Assert.Equal("# budget.xlsb\n\n## Q1\n\nItem\tCost\n\nPaper\t12.5\n\n## Q2\n\nToner\t40\n", read.Text);
        Assert.Equal(["budget.xlsb > Q1", "budget.xlsb > Q2"], HeadingPaths(read));
        Assert.Null(read.SkipReason);
    }

    [Fact]
    public async Task It_reads_the_same_text_and_title_as_the_same_workbook_saved_as_xlsx()
    {
        var xlsx = Workbook(
            [
                Ws("Stock",
                    Row(Shared(0), Num("14")),
                    Row(Str("Checked"), Num("46082", 1), Num("0.5625", 3)),
                    Row(Bool(true), Error("#N/A"), "<c/>", Formula("SUM(A1:A3)", "118"), Formula("A1", "Dock", "str"),
                        Formula("A1>0", "1", "b"), Formula("1/0", "#DIV/0!", "e")),
                    Row("<c/>", "<c/>")),
                new Sheet("Old", Row(Str("Kept")), State: "hidden"),
            ],
            shared: [T("Pallets")], styles: NumberStyles, title: "Stock count");
        var xlsb = BinaryWorkbook(
            [
                Bs("Stock",
                    BRow(BShared(0), BRk(RkInt(14))),
                    BRow(BStr("Checked"), BNum(46082, 1), BNum(0.5625, 3)),
                    BRow(BBool(true), BError(0x2A), BBlank(), BFormulaNum(118), BFormulaText("Dock"),
                        BFormulaBool(true), BFormulaError(0x07)),
                    BRow(BBlank(), BBlank())),
                Bs("Old", BRow(BStr("Kept"))) with { State = 1 },
            ],
            shared: ["Pallets"], styles: BinaryNumberStyles(), title: "Stock count");

        var fromXml = await ReadAsync(xlsx, path: "stores/stock.xlsx");
        var fromRecords = await ReadAsync(xlsb, path: "stores/stock.xlsx");

        Assert.Equal(
            "# stock.xlsx\n\n## Stock\n\nPallets\t14\n\nChecked\t2026-03-01\t13:30\n\n" +
            "TRUE\t#N/A\t118\tDock\tTRUE\t#DIV/0!\n\n## Old\n\nKept\n",
            fromXml.Text);
        Assert.Equal(fromXml.Text, fromRecords.Text);
        Assert.Equal("Stock count", fromRecords.Title);
    }

    [Fact]
    public async Task A_number_stored_as_an_rk_reads_as_its_value_whole_divided_by_100_or_as_a_double()
    {
        var file = BinaryWorkbook(Bs("N", BRow(
            BRk(RkInt(42)), BRk(RkInt(-7)), BRk(RkInt(1250, times100: true)),
            BRk(RkDouble(12.5)), BRk(RkDouble(-0.5)), BRk(RkDouble(1234.5)), BRk(RkDouble(12345, times100: true)))));

        Assert.Contains("42\t-7\t12.5\t12.5\t-0.5\t1234.5\t123.45", (await ReadAsync(file)).Text);
    }

    [Fact]
    public async Task A_number_styled_as_a_date_or_a_time_reads_as_one_by_the_cell_formats_alone()
    {
        // The styles begin with a cell style format showing a date. A cell's
        // style counts among the cell formats only, so style 0 is General and
        // the last number stays a number.
        var file = BinaryWorkbook([Bs("Dates", BRow(
                BNum(46082, 1), BNum(46082.75, 2), BNum(0.5625, 3), BNum(1.5, 4),
                BNum(3, 5), BNum(46082, 6), BNum(1234.5, 7), BNum(46082, 0)))],
            styles: BinaryNumberStyles());

        Assert.Contains("2026-03-01\t2026-03-01 18:00\t13:30\t1.5\t3\t2026-03-01\t1234.5\t46082", (await ReadAsync(file)).Text);
    }

    [Fact]
    public async Task The_date_system_is_the_one_the_workbook_properties_name()
    {
        var days1904 = BinaryWorkbook([Bs("D", BRow(BNum(0, 1), BNum(1, 1)))], styles: BinaryNumberStyles(), date1904: true);
        var days1900 = BinaryWorkbook([Bs("D", BRow(BNum(59, 1), BNum(60, 1), BNum(61, 1)))], styles: BinaryNumberStyles());

        Assert.Contains("1904-01-01\t1904-01-02", (await ReadAsync(days1904)).Text);
        Assert.Contains("1900-02-28\t1900-02-29\t1900-03-01", (await ReadAsync(days1900)).Text);
    }

    [Fact]
    public async Task Errors_read_as_excel_writes_them_and_a_code_the_format_does_not_have_is_an_empty_cell()
    {
        var file = BinaryWorkbook(Bs("E", BRow(
            BError(0x00), BError(0x07), BError(0x0F), BError(0x17), BError(0x1D), BError(0x24), BError(0x2A), BError(0x2B),
            BError(0x99), BBool(false), BStr("End"))));

        Assert.Contains("#NULL!\t#DIV/0!\t#VALUE!\t#REF!\t#NAME?\t#NUM!\t#N/A\t#GETTING_DATA\tFALSE\tEnd", (await ReadAsync(file)).Text);
    }

    [Theory]
    [InlineData("finance/rates.xlsb")]
    [InlineData("FINANCE/RATES.XLSB")]
    [InlineData("finance/rates.xlsx")]
    public async Task A_binary_workbook_is_read_whatever_its_name_says(string path)
    {
        // Under a .xlsx name its content types still declare it macro-enabled,
        // as every binary workbook's do, though it holds no macro.
        var read = await ReadAsync(BinaryWorkbook(Bs("Rates", BRow(BStr("Rate"), BNum(4.5)))), path: path);

        Assert.Null(read.SkipReason);
        Assert.Contains("Rate\t4.5", read.Text);
    }

    [Fact]
    public async Task Hidden_sheets_are_read_and_a_chart_sheet_a_sheet_with_no_part_and_an_empty_one_add_nothing()
    {
        var file = BinaryWorkbook(
            Bs("Shown", BRow(BStr("Visible row"))),
            Bs("Tucked away", BRow(BStr("Hidden row"))) with { State = 1 },
            Bs("Very hidden", BRow(BStr("Very hidden row"))) with { State = 2 },
            // Never opened: these bytes are not records.
            new BinarySheet("Chart", "not a sheet"u8.ToArray(), Kind: "chartsheet"),
            new BinarySheet("Module", [], State: 2, NoRelationship: true),
            Bs("Blank", BRow(BBlank())));

        var read = await ReadAsync(file);

        Assert.Equal(
            "# budget.xlsb\n\n## Shown\n\nVisible row\n\n## Tucked away\n\nHidden row\n\n## Very hidden\n\nVery hidden row\n",
            read.Text);
    }

    [Fact]
    public async Task A_binary_workbook_carrying_a_macro_project_or_an_excel_4_macro_sheet_is_skipped_as_macro_enabled()
    {
        var withProject = BinaryWorkbook([Bs("S", BRow(BStr("x")))], parts: new Dictionary<string, byte[]> { ["xl/vbaProject.bin"] = [1, 2, 3] });
        var withMacroSheet = BinaryWorkbook([Bs("S", BRow(BStr("x")))],
            extraRels: [("rIdMacro", "http://schemas.microsoft.com/office/2006/relationships/xlMacrosheet", "macrosheets/sheet1.bin", false)]);

        Assert.Equal("macro-enabled", (await ReadAsync(withProject)).SkipReason);
        Assert.Equal("macro-enabled", (await ReadAsync(withMacroSheet)).SkipReason);
    }

    [Fact]
    public async Task Its_text_holds_no_control_character_and_no_half_of_a_surrogate_pair_and_an_escape_is_what_was_typed()
    {
        // A binary workbook holds UTF-16 as it is, so any code unit can be in
        // a cell, and _x0041_ in one is text, not an escape.
        var file = BinaryWorkbook(
            [Bs("T", BRow(BStr("tab\there\u0001bell\uD800lone _x0041_ kept"), BShared(0)))],
            shared: ["line\nbreak\0nul"]);

        var read = await ReadAsync(file);

        Assert.Contains("tab here bell�lone _x0041_ kept\tline break nul", read.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("\0", read.Text, StringComparison.Ordinal);
        _ = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetBytes(read.Text);
    }

    [Fact]
    public async Task A_cell_cannot_pose_as_a_heading_and_nor_can_a_sheet_name()
    {
        var read = await ReadAsync(BinaryWorkbook(Bs("# Not a sheet heading", BRow(BStr("# Not a heading")), BRow(BStr("```")))));

        Assert.Equal(["budget.xlsb > \\# Not a sheet heading"], HeadingPaths(read));
        Assert.Contains("\\# Not a heading", read.Text);
        Assert.Contains("\\```", read.Text);
    }

    [Fact]
    public async Task A_binary_workbook_with_no_text_is_empty_and_not_skipped_and_its_title_is_the_title()
    {
        var read = await ReadAsync(BinaryWorkbook([Bs("Blank", BRow(BBlank()))], title: "Rates"));

        Assert.Equal("", read.Text);
        Assert.Null(read.SkipReason);
        Assert.Equal("Rates", read.Title);
    }

    // ---- What it refuses ----

    [Fact]
    public async Task A_record_that_runs_past_the_end_of_its_part_refuses_the_file_whole()
    {
        // A text cell that says it is 200 bytes long, with four after it; the
        // row before it is never returned on its own.
        var longer = WithSheetPart([.. SheetOpening(), .. BStr("Read before it"), 0x06, 0xC8, 0x01, 0x00, 0x00, 0x00, 0x00]);
        // A type that says a second byte follows, at the last byte of the part.
        var cut = WithSheetPart([.. SheetOpening(), .. BStr("Read before it"), 0x86]);
        // In the workbook part: a sheet's record longer than what is left.
        var book = BinaryWorkbook([Bs("S", BRow(BStr("x")))], parts: new Dictionary<string, byte[]>
        {
            ["xl/workbook.bin"] = [.. Record(Brt.BeginBook), .. Record(Brt.BeginBundleShs), 0x9C, 0x01, 0x7F, 0x00],
        });

        Assert.Equal("the part xl/worksheets/sheet1.bin holds a record that runs past its end", await RefusalAsync(longer));
        Assert.Equal("the part xl/worksheets/sheet1.bin holds a record that runs past its end", await RefusalAsync(cut));
        Assert.Equal("the part xl/workbook.bin holds a record that runs past its end", await RefusalAsync(book));
    }

    [Fact]
    public async Task A_cell_that_names_a_shared_string_past_the_table_refuses_the_file()
    {
        Assert.Contains("Only", (await ReadAsync(BinaryWorkbook([Bs("S", BRow(BShared(0)))], shared: ["Only"]))).Text);
        Assert.Equal("holds a cell that names shared string 7, past the 1 its table holds",
            await RefusalAsync(BinaryWorkbook([Bs("S", BRow(BShared(0), BShared(7)))], shared: ["Only"])));
        Assert.Equal("holds a cell that names shared string 0, past the 0 its table holds",
            await RefusalAsync(BinaryWorkbook(Bs("S", BRow(BShared(0))))));
    }

    [Fact]
    public async Task A_workbook_listing_more_sheets_than_the_limit_is_refused()
    {
        var four = BinaryWorkbook(Enumerable.Range(1, 4).Select(i => Bs($"S{i}", BRow(BStr("x")))).ToArray());
        var three = BinaryWorkbook(Enumerable.Range(1, 3).Select(i => Bs($"S{i}", BRow(BStr("x")))).ToArray());

        Assert.Equal("holds 4 sheets, more than the 3 this reader reads", await RefusalAsync(four, Small));
        Assert.Equal(["budget.xlsb > S1", "budget.xlsb > S2", "budget.xlsb > S3"], HeadingPaths(await ReadAsync(three, Small)));
    }

    [Fact]
    public async Task A_sheet_whose_relationship_is_not_listed_points_outside_or_names_no_part_is_damaged()
    {
        var sheet = Bs("S", BRow(BStr("Inside")));
        byte[] With(params (string Id, string Type, string Target, bool External)[] rels) =>
            BinaryWorkbook([sheet], parts: new Dictionary<string, byte[]> { ["xl/_rels/workbook.bin.rels"] = Encoding.UTF8.GetBytes(Relationships(rels)) });

        Assert.Equal(Damaged, await RefusalAsync(With(("rIdOther", $"{R}/worksheet", "worksheets/sheet1.bin", false))));
        Assert.Equal(Damaged, await RefusalAsync(With(("rId1", $"{R}/worksheet", "http://127.0.0.1:9/sheet.bin", true))));
        Assert.Equal(Damaged, await RefusalAsync(With(("rId1", $"{R}/worksheet", "../outside/sheet1.bin", false))));
        Assert.Equal(Damaged, await RefusalAsync(With(("rId1", $"{R}/worksheet", "worksheets/missing.bin", false))));
    }

    [Fact]
    public async Task A_part_that_does_not_begin_as_its_kind_must_is_damaged()
    {
        var notABook = BinaryWorkbook([Bs("S", BRow(BStr("x")))], parts: new Dictionary<string, byte[]>
        {
            ["xl/workbook.bin"] = [.. Record(Brt.BeginSheet), .. Record(Brt.EndSheet)],
        });

        Assert.Equal(Damaged, await RefusalAsync(notABook));
        Assert.Equal(Damaged, await RefusalAsync(WithSheetPart([.. Record(Brt.BeginBook), .. Record(Brt.EndBook)])));
        Assert.Equal(Damaged, await RefusalAsync(WithSheetPart([])));
    }

    [Fact]
    public async Task A_field_past_the_end_of_its_record_is_damaged()
    {
        // A string that says it is 100 code units long in a record that holds two.
        var longString = WithSheetPart([.. SheetOpening(), .. Record(Brt.CellSt, CellAt(0, 0), U32(100), Encoding.Unicode.GetBytes("ab"))]);
        // A count whose size in bytes wraps past a whole number to nothing,
        // which would otherwise read as an empty string.
        var wrapping = WithSheetPart([.. SheetOpening(), .. Record(Brt.CellSt, CellAt(0, 0), U32(0x80000000), Encoding.Unicode.GetBytes("ab"))]);
        // A number cell too short for the column and style every cell begins with.
        var shortCell = WithSheetPart([.. SheetOpening(), .. Record(Brt.CellRk, U32(0))]);

        Assert.Equal(Damaged, await RefusalAsync(longString));
        Assert.Equal(Damaged, await RefusalAsync(wrapping));
        Assert.Equal(Damaged, await RefusalAsync(shortCell));
    }

    [Fact]
    public async Task The_cell_limit_counts_empty_cells_over_every_sheet()
    {
        // 30 cells on each of two sheets, 20 of them empty: 60 over the 50 allowed.
        var rows = Enumerable.Range(0, 10).Select(_ => BRow(BStr("a"), BBlank(), BBlank())).ToArray();

        Assert.Equal("holds more than 50 cells, the most this reader reads",
            await RefusalAsync(BinaryWorkbook(Bs("One", rows), Bs("Two", rows)), Small));
        Assert.NotNull(await ReadAsync(BinaryWorkbook(Bs("One", rows)), Small));
    }

    [Fact]
    public async Task More_shared_strings_than_the_cell_limit_is_refused()
    {
        var file = BinaryWorkbook([Bs("S", BRow(BShared(0)))], shared: Enumerable.Range(0, 51).Select(i => $"s{i}").ToArray());

        Assert.Equal("holds more than 50 shared strings, more than the cells this reader reads", await RefusalAsync(file, Small));
    }

    [Fact]
    public async Task One_long_shared_string_used_by_many_cells_is_stopped_at_the_text_limit()
    {
        var file = BinaryWorkbook([Bs("S", Enumerable.Range(0, 40).Select(_ => BRow(BShared(0))).ToArray())], shared: [new string('a', 100)]);

        Assert.Equal("gives more than 2000 characters of text, the most this reader keeps", await RefusalAsync(file, Small));
    }

    [Fact]
    public async Task A_record_part_that_expands_more_than_the_ratio_allows_is_refused()
    {
        var part = WithSheetPart([.. Record(Brt.BeginSheet), .. Record(Brt.WsProp, new byte[16 * 1024 * 1024]), .. Record(Brt.EndSheet)]);

        Assert.Equal("the part xl/worksheets/sheet1.bin expands more than 100 times its size in the file", await RefusalAsync(part));
    }

    [Fact]
    public async Task A_record_part_that_is_itself_an_archive_is_refused()
    {
        var part = WithSheetPart(DocxFixtures.Zip(new (string, byte[])[] { ("inner.bin", [1, 2, 3]) }));

        Assert.Equal("holds an archive where the part xl/worksheets/sheet1.bin should be", await RefusalAsync(part));
    }

    [Fact]
    public async Task A_binary_workbook_that_runs_past_its_time_budget_is_stopped()
    {
        Assert.Equal("took longer than 0 seconds to read",
            await RefusalAsync(BinaryWorkbook(Bs("S", BRow(BStr("Quick.")))), Small with { TimeBudget = TimeSpan.Zero }));
    }

    // ---- Nothing reaches out ----

    [Fact]
    public async Task No_link_connection_or_hyperlink_is_followed_and_the_saved_values_are_read()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var host = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}";
        var file = BinaryWorkbook(
            [Bs("Links", BRow(BStr("Rate"), BFormulaText("Saved web result")))],
            extraRels:
            [
                ("rIdExternal", $"{R}/externalLink", "externalLinks/externalLink1.bin", false),
                ("rIdConnections", $"{R}/connections", "connections.bin", false),
            ],
            parts: new Dictionary<string, byte[]>
            {
                ["xl/externalLinks/_rels/externalLink1.bin.rels"] = Encoding.UTF8.GetBytes(
                    Relationships(("rId1", $"{R}/externalLinkPath", $"{host}/book.xlsb", true))),
                ["xl/worksheets/_rels/sheet1.bin.rels"] = Encoding.UTF8.GetBytes(
                    Relationships(("rIdLink", $"{R}/hyperlink", $"{host}/link", true))),
            });

        var read = await ReadAsync(file);

        Assert.Contains("Rate\tSaved web result", read.Text);
        Assert.DoesNotContain("127.0.0.1", read.Text);
        await Task.Delay(500);
        Assert.False(listener.Pending(), "Something connected to an address the workbook points at.");

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
        await Task.Delay(200);
        Assert.True(listener.Pending(), "The listener cannot see a connection, so its silence above proves nothing.");
    }
}

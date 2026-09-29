extern alias docx;

using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;
using docx::Premagentic.Extensions.Shared;
using Premagentic.Core.Ingestion.Readers;

namespace Premagentic.Readers.Tests;

/// <summary>
/// The container guards every reader of a zip-based format stands on, held to
/// their limits directly. The shared source is compiled into each reader that
/// opens a zip; these tests take the copy in the Word reader's assembly, which
/// is the same source. Every file is built here in memory.
/// </summary>
[Collection(SerialReaders.Name)]
public sealed class ZipPartsTests
{
    private const string Damaged = "damaged, or not a test file";

    private static readonly ZipLimits Small = new(
        MaxEntries: 20,
        MaxUncompressedBytes: 1024L * 1024 * 1024,
        MaxReadBytes: 8L * 1024 * 1024,
        MaxCompressionRatio: 100,
        RatioFloorBytes: 1024 * 1024,
        MaxDepth: 40);

    private static ReadClock NoHurry => new(TimeSpan.FromMinutes(1), CancellationToken.None);

    private static byte[] Zip(params (string Name, byte[] Data)[] entries) => DocxFixtures.Zip(entries);

    private static byte[] Text(string text) => Encoding.UTF8.GetBytes(text);

    private static string Refusal(Action action) => Assert.Throws<UnreadableDocumentException>(action).Message;

    private static string Refusal(byte[] file, ZipLimits? limits = null, Action<ZipParts>? then = null) =>
        Refusal(() =>
        {
            using var parts = ZipParts.Open(file, limits ?? Small, NoHurry, Damaged);
            then?.Invoke(parts);
        });

    /// <summary>A zip of one part that expands to this many bytes of XML and compresses to almost nothing.</summary>
    private static byte[] Inflating(string name, long bytes)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        using (var entry = zip.CreateEntry(name, CompressionLevel.SmallestSize).Open())
        {
            entry.Write("<a>"u8);
            var spaces = new byte[1024 * 1024];
            Array.Fill(spaces, (byte)' ');
            for (var left = bytes - 7; left > 0; left -= spaces.Length)
                entry.Write(spaces, 0, (int)Math.Min(left, spaces.Length));
            entry.Write("</a>"u8);
        }
        return buffer.ToArray();
    }

    private static string Nested(int depth) =>
        string.Concat(Enumerable.Repeat("<a>", depth)) + "deep" + string.Concat(Enumerable.Repeat("</a>", depth));

    // ---- Opening ----

    [Fact]
    public void A_zip_whose_end_record_states_too_many_entries_is_refused_before_it_is_opened()
    {
        // Not a zip past its last 22 bytes, so opening it would call it
        // damaged: this reason can only come from the count read first.
        Assert.Equal("holds 60000 entries, more than the 20 this reader opens", Refusal(DocxFixtures.EndRecordStating(60000)));
    }

    [Fact]
    public void A_zip_with_too_many_entries_is_refused_once_opened_when_its_end_record_states_no_count()
    {
        var file = DocxFixtures.Zip(Enumerable.Range(0, 70_000).Select(i => ($"extra/{i}.xml", "<x/>"u8.ToArray())),
            CompressionLevel.NoCompression);

        Assert.Null(ZipParts.StatedEntryCount(file));
        Assert.Equal("holds 70000 entries, more than the 20 this reader opens", Refusal(file));
    }

    [Fact]
    public void Exactly_as_many_entries_as_the_limit_is_opened()
    {
        var file = DocxFixtures.Zip(Enumerable.Range(0, 20).Select(i => ($"extra/{i}.xml", "<x/>"u8.ToArray())));

        using var parts = ZipParts.Open(file, Small, NoHurry, Damaged);
        Assert.Equal(20, parts.Entries.Count);
    }

    [Fact]
    public void A_zip_that_says_it_expands_past_the_limit_is_refused_without_reading_it()
    {
        Assert.Equal("says it expands to more than 1024 MB, the most this reader opens", Refusal(DocxFixtures.StatingAHugeEntry()));
    }

    [Fact]
    public void Bytes_that_are_not_a_zip_are_damaged_with_the_readers_own_reason()
    {
        Assert.Equal(Damaged, Refusal("plain text, not a zip"u8.ToArray()));
        Assert.Equal(Damaged, Refusal(DocxFixtures.Truncated()));
    }

    // ---- Reading a part ----

    [Fact]
    public void A_part_is_found_ignoring_case_and_a_missing_one_is_null()
    {
        using var parts = ZipParts.Open(Zip(("Folder/Part.xml", Text("<root>hello</root>"))), Small, NoHurry, Damaged);

        Assert.Equal("hello", parts.Xml("folder/part.xml")?.Root?.Value);
        Assert.Null(parts.Xml("folder/other.xml"));
        Assert.Null(parts.Reader("folder/other.xml"));
    }

    [Fact]
    public void Two_parts_with_one_name_are_refused()
    {
        var file = Zip(("part.xml", Text("<one/>")), ("PART.xml", Text("<two/>")));

        Assert.Equal("holds two parts named part.xml", Refusal(file, then: p => p.Xml("part.xml")));
    }

    [Fact]
    public void A_part_that_is_itself_an_archive_is_refused()
    {
        var file = Zip(("part.xml", Zip(("inner.xml", Text("<hidden/>")))));

        Assert.Equal("holds an archive where the part part.xml should be", Refusal(file, then: p => p.Reader("part.xml")));
    }

    [Fact]
    public void A_part_that_is_not_well_formed_is_damaged_before_a_reader_is_handed_over()
    {
        var file = Zip(("part.xml", Text("<root><open>unclosed</root>")));

        Assert.Equal(Damaged, Refusal(file, then: p => p.Reader("part.xml")));
    }

    [Fact]
    public void A_part_that_expands_past_the_read_limit_is_refused_before_it_is_held()
    {
        // 64 MB that compresses to well under one. Measured with the ratio
        // check out of the way, so this is the size limit alone.
        var file = Inflating("part.xml", 64L * 1024 * 1024);
        Assert.True(file.Length < 1024 * 1024, $"The fixture is {file.Length} bytes.");
        var ratioOff = Small with { MaxCompressionRatio = int.MaxValue };

        var before = GC.GetTotalAllocatedBytes(precise: true);
        var reason = Refusal(file, ratioOff, p => p.Reader("part.xml"));
        var allocated = GC.GetTotalAllocatedBytes(precise: true) - before;

        Assert.Equal("its text parts expand past 8 MB, the most this reader reads", reason);
        Assert.True(allocated < 48L * 1024 * 1024, $"Reading it allocated {allocated / (1024 * 1024)} MB.");
    }

    [Fact]
    public void The_read_limit_counts_every_part_read_together()
    {
        // Each part is under the limit alone; the two are over it together.
        var half = "<a>" + new string(' ', 5 * 1024 * 1024) + "</a>";
        var file = Zip(("one.xml", Text(half)), ("two.xml", Text(half)));
        var ratioOff = Small with { MaxCompressionRatio = int.MaxValue };

        Assert.Equal("its text parts expand past 8 MB, the most this reader reads",
            Refusal(file, ratioOff, p => { p.Reader("one.xml"); p.Reader("two.xml"); }));
    }

    [Fact]
    public void A_part_that_expands_more_than_the_ratio_allows_is_refused()
    {
        var file = Inflating("part.xml", 4L * 1024 * 1024);

        Assert.Equal("the part part.xml expands more than 100 times its size in the file",
            Refusal(file, then: p => p.Reader("part.xml")));
    }

    [Fact]
    public void A_part_under_the_ratio_floor_may_expand_as_far_as_it_likes()
    {
        var file = Inflating("part.xml", 512 * 1024);

        using var parts = ZipParts.Open(file, Small, NoHurry, Damaged);
        Assert.NotNull(parts.Xml("part.xml"));
    }

    [Fact]
    public void A_part_nested_deeper_than_the_limit_is_refused_before_a_reader_is_handed_over()
    {
        var file = Zip(("part.xml", Text(Nested(60))));

        Assert.Equal("the part part.xml nests its XML more than 40 deep, the most this reader follows",
            Refusal(file, then: p => p.Reader("part.xml")));
    }

    [Fact]
    public void A_part_nested_as_deep_as_the_limit_is_read()
    {
        // Depth counts from 0 at the root, and the text inside the deepest
        // element is one deeper than it.
        using var parts = ZipParts.Open(Zip(("part.xml", Text(Nested(40)))), Small, NoHurry, Damaged);

        Assert.Equal("deep", parts.Xml("part.xml")?.Root?.Value);
    }

    [Fact]
    public void A_part_that_runs_past_the_time_budget_is_stopped()
    {
        var file = Zip(("part.xml", Text("<a/>")));

        Assert.Equal("took longer than 0 seconds to read", Refusal(() =>
        {
            using var parts = ZipParts.Open(file, Small, new ReadClock(TimeSpan.Zero, CancellationToken.None), Damaged);
            parts.Reader("part.xml");
        }));
    }

    [Fact]
    public void A_part_with_an_external_entity_is_refused_and_the_entity_is_never_fetched()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var part = $"<!DOCTYPE a [<!ENTITY e SYSTEM \"http://127.0.0.1:{port}/entity\">]><a>&e;</a>";

        Assert.Equal(Damaged, Refusal(Zip(("part.xml", Text(part))), then: p => p.Xml("part.xml")));
        Thread.Sleep(500);
        Assert.False(listener.Pending(), "The entity's address was fetched.");
    }

    // ---- Names inside the file ----

    private static readonly byte[] Related = Zip(
        ("_rels/.rels", Text("<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
            "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"/book/main.xml\"/>" +
            "</Relationships>")),
        ("book/_rels/main.xml.rels", Text("<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
            "<Relationship Id=\"rId1\" Type=\"t/sheet\" Target=\"sheets/one.xml\"/>" +
            "<Relationship Id=\"rId2\" Type=\"t/link\" Target=\"http://127.0.0.1/away\" TargetMode=\"External\"/>" +
            "</Relationships>")),
        ("book/main.xml", Text("<main/>")),
        ("book/sheets/one.xml", Text("<sheet/>")));

    [Fact]
    public void The_main_part_is_the_one_the_package_names_and_the_fallback_when_it_names_none()
    {
        using (var parts = ZipParts.Open(Related, Small, NoHurry, Damaged))
            Assert.Equal("book/main.xml", parts.MainPart("fallback.xml"));
        using (var parts = ZipParts.Open(Zip(("book/main.xml", Text("<main/>"))), Small, NoHurry, Damaged))
            Assert.Equal("fallback.xml", parts.MainPart("fallback.xml"));
    }

    [Fact]
    public void A_parts_relationships_are_read_with_the_ones_outside_the_file_marked()
    {
        using var parts = ZipParts.Open(Related, Small, NoHurry, Damaged);

        var rels = parts.Relationships("book/main.xml");

        Assert.Equal(["rId1:sheets/one.xml:False", "rId2:http://127.0.0.1/away:True"],
            rels.Select(r => $"{r.Id}:{r.Target}:{r.External}"));
        Assert.Empty(parts.Relationships("book/sheets/one.xml"));
    }

    [Theory]
    [InlineData("sheets/one.xml", "book/sheets/one.xml")]
    [InlineData("/book/sheets/one.xml", "book/sheets/one.xml")]
    [InlineData("sheets/sheet%201.xml", "book/sheets/sheet 1.xml")]
    public void A_target_resolves_to_a_name_inside_the_file(string target, string expected)
    {
        using var parts = ZipParts.Open(Related, Small, NoHurry, Damaged);

        Assert.Equal(expected, parts.Resolve("book/main.xml", target));
    }

    [Theory]
    [InlineData("../outside.xml")]
    [InlineData("sheets/../../outside.xml")]
    [InlineData("%2E%2E/outside.xml")]
    [InlineData("C:/outside.xml")]
    [InlineData("sheets\\one.xml")]
    [InlineData("sheets%5Cone.xml")]
    [InlineData("")]
    public void A_target_that_climbs_names_a_drive_or_uses_a_backslash_refuses_the_file(string target)
    {
        using var parts = ZipParts.Open(Related, Small, NoHurry, Damaged);

        Assert.Equal(Damaged, Refusal(() => parts.Resolve("book/main.xml", target)));
    }

    [Fact]
    public void A_macro_project_anywhere_is_found_without_being_opened()
    {
        using (var parts = ZipParts.Open(Zip(("deep/folder/vbaProject.bin", "not a real project"u8.ToArray())), Small, NoHurry, Damaged))
            Assert.True(parts.HoldsMacroProject);
        using (var parts = ZipParts.Open(Related, Small, NoHurry, Damaged))
            Assert.False(parts.HoldsMacroProject);
    }
}

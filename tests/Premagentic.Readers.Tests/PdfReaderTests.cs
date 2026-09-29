using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Premagentic.Conformance;
using Premagentic.Core.Ingestion;
using Premagentic.Core.Ingestion.Readers;
using Premagentic.Extensions.Pdf;

namespace Premagentic.Readers.Tests;

/// <summary>The PDF reader held to the reader contract every reader keeps.</summary>
public sealed class PdfReaderConformance : DocumentReaderConformance
{
    protected override IDocumentReader Reader { get; } = new PdfDocumentReader();

    protected override IReadOnlyList<ReaderSample> Samples { get; } =
    [
        new("handbook/leave.pdf",
            PdfFixtures.Text(["Annual leave is booked two weeks ahead."], ["Sick leave needs a note after three days."]),
            ["Annual leave is booked two weeks ahead.", "Sick leave needs a note after three days."]),
    ];
}

/// <summary>
/// What the PDF reader reads and what it refuses. Every file is generated here;
/// none is a real document. The limits are set small where a test needs to
/// cross one, so no test builds a file of a size worth caring about unless it
/// is the size that is being tested.
/// </summary>
[Collection(SerialReaders.Name)]
public sealed class PdfReaderTests
{
    private static readonly PdfReaderLimits Small = PdfReaderLimits.Default with
    {
        MaxPages = 3,
        MaxDecodedBytes = 8L * 1024 * 1024,
        MaxTextChars = 2000,
        TimeBudget = TimeSpan.FromSeconds(30),
    };

    private static async Task<ReadDocument> ReadAsync(byte[] file, PdfReaderLimits? limits = null, Action<int>? onPage = null)
    {
        var reader = new PdfDocumentReader(limits ?? PdfReaderLimits.Default) { OnPage = onPage };
        using var content = new MemoryStream(file, writable: false);
        return await reader.ReadAsync(content, "invented.pdf", CancellationToken.None);
    }

    private static async Task<string> RefusalAsync(byte[] file, PdfReaderLimits? limits = null, Action<int>? onPage = null) =>
        (await Assert.ThrowsAsync<UnreadableDocumentException>(() => ReadAsync(file, limits, onPage))).Message;

    [Fact]
    public async Task Each_page_is_read_in_order_under_a_heading_that_names_it()
    {
        var read = await ReadAsync(PdfFixtures.Text(
            ["The dock opens at seven.", "Deliveries sign in at the gate."],
            ["Returns go to bay four."]));

        Assert.Equal(
            "# Page 1\n\nThe dock opens at seven.\nDeliveries sign in at the gate.\n\n# Page 2\n\nReturns go to bay four.\n",
            read.Text);
        Assert.Null(read.SkipReason);

        // The heading is what a citation carries as the passage's place.
        var chunks = MarkdownChunker.Chunk(read.Text);
        Assert.Equal(["Page 1", "Page 2"], chunks.Select(c => c.HeadingPath));
    }

    [Fact]
    public async Task The_title_the_file_states_is_the_title()
    {
        var read = await ReadAsync(PdfFixtures.TextWithTitle("Dock rota", ["Opening hours."]));

        Assert.Equal("Dock rota", read.Title);
    }

    [Fact]
    public async Task A_line_of_the_document_cannot_pose_as_a_page_heading_or_hide_the_ones_after_it()
    {
        var read = await ReadAsync(PdfFixtures.Text(["# Page 99", "```", "Real text."], ["After the fence."]));

        var chunks = MarkdownChunker.Chunk(read.Text);
        Assert.Equal(["Page 1", "Page 2"], chunks.Select(c => c.HeadingPath));
        Assert.Contains("\\# Page 99", read.Text);
        Assert.Contains("After the fence.", chunks[1].Content);
    }

    [Fact]
    public async Task A_file_whose_pages_are_pictures_is_skipped_as_having_no_text_layer()
    {
        var read = await ReadAsync(PdfFixtures.Scanned(2));

        Assert.Equal("no text layer", read.SkipReason);
        Assert.Equal("", read.Text);
    }

    [Fact]
    public async Task A_file_with_more_pages_than_the_limit_is_refused_and_not_read_in_part()
    {
        var pages = Enumerable.Range(1, 5).Select(n => new[] { $"Page {n} text." }).ToArray();

        Assert.Equal("has 5 pages, more than the 3 this reader reads", await RefusalAsync(PdfFixtures.Text(pages), Small));
    }

    [Fact]
    public async Task A_page_tree_that_lists_one_page_two_hundred_thousand_times_is_refused_at_the_default_limit()
    {
        var clock = Stopwatch.StartNew();

        Assert.Equal("has 200000 pages, more than the 5000 this reader reads",
            await RefusalAsync(PdfFixtures.ListingOnePageManyTimes(200_000)));
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(20), $"Took {clock.Elapsed}.");
    }

    [Theory]
    [InlineData("loops on itself")]
    [InlineData("loops through two nodes")]
    [InlineData("nests five thousand deep")]
    [InlineData("declares a million pages")]
    public async Task A_page_tree_built_to_trap_a_reader_ends_in_a_reading_or_a_reason(string shape)
    {
        var file = shape switch
        {
            "loops on itself" => PdfFixtures.PageTreeThatLoops(),
            "loops through two nodes" => PdfFixtures.PageTreeThatLoopsThroughTwoNodes(),
            "nests five thousand deep" => PdfFixtures.PageTreeNestedDeep(5000),
            _ => PdfFixtures.DeclaringPages(1_000_000),
        };
        var clock = Stopwatch.StartNew();
        try
        {
            var read = await ReadAsync(file, Small with { TimeBudget = TimeSpan.FromSeconds(10), Grace = TimeSpan.FromSeconds(2) });
            Assert.Contains("real page", read.Text, StringComparison.OrdinalIgnoreCase);
        }
        catch (UnreadableDocumentException)
        {
            // A reason is also an answer; hanging or crashing is not.
        }
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(15), $"Took {clock.Elapsed}.");
    }

    [Fact]
    public async Task A_small_file_that_inflates_past_the_limit_is_refused_before_it_is_inflated()
    {
        // 256 MB of zeros compresses to a few hundred KB. The limit is 8 MB, so
        // the stream must be measured and refused, never decoded in full.
        var bomb = PdfFixtures.InflatingTo(256L * 1024 * 1024);
        Assert.True(bomb.Length < 2 * 1024 * 1024, $"The fixture is {bomb.Length} bytes.");

        var before = GC.GetTotalAllocatedBytes(precise: true);
        var reason = await RefusalAsync(bomb, Small);
        var allocated = GC.GetTotalAllocatedBytes(precise: true) - before;

        Assert.Equal("its compressed streams expand past 8 MB, the most this reader decodes", reason);
        Assert.True(allocated < 128L * 1024 * 1024, $"Reading it allocated {allocated / (1024 * 1024)} MB.");
    }

    [Fact]
    public async Task A_stream_in_another_filter_that_expands_past_the_limit_is_refused_once_decoded()
    {
        var reason = await RefusalAsync(PdfFixtures.ExpandingByRunLength(16L * 1024 * 1024), Small);

        Assert.Equal("its compressed streams expand past 8 MB, the most this reader decodes", reason);
    }

    [Fact]
    public async Task A_file_that_gives_more_text_than_the_limit_is_refused()
    {
        var lines = Enumerable.Range(1, 60).Select(n => $"Line {n} of a long page of invented text.").ToArray();

        Assert.Equal("gives more than 2000 characters of text, the most this reader keeps",
            await RefusalAsync(PdfFixtures.Text(lines), Small));
    }

    [Fact]
    public async Task A_file_larger_than_the_limit_is_refused_before_it_is_parsed()
    {
        Assert.Equal("larger than 100 bytes, the most this reader reads",
            await RefusalAsync(PdfFixtures.Text(["Short."]), Small with { MaxFileBytes = 100 }));
    }

    [Fact]
    public async Task A_file_that_runs_past_its_time_budget_is_stopped_at_the_next_check()
    {
        // No budget at all, and a long wait before the run gives up on it: the
        // reading itself must notice the time and stop.
        Assert.Equal("took longer than 0 seconds to read",
            await RefusalAsync(PdfFixtures.Text(["Quick."]), Small with { TimeBudget = TimeSpan.Zero, Grace = TimeSpan.FromMinutes(1) }));
    }

    [Fact]
    public async Task A_file_whose_reading_does_not_come_back_is_reported_and_the_run_moves_on()
    {
        // The reading is held for three seconds between checks, as a library
        // stuck inside one page would be. The run must not wait for it.
        var clock = Stopwatch.StartNew();

        var reason = await RefusalAsync(PdfFixtures.Text(["Slow."]),
            Small with { TimeBudget = TimeSpan.FromMilliseconds(200), Grace = TimeSpan.Zero },
            onPage: _ => Thread.Sleep(3000));

        Assert.Equal("took longer than 0.2 seconds to read", reason);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(2), $"The run waited {clock.Elapsed}.");
    }

    [Fact]
    public async Task A_file_nested_deeper_than_the_limit_is_refused_with_the_limit_named()
    {
        Assert.Equal("nests its structure more than 40 deep, the most this reader follows",
            await RefusalAsync(PdfFixtures.ContentNested(60), Small with { MaxDepth = 40 }));
    }

    [Fact]
    public async Task A_file_nested_a_hundred_thousand_deep_is_refused_at_the_default_limit()
    {
        // The library stops at the depth it is given, so this process survives
        // what would otherwise be a stack overflow.
        Assert.Equal("nests its structure more than 256 deep, the most this reader follows",
            await RefusalAsync(PdfFixtures.ContentNested(100_000)));
    }

    [Fact]
    public async Task A_password_protected_file_is_unreadable_with_that_reason()
    {
        Assert.Equal("password protected", await RefusalAsync(PdfFixtures.Encrypted()));
    }

    [Fact]
    public async Task A_damaged_file_or_one_that_is_not_a_pdf_is_unreadable_with_that_reason()
    {
        Assert.Equal("damaged, or not a PDF", await RefusalAsync(PdfFixtures.Truncated()));
        Assert.Equal("damaged, or not a PDF", await RefusalAsync(PdfFixtures.NotAPdf()));
    }

    [Fact]
    public async Task No_link_action_script_or_embedded_file_is_followed_and_the_link_text_is_read()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var read = await ReadAsync(PdfFixtures.PointingAt(port, "Read the leave policy", "EMBEDDEDWORDS"));

        Assert.Contains("Read the leave policy", read.Text);
        Assert.DoesNotContain("EMBEDDEDWORDS", read.Text);
        Assert.DoesNotContain("127.0.0.1", read.Text);
        await Task.Delay(500);
        Assert.False(listener.Pending(), "Something connected to the address the file points at.");

        // The control: the same listener does see a connection when one is made.
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        await Task.Delay(200);
        Assert.True(listener.Pending(), "The listener cannot see a connection, so its silence above proves nothing.");
    }
}

/// <summary>
/// Tests that measure memory or time, run one at a time so another test's
/// allocations or threads do not land in their numbers.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SerialReaders
{
    public const string Name = "Readers measured alone";
}

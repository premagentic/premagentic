using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using Premagentic.Core.Extensions;
using Premagentic.Core.Ingestion.Readers;
using Premagentic.Extensions.Shared;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;
using UglyToad.PdfPig.Exceptions;
using UglyToad.PdfPig.Filters;
using UglyToad.PdfPig.Tokens;

namespace Premagentic.Extensions.Pdf;

/// <summary>Registers the PDF reader.</summary>
public sealed class PdfReaderExtension : IExtension
{
    public string Name => "pdf-reader";

    public void Register(ExtensionRegistrations registrations) => registrations.AddReader(new PdfDocumentReader());
}

/// <summary>
/// How much of a PDF this reader will take on. A file over any of them is
/// reported unreadable with the limit it crossed, never read in part: a
/// document indexed without its last pages would answer as if it had none.
/// </summary>
/// <param name="MaxFileBytes">The largest file read at all.</param>
/// <param name="MaxPages">The most pages a file may have.</param>
/// <param name="MaxDecodedBytes">
/// The most bytes every compressed stream in the file may decode to, together.
/// A stream compressed with Flate is measured before it is decoded, so a small
/// file that inflates to gigabytes is refused before the memory is taken.
/// </param>
/// <param name="MaxTextChars">The most characters of text a file may give.</param>
/// <param name="TimeBudget">
/// How long one file may take. Checked between pages and at every stream the
/// library decodes.
/// </param>
/// <param name="Grace">
/// How much longer than <paramref name="TimeBudget"/> the run waits for a file
/// whose reading has not come back to a check, before it reports the file and
/// moves on. The reading is left to finish on its own; the run does not wait
/// for it.
/// </param>
/// <param name="MaxDepth">
/// How deep the file's structure may nest: arrays and dictionaries inside one
/// another, in its objects and in its page content. The library parses nesting
/// by recursion and stops at this depth, so a file built to nest without end
/// is refused rather than overflowing the stack, which would end the process.
/// </param>
public sealed record PdfReaderLimits(
    long MaxFileBytes, int MaxPages, long MaxDecodedBytes, int MaxTextChars, TimeSpan TimeBudget, TimeSpan Grace,
    int MaxDepth)
{
    public static PdfReaderLimits Default { get; } = new(
        MaxFileBytes: 200L * 1024 * 1024,
        MaxPages: 5000,
        MaxDecodedBytes: 512L * 1024 * 1024,
        MaxTextChars: 10_000_000,
        TimeBudget: TimeSpan.FromSeconds(60),
        Grace: TimeSpan.FromSeconds(30),
        MaxDepth: 256);
}

/// <summary>
/// Reads the text layer of a PDF, page by page in the order the file draws it,
/// with each page under a heading <c>Page N</c> so a passage's heading path
/// names its page.
/// <para>
/// Text only. Nothing in the file is followed, run or opened: links, launch
/// and submit actions, scripts, embedded files and attachments are never read,
/// and the library it uses has no network code in it. A page that is a picture
/// is not read either: a file with no text on any page is skipped with the
/// reason <c>no text layer</c>, and nothing here recognizes characters in an
/// image.
/// </para>
/// </summary>
public sealed class PdfDocumentReader(PdfReaderLimits limits) : IDocumentReader
{
    public PdfDocumentReader() : this(PdfReaderLimits.Default)
    {
    }

    public const string ReaderName = "pdf";

    public string Name => ReaderName;

    public IReadOnlyList<string> Extensions { get; } = [".pdf"];

    /// <summary>Called before each page is read, with its number. For tests that need a slow file.</summary>
    internal Action<int>? OnPage { get; set; }

    public async Task<ReadDocument> ReadAsync(Stream content, string path, CancellationToken ct)
    {
        var bytes = await ReaderBytes.AllAsync(content, ct);
        if (bytes.Length == 0) return new ReadDocument("", null, null);
        if (bytes.Length > limits.MaxFileBytes)
            throw new UnreadableDocumentException($"larger than {Sizes.Of(limits.MaxFileBytes)}, the most this reader reads");

        var budget = new Budget(limits, ct);
        var reading = Task.Run(() => Read(bytes, budget), ct);
        try
        {
            return await reading.WaitAsync(limits.TimeBudget + limits.Grace, ct);
        }
        catch (TimeoutException)
        {
            // The reading has not come back to a check. It is told to stop at
            // its next one and left to end on its own; whatever it throws then
            // is observed here so it is not reported as unobserved.
            budget.Abandon();
            _ = reading.ContinueWith(t => _ = t.Exception, TaskScheduler.Default);
            throw new UnreadableDocumentException(OverTime);
        }
    }

    private string OverTime => $"took longer than {limits.TimeBudget.TotalSeconds:0.#} seconds to read";

    private ReadDocument Read(byte[] bytes, Budget budget)
    {
        var options = new ParsingOptions
        {
            UseLenientParsing = true,
            SkipMissingFonts = true,
            MaxStackDepth = limits.MaxDepth,
            FilterProvider = new BudgetedFilterProvider(DefaultFilterProvider.Instance, budget),
        };

        try
        {
            using var document = PdfDocument.Open(bytes, options);
            budget.ThrowIfOver();
            if (document.NumberOfPages > limits.MaxPages)
                throw new UnreadableDocumentException(
                    $"has {document.NumberOfPages} pages, more than the {limits.MaxPages} this reader reads");

            var text = new StringBuilder();
            var sawAPage = false;
            for (var number = 1; number <= document.NumberOfPages; number++)
            {
                budget.ThrowIfOver();
                OnPage?.Invoke(number);
                budget.ThrowIfOver();

                var page = document.GetPage(number);
                var pageText = ReaderMarkdown.Block(ContentOrderTextExtractor.GetText(page));
                budget.ThrowIfOver();
                sawAPage = true;
                if (pageText.Trim().Length == 0) continue;

                text.Append(ReaderMarkdown.Heading(1, $"Page {number}")).Append("\n\n").Append(pageText).Append("\n\n");
                if (text.Length > limits.MaxTextChars)
                    throw new UnreadableDocumentException(
                        $"gives more than {limits.MaxTextChars} characters of text, the most this reader keeps");
            }

            if (text.Length == 0)
                return sawAPage ? ReadDocument.Skipped("no text layer") : new ReadDocument("", null, null);

            return new ReadDocument(text.ToString().TrimEnd('\n') + "\n", Title(document), null);
        }
        catch (PdfDocumentEncryptedException)
        {
            throw new UnreadableDocumentException("password protected");
        }
        catch (PdfDocumentStackDepthException)
        {
            throw new UnreadableDocumentException(
                $"nests its structure more than {limits.MaxDepth} deep, the most this reader follows");
        }
        catch (Exception ex) when (budget.Tripped is { } limit && ex is not UnreadableDocumentException)
        {
            // The library may turn a refusal from inside its decoding into an
            // error of its own; the limit that was crossed is the reason.
            throw new UnreadableDocumentException(limit);
        }
        catch (PdfDocumentFormatException)
        {
            throw new UnreadableDocumentException("damaged, or not a PDF");
        }
    }

    private static string? Title(PdfDocument document)
    {
        var title = document.Information.Title;
        if (string.IsNullOrWhiteSpace(title)) return null;
        var line = string.Join(' ', title.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return line.Length <= 300 ? line : line[..300];
    }

    /// <summary>
    /// What one file has used: its deadline and the bytes its streams decoded
    /// to. The first limit crossed is kept, because the library may catch what
    /// is thrown from inside its decoding and go on; every later check throws
    /// again, and the reader reports the limit rather than a part of the file.
    /// </summary>
    internal sealed class Budget(PdfReaderLimits limits, CancellationToken ct)
    {
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private long _decoded;
        private volatile string? _tripped;

        public string? Tripped => _tripped;

        public long Remaining => limits.MaxDecodedBytes - Interlocked.Read(ref _decoded);

        public void Abandon() => _tripped ??= $"took longer than {limits.TimeBudget.TotalSeconds:0.#} seconds to read";

        public void ThrowIfOver()
        {
            ct.ThrowIfCancellationRequested();
            if (_tripped is null && _clock.Elapsed > limits.TimeBudget)
                _tripped = $"took longer than {limits.TimeBudget.TotalSeconds:0.#} seconds to read";
            if (_tripped is { } limit) throw new UnreadableDocumentException(limit);
        }

        public void Take(long bytes)
        {
            if (Interlocked.Add(ref _decoded, bytes) > limits.MaxDecodedBytes)
                _tripped ??= $"its compressed streams expand past {Sizes.Of(limits.MaxDecodedBytes)}, the most this reader decodes";
            ThrowIfOver();
        }
    }

    /// <summary>The library's own filters, each one held to the file's budget.</summary>
    internal sealed class BudgetedFilterProvider(IFilterProvider inner, Budget budget) : IFilterProvider
    {
        public IReadOnlyList<IFilter> GetFilters(DictionaryToken dictionary) => Wrap(inner.GetFilters(dictionary));

        public IReadOnlyList<IFilter> GetNamedFilters(IReadOnlyList<NameToken> names) => Wrap(inner.GetNamedFilters(names));

        public IReadOnlyList<IFilter> GetAllFilters() => Wrap(inner.GetAllFilters());

        private IReadOnlyList<IFilter> Wrap(IReadOnlyList<IFilter> filters) =>
            [.. filters.Select(f => f is BudgetedFilter ? f : new BudgetedFilter(f, budget))];
    }

    internal sealed class BudgetedFilter(IFilter inner, Budget budget) : IFilter
    {
        public bool IsSupported => inner.IsSupported;

        public Memory<byte> Decode(Memory<byte> input, DictionaryToken streamDictionary, IFilterProvider filterProvider, int filterIndex)
        {
            budget.ThrowIfOver();

            // Flate is how a bomb is built: measured first, without keeping
            // what it decodes, so a stream that would expand past what is left
            // is refused before the library allocates it.
            if (inner is FlateFilter && InflatedLength(input.Span, budget.Remaining + 1) > budget.Remaining)
                budget.Take(budget.Remaining + 1);

            var output = inner.Decode(input, streamDictionary, filterProvider, filterIndex);
            budget.Take(output.Length);
            return output;
        }

        /// <summary>
        /// How many bytes a Flate stream inflates to, counting no further than
        /// <paramref name="cap"/>. Read the way the library reads it: past a
        /// two-byte zlib header when there is one. A stream that is damaged
        /// part way counts what came out before the damage.
        /// </summary>
        internal static long InflatedLength(ReadOnlySpan<byte> data, long cap)
        {
            var start = data.Length >= 2 && (data[0] & 0x0F) == 8 && ((data[0] << 8) | data[1]) % 31 == 0 ? 2 : 0;
            using var deflate = new DeflateStream(new MemoryStream(data[start..].ToArray(), writable: false), CompressionMode.Decompress);
            var chunk = new byte[64 * 1024];
            long total = 0;
            try
            {
                int read;
                while (total <= cap && (read = deflate.Read(chunk, 0, chunk.Length)) > 0) total += read;
            }
            catch (InvalidDataException)
            {
                // Damaged past this point; what was counted stands.
            }
            return total;
        }
    }
}

using System.Runtime.CompilerServices;
using System.Text;
using Premagentic.Core;
using Premagentic.Core.Ingestion.Readers;
using Premagentic.Core.Security;
using Premagentic.Core.Sources;

namespace Premagentic.Tests;

/// <summary>
/// The size limit the pipeline holds every file to before any reader runs,
/// proved with streams that count what is taken from them, so no test needs a
/// file of the limit's size.
/// </summary>
public class PipelineReadLimitTests
{
    /// <summary>
    /// Zeros, as many as it is told to hold, counting every byte handed out.
    /// Seekable only when asked, which is how a stream states its length.
    /// </summary>
    private sealed class CountingStream(long length, bool seekable) : Stream
    {
        private long _position;

        public long Taken { get; private set; }

        public override bool CanRead => true;
        public override bool CanSeek => seekable;
        public override bool CanWrite => false;
        public override long Length => seekable ? length : throw new NotSupportedException();

        public override long Position
        {
            get => seekable ? _position : throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var got = (int)Math.Min(count, length - _position);
            Array.Clear(buffer, offset, got);
            _position += got;
            Taken += got;
            return got;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>A reader that records what it was handed, standing in for any reader at all.</summary>
    private sealed class RecordingReader : IDocumentReader
    {
        public long? Handed { get; private set; }
        public string Name => "recording";
        public IReadOnlyList<string> Extensions { get; } = [".rec"];

        public Task<ReadDocument> ReadAsync(Stream content, string path, CancellationToken ct)
        {
            Handed = content.Length;
            return Task.FromResult(new ReadDocument("Read.", null, null));
        }
    }

    private static SourceContent Content(string path, Stream stream) => new(
        path, _ => Task.FromResult(stream),
        (read, hash) => new SourceDocument(path, read.Title, read.Text, hash, DocumentAccess.Everyone));

    [Fact]
    public async Task A_file_longer_than_the_limit_is_unreadable_and_read_no_further_than_one_byte_past_it()
    {
        // Not seekable, so its length is found only by reading; a Markdown
        // path, so the built-in reader, which has no limit of its own, is the
        // one held to this.
        var stream = new CountingStream(10L * 1024 * 1024, seekable: false);

        var read = await DocumentSourceReading.ReadAsync(Content("notes/huge.md", stream), ReaderRegistry.BuiltIn, 1024 * 1024, CancellationToken.None);

        Assert.Null(read.Document);
        Assert.Equal("notes/huge.md", read.Failure!.Path);
        Assert.Equal("larger than 1 MB, the most the pipeline reads", read.Failure.Reason);
        Assert.Equal(1024 * 1024 + 1, stream.Taken);
    }

    [Fact]
    public async Task A_file_exactly_as_long_as_the_limit_is_read_whole()
    {
        var reader = new RecordingReader();
        var stream = new CountingStream(1024 * 1024, seekable: false);

        var read = await DocumentSourceReading.ReadAsync(Content("notes/full.rec", stream), new ReaderRegistry(reader), 1024 * 1024, CancellationToken.None);

        Assert.NotNull(read.Document);
        Assert.Equal(1024 * 1024, reader.Handed);
    }

    [Fact]
    public async Task A_file_that_states_a_length_over_the_limit_is_refused_before_a_byte_is_read_and_no_reader_sees_it()
    {
        // Five gigabytes by its own account, which is how a file on disk
        // arrives. The default limit, through the entry point every run uses.
        var reader = new RecordingReader();
        var stream = new CountingStream(5L * 1024 * 1024 * 1024, seekable: true);

        var read = await DocumentSourceReading.ReadAsync(Content("archive/dump.rec", stream), new ReaderRegistry(reader));

        Assert.Equal(256L * 1024 * 1024, DocumentSourceReading.MaxFileBytes);
        Assert.Equal("larger than 256 MB, the most the pipeline reads", read.Failure!.Reason);
        Assert.Equal(0, stream.Taken);
        Assert.Null(reader.Handed);
    }

    private sealed class TwoFiles(params SourceContent[] contents) : IDocumentSource
    {
        public string Name => "two-files";
        public string PathPrefix => "";

        public async IAsyncEnumerable<SourceRead> EnumerateAsync([EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            foreach (var content in contents) yield return SourceRead.Unread(content);
        }
    }

    [Fact]
    public async Task A_file_over_the_limit_is_reported_and_the_file_after_it_is_read()
    {
        var source = new TwoFiles(
            Content("big/dump.md", new CountingStream(5L * 1024 * 1024 * 1024, seekable: true)),
            Content("small/note.md", new MemoryStream(Encoding.UTF8.GetBytes("# Note\n\nThe dock opens at six.\n"))));

        var reads = new List<SourceRead>();
        await foreach (var read in source.ReadThroughAsync(ReaderRegistry.BuiltIn)) reads.Add(read);

        Assert.Equal(2, reads.Count);
        Assert.Equal(("big/dump.md", "larger than 256 MB, the most the pipeline reads"), (reads[0].Failure!.Path, reads[0].Failure!.Reason));
        Assert.Equal("small/note.md", reads[1].Document!.Path);
    }
}

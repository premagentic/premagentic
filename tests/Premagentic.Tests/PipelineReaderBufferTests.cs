using System.Security.Cryptography;
using Premagentic.Core;
using Premagentic.Core.Ingestion.Readers;
using Premagentic.Core.Security;
using Premagentic.Core.Sources;

namespace Premagentic.Tests;

/// <summary>
/// What the pipeline hands a reader: the array it read the file into, once,
/// read-only as a stream and with its buffer visible, so a reader need not
/// copy the file; and that the array is hashed before the reader runs and not
/// read after, so a reader that writes into it changes nothing the pipeline
/// keeps.
/// </summary>
public class PipelineReaderBufferTests
{
    /// <summary>What a reader was handed, as it saw it.</summary>
    private sealed class BufferReader(bool scribble) : IDocumentReader
    {
        public bool Visible { get; private set; }
        public bool CanWrite { get; private set; }
        public byte[]? Array { get; private set; }
        public int Offset { get; private set; }
        public int Count { get; private set; }
        public string Name => "buffer";
        public IReadOnlyList<string> Extensions { get; } = [".buf"];

        public Task<ReadDocument> ReadAsync(Stream content, string path, CancellationToken ct)
        {
            CanWrite = content.CanWrite;
            if (content is MemoryStream memory && memory.TryGetBuffer(out var segment))
            {
                Visible = true;
                Array = segment.Array;
                Offset = segment.Offset;
                Count = segment.Count;
                // A reader that does what it may not: writes over every byte
                // it was handed, through the array the stream exposes.
                if (scribble) segment.AsSpan().Fill(0xFF);
            }
            return Task.FromResult(new ReadDocument("Read.", null, null));
        }
    }

    private static SourceContent Content(string path, Func<Stream> open) => new(
        path, _ => Task.FromResult(open()),
        (read, hash) => new SourceDocument(path, read.Title, read.Text, hash, DocumentAccess.Everyone));

    private static byte[] Invented(int length)
    {
        var bytes = new byte[length];
        for (var i = 0; i < length; i++) bytes[i] = (byte)(i * 7 + 3);
        return bytes;
    }

    private static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    [Fact]
    public async Task A_reader_is_handed_the_bytes_the_pipeline_read_through_a_visible_read_only_buffer_of_exactly_that_length()
    {
        var file = Invented(100_003);
        var reader = new BufferReader(scribble: false);

        var read = await DocumentSourceReading.ReadAsync(
            Content("notes/a.buf", () => new MemoryStream(file, writable: false)), new ReaderRegistry(reader));

        Assert.NotNull(read.Document);
        Assert.True(reader.Visible, "the reader could not take the pipeline's array and would have to copy it");
        Assert.False(reader.CanWrite);
        Assert.Equal(0, reader.Offset);
        Assert.Equal(file.Length, reader.Count);
        Assert.Equal(file.Length, reader.Array!.Length);
        Assert.Equal(file, reader.Array);
        // The pipeline's own array, not the source's: the source's bytes are
        // copied once, into the array the pipeline hashes.
        Assert.NotSame(file, reader.Array);
    }

    [Fact]
    public async Task A_stream_that_states_no_length_is_handed_over_exactly_as_long_as_it_is()
    {
        // Not seekable, so the pipeline reads into a buffer that grows past the
        // length; what the reader is handed is the bytes and not the slack.
        var file = Invented(100_003);
        var reader = new BufferReader(scribble: false);

        var read = await DocumentSourceReading.ReadAsync(
            Content("notes/b.buf", () => new UnseekableStream(file)), new ReaderRegistry(reader));

        Assert.Equal(file.Length, reader.Count);
        Assert.Equal(file, reader.Array![reader.Offset..(reader.Offset + reader.Count)]);
        Assert.Equal(Sha(file), read.Document!.ContentHash);
    }

    [Fact]
    public async Task A_reader_that_writes_into_the_buffer_changes_nothing_the_pipeline_hashed()
    {
        var file = Invented(4096);
        var reader = new BufferReader(scribble: true);

        var read = await DocumentSourceReading.ReadAsync(
            Content("notes/c.buf", () => new MemoryStream(file, writable: false)), new ReaderRegistry(reader));

        // The control: the reader did overwrite the array the pipeline read
        // into, so a hash taken from it now would differ.
        Assert.All(reader.Array!, b => Assert.Equal(0xFF, b));
        Assert.NotEqual(Sha(file), Sha(reader.Array!));
        // The document carries the hash of the file as it was read.
        Assert.Equal(Sha(file), read.Document!.ContentHash);
        Assert.Equal("Read.", read.Document.Text);
    }

    /// <summary>The given bytes, read in small pieces, with no length to state.</summary>
    private sealed class UnseekableStream(byte[] bytes) : Stream
    {
        private int _at;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var got = Math.Min(Math.Min(count, 1000), bytes.Length - _at);
            System.Array.Copy(bytes, _at, buffer, offset, got);
            _at += got;
            return got;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

namespace Premagentic.Extensions.Shared;

/// <summary>
/// The bytes of the file a reader was handed. The pipeline reads a file once,
/// into one array, and hands a reader a read-only stream over that array with
/// the array visible, so a reader that needs the whole file takes the array
/// itself rather than a copy of it: a file at the pipeline's 256 MB cap is then
/// held once while it is read, not three times. Any other stream is copied,
/// from where it stands.
/// <para>
/// The array is only read. The pipeline hashed it before the reader was called
/// and does not read it again, so nothing a reader did to it could change what
/// was hashed; it could change only its own reading.
/// </para>
/// </summary>
internal static class ReaderBytes
{
    internal static async Task<byte[]> AllAsync(Stream content, CancellationToken ct)
    {
        if (content is MemoryStream memory && memory.Position == 0 && memory.TryGetBuffer(out var visible)
            && visible.Array is { } array && visible.Offset == 0 && visible.Count == array.Length)
            return array;

        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, ct);
        return buffer.ToArray();
    }
}

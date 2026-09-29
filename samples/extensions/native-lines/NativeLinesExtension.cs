using System.Runtime.InteropServices;
using Premagentic.Core;
using Premagentic.Core.Extensions;
using Premagentic.Core.Ingestion;

namespace Premagentic.Samples.Chunking;

/// <summary>
/// An extension that brings a native library: a chunker that cuts a document
/// into one chunk per line, finding each line's end by calling a C function in
/// <c>prem_sample_lines</c>, built for each platform and shipped under
/// <c>runtimes/&lt;rid&gt;/native/</c> in the extension's folder.
/// <para>
/// The host loads a native library only when the manifest lists it with its
/// hash, and measures it again just before it loads it, so a native file is
/// held to the same rule as the assembly. This project's build copies the
/// committed library for each platform into place and writes the manifest's
/// <c>files</c> list; the library's source and how it was built are in the
/// sample's README.
/// </para>
/// </summary>
public sealed class NativeLinesExtension : IExtension
{
    public string Name => "native-lines";

    public void Register(ExtensionRegistrations registrations) =>
        registrations.AddChunker(new NativeLineChunker());
}

/// <summary>One chunk per line that has text on it, with no heading of its own; text taken from the document and nothing else.</summary>
public sealed class NativeLineChunker : IChunker
{
    public string Name => "native-line";

    public IReadOnlyList<DocumentChunk> Chunk(SourceDocument document)
    {
        var text = document.Text;
        var chunks = new List<DocumentChunk>();
        var start = 0;
        while (start <= text.Length)
        {
            var end = LineEnd(text, start);
            var line = text[start..end].Trim();
            if (line.Length > 0) chunks.Add(new DocumentChunk(chunks.Count, "", line));
            start = end + 1;
        }
        return chunks;
    }

    private static unsafe int LineEnd(string text, int start)
    {
        fixed (char* p = text) return prem_sample_line_end(p, text.Length, start);
    }

    [DllImport("prem_sample_lines", CallingConvention = CallingConvention.Cdecl)]
    private static extern unsafe int prem_sample_line_end(char* text, int length, int start);
}

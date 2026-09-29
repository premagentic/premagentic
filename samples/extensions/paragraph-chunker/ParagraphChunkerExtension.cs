using Premagentic.Core;
using Premagentic.Core.Extensions;
using Premagentic.Core.Ingestion;
using Premagentic.Samples.ParagraphRules;

namespace Premagentic.Samples.Chunking;

/// <summary>
/// An extension that brings a library of its own: a chunker that cuts at blank
/// lines, with the cutting done by <c>ParagraphRules.dll</c>, which ships in
/// the extension's folder beside this assembly.
/// <para>
/// A file an extension loads from its folder must be in its manifest's
/// <c>files</c> list with its hash, or it is not loaded and the extension is
/// refused. This project's build writes that list; <c>prem extensions allow</c>
/// then measures the assembly and every listed file, and the hash it allows
/// covers all of them, so replacing the library and rewriting the manifest to
/// match does not carry the administrator's decision over.
/// </para>
/// </summary>
public sealed class ParagraphChunkerExtension : IExtension
{
    public string Name => "paragraph-chunker";

    public void Register(ExtensionRegistrations registrations) =>
        registrations.AddChunker(new ParagraphChunker());
}

/// <summary>One chunk per paragraph, with no heading of its own; text taken from the document and nothing else.</summary>
public sealed class ParagraphChunker : IChunker
{
    public string Name => "paragraph";

    public IReadOnlyList<DocumentChunk> Chunk(SourceDocument document) =>
        [.. Paragraphs.Of(document.Text).Select((paragraph, seq) => new DocumentChunk(seq, "", paragraph))];
}

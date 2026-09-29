namespace Premagentic.Core.Ingestion;

/// <summary>
/// Cuts one document's text into the chunks that are embedded, stored and
/// searched. A source chooses its chunker by name from a
/// <see cref="ChunkerRegistry"/>, so an installation can cut contracts one way
/// and runbooks another without a change to this library.
/// <para>
/// A chunker only cuts. It never writes, never calls out, and never executes
/// anything in the text. What it returns is what a search serves and cites, so
/// every chunk's content is text taken from the document, never text written
/// about it.
/// </para>
/// </summary>
public interface IChunker
{
    /// <summary>
    /// The name a source chooses this chunker by: up to 64 letters, digits,
    /// dots, hyphens and underscores, starting with a letter or digit. It is
    /// stored with every document the chunker cuts, so a source switched to
    /// another chunker has every document cut again at its next ingest. A
    /// chunker whose output changes for the same text takes a new name;
    /// otherwise the documents it already cut keep their old chunks until their
    /// content changes.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// The chunks of <paramref name="document"/>, numbered from 0 in order, or
    /// none when it has no text to index.
    /// </summary>
    IReadOnlyList<DocumentChunk> Chunk(SourceDocument document);
}

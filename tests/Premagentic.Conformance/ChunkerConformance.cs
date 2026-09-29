using Premagentic.Core.Extensions;
using Premagentic.Core;
using Premagentic.Core.Ingestion;
using Premagentic.Core.Security;

namespace Premagentic.Conformance;

/// <summary>
/// Inherit this and give it your chunker to prove it keeps the chunker
/// contract. What a chunker returns is what a search serves and cites, so the
/// whole of the contract is that a chunk is text taken from the document,
/// numbered, and that the same document cut twice gives the same chunks.
/// <para>
/// Add documents of your own with <see cref="Documents"/> if your chunker has
/// a format in mind. Every document the kit brings is invented.
/// </para>
/// </summary>
public abstract class ChunkerConformance
{
    /// <summary>
    /// This kit proves the version of this seam that the PremAgentic your
    /// extension builds against offers. A kit from another release fails here,
    /// naming the version to match, rather than passing in silence.
    /// </summary>
    [Fact]
    public void The_kit_proves_the_seams_this_extension_builds_against() =>
        KitSeams.AssertProves(SeamVersions.ChunkerName, nameof(SeamVersions.Chunker));

    protected abstract IChunker Chunker { get; }

    /// <summary>The documents this chunker is cut over. Override to add your own format's.</summary>
    protected virtual IReadOnlyList<SourceDocument> Documents => Invented;

    /// <summary>Documents with text in every shape a chunker meets, all made up.</summary>
    protected static IReadOnlyList<SourceDocument> Invented { get; } =
    [
        Document("one-line.md", "The side door opens at eight."),
        Document("headings.md",
            "# Opening\n\n## Doors\nThe side door opens at eight.\nThe front door opens at nine.\n\n## Lights\nThe last person out turns them off.\n"),
        Document("windows-line-endings.md", "# Closing\r\n\r\nThe last van leaves at six.\r\nThe gate is locked after it.\r\n"),
        Document("fenced.md", "# Notes\n\n```\n# not a heading\nkeep this line\n```\n\nAfter the fence.\n"),
        Document("long.md", string.Join("\n\n", Enumerable.Range(1, 400).Select(n => $"Paragraph {n} of the long note about the yard."))),
        Document("no-text.md", ""),
        Document("only-spaces.md", "   \n\n\t\n"),
    ];

    [Fact]
    public void The_name_is_one_a_source_can_choose()
    {
        Assert.True(ChunkerRegistry.IsName(Chunker.Name),
            $"'{Chunker.Name}' cannot name a chunker: a name is up to 64 letters, digits, dots, hyphens and " +
            "underscores, starting with a letter or digit.");
    }

    [Fact]
    public void Every_chunk_is_text_taken_from_the_document()
    {
        foreach (var document in Documents)
        {
            var whole = ConformanceText.Flatten(document.Text);
            foreach (var chunk in Chunker.Chunk(document))
            {
                var part = ConformanceText.Flatten(chunk.Content);
                Assert.True(whole.Contains(part, StringComparison.Ordinal),
                    $"The chunker '{Chunker.Name}' returned a chunk of '{document.Path}' that is not in the document. " +
                    "A chunk is text taken from the document, never text written about it. The chunk was: " + Clip(part));
            }
        }
    }

    [Fact]
    public void Chunks_are_numbered_from_zero_in_order()
    {
        foreach (var document in Documents)
        {
            var chunks = Chunker.Chunk(document);
            Assert.Equal(Enumerable.Range(0, chunks.Count), chunks.Select(c => c.Seq));
            foreach (var chunk in chunks) Assert.NotNull(chunk.HeadingPath);
        }
    }

    [Fact]
    public void A_document_with_nothing_to_index_gives_no_chunks()
    {
        foreach (var document in Documents.Where(d => ConformanceText.Flatten(d.Text).Length == 0))
            Assert.Empty(Chunker.Chunk(document));
    }

    [Fact]
    public void The_same_document_cut_twice_gives_the_same_chunks()
    {
        foreach (var document in Documents)
            Assert.Equal(
                Chunker.Chunk(document).Select(c => (c.Seq, c.HeadingPath, c.Content)),
                Chunker.Chunk(document).Select(c => (c.Seq, c.HeadingPath, c.Content)));
    }

    /// <summary>A document with the parts a chunker needs and nothing a chunker reads beyond the text.</summary>
    protected static SourceDocument Document(string path, string text) =>
        new(path, null, text, $"conformance-{path}", DocumentAccess.NoOne);

    private static string Clip(string text) => text.Length <= 120 ? text : text[..120] + "...";
}

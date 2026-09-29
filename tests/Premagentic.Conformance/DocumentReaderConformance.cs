using Premagentic.Core.Extensions;
using Premagentic.Core.Ingestion;
using Premagentic.Core.Ingestion.Readers;

namespace Premagentic.Conformance;

/// <summary>
/// A file this reader can read, and words its text must carry.
/// </summary>
/// <param name="Content">
/// The file's bytes. Invent them, or build them with whatever library your
/// reader reads: never a real document from anyone's corpus.
/// </param>
/// <param name="Expected">
/// Words or phrases that are in the file and must be in the text. Give enough
/// of them that a reader returning a description of the file instead of its
/// contents would fail.
/// </param>
public sealed record ReaderSample(string Path, byte[] Content, IReadOnlyList<string> Expected);

/// <summary>
/// Inherit this and give it your reader to prove it keeps the reader contract:
/// it names itself and the extensions it reads, it returns what is in the
/// file, and an empty file does not throw and does not become text about
/// itself.
/// </summary>
public abstract class DocumentReaderConformance
{
    /// <summary>
    /// This kit proves the version of this seam that the PremAgentic your
    /// extension builds against offers. A kit from another release fails here,
    /// naming the version to match, rather than passing in silence.
    /// </summary>
    [Fact]
    public void The_kit_proves_the_seams_this_extension_builds_against() =>
        KitSeams.AssertProves(SeamVersions.ReaderName, nameof(SeamVersions.Reader));

    protected abstract IDocumentReader Reader { get; }

    /// <summary>At least one file this reader reads, with words its text must carry.</summary>
    protected abstract IReadOnlyList<ReaderSample> Samples { get; }

    [Fact]
    public void It_names_itself_and_the_extensions_it_reads()
    {
        Assert.True(ChunkerRegistry.IsName(Reader.Name),
            $"'{Reader.Name}' cannot name a reader: a name is up to 64 letters, digits, dots, hyphens and " +
            "underscores, starting with a letter or digit.");
        Assert.NotEmpty(Reader.Extensions);
        foreach (var extension in Reader.Extensions)
        {
            Assert.StartsWith(".", extension);
            Assert.True(extension.Length > 1, $"'{extension}' is not a file extension: a dot and at least one character.");
            Assert.Equal(extension.ToLowerInvariant(), extension);
        }
    }

    [Fact]
    public async Task It_returns_what_is_in_the_file()
    {
        Assert.NotEmpty(Samples);
        foreach (var sample in Samples)
        {
            using var content = new MemoryStream(sample.Content, writable: false);
            var read = await Reader.ReadAsync(content, sample.Path, CancellationToken.None);

            Assert.NotNull(read);
            var text = ConformanceText.Flatten(read.Text);
            foreach (var expected in sample.Expected)
                Assert.True(text.Contains(ConformanceText.Flatten(expected), StringComparison.Ordinal),
                    $"The reader '{Reader.Name}' read '{sample.Path}' without \"{expected}\" in the text. " +
                    "A reader returns the text of the file, not a description of it.");
        }
    }

    [Fact]
    public async Task An_empty_file_does_not_throw_and_has_no_text()
    {
        foreach (var path in Samples.Select(s => s.Path).DefaultIfEmpty("empty"))
        {
            using var nothing = new MemoryStream([], writable: false);
            var read = await Reader.ReadAsync(nothing, path, CancellationToken.None);

            Assert.NotNull(read);
            Assert.True(ConformanceText.Flatten(read.Text).Length == 0,
                $"The reader '{Reader.Name}' made text out of an empty file: \"{read.Text}\". " +
                "Text comes from the file, so an empty file has none.");
        }
    }
}

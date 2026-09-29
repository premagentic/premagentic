using System.Text;
using Premagentic.Conformance;
using Premagentic.Core.Embeddings;
using Premagentic.Core.Extensions;
using Premagentic.Core.Ingestion;
using Premagentic.Core.Ingestion.Readers;
using Premagentic.Core.Security;
using Premagentic.Core.Sources;

namespace Premagentic.Tests;

// The conformance kit, run against what this repository ships. An extension
// author inherits the same fixtures, so the kit is held to the same standard as
// the code: break a built-in and its named fixture fails.

public sealed class MarkdownChunkerConformanceTests : ChunkerConformance
{
    protected override IChunker Chunker => MarkdownChunker.Instance;
}

/// <summary>The sample extension's chunker, through the host that loaded it.</summary>
public sealed class SentenceChunkerConformanceTests : ChunkerConformance
{
    private static readonly Lazy<IChunker> Loaded =
        new(() => SampleExtension.LoadedOnce().Chunkers.Resolve(SampleExtension.ChunkerName));

    protected override IChunker Chunker => Loaded.Value;
}

public sealed class MarkdownReaderConformanceTests : DocumentReaderConformance
{
    protected override IDocumentReader Reader => MarkdownReader.Instance;

    protected override IReadOnlyList<ReaderSample> Samples { get; } =
    [
        new("yard.md", Utf8("---\ntitle: The yard\nstatus: active\n---\n\n# Yard\n\nThe gate is locked at six.\n"),
            ["Yard", "The gate is locked at six."]),
        new("plain.markdown", Utf8("# Vans\n\nThe last van leaves at six.\n"), ["The last van leaves at six."]),
    ];

    private static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text);
}

public sealed class PlainTextReaderConformanceTests : DocumentReaderConformance
{
    protected override IDocumentReader Reader => PlainTextReader.Instance;

    protected override IReadOnlyList<ReaderSample> Samples { get; } =
    [
        new("notes.txt", Encoding.UTF8.GetBytes("The side door opens at eight.\nThe front door opens at nine.\n"),
            ["The side door opens at eight.", "The front door opens at nine."]),
    ];
}

/// <summary>
/// The sample extension's reader, taken out of the host that loaded it, so the
/// kit holds an extension's reader to the same contract as a built-in one.
/// </summary>
public sealed class SampleCsvReaderConformanceTests : DocumentReaderConformance
{
    private static readonly Lazy<IDocumentReader> Loaded =
        new(() => SampleExtension.LoadedOnce().Readers.ForPath("sample" + SampleExtension.ReaderExtension)!);

    protected override IDocumentReader Reader => Loaded.Value;

    protected override IReadOnlyList<ReaderSample> Samples { get; } =
    [
        new("rates.csv",
            Encoding.UTF8.GetBytes("code,description\nBX7,\"the blue hangar, north end\"\nBX8,the shed\n"),
            ["code", "BX7", "the blue hangar, north end", "the shed"]),
    ];
}

public sealed class HashEmbeddingProviderConformanceTests : EmbeddingProviderConformance
{
    protected override IEmbeddingProvider Provider { get; } = new HashEmbeddingProvider();
}

/// <summary>
/// The file system connector through the kit. The unreadable item is a file
/// this test holds open, and the fixture proves it really cannot be read before
/// claiming the connector was tested against one: a lock is advisory on some
/// systems, and a process running as root ignores file permissions.
/// </summary>
public sealed class FileSystemSourceConformanceTests : DocumentSourceConformance, IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("prem-source-conformance-");
    private FileStream? _held;

    public void Dispose()
    {
        _held?.Dispose();
        try
        {
            _root.Delete(recursive: true);
        }
        catch (IOException)
        {
            // A folder a test could not remove is the temp folder's to clean.
        }
    }

    protected override Task<IDocumentSource> SourceAsync()
    {
        var folder = Folder("readable");
        File.WriteAllText(Path.Combine(folder, "yard.md"), "# Yard\n\nThe gate is locked at six.\n");
        File.WriteAllText(Path.Combine(folder, "notes.txt"), "The side door opens at eight.\n");
        File.WriteAllText(Path.Combine(folder, "picture.png"), "not read by any built-in reader");
        return Task.FromResult<IDocumentSource>(new FileSystemSource(folder, DocumentAccess.Everyone, "yard"));
    }

    protected override Task<IDocumentSource?> WithAnUnreadableItemAsync()
    {
        var folder = Folder("with-unreadable");
        File.WriteAllText(Path.Combine(folder, "open.md"), "# Open\n\nAnyone can read this one.\n");

        var closed = Path.Combine(folder, "closed.md");
        File.WriteAllText(closed, "# Closed\n\nThe test holds this one open.\n");
        _held = new FileStream(closed, FileMode.Open, FileAccess.Read, FileShare.None);
        if (CanRead(closed) && !OperatingSystem.IsWindows())
        {
            // A share mode is advisory here, so take the permissions away too.
            File.SetUnixFileMode(closed, UnixFileMode.None);
        }
        if (CanRead(closed))
        {
            // This machine lets its own account read the file however it is
            // held, so the kit's fake holds an item unreadable instead.
            _held.Dispose();
            _held = null;
            return Task.FromResult<IDocumentSource?>(new OneUnreadableItem(new FileSystemSource(folder, DocumentAccess.Everyone)));
        }

        return Task.FromResult<IDocumentSource?>(new FileSystemSource(folder, DocumentAccess.Everyone));
    }

    protected override Task<IDocumentSource?> WithPermissionsItCannotReadAsync()
    {
        var folder = Folder("unknown-rights");
        File.WriteAllText(Path.Combine(folder, "unknown.md"), "# Unknown\n\nNobody could say who may read this.\n");
        // What this connector is given when the caller could not work out who
        // may read the folder. The connector carries it through unchanged.
        return Task.FromResult<IDocumentSource?>(new FileSystemSource(folder, DocumentAccess.NoOne));
    }

    private string Folder(string name) => Directory.CreateDirectory(Path.Combine(_root.FullName, name)).FullName;

    private static bool CanRead(string path)
    {
        try
        {
            using var probe = File.OpenRead(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}

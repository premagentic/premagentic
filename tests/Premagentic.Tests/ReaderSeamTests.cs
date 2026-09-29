using System.Security.Cryptography;
using System.Text;
using Premagentic.Core;
using Premagentic.Core.Embeddings;
using Premagentic.Core.Ingestion;
using Premagentic.Core.Ingestion.Readers;
using Premagentic.Core.Security;
using Premagentic.Core.Sources;
using Premagentic.Core.Sources.Registry;
using Premagentic.Core.Storage;

namespace Premagentic.Tests;

/// <summary>
/// The reader seam: which formats a process can read, what happens to a file no
/// reader claims, and what a connector still owns once a reader has read the
/// content. Every file here is invented.
/// </summary>
public class ReaderSeamTests
{
    /// <summary>A reader for an invented format, standing in for one an installation would add.</summary>
    private sealed class UppercaseReader : IDocumentReader
    {
        public string Name => "shout";
        public IReadOnlyList<string> Extensions { get; } = [".shout"];

        public async Task<ReadDocument> ReadAsync(Stream content, string path, CancellationToken ct)
        {
            using var reader = new StreamReader(content, Encoding.UTF8, leaveOpen: true);
            var text = (await reader.ReadToEndAsync(ct)).ToUpperInvariant();
            return new ReadDocument(text, null, null);
        }
    }

    private sealed class NamedReader(string name, params string[] extensions) : IDocumentReader
    {
        public string Name => name;
        public IReadOnlyList<string> Extensions { get; } = extensions;
        public Task<ReadDocument> ReadAsync(Stream content, string path, CancellationToken ct) =>
            Task.FromResult(new ReadDocument("", null, null));
    }

    [Fact]
    public void The_built_in_registry_reads_markdown_and_text_and_nothing_else()
    {
        var readers = ReaderRegistry.BuiltIn;

        Assert.Equal("markdown", readers.ForPath("handbook/travel.md")?.Name);
        Assert.Equal("markdown", readers.ForPath("handbook/travel.markdown")?.Name);
        Assert.Equal("markdown", readers.ForPath("handbook/SHOUTED.MD")?.Name);
        Assert.Equal("text", readers.ForPath("minutes.txt")?.Name);
        Assert.Equal(["markdown", "text"], readers.Names);
        Assert.Equal([".markdown", ".md", ".txt"], readers.Extensions);
    }

    [Theory]
    [InlineData("floor-plan.pdf")]
    [InlineData("scans/receipt.PDF")]
    [InlineData("budget.xlsx")]
    [InlineData("letter.docx")]
    [InlineData("README")]
    public void A_format_no_reader_claims_has_none(string path) =>
        Assert.Null(ReaderRegistry.BuiltIn.ForPath(path));

    [Fact]
    public void A_reader_can_be_added_and_cannot_take_over_a_built_in_format()
    {
        Assert.Equal("shout", new ReaderRegistry(new UppercaseReader()).ForPath("notice.shout")?.Name);

        Assert.Contains("both claim '.md'", Assert.Throws<ArgumentException>(
            () => new ReaderRegistry(new NamedReader("other", ".md"))).Message);
        Assert.Contains("both claim '.rtf'", Assert.Throws<ArgumentException>(
            () => new ReaderRegistry(new NamedReader("one", ".rtf"), new NamedReader("two", ".rtf"))).Message);
        Assert.Contains("Two readers are named", Assert.Throws<ArgumentException>(
            () => new ReaderRegistry(new NamedReader("markdown", ".rtf"))).Message);
        Assert.Contains("is not an extension", Assert.Throws<ArgumentException>(
            () => new ReaderRegistry(new NamedReader("caps", ".RTF"))).Message);
        Assert.Contains("claims no extension", Assert.Throws<ArgumentException>(
            () => new ReaderRegistry(new NamedReader("nothing"))).Message);
    }

    [Fact]
    public async Task The_built_in_readers_split_the_frontmatter_block_off_the_body()
    {
        var read = await ReadAsync(MarkdownReader.Instance, """
            ---
            title: Per diem
            status: superseded
            ---
            # Per diem

            The rate is set every January.
            """);

        Assert.Equal("Per diem", read.Title);
        Assert.StartsWith("# Per diem", read.Text);
        Assert.Equal("superseded", read.Frontmatter!["status"]);
        Assert.Equal(FrontmatterState.Parsed, read.ParsedFrontmatter!.State);
    }

    [Fact]
    public async Task A_document_with_no_block_takes_its_title_from_the_first_heading()
    {
        var read = await ReadAsync(MarkdownReader.Instance, "# Loading dock\n\nOpen at seven.\n");

        Assert.Equal("Loading dock", read.Title);
        Assert.Equal(FrontmatterState.Absent, read.ParsedFrontmatter!.State);
        Assert.Null(read.ParsedFrontmatter.Fields.GetValueOrDefault("title"));
    }

    [Fact]
    public async Task A_block_that_cannot_be_read_is_reported_as_unparseable_rather_than_absent()
    {
        var read = await ReadAsync(PlainTextReader.Instance, "---\ntitle: [unclosed\n---\nBody.\n");

        Assert.Equal(FrontmatterState.Unparseable, read.ParsedFrontmatter!.State);
    }

    [Fact]
    public async Task A_reader_that_parses_no_frontmatter_leaves_the_connector_a_document_with_none()
    {
        var root = Directory.CreateTempSubdirectory("premagentic-seam-").FullName;
        File.WriteAllText(Path.Combine(root, "notice.shout"), "the vents close at dusk");
        var source = new FileSystemSource(root, DocumentAccess.Everyone, "yard");

        var read = Assert.Single(await ReadAllAsync(source, new ReaderRegistry(new UppercaseReader())));
        var doc = read.Document!;

        Assert.Equal("yard/notice.shout", doc.Path);
        Assert.Equal("THE VENTS CLOSE AT DUSK", doc.Text);
        // The reader stated no title and no fields, so the connector falls back
        // to the file name and the document carries no OKF reading at all.
        Assert.Equal("notice", doc.Title);
        Assert.Null(doc.Okf);
        Assert.Equal(DocumentLifecycle.Active, doc.LifecycleStatus);
        Assert.Null(doc.DocClass);
    }

    [Fact]
    public async Task The_content_hash_describes_the_file_and_not_the_text_read_out_of_it()
    {
        var root = Directory.CreateTempSubdirectory("premagentic-seam-hash-").FullName;
        var file = Path.Combine(root, "rota.md");
        var text = "---\ntitle: Rota\n---\n# Rota\n\nTwo people open on Saturdays.\n";
        // Written with a byte-order mark, so the file's bytes and the text read
        // out of them differ. The hash has to describe the file: a reader for a
        // format that cannot be turned back into bytes could not hash its text.
        File.WriteAllText(file, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        var read = Assert.Single(await ReadAllAsync(
            new FileSystemSource(root, DocumentAccess.Everyone), ReaderRegistry.BuiltIn));
        var doc = read.Document!;

        Assert.Equal(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))), doc.ContentHash);
        Assert.NotEqual(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))), doc.ContentHash);
        // The mark is stripped rather than decoded into the first character,
        // which is the only reason the block above it parses at all.
        Assert.Equal("Rota", doc.Title);
    }

    [Fact]
    public async Task A_path_that_cannot_be_opened_is_a_failure_and_keeps_its_index_entry()
    {
        var content = new SourceContent(
            "vault/locked.md",
            _ => throw new UnauthorizedAccessException("access denied"),
            (_, _) => throw new InvalidOperationException("a document must not be completed from a file that was not read"));

        var read = await DocumentSourceReading.ReadAsync(content, ReaderRegistry.BuiltIn);

        Assert.Null(read.Document);
        Assert.Null(read.Skip);
        Assert.Equal("vault/locked.md", read.Failure!.Path);
        Assert.Equal("UnauthorizedAccessException: access denied", read.Failure.Reason);
    }

    /// <summary>A reader for an invented format whose behavior on each file the test chooses.</summary>
    private sealed class ScriptedReader(Func<string, ReadDocument> onFile) : IDocumentReader
    {
        public string Name => "scripted";
        public IReadOnlyList<string> Extensions { get; } = [".scr"];
        public Task<ReadDocument> ReadAsync(Stream content, string path, CancellationToken ct) =>
            Task.FromResult(onFile(path));
    }

    private static SourceContent ContentAt(string path) => new(
        path, _ => Task.FromResult<Stream>(new MemoryStream([1, 2, 3])),
        (read, hash) => new SourceDocument(path, read.Title, read.Text, hash, DocumentAccess.Everyone));

    [Fact]
    public async Task A_reader_that_recognizes_a_file_and_will_not_index_it_makes_a_skip_counted_under_its_reason()
    {
        var readers = new ReaderRegistry(new ScriptedReader(_ => ReadDocument.Skipped("no text layer")));

        var read = await DocumentSourceReading.ReadAsync(ContentAt("scans/receipt.scr"), readers);

        Assert.Null(read.Document);
        Assert.Null(read.Failure);
        Assert.Equal("scans/receipt.scr", read.Skip!.Path);
        Assert.Equal(".scr (no text layer)", read.Skip.Extension);
    }

    [Fact]
    public void A_skip_needs_a_reason_a_person_can_read()
    {
        Assert.Throws<ArgumentException>(() => ReadDocument.Skipped(" "));
        Assert.Equal("macro-enabled", ReadDocument.Skipped("  macro-enabled ").SkipReason);
    }

    [Fact]
    public async Task A_reader_that_calls_a_file_unreadable_makes_a_failure_with_its_words_and_no_type_name()
    {
        var readers = new ReaderRegistry(new ScriptedReader(_ => throw new UnreadableDocumentException("password protected")));

        var read = await DocumentSourceReading.ReadAsync(ContentAt("hr/offer.scr"), readers);

        Assert.Null(read.Document);
        Assert.Null(read.Skip);
        Assert.Equal("hr/offer.scr", read.Failure!.Path);
        Assert.Equal("password protected", read.Failure.Reason);
    }

    [Fact]
    public async Task A_reader_that_throws_is_contained_and_the_file_is_unreadable_with_the_reader_named()
    {
        var readers = new ReaderRegistry(new ScriptedReader(_ => throw new InvalidOperationException("the table\nis broken")));

        var read = await DocumentSourceReading.ReadAsync(ContentAt("ops/plan.scr"), readers);

        Assert.Null(read.Document);
        Assert.Null(read.Skip);
        Assert.Equal("ops/plan.scr", read.Failure!.Path);
        Assert.Equal(
            "the reader 'scripted' failed on this file (InvalidOperationException: the table is broken)",
            read.Failure.Reason);
    }

    [Fact]
    public async Task One_file_a_reader_throws_on_does_not_stop_the_files_beside_it()
    {
        var root = Directory.CreateTempSubdirectory("premagentic-seam-throw-").FullName;
        File.WriteAllText(Path.Combine(root, "a-bad.scr"), "x");
        File.WriteAllText(Path.Combine(root, "b-good.scr"), "the dock opens at seven");
        var readers = new ReaderRegistry(new ScriptedReader(path =>
            path.EndsWith("a-bad.scr", StringComparison.Ordinal)
                ? throw new NullReferenceException()
                : new ReadDocument("the dock opens at seven", null, null)));

        var reads = await ReadAllAsync(new FileSystemSource(root, DocumentAccess.Everyone, "yard"), readers);

        Assert.Equal(2, reads.Count);
        Assert.Equal("yard/a-bad.scr", Assert.Single(reads, r => r.Failure is not null).Failure!.Path);
        Assert.Equal("the dock opens at seven", Assert.Single(reads, r => r.Document is not null).Document!.Text);
    }

    [Fact]
    public async Task A_connector_that_fails_to_finish_a_document_is_not_blamed_on_the_reader()
    {
        var readers = new ReaderRegistry(new ScriptedReader(_ => new ReadDocument("fine", null, null)));
        var content = new SourceContent(
            "ops/plan.scr", _ => Task.FromResult<Stream>(new MemoryStream([1])),
            (_, _) => throw new InvalidOperationException("connector defect"));

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            () => DocumentSourceReading.ReadAsync(content, readers));
        Assert.Equal("connector defect", thrown.Message);
    }

    [Fact]
    public async Task A_run_being_cancelled_is_not_contained_as_a_bad_file()
    {
        using var canceled = new CancellationTokenSource();
        await canceled.CancelAsync();
        var readers = new ReaderRegistry(new ScriptedReader(_ => throw new OperationCanceledException(canceled.Token)));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => DocumentSourceReading.ReadAsync(ContentAt("late.scr"), readers, canceled.Token));
    }

    private static async Task<ReadDocument> ReadAsync(IDocumentReader reader, string text)
    {
        using var content = new MemoryStream(Encoding.UTF8.GetBytes(text));
        return await reader.ReadAsync(content, "invented.md", CancellationToken.None);
    }

    private static async Task<List<SourceRead>> ReadAllAsync(IDocumentSource source, ReaderRegistry readers)
    {
        var reads = new List<SourceRead>();
        await foreach (var read in source.ReadThroughAsync(readers)) reads.Add(read);
        return reads;
    }
}

/// <summary>
/// What a run does with a format this process has no reader for, and what
/// adding one changes, end to end. Requires a running Docker daemon.
/// </summary>
public sealed class ReaderSeamIngestTests(DatastoreTestDatabase server) : IClassFixture<DatastoreTestDatabase>
{
    private sealed class RichTextReader : IDocumentReader
    {
        public string Name => "rich-text";
        public IReadOnlyList<string> Extensions { get; } = [".rtf"];

        public async Task<ReadDocument> ReadAsync(Stream content, string path, CancellationToken ct)
        {
            // Invented and deliberately crude: the point is that the text
            // reaches the index through a reader this core does not ship.
            using var reader = new StreamReader(content, Encoding.UTF8, leaveOpen: true);
            var raw = await reader.ReadToEndAsync(ct);
            return new ReadDocument(raw.Replace("\\par", "\n").Replace("{\\rtf1}", "").Trim(), null, null);
        }
    }

    [Fact]
    public async Task A_format_with_no_reader_is_counted_and_a_reader_makes_the_same_folder_index()
    {
        var folder = SourcesTests.Folder(
            ("handover.rtf", "{\\rtf1}Night staff log the freezer temperature at midnight.\\par"),
            ("rota.md", "# Rota\n\n## Weekend\nTwo people open the shop on Saturdays.\n"));

        await using var db = new PremagenticDatabase(await server.CreateDatabaseAsync());
        await db.InitializeAsync();
        var tenant = await db.EnsureTenantAsync("t", "T");
        var source = new FileSystemSource(folder, DocumentAccess.Everyone, "office");

        // As shipped: the Markdown file is indexed and the other format is
        // counted as not read, never guessed at.
        var withoutReader = await new IngestPipeline(db, new HashEmbeddingProvider())
            .RunAsync(tenant, source);

        Assert.Equal((1, 1, 1), (withoutReader.Scanned, withoutReader.Ingested, withoutReader.Skipped));
        Assert.Equal(new Dictionary<string, int> { [".rtf"] = 1 }, withoutReader.SkippedFormats);
        Assert.Equal(0L, await CountAsync(db, "SELECT count(*) FROM prem_index.document WHERE path = 'office/handover.rtf'"));

        // With a reader for it, the same folder and the same connector index it.
        var withReader = await new IngestPipeline(
                db, new HashEmbeddingProvider(), readers: new ReaderRegistry(new RichTextReader()))
            .RunAsync(tenant, source);

        Assert.Equal((2, 1, 0), (withReader.Scanned, withReader.Ingested, withReader.Skipped));
        Assert.Equal(1L, await CountAsync(db, "SELECT count(*) FROM prem_index.document WHERE path = 'office/handover.rtf'"));
        Assert.Equal(1L, await CountAsync(db,
            "SELECT count(*) FROM prem_index.chunk c JOIN prem_index.document d ON d.id = c.document_id " +
            "WHERE d.path = 'office/handover.rtf' AND c.content LIKE '%freezer temperature%'"));
    }

    /// <summary>Reads what a test file says: a line naming what to do with the file.</summary>
    private sealed class DirectedReader : IDocumentReader
    {
        public string Name => "directed";
        public IReadOnlyList<string> Extensions { get; } = [".dir"];

        public async Task<ReadDocument> ReadAsync(Stream content, string path, CancellationToken ct)
        {
            using var reader = new StreamReader(content, Encoding.UTF8, leaveOpen: true);
            var line = (await reader.ReadToEndAsync(ct)).Trim();
            return line switch
            {
                "skip" => ReadDocument.Skipped("no text layer"),
                "unreadable" => throw new UnreadableDocumentException("password protected"),
                "throw" => throw new InvalidOperationException("broken"),
                _ => new ReadDocument(line, null, null),
            };
        }
    }

    [Fact]
    public async Task A_run_counts_each_reader_verdict_and_one_bad_file_never_stops_it()
    {
        var folder = SourcesTests.Folder(
            ("a-good.dir", "# Dock\n\nThe loading dock opens at seven."),
            ("b-skip.dir", "skip"),
            ("c-locked.dir", "unreadable"),
            ("d-broken.dir", "throw"),
            ("e-good.md", "# Rota\n\n## Weekend\nTwo people open on Saturdays.\n"));

        await using var db = new PremagenticDatabase(await server.CreateDatabaseAsync());
        await db.InitializeAsync();
        var tenant = await db.EnsureTenantAsync("t", "T");
        var source = new FileSystemSource(folder, DocumentAccess.Everyone, "office");

        var summary = await new IngestPipeline(
                db, new HashEmbeddingProvider(), readers: new ReaderRegistry(new DirectedReader()))
            .RunAsync(tenant, source);

        Assert.Equal(new Dictionary<string, int> { [".dir (no text layer)"] = 1 }, summary.SkippedFormats);
        Assert.Equal(["office/c-locked.dir", "office/d-broken.dir"], summary.FailureList.Select(f => f.Path).Order().ToArray());
        Assert.Equal("password protected", summary.FailureList.Single(f => f.Path.EndsWith("c-locked.dir")).Reason);
        Assert.Equal(2, summary.Unreadable);
        Assert.Equal(2, summary.Ingested);
        Assert.Equal(1L, await CountAsync(db, "SELECT count(*) FROM prem_index.document WHERE path = 'office/a-good.dir'"));
        Assert.Equal(1L, await CountAsync(db, "SELECT count(*) FROM prem_index.document WHERE path = 'office/e-good.md'"));
        Assert.Equal(0L, await CountAsync(db, "SELECT count(*) FROM prem_index.document WHERE path = 'office/b-skip.dir'"));
    }

    /// <summary>
    /// Reads an invented ".note" format, standing in for a reader an
    /// installation adds: the file's text, or a skip when the file says it has
    /// no text, as a PDF saved again without its text layer would.
    /// </summary>
    private sealed class NoteReader : IDocumentReader
    {
        public string Name => "note";
        public IReadOnlyList<string> Extensions { get; } = [".note"];

        public async Task<ReadDocument> ReadAsync(Stream content, string path, CancellationToken ct)
        {
            using var reader = new StreamReader(content, Encoding.UTF8, leaveOpen: true);
            var text = (await reader.ReadToEndAsync(ct)).Trim();
            return text == "no text" ? ReadDocument.Skipped("no text layer") : new ReadDocument(text, null, null);
        }
    }

    private static readonly ReaderRegistry WithNotes = new(new NoteReader());

    private const string NoteCount = "SELECT count(*) FROM prem_index.document WHERE path = 'office/badge.note'";

    [Fact]
    public async Task A_document_whose_reader_is_not_loaded_now_is_kept_until_removal_is_asked_for()
    {
        var folder = SourcesTests.Folder(
            ("badge.note", "Visitors wear a yellow badge on the warehouse floor."),
            ("rota.md", "# Rota\n\nTwo people open on Saturdays.\n"));
        await using var db = new PremagenticDatabase(await server.CreateDatabaseAsync());
        await db.InitializeAsync();
        var tenant = await db.EnsureTenantAsync("t", "T");
        var source = new FileSystemSource(folder, DocumentAccess.Everyone, "office");

        await new IngestPipeline(db, new HashEmbeddingProvider(), readers: WithNotes).RunAsync(tenant, source);
        Assert.Equal(1L, await CountAsync(db, NoteCount));

        // The reader is not loaded now: refused at start after an upgrade, or
        // disallowed. The document it read is kept, counted and named.
        var log = new List<string>();
        var without = await new IngestPipeline(db, new HashEmbeddingProvider()).RunAsync(tenant, source, log.Add);

        Assert.Equal((1, 0, 0), (without.KeptWithoutReader, without.RemovedNowSkipped, without.OrphansRemoved));
        Assert.Equal(new Dictionary<string, int> { [".note"] = 1 }, without.SkippedFormats);
        Assert.Equal(1L, await CountAsync(db, NoteCount));
        Assert.Contains("  office/badge.note: no reader here reads its format now; keeping the existing index entry", log);

        // Removing it is an explicit act.
        log.Clear();
        var removing = await new IngestPipeline(db, new HashEmbeddingProvider()) { RemoveUnread = true }
            .RunAsync(tenant, source, log.Add);

        Assert.Equal((0, 1, 1), (removing.KeptWithoutReader, removing.RemovedNowSkipped, removing.OrphansRemoved));
        Assert.Equal(0L, await CountAsync(db, NoteCount));
        Assert.Equal(1L, await CountAsync(db, "SELECT count(*) FROM prem_index.document WHERE path = 'office/rota.md'"));
        Assert.Contains(log, l => l.StartsWith("  removed, now skipped: office/badge.note (no reader here reads", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_document_its_reader_now_finds_no_text_in_is_removed_counted_and_named()
    {
        var folder = SourcesTests.Folder(("badge.note", "Visitors wear a yellow badge on the warehouse floor."));
        await using var db = new PremagenticDatabase(await server.CreateDatabaseAsync());
        await db.InitializeAsync();
        var tenant = await db.EnsureTenantAsync("t", "T");
        var source = new FileSystemSource(folder, DocumentAccess.Everyone, "office");
        await new IngestPipeline(db, new HashEmbeddingProvider(), readers: WithNotes).RunAsync(tenant, source);

        // Saved again with no text in it: the reader looked and found none, so
        // the text indexed earlier is no longer in the file.
        File.WriteAllText(Path.Combine(folder, "badge.note"), "no text");
        var log = new List<string>();
        var summary = await new IngestPipeline(db, new HashEmbeddingProvider(), readers: WithNotes).RunAsync(tenant, source, log.Add);

        Assert.Equal((0, 1, 1), (summary.KeptWithoutReader, summary.RemovedNowSkipped, summary.OrphansRemoved));
        Assert.Equal(new Dictionary<string, int> { [".note (no text layer)"] = 1 }, summary.SkippedFormats);
        Assert.Equal(0L, await CountAsync(db, NoteCount));
        Assert.Contains("  removed, now skipped: office/badge.note (no text layer)", log);
    }

    [Fact]
    public async Task A_file_no_reader_reads_that_was_never_indexed_is_skipped_and_nothing_is_counted_as_kept()
    {
        var folder = SourcesTests.Folder(("badge.note", "Never indexed."), ("rota.md", "# Rota\n\nOpen on Saturdays.\n"));
        await using var db = new PremagenticDatabase(await server.CreateDatabaseAsync());
        await db.InitializeAsync();
        var tenant = await db.EnsureTenantAsync("t", "T");

        var summary = await new IngestPipeline(db, new HashEmbeddingProvider())
            .RunAsync(tenant, new FileSystemSource(folder, DocumentAccess.Everyone, "office"));

        Assert.Equal((0, 0, 1), (summary.KeptWithoutReader, summary.RemovedNowSkipped, summary.Skipped));
    }

    [Fact]
    public async Task The_recorded_run_keeps_what_was_kept_without_a_reader_and_what_was_removed_because_now_skipped()
    {
        var folder = SourcesTests.Folder(("badge.note", "Visitors wear a yellow badge on the warehouse floor."));
        await using var db = new PremagenticDatabase(await server.CreateDatabaseAsync());
        await db.InitializeAsync();
        var tenant = await db.EnsureTenantAsync("t", "T");
        var source = new FileSystemSource(folder, DocumentAccess.Everyone, "office");
        var runs = new IngestRuns(db, tenant);
        await runs.RunAsync(new IngestPipeline(db, new HashEmbeddingProvider(), readers: WithNotes), source, null);

        var kept = await runs.RunAsync(new IngestPipeline(db, new HashEmbeddingProvider()), source, null);
        var removed = await runs.RunAsync(
            new IngestPipeline(db, new HashEmbeddingProvider()) { RemoveUnread = true }, source, null);

        var keptRecord = (await runs.GetAsync(kept.RunId))!.Summary!;
        var removedRecord = (await runs.GetAsync(removed.RunId))!.Summary!;
        Assert.Equal((1, 0), (keptRecord.KeptWithoutReader, keptRecord.RemovedNowSkipped));
        Assert.Equal((0, 1, 1), (removedRecord.KeptWithoutReader, removedRecord.RemovedNowSkipped, removedRecord.OrphansRemoved));
    }

    private static async Task<long> CountAsync(PremagenticDatabase db, string sql)
    {
        await using var cmd = db.DataSource.CreateCommand(sql);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }
}

using Premagentic.Cli.Admin;
using Premagentic.Core.Embeddings;
using Premagentic.Core.Ingestion;
using Premagentic.Core.Ingestion.Readers;
using Premagentic.Core.Security;
using Premagentic.Core.Sources;
using Premagentic.Core.Sources.Registry;
using Premagentic.Core.Storage;

namespace Premagentic.Tests;

/// <summary>
/// <c>prem sources</c> and <c>prem ingest</c> as an administrator uses
/// them. Requires a running Docker daemon.
/// </summary>
[Collection(ConsoleCollection.Name)]
public sealed class SourcesCommandTests(DatastoreTestDatabase server) : IClassFixture<DatastoreTestDatabase>
{
    private async Task<(PremagenticDatabase Db, Guid Tenant)> EmptyAsync()
    {
        var db = new PremagenticDatabase(await server.CreateDatabaseAsync());
        await db.InitializeAsync();
        return (db, await db.EnsureTenantAsync("t", "T"));
    }

    private static Task<(int Exit, string Out, string Err)> Cli(PremagenticDatabase db, Guid tenant, params string[] args) =>
        CliWith(null, db, tenant, args);

    /// <summary>The CLI in a process whose chunkers are <paramref name="chunkers"/>, or the built-in ones.</summary>
    private static Task<(int Exit, string Out, string Err)> CliWith(
        ChunkerRegistry? chunkers, PremagenticDatabase db, Guid tenant, params string[] args) =>
        ConsoleCapture.RunAsync(() => args[0] switch
        {
            "ingest" => SourcesCommands.IngestAsync(args, db, tenant, headingPrefix: true, new HashEmbeddingProvider(), chunkers),
            "sources" => SourcesCommands.RunAsync(args, db, tenant, chunkers),
            _ => AdminCommands.RunAsync(args, db, tenant),
        });

    /// <summary>Every stored value of every document and chunk, in a stable order, with the access list by its text.</summary>
    private static async Task<string[]> IndexAsync(PremagenticDatabase db)
    {
        await using var cmd = db.DataSource.CreateCommand("""
            SELECT concat_ws('|', d.path, d.title, d.doc_class, d.lifecycle_status, d.source_name, d.content_hash,
                   d.okf_concept_id, d.trust_tier, d.authorship, d.stale_after, d.generated_at, d.last_verified_at,
                   d.frontmatter_state, d.acl_from_rule, s.canonical_text,
                   c.seq, c.heading_path, c.content, encode(c.embedding, 'hex'), c.embedding_model)
            FROM prem_index.document d
            JOIN prem_config.acl_set s ON s.tenant_id = d.tenant_id AND s.id = d.acl_set_id
            JOIN prem_index.chunk c ON c.document_id = d.id
            ORDER BY d.path, c.seq
            """);
        var rows = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync()) rows.Add(reader.GetString(0));
        return rows.ToArray();
    }

    [Fact]
    public async Task Ingest_by_source_name_stores_what_the_long_form_stores()
    {
        var folder = SourcesTests.BundleCopy(("attachments/price-sheet.pdf", "%PDF invented"));

        var (byHand, handTenant) = await EmptyAsync();
        await using var _ = byHand;
        var longForm = await Cli(byHand, handTenant,
            "ingest", folder, "--prefix", "greenhouse", "--okf-bundle", "--undeclared-as-machine", "--public");
        Assert.True(longForm.Exit == 0, longForm.Err);

        var (byName, nameTenant) = await EmptyAsync();
        await using var __ = byName;
        Assert.Equal(0, (await Cli(byName, nameTenant,
            "sources", "add", "greenhouse", folder, "--prefix", "greenhouse", "--okf-bundle", "--undeclared-as-machine")).Exit);
        Assert.Equal(0, (await Cli(byName, nameTenant, "rules", "set", "--prefix", "greenhouse", "--public")).Exit);
        var bySource = await Cli(byName, nameTenant, "ingest", "--source", "greenhouse");
        Assert.True(bySource.Exit == 0, bySource.Err);

        var expected = await IndexAsync(byHand);
        Assert.NotEmpty(expected);
        Assert.Equal(expected, await IndexAsync(byName));
        // The setting traveled: a concept with no author is stored as machine-written.
        Assert.Contains(expected, row => row.StartsWith("greenhouse/glossary.md|") && row.Contains("|glossary|0|2|"));

        // Both runs are recorded, and only the second belongs to a source.
        var handRun = Assert.Single(await new IngestRuns(byHand, handTenant).ListAsync());
        var nameRun = Assert.Single(await new IngestRuns(byName, nameTenant).ListAsync());
        Assert.Null(handRun.SourceId);
        Assert.Equal((await new SourceRegistry(byName, nameTenant).FindAsync("greenhouse"))!.Id, nameRun.SourceId);
        Assert.Equal(handRun.Summary!.Ingested, nameRun.Summary!.Ingested);
        Assert.Equal(1, nameRun.Summary.Skipped);
    }

    [Theory]
    [InlineData("--prefix", "elsewhere")]
    [InlineData("--okf-bundle")]
    [InlineData("--public")]
    [InlineData("extra-folder")]
    public async Task Ingest_by_source_name_refuses_what_belongs_to_the_registry(params string[] extra)
    {
        var (db, tenant) = await EmptyAsync();
        await using var _ = db;
        await Cli(db, tenant, "sources", "add", "desk", SourcesTests.Folder(("a.md", "# A\n\n## Body\nText.\n")), "--prefix", "desk");

        var run = await Cli(db, tenant, ["ingest", "--source", "desk", .. extra]);

        Assert.Equal(1, run.Exit);
        Assert.Contains("Only --allow-empty-source and --remove-unread may be added", run.Err);
        Assert.Empty(await new IngestRuns(db, tenant).ListAsync());
    }

    [Fact]
    public async Task Status_prints_the_last_run_and_how_many_concepts_declare_no_author()
    {
        var (db, tenant) = await EmptyAsync();
        await using var _ = db;
        var folder = SourcesTests.BundleCopy(("attachments/price-sheet.pdf", "%PDF invented"));
        await Cli(db, tenant, "sources", "add", "greenhouse", folder, "--prefix", "greenhouse", "--okf-bundle");
        await Cli(db, tenant, "rules", "set", "--prefix", "greenhouse", "--public");

        var before = await Cli(db, tenant, "sources", "status", "greenhouse");
        Assert.Contains("No ingest has run for this source yet", before.Out);

        var ingest = await Cli(db, tenant, "ingest", "--source", "greenhouse");
        Assert.Contains("skipped 1.", ingest.Out);
        Assert.Contains(": .pdf 1.", ingest.Out);

        var status = await Cli(db, tenant, "sources", "status", "greenhouse");
        Assert.Equal(0, status.Exit);
        Assert.Contains("Last run", status.Out);
        Assert.Contains("completed", status.Out);
        Assert.Contains("skipped 1 (.pdf 1)", status.Out);
        Assert.Contains("okf_version 0.2", status.Out);
        Assert.Contains($"{SourcesTests.Undeclared.Length} concept(s) do not say who wrote them, and are served as ordinary files", status.Out);
        Assert.Contains("'prem sources set greenhouse --undeclared-as-machine on'", status.Out);
    }

    [Fact]
    public async Task Removing_a_source_says_its_documents_stay_and_how_to_remove_them()
    {
        var (db, tenant) = await EmptyAsync();
        await using var _ = db;
        var folder = SourcesTests.Folder(("a.md", "# A\n\n## Body\nText.\n"), ("b.md", "# B\n\n## Body\nMore.\n"));
        await Cli(db, tenant, "sources", "add", "desk", folder, "--prefix", "desk");
        await Cli(db, tenant, "rules", "set", "--prefix", "desk", "--public");
        await Cli(db, tenant, "ingest", "--source", "desk");

        var removed = await Cli(db, tenant, "sources", "remove", "desk");

        Assert.Equal(0, removed.Exit);
        Assert.Contains("its 2 indexed document(s) are NOT deleted", removed.Out);
        Assert.Contains("--prefix desk --allow-empty-source", removed.Out);
        Assert.Contains("No sources are registered", (await Cli(db, tenant, "sources", "list")).Out);
    }

    [Fact]
    public async Task A_chunker_is_named_at_add_and_set_and_one_the_process_does_not_have_is_refused_or_fails_the_run()
    {
        var (db, tenant) = await EmptyAsync();
        await using var _ = db;
        var lines = new ChunkerRegistry(new LineChunker());
        var folder = SourcesTests.Folder(("a.md", "# A\n\n## Body\nText.\nMore text.\n"));

        var listed = await CliWith(lines, db, tenant, "sources", "chunkers");
        Assert.Equal(["markdown (the default)", "by-line"], listed.Out.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        var unknown = await Cli(db, tenant, "sources", "add", "desk", folder, "--prefix", "desk", "--chunker", "by-line");
        Assert.Equal(1, unknown.Exit);
        Assert.Contains("There is no chunker named 'by-line'", unknown.Err);
        Assert.Empty(await new SourceRegistry(db, tenant).ListAsync());

        var added = await CliWith(lines, db, tenant, "sources", "add", "desk", folder, "--prefix", "desk", "--chunker", "BY-LINE");
        Assert.True(added.Exit == 0, added.Err);
        Assert.Contains("chunker by-line", added.Out);
        Assert.Equal(0, (await Cli(db, tenant, "rules", "set", "--prefix", "desk", "--public")).Exit);

        // A process without that chunker records the run as failed, with the name, and indexes nothing.
        var failed = await Cli(db, tenant, "ingest", "--source", "desk");
        Assert.Equal(1, failed.Exit);
        Assert.Contains("The chunker 'by-line' is not registered in this process", failed.Err);
        Assert.Contains("recorded as failed", failed.Err);
        var run = Assert.Single(await new IngestRuns(db, tenant).ListAsync());
        Assert.Equal((IngestRunOutcome.Failed, "by-line"), (run.Outcome, run.Chunker));
        var desk = (await new SourceRegistry(db, tenant).FindAsync("desk"))!;
        Assert.Equal(0L, await new SourceRegistry(db, tenant).IndexedDocumentCountAsync(desk));

        var ingested = await CliWith(lines, db, tenant, "ingest", "--source", "desk");
        Assert.True(ingested.Exit == 0, ingested.Err);
        Assert.Contains("cut by the by-line chunker", ingested.Out);
        Assert.Equal(1L, await new SourceRegistry(db, tenant).IndexedDocumentCountAsync(desk));

        var set = await CliWith(lines, db, tenant, "sources", "set", "desk", "--chunker", "markdown");
        Assert.Equal(0, set.Exit);
        Assert.Contains("cuts, embeds and stores every document again", set.Out);
        Assert.Equal(1, (await Cli(db, tenant, "sources", "set", "desk", "--chunker", "by-line")).Exit);
        Assert.Contains("--chunker needs a chunker name", (await Cli(db, tenant, "sources", "set", "desk", "--chunker")).Err);
        Assert.Contains("Only --allow-empty-source and --remove-unread may be added", (await Cli(db, tenant, "ingest", "--source", "desk", "--chunker", "markdown")).Err);
        Assert.Equal("markdown", (await new SourceRegistry(db, tenant).FindAsync("desk"))!.Chunker);
    }

    [Fact]
    public async Task A_missing_folder_is_refused_cleanly_and_its_run_recorded_as_failed()
    {
        var (db, tenant) = await EmptyAsync();
        await using var _ = db;
        var missing = Path.Combine(Path.GetTempPath(), "premagentic-missing-" + Guid.NewGuid().ToString("N"));

        var run = await Cli(db, tenant, "ingest", missing, "--public");

        Assert.Equal(1, run.Exit);
        Assert.Contains("recorded as failed", run.Err);
        Assert.Equal(IngestRunOutcome.Failed, Assert.Single(await new IngestRuns(db, tenant).ListAsync()).Outcome);
    }

    /// <summary>An invented ".note" format, read by a reader the CLI in this test does not have.</summary>
    private sealed class NoteReader : IDocumentReader
    {
        public string Name => "note";
        public IReadOnlyList<string> Extensions { get; } = [".note"];

        public async Task<ReadDocument> ReadAsync(Stream content, string path, CancellationToken ct)
        {
            using var reader = new StreamReader(content, leaveOpen: true);
            return new ReadDocument(await reader.ReadToEndAsync(ct), null, null);
        }
    }

    [Fact]
    public async Task An_ingest_keeps_a_document_no_reader_here_reads_and_removes_it_only_with_remove_unread()
    {
        var (db, tenant) = await EmptyAsync();
        await using var _ = db;
        var folder = SourcesTests.Folder(
            ("badge.note", "Visitors wear a yellow badge on the warehouse floor."),
            ("rota.md", "# Rota\n\nTwo people open on Saturdays.\n"));
        // Indexed earlier by a reader this CLI does not have, as after a
        // reader was refused at start or disallowed.
        await new IngestPipeline(db, new HashEmbeddingProvider(), readers: new ReaderRegistry(new NoteReader()))
            .RunAsync(tenant, new FileSystemSource(folder, DocumentAccess.Everyone, "office"));

        var kept = await Cli(db, tenant, "ingest", folder, "--public", "--prefix", "office");

        Assert.True(kept.Exit == 0, kept.Err);
        Assert.Contains("  office/badge.note: no reader here reads its format now; keeping the existing index entry", kept.Out);
        Assert.Contains("Kept 1 document(s) indexed earlier in a format no reader here reads now", kept.Out);
        Assert.DoesNotContain("removed, now skipped", kept.Out);

        var removed = await Cli(db, tenant, "ingest", folder, "--public", "--prefix", "office", "--remove-unread");

        Assert.True(removed.Exit == 0, removed.Err);
        Assert.Contains("  removed, now skipped: office/badge.note", removed.Out);
        Assert.Contains("Removed 1 document(s) indexed earlier because the file is now skipped", removed.Out);
        Assert.DoesNotContain("Kept ", removed.Out);
    }
}

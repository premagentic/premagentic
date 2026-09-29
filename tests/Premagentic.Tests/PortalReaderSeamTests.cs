using System.Text;
using Premagentic.Core.Ingestion.Readers;
using Premagentic.Core.Sources.Registry;

namespace Premagentic.Tests;

/// <summary>
/// The reader seam reaching a run started from the PORTAL. The seam and its
/// registry were proven on their own before this, and still every host went on
/// passing the built-in readers, so an extension's reader would load, show on
/// the health page as loaded, and index nothing. A seam is done when a host
/// uses it, which is what this proves for the portal's own run button. Every
/// file here is invented. Requires a running Docker daemon.
/// </summary>
public sealed class PortalReaderSeamTests(DatastoreTestDatabase server) : IClassFixture<DatastoreTestDatabase>
{
    private const string Notice = "yard/notice.shout";

    /// <summary>A reader for an invented format, standing in for one an extension brings.</summary>
    private sealed class ShoutReader : IDocumentReader
    {
        public string Name => "shout";
        public IReadOnlyList<string> Extensions { get; } = [".shout"];

        public async Task<ReadDocument> ReadAsync(Stream content, string path, CancellationToken ct)
        {
            using var reader = new StreamReader(content, Encoding.UTF8, leaveOpen: true);
            return new ReadDocument((await reader.ReadToEndAsync(ct)).ToUpperInvariant(), null, null);
        }
    }

    private static string Folder(params (string Path, string Text)[] extra) => SourcesTests.Folder(
        [("notice.shout", "the vents close at dusk"),
         ("rota.md", "# Rota\n\n## Weekend\nTwo people open the yard on Saturdays.\n"),
         .. extra]);

    /// <summary>
    /// Adds the source, opens it to everyone and presses run, as a person
    /// would, then waits for the run to finish. The portal's run is a
    /// background job: asserting before it ends reads an empty index and calls
    /// it a result.
    /// </summary>
    private static async Task<IngestRunRecord> RunFromThePortalAsync(PortalWorld p, string folder)
    {
        using (await p.PostAsync("/portal/sources", p.Admin,
                   [("name", "yard"), ("folder", folder), ("prefix", "yard"), ("chunker", "markdown")])) { }
        using (await p.PostAsync("/portal/permissions/rules", p.Admin,
                   [("source", "filesystem"), ("prefix", "yard"), ("entries", "allow everyone")])) { }
        using (var started = await p.PostAsync("/portal/sources/yard/run", p.Admin))
            Assert.DoesNotContain("error=", started.Headers.Location?.OriginalString ?? "");

        var runs = new IngestRuns(p.World.Db, p.World.Tenant);
        var source = (await new SourceRegistry(p.World.Db, p.World.Tenant).FindAsync("yard"))!;
        IngestRunRecord? run = null;
        for (var i = 0; i < 300 && run is not { Outcome: not IngestRunOutcome.Running }; i++)
        {
            await Task.Delay(100);
            run = await runs.LastAsync(source.Id);
        }
        Assert.Equal(IngestRunOutcome.Completed, run!.Outcome);
        return run;
    }

    [Fact]
    public async Task A_portal_run_reads_only_what_the_built_in_readers_claim()
    {
        await using var p = await PortalWorld.NewAsync(server);

        var run = await RunFromThePortalAsync(p, Folder());

        // The Markdown file is indexed and the other format is counted as not
        // read. This is the control: it is what the portal did before the seam
        // reached it, and it is what a deployment with no extensions still does.
        Assert.Equal(1, run.Summary!.SkippedFormats.GetValueOrDefault(".shout"));
        Assert.Equal(1, await p.ScalarAsync("SELECT count(*) FROM prem_index.document WHERE path LIKE 'yard/%'"));
        Assert.Equal(0, await p.ScalarAsync($"SELECT count(*) FROM prem_index.document WHERE path = '{Notice}'"));
    }

    [Fact]
    public async Task A_portal_run_reads_a_format_a_real_extension_brings()
    {
        // The whole way through, as a deployment does it: the sample extension
        // laid out in a folder, allow-listed by the hash of its bytes, loaded by
        // the host, and its reader handed to the portal by the composition
        // point. The registry-only test beside this one proves the portal
        // passes what it is given; this one proves what it is given is real.
        var host = SampleExtension.LoadedOnce();
        Assert.Contains(SampleExtension.ReaderName, host.Readers.Names);
        Assert.Null(ReaderRegistry.BuiltIn.ForPath("rota" + SampleExtension.ReaderExtension));

        await using var p = await PortalWorld.NewAsync(server, options: new ApiHostOptions { Readers = host.Readers });

        await RunFromThePortalAsync(p, Folder(("rota.csv", "day,who\nSaturday,two people\n")));

        Assert.Equal(2, await p.ScalarAsync("SELECT count(*) FROM prem_index.document WHERE path LIKE 'yard/%'"));
        Assert.Equal(1, await p.ScalarAsync("SELECT count(*) FROM prem_index.document WHERE path = 'yard/rota.csv'"));
        // The extension's reading of it, not some fallback that happened to
        // index the bytes: these words are its doing.
        Assert.Equal(1, await p.ScalarAsync(
            "SELECT count(*) FROM prem_index.chunk c JOIN prem_index.document d ON d.id = c.document_id " +
            "WHERE d.path = 'yard/rota.csv' AND c.content LIKE '%Saturday two people%'"));
    }

    [Fact]
    public async Task A_portal_run_reads_a_format_only_a_registered_reader_claims()
    {
        await using var p = await PortalWorld.NewAsync(
            server, options: new ApiHostOptions { Readers = new ReaderRegistry(new ShoutReader()) });

        await RunFromThePortalAsync(p, Folder());

        Assert.Equal(2, await p.ScalarAsync("SELECT count(*) FROM prem_index.document WHERE path LIKE 'yard/%'"));
        Assert.Equal(1, await p.ScalarAsync($"SELECT count(*) FROM prem_index.document WHERE path = '{Notice}'"));
        // Read by that reader and not by some fallback: the text is its doing.
        Assert.Equal(1, await p.ScalarAsync(
            "SELECT count(*) FROM prem_index.chunk c JOIN prem_index.document d ON d.id = c.document_id " +
            $"WHERE d.path = '{Notice}' AND c.content = 'THE VENTS CLOSE AT DUSK'"));
    }
}

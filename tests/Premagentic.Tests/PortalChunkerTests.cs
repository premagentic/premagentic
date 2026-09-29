using Premagentic.Core.Admin;
using Premagentic.Core.Ingestion;
using Premagentic.Core.Sources.Registry;

namespace Premagentic.Tests;

/// <summary>
/// The chunker on the portal's source pages, in a process that has only the
/// built-in chunker. Every data item is invented. Requires a running Docker
/// daemon.
/// </summary>
public sealed class PortalChunkerTests(DatastoreTestDatabase server) : IClassFixture<DatastoreTestDatabase>
{
    [Fact]
    public async Task The_source_form_names_a_chunker_refuses_one_the_process_lacks_and_run_now_under_one_fails_closed()
    {
        await using var p = await PortalWorld.NewAsync(server);
        var folder = SourcesTests.Folder(("rota.md", "# Rota\n\n## Weekend\nTwo people open the potting shed on Saturdays.\n"));

        Assert.Contains("<option value=\"markdown\" selected>markdown</option>", await p.TextAsync(await p.GetAsync("/portal/sources", p.Admin)));

        using (var refused = await p.PostAsync("/portal/sources", p.Admin,
                   [("name", "shed"), ("folder", folder), ("prefix", "shed"), ("chunker", LineChunker.DefaultName)]))
            Assert.Contains("There%20is%20no%20chunker%20named", refused.Headers.Location?.OriginalString ?? "");
        Assert.Equal(0, await p.ScalarAsync("SELECT count(*) FROM prem_config.source"));

        using (await p.PostAsync("/portal/sources", p.Admin, [("name", "shed"), ("folder", folder), ("prefix", "shed"), ("chunker", "markdown")])) { }
        Assert.Equal(1, await p.ScalarAsync("SELECT count(*) FROM prem_config.source WHERE name = 'shed' AND chunker = 'markdown'"));

        // Another process, which has the line chunker, switches the source to it.
        await new SourceRegistry(p.World.Db, p.World.Tenant, new ChunkerRegistry(new LineChunker()))
            .UpdateAsync("shed", null, null, new AdminActor("cli", "other-process"), LineChunker.DefaultName);
        Assert.Contains("by-line (not in this process: a run fails before it reads anything)",
            await p.TextAsync(await p.GetAsync("/portal/sources/shed", p.Auditor)));

        // The form sends the chunker back unchanged, which is not a change, so the other values still save.
        using (var saved = await p.PostAsync("/portal/sources/shed/set", p.Admin, [("okfBundle", "on"), ("chunker", LineChunker.DefaultName)]))
            Assert.DoesNotContain("error=", saved.Headers.Location?.OriginalString ?? "");
        Assert.Equal(1, await p.ScalarAsync("SELECT count(*) FROM prem_config.source WHERE name = 'shed' AND chunker = 'by-line' AND okf_bundle"));

        using (await p.PostAsync("/portal/permissions/rules", p.Admin, [("source", "filesystem"), ("prefix", "shed"), ("entries", "allow everyone")])) { }
        using (var started = await p.PostAsync("/portal/sources/shed/run", p.Admin))
            Assert.DoesNotContain("error=", started.Headers.Location?.OriginalString ?? "");

        var runs = new IngestRuns(p.World.Db, p.World.Tenant);
        var source = (await new SourceRegistry(p.World.Db, p.World.Tenant).FindAsync("shed"))!;
        IngestRunRecord? run = null;
        for (var i = 0; i < 300 && run is not { Outcome: not IngestRunOutcome.Running }; i++)
        {
            await Task.Delay(100);
            run = await runs.LastAsync(source.Id);
        }
        Assert.Equal((IngestRunOutcome.Failed, LineChunker.DefaultName), (run!.Outcome, run.Chunker));
        Assert.Contains("'by-line'", run.Error);
        Assert.Contains("<dt>Chunker</dt><dd>by-line</dd>", await p.TextAsync(await p.GetAsync("/portal/runs/" + run.Id, p.Auditor)));
        Assert.Equal(0, await p.ScalarAsync("SELECT count(*) FROM prem_index.document WHERE path LIKE 'shed/%'"));

        // Back to one this process has, recorded with the administrator.
        using (var back = await p.PostAsync("/portal/sources/shed/set", p.Admin, [("okfBundle", "on"), ("chunker", "markdown")]))
            Assert.Contains("cuts%2C%20embeds%20and%20stores", back.Headers.Location?.OriginalString ?? "");
        Assert.Equal(1, await p.ScalarAsync(
            $"SELECT count(*) FROM prem_config.admin_event WHERE kind = 'source.set' AND target = 'shed' " +
            $"AND new_value->>'chunker' = 'markdown' AND old_value->>'chunker' = 'by-line' AND actor_user_id = '{p.World.Carol.Id}'"));
    }
}

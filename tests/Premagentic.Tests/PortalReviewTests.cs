using System.Net;
using System.Text.Encodings.Web;
using System.Text.RegularExpressions;
using Premagentic.Core.Admin;
using Premagentic.Core.Embeddings;
using Premagentic.Core.Ingestion;
using Premagentic.Core.Okf;
using Premagentic.Core.Sources.Registry;

namespace Premagentic.Tests;

/// <summary>
/// The review queue page and its count on the health page, through the real
/// API host. Every data item is invented. Requires a running Docker daemon.
/// </summary>
public sealed class PortalReviewTests(DatastoreTestDatabase server) : IClassFixture<DatastoreTestDatabase>
{
    [Fact]
    public async Task The_review_queue_shows_what_waits_for_a_person_and_where_it_lives_and_changes_nothing()
    {
        await using var p = await PortalWorld.NewAsync(server);
        var folder = SourcesTests.Folder(("summary.md",
            "---\ntitle: Summary\ngenerated: { by: summary_agent/1.0, at: 2026-06-01T08:00:00Z }\n---\n\n# Summary\n\n## Week\nThe potting shed restocked on Tuesday.\n"));
        var registry = new SourceRegistry(p.World.Db, p.World.Tenant);
        var desk = await registry.AddAsync("desk", folder, "desk", false, false, new AdminActor("cli", "test-account"));
        await new IngestRuns(p.World.Db, p.World.Tenant).RunAsync(
            new IngestPipeline(p.World.Db, new SeededEmbeddingProvider()), desk.ToFileSystemSource(), desk.Id);
        var count = await new ReviewQueue(p.World.Db, p.World.Tenant).CountAsync();
        Assert.True(count > 2);
        var state = await StateAsync(p);

        foreach (var session in new[] { p.Auditor, p.Admin })
        {
            var page = await p.TextAsync(await p.GetAsync("/portal/review", session));
            Assert.Contains($"<p>{count} document(s).</p>", page);
            Assert.Contains("okf/care/humidity.md", page);
            Assert.DoesNotContain("okf/care/watering.md", page);
            Assert.DoesNotContain("pub/handbook.md", page);
            Assert.Contains("<td>desk</td><td>machine</td><td>unverified</td>", page);
            Assert.Contains($"<td>{HtmlEncoder.Default.Encode(folder)}</td>", page);
            Assert.Contains("(no registered source or recorded run reads this prefix)", page);

            // Not even an administrator gets a form that writes: the only form is the filter, and it reads.
            var main = page[page.IndexOf("<main>", StringComparison.Ordinal)..];
            var forms = Regex.Matches(main, "<form[^>]*>").Select(m => m.Value).ToArray();
            Assert.Equal(["<form method=\"get\" action=\"/portal/review\" class=\"inline\">"], forms);
            Assert.DoesNotContain("prem_antiforgery", main);
        }

        var machineConfirmed = await p.TextAsync(await p.GetAsync("/portal/review?tier=machine-confirmed", p.Auditor));
        Assert.Contains("okf/care/humidity.md", machineConfirmed);
        Assert.DoesNotContain("okf/care/pest-scouting.md", machineConfirmed);
        var bySource = await p.TextAsync(await p.GetAsync("/portal/review?source=desk", p.Auditor));
        Assert.Contains("<p>1 document(s).</p>", bySource);

        using (var post = await p.PostAsync("/portal/review", p.Admin))
            Assert.False(post.IsSuccessStatusCode);

        Assert.Contains($"<a href=\"/portal/review\">{count} document(s) below human-reviewed</a>",
            await p.TextAsync(await p.GetAsync("/portal/health", p.Auditor)));
        Assert.Equal(state, await StateAsync(p));
        using (var member = await p.GetAsync("/portal/review", p.Member))
            Assert.Equal(HttpStatusCode.Forbidden, member.StatusCode);
    }

    /// <summary>What a write anywhere would move: the change record, the runs, and every stored document.</summary>
    private static async Task<(long, long, long, string)> StateAsync(PortalWorld p)
    {
        await using var cmd = p.World.Db.DataSource.CreateCommand(
            "SELECT string_agg(path || '|' || updated_at::text || '|' || trust_tier, ',' ORDER BY path) FROM prem_index.document");
        return (await p.ScalarAsync("SELECT count(*) FROM prem_config.admin_event"),
            await p.ScalarAsync("SELECT count(*) FROM prem_config.ingest_run"),
            await p.ScalarAsync("SELECT count(*) FROM prem_index.chunk"),
            (string)(await cmd.ExecuteScalarAsync())!);
    }
}

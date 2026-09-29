using System.Text.RegularExpressions;
using Premagentic.Core.Admin;
using Premagentic.Core.Embeddings;
using Premagentic.Core.Ingestion;
using Premagentic.Core.Reminders;
using Premagentic.Core.Sources.Registry;

namespace Premagentic.Tests;

/// <summary>
/// The reminders view on the Review page: what the reminders job last
/// computed, per owner, read from what the built-in sink kept. Every path and
/// owner is invented. Requires a running Docker daemon.
/// </summary>
public sealed class PortalRemindersTests(DatastoreTestDatabase server) : IClassFixture<DatastoreTestDatabase>
{
    private static readonly DateTimeOffset Computed = new(2026, 3, 2, 6, 30, 0, TimeSpan.Zero);

    [Fact]
    public async Task Before_the_job_has_run_the_review_page_says_so()
    {
        await using var p = await PortalWorld.NewAsync(server);

        var page = await p.TextAsync(await p.GetAsync("/portal/review", p.Auditor));

        Assert.Contains("<h2>Reminders</h2>", page);
        Assert.Contains("The reminders job has never run", page);
    }

    [Fact]
    public async Task The_latest_run_is_shown_per_owner_and_the_unowned_list_only_to_administrators()
    {
        await using var p = await PortalWorld.NewAsync(server);
        var sink = new TableReminderSink(p.World.Db, p.World.Tenant);
        // An older run first, so the page is shown to read the latest and not any.
        await sink.DeliverAsync([Summary("user:alice", stale: ["desk/old-rota.md"])], Computed.AddDays(-7), CancellationToken.None);
        await sink.DeliverAsync(
        [
            Summary("user:alice", stale: ["desk/rota.md"], inReview: ["desk/summary.md"]),
            Summary("group:Staff", inReview: ["floor/notes.md"]),
            Summary(ReminderSummary.Administrators, unowned: ["loose/leaflet.md"]),
        ], Computed, CancellationToken.None);

        var auditor = await p.TextAsync(await p.GetAsync("/portal/review", p.Auditor));
        Assert.Contains("computed at 2026-03-02 06:30 UTC", auditor);
        Assert.Matches(Row("user:alice", "past its stale date", "desk/rota.md"), auditor);
        Assert.Matches(Row("user:alice", "waiting for review", "desk/summary.md"), auditor);
        Assert.Matches(Row("group:Staff", "waiting for review", "floor/notes.md"), auditor);
        Assert.DoesNotContain("desk/old-rota.md", auditor);
        Assert.DoesNotContain("loose/leaflet.md", auditor);

        var admin = await p.TextAsync(await p.GetAsync("/portal/review", p.Admin));
        Assert.Matches(Row("administrators", "from a source with no owner", "loose/leaflet.md"), admin);
        Assert.Matches(Row("user:alice", "past its stale date", "desk/rota.md"), admin);

        // Nothing on the page sends or runs anything: the only form is the queue's filter.
        var main = admin[admin.IndexOf("<main>", StringComparison.Ordinal)..];
        Assert.Equal(["<form method=\"get\" action=\"/portal/review\" class=\"inline\">"],
            Regex.Matches(main, "<form[^>]*>").Select(m => m.Value));
    }

    [Fact]
    public async Task A_run_of_the_reminders_job_reaches_the_review_page_end_to_end()
    {
        await using var p = await PortalWorld.NewAsync(server, okfBundle: false);
        // A source alice answers for, holding one document past its stale date
        // and one a machine wrote, and a source nobody answers for.
        var actor = new AdminActor("cli", "test-account");
        var registry = new SourceRegistry(p.World.Db, p.World.Tenant);
        var desk = await registry.AddAsync("desk", SourcesTests.Folder(
            ("rota.md", Doc("title: Rota\nstale_after: 2026-01-01T00:00:00Z", "Rota", "Two people open the shop on Saturdays.")),
            ("summary.md", Doc("title: Summary\ngenerated: { by: summary_agent/1.0, at: 2026-06-01T08:00:00Z }", "Summary", "The potting shed restocked on Tuesday."))),
            "desk", false, false, actor);
        await registry.SetOwnerAsync("desk", p.World.Alice.Id, actor);
        var loose = await registry.AddAsync("loose", SourcesTests.Folder(
            ("leaflet.md", Doc("title: Leaflet\nstale_after: 2026-01-01T00:00:00Z", "Leaflet", "Seed trays are two for one."))),
            "loose", false, false, actor);
        var runs = new IngestRuns(p.World.Db, p.World.Tenant);
        var pipeline = new IngestPipeline(p.World.Db, new SeededEmbeddingProvider());
        foreach (var source in new[] { desk, loose }) await runs.RunAsync(pipeline, source.ToFileSystemSource(), source.Id);

        var run = await new RemindersJob(p.World.Db, p.World.Tenant, []).RunAsync(plan: false, CancellationToken.None);
        Assert.True(run.Stale > 0 && run.Unowned > 0, $"the job found {run.Stale} stale and {run.Unowned} unowned");

        var auditor = await p.TextAsync(await p.GetAsync("/portal/review", p.Auditor));
        Assert.Contains($"computed at {Premagentic.Portal.Html.Layout.WhenText(run.ComputedAt)}", auditor);
        Assert.Matches(Row("user:alice", "past its stale date", "desk/rota.md"), auditor);
        Assert.DoesNotContain("loose/leaflet.md", auditor);
        var admin = await p.TextAsync(await p.GetAsync("/portal/review", p.Admin));
        Assert.Matches(Row("administrators", "from a source with no owner", "loose/leaflet.md"), admin);
    }

    [Fact]
    public async Task A_stale_date_nobody_can_read_is_said_as_such_and_not_as_the_year_one()
    {
        await using var p = await PortalWorld.NewAsync(server);
        await new TableReminderSink(p.World.Db, p.World.Tenant).DeliverAsync(
        [
            new ReminderSummary("user:alice",
                [new ReminderItem("desk/garbled.md", null, DateTimeOffset.MinValue), new ReminderItem("desk/rota.md", null, Computed)],
                [], []),
        ], Computed, CancellationToken.None);

        var page = await p.TextAsync(await p.GetAsync("/portal/review", p.Auditor));

        Assert.Matches(new Regex("desk/garbled.md</a></td><td>unreadable date: always stale</td>"), page);
        Assert.DoesNotContain("0001-01-01", page);
        // The control: a date that can be read is shown as a date.
        Assert.Matches(new Regex("desk/rota.md</a></td><td><span class=\"when\">2026-03-02 06:30 UTC</span></td>"), page);
    }

    /// <summary>An invented Markdown document with the given front matter, one heading and one line.</summary>
    private static string Doc(string frontMatter, string title, string line) =>
        $"---\n{frontMatter}\n---\n\n# {title}\n\n## Body\n{line}\n";

    private static ReminderSummary Summary(
        string owner, string[]? stale = null, string[]? inReview = null, string[]? unowned = null) =>
        new(owner, Items(stale), Items(inReview), Items(unowned));

    private static ReminderItem[] Items(string[]? paths) =>
        [.. (paths ?? []).Select(path => new ReminderItem(path, null, Computed.AddDays(-3)))];

    /// <summary>One row of the reminders table, owner, reason and document (with its title, when it has one) in that order.</summary>
    private static Regex Row(string owner, string why, string path) =>
        new($"<tr><td>{Regex.Escape(owner)}</td><td>{Regex.Escape(why)}</td><td><a href=\"[^\"]*\">{Regex.Escape(path)}( [(][^)]*[)])?</a></td>");
}

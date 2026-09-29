using Premagentic.Core.Admin;
using Premagentic.Core.Audit;
using Premagentic.Core.Identity;
using Premagentic.Core.Storage;
using NpgsqlTypes;

namespace Premagentic.Tests;

/// <summary>
/// The usage read model over an invented audit trail: the totals per period
/// with empty periods present, the hosted figure, each caller's use, the most
/// served documents, the content gaps, and the window bound. Requires a
/// running Docker daemon.
/// </summary>
public sealed class UsageQueriesTests(DatastoreTestDatabase server) : IClassFixture<DatastoreTestDatabase>
{
    private static readonly DateTimeOffset March1 = new(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly UsageWindow ThreeDays = new(March1, March1.AddDays(3));

    private const string Leave = "handbook/leave.md";
    private const string Pay = "handbook/pay.md";

    private sealed record World(PremagenticDatabase Db, Guid Tenant, UsageQueries Usage, Guid Alice, Guid Bob, Guid LocalBot, Guid CloudBot)
        : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }

    /// <summary>
    /// Five reads inside the window and two just outside it:
    /// March 1, Alice finds the leave policy, Bob asks about parking and gets nothing;
    /// March 2, a hosted agent is served two passages, a local agent asks about
    /// parking again and gets nothing, Alice fetches a section; March 3, nothing.
    /// </summary>
    private async Task<World> NewAsync()
    {
        var db = new PremagenticDatabase(await server.CreateDatabaseAsync());
        await db.InitializeAsync();
        var tenant = await db.EnsureTenantAsync("t", "T");
        var store = new IdentityStore(db, tenant);
        var alice = await store.CreateUserAsync("alice", "Alice", Role.Member);
        var bob = await store.CreateUserAsync("bob", "Bob", Role.Member);
        var local = await store.CreateAgentAsync("local-bot", bob.Id, AgentMode.ActsForUser, 60, null, ModelLocation.Local);
        var cloud = await store.CreateAgentAsync("cloud-bot", alice.Id, AgentMode.ActsForUser, 60, null, ModelLocation.Hosted, "a vendor");

        await using (var doc = db.DataSource.CreateCommand(
            "INSERT INTO prem_index.document(tenant_id, path, title, content_hash) VALUES(@t, @path, 'Leave', 'h')"))
        {
            doc.Parameters.AddWithValue("t", tenant);
            doc.Parameters.AddWithValue("path", Leave);
            await doc.ExecuteNonQueryAsync();
        }

        var w = new World(db, tenant, new UsageQueries(db, tenant), alice.Id, bob.Id, local.Id, cloud.Id);
        await EventAsync(w, March1.AddHours(10), "search", "leave policy", w.Alice, null, null, Leave);
        await EventAsync(w, March1.AddHours(11), "search", "parking", w.Bob, null, null);
        await EventAsync(w, March1.AddDays(1).AddHours(9), "search", "leave policy", w.Alice, w.CloudBot, "hosted", Leave, Pay);
        await EventAsync(w, March1.AddDays(1).AddHours(9).AddMinutes(5), "search", " Parking ", w.Bob, w.LocalBot, "local");
        await EventAsync(w, March1.AddDays(1).AddHours(9).AddMinutes(10), "section", Pay, w.Alice, null, null, Pay);
        // Outside: a moment before the window opens, and the instant it closes.
        await EventAsync(w, March1.AddTicks(-10), "search", "old question", w.Alice, null, null);
        await EventAsync(w, ThreeDays.To, "search", "late question", w.Bob, null, null);
        return w;
    }

    private static async Task EventAsync(
        World w, DateTimeOffset at, string kind, string query, Guid user, Guid? agent, string? model, params string[] paths)
    {
        var passages = "[" + string.Join(",", paths.Select(p => $$"""{"path": "{{p}}", "heading_path": "", "content_hash": "c"}""")) + "]";
        await using var cmd = w.Db.DataSource.CreateCommand("""
            INSERT INTO prem_config.retrieval_event(
                tenant_id, kind, query, caller_user_id, caller_agent_id, include_historical, passages, elapsed_ms,
                model_location, created_at)
            VALUES(@t, @kind, @query, @user, @agent, false, @passages::jsonb, 1, @model, @at)
            """);
        cmd.Parameters.AddWithValue("t", w.Tenant);
        cmd.Parameters.AddWithValue("kind", kind);
        cmd.Parameters.AddWithValue("query", query);
        cmd.Parameters.AddWithValue("user", user);
        cmd.Parameters.AddWithValue("agent", NpgsqlDbType.Uuid, (object?)agent ?? DBNull.Value);
        cmd.Parameters.AddWithValue("passages", passages);
        cmd.Parameters.AddWithValue("model", NpgsqlDbType.Text, (object?)model ?? DBNull.Value);
        cmd.Parameters.AddWithValue("at", at);
        await cmd.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task The_totals_count_each_day_and_keep_the_empty_one()
    {
        await using var w = await NewAsync();

        var days = await w.Usage.TotalsAsync(ThreeDays, UsagePeriod.Day, hostedOnly: false, default);

        Assert.Equal(
            [
                (March1, new UsageTotals(Questions: 2, People: 2, Agents: 0, NoPassage: 1, PassagesToHostedModels: 0)),
                (March1.AddDays(1), new UsageTotals(Questions: 2, People: 1, Agents: 2, NoPassage: 1, PassagesToHostedModels: 2)),
                (March1.AddDays(2), new UsageTotals(0, 0, 0, 0, 0)),
            ],
            days);
    }

    [Fact]
    public async Task Hosted_only_narrows_every_count_to_what_left_the_network()
    {
        await using var w = await NewAsync();

        var days = await w.Usage.TotalsAsync(ThreeDays, UsagePeriod.Day, hostedOnly: true, default);

        Assert.Equal(
            [
                (March1, new UsageTotals(0, 0, 0, 0, 0)),
                (March1.AddDays(1), new UsageTotals(Questions: 1, People: 0, Agents: 1, NoPassage: 0, PassagesToHostedModels: 2)),
                (March1.AddDays(2), new UsageTotals(0, 0, 0, 0, 0)),
            ],
            days);
        Assert.Equal([w.CloudBot], (await w.Usage.ByCallerAsync(ThreeDays, true, default)).Select(c => c.Id));
        Assert.Equal([(Leave, 1), (Pay, 1)], (await w.Usage.MostServedAsync(ThreeDays, true, 10, default)).Select(d => (d.Path, d.Times)));
        Assert.Empty(await w.Usage.GapsAsync(ThreeDays, true, 10, default));
    }

    [Fact]
    public async Task A_week_starts_on_monday_and_a_month_holds_the_whole_window()
    {
        await using var w = await NewAsync();

        // March 1, 2026 is a Sunday: it belongs to the week of Monday, February 23.
        var weeks = await w.Usage.TotalsAsync(ThreeDays, UsagePeriod.Week, false, default);
        Assert.Equal([(March1.AddDays(-6), 2), (March1.AddDays(1), 2)], weeks.Select(p => (p.PeriodStart, p.Totals.Questions)));

        var months = await w.Usage.TotalsAsync(ThreeDays, UsagePeriod.Month, false, default);
        Assert.Equal([(March1, 4)], months.Select(p => (p.PeriodStart, p.Totals.Questions)));
    }

    [Fact]
    public async Task Each_caller_is_listed_with_their_questions_and_an_agent_with_where_its_model_runs()
    {
        await using var w = await NewAsync();

        var callers = (await w.Usage.ByCallerAsync(ThreeDays, false, default)).ToDictionary(c => c.Name);

        Assert.Equal(["alice", "bob", "cloud-bot", "local-bot"], callers.Keys.Order());
        Assert.Equal(new UsageByCaller("person", w.Alice, "alice", 1, 0, March1.AddDays(1).AddHours(9).AddMinutes(10), null), callers["alice"]);
        Assert.Equal(new UsageByCaller("person", w.Bob, "bob", 1, 1, March1.AddHours(11), null), callers["bob"]);
        Assert.Equal(new UsageByCaller("agent", w.CloudBot, "cloud-bot", 1, 0, March1.AddDays(1).AddHours(9), "hosted"), callers["cloud-bot"]);
        Assert.Equal("local", callers["local-bot"].ModelLocation);
    }

    [Fact]
    public async Task The_most_served_documents_carry_their_title_when_indexed()
    {
        await using var w = await NewAsync();

        var served = await w.Usage.MostServedAsync(ThreeDays, false, 10, default);

        Assert.Equal([new ServedDocument(Leave, "Leave", 2), new ServedDocument(Pay, null, 2)], served);
        Assert.Single(await w.Usage.MostServedAsync(ThreeDays, false, 1, default));
    }

    [Fact]
    public async Task The_gaps_group_one_question_asked_twice_and_leave_out_the_window_edges()
    {
        await using var w = await NewAsync();

        var gap = Assert.Single(await w.Usage.GapsAsync(ThreeDays, false, 10, default));

        Assert.Equal("parking", gap.Query, ignoreCase: true);
        Assert.Equal(2, gap.Times);
        Assert.Equal(March1.AddDays(1).AddHours(9).AddMinutes(5), gap.LastAsked);
    }

    [Fact]
    public async Task The_export_takes_the_same_window_and_filter()
    {
        await using var w = await NewAsync();
        var trail = new AuditTrail(w.Db, w.Tenant);

        var all = new List<string>();
        await foreach (var line in trail.JsonLinesAsync(ThreeDays, false)) all.Add(line);
        var hosted = new List<string>();
        await foreach (var line in trail.JsonLinesAsync(ThreeDays, true)) hosted.Add(line);

        Assert.Equal(5, all.Count);
        Assert.DoesNotContain(all, l => l.Contains("old question") || l.Contains("late question"));
        Assert.Contains("\"hosted\"", Assert.Single(hosted));
    }

    [Fact]
    public async Task A_window_longer_than_366_days_or_backwards_is_refused_everywhere()
    {
        await using var w = await NewAsync();

        var year = new UsageWindow(March1, March1.AddDays(366));
        Assert.Equal(3, (await w.Usage.TotalsAsync(year, UsagePeriod.Day, false, default)).Take(3).Count());

        var tooLong = Assert.Throws<ArgumentException>(() => new UsageWindow(March1, March1.AddDays(400)));
        Assert.Contains("at most 366 days", tooLong.Message);
        Assert.Throws<ArgumentException>(() => new UsageWindow(March1, March1));
        Assert.Throws<ArgumentException>(() => new UsageWindow(March1, March1.AddDays(-1)));

        // A window widened after construction is still refused by every query.
        var widened = ThreeDays with { From = March1.AddDays(-400) };
        await Assert.ThrowsAsync<ArgumentException>(() => w.Usage.TotalsAsync(widened, UsagePeriod.Day, false, default));
        await Assert.ThrowsAsync<ArgumentException>(() => w.Usage.ByCallerAsync(widened, false, default));
        await Assert.ThrowsAsync<ArgumentException>(() => w.Usage.MostServedAsync(widened, false, 10, default));
        await Assert.ThrowsAsync<ArgumentException>(() => w.Usage.GapsAsync(widened, false, 10, default));
        await Assert.ThrowsAsync<ArgumentException>(async () =>
        {
            await foreach (var _ in new AuditTrail(w.Db, w.Tenant).JsonLinesAsync(widened, false)) { }
        });
    }
}

using System.Globalization;
using Premagentic.Core.Audit;
using Premagentic.Portal.Html;
using Microsoft.AspNetCore.Http;

namespace Premagentic.Portal.Pages;

/// <summary>
/// Usage, for the people who may read the audit trail: how much the deployment
/// is asked, by whom, what it served, what it could not answer, and what left
/// the network, over a window of up to 366 days. Everything here is read from
/// the audit trail and nothing on the page writes anything, not even a record
/// that the page was opened.
/// </summary>
internal static class UsagePages
{
    public const string Path = "/portal/usage";

    /// <summary>The longest window, the read model's bound, said to the person when a window passes it.</summary>
    public const int MaxDays = 366;

    internal const int DefaultDays = 30;
    private const int Top = 20;

    /// <summary>
    /// Rows per page of the people and assistants tables, so a deployment with
    /// hundreds of people gets a page that loads and can be read.
    /// </summary>
    public const int PageRows = 50;

    private static readonly (string, string)[] Periods = [("day", "by day"), ("week", "by week"), ("month", "by month")];

    public static async Task<IResult> Show(HttpContext http)
    {
        var r = PortalRequest.Of(http);
        var today = r.Clock.GetUtcNow().UtcDateTime.Date;
        var to = Date(r.Query("to")) ?? today;
        var from = Date(r.Query("from")) ?? to.AddDays(-(DefaultDays - 1));
        var period = r.Query("period") switch { "week" => UsagePeriod.Week, "month" => UsagePeriod.Month, _ => UsagePeriod.Day };
        var hostedOnly = r.Query("hosted") == "yes";
        var name = r.Query("name")?.Trim();

        var picker = Picker(from, to, period, hostedOnly, name);

        var usage = new UsageQueries(r.Db, r.Tenant);
        UsageWindow window;
        IReadOnlyList<(DateTimeOffset PeriodStart, UsageTotals Totals)> totals;
        IReadOnlyList<UsageByCaller> callers;
        IReadOnlyList<ServedDocument> served;
        IReadOnlyList<ContentGap> gaps;
        try
        {
            // The last day is whole: a window to the 24th reads to the end of
            // it. The window refuses a span that is backwards or too long.
            window = new UsageWindow(new DateTimeOffset(from, TimeSpan.Zero), new DateTimeOffset(to.AddDays(1), TimeSpan.Zero));
            totals = await usage.TotalsAsync(window, period, hostedOnly, r.Aborted);
            callers = await usage.ByCallerAsync(window, hostedOnly, r.Aborted);
            served = await usage.MostServedAsync(window, hostedOnly, Top, r.Aborted);
            gaps = await usage.GapsAsync(window, hostedOnly, Top, r.Aborted);
        }
        catch (ArgumentException ex)
        {
            return Layout.Page(r, "Usage", M.H($"""
                {picker}
                <p class="error">{ex.Message} The page reads at most {MaxDays} days at a time, so a year's use is one window
                and a longer span is read a year at a time.</p>
                """));
        }

        var span = $"{Day(from)} to {Day(to)}";
        var people = callers.Where(c => c.Kind == "person").ToArray();
        var agents = callers.Where(c => c.Kind == "agent").ToArray();

        // The figures count everyone in the window; the tables below show the
        // callers whose name holds the filter, a page at a time.
        var link = (string table, int page) => M.Url(Path,
            ("from", Day(from)), ("to", Day(to)), ("period", PeriodKey(period)), ("hosted", hostedOnly ? "yes" : null),
            ("name", name), ("people", table == "people" ? Page(page) : r.Query("people")),
            ("assistants", table == "assistants" ? Page(page) : r.Query("assistants")));
        var (shownPeople, peoplePager) = Paged(Named(people, name), PageOf(r.Query("people")), "people", link);
        var (shownAgents, agentsPager) = Paged(Named(agents, name), PageOf(r.Query("assistants")), "assistants", link);

        return Layout.Page(r, "Usage", M.H($"""
            {picker}
            <p class="note">{span}, UTC{(hostedOnly ? ", hosted-model agents only" : "")}. Read from the audit trail. Nothing on this page changes anything.</p>
            <dl class="figures">
            {Figure("Questions", totals.Sum(t => t.Totals.Questions))}
            {Figure("People", people.Length)}
            {Figure("Assistants", agents.Length)}
            {Figure("No passage found", totals.Sum(t => t.Totals.NoPassage))}
            {Figure("Passages served to hosted-model agents", totals.Sum(t => t.Totals.PassagesToHostedModels))}
            </dl>
            <p class="note">A question is a search; a section fetched by its path counts toward what was served, what went to hosted-model agents and when somebody was last active, not toward questions.
            People are those who asked for themselves; a question through an assistant counts toward assistants.
            Passages served to hosted-model agents went to agents registered as using a model outside the network; an agent registered as local is not counted, whatever model it uses. {Layout.Link(M.Url("/portal/audit", ("left", "yes")), "Each one is in the audit trail")}.</p>
            <h2>{Periods.First(p => p.Item1 == PeriodKey(period)).Item2}</h2>
            {Layout.Table(["From", "Questions", "People", "Assistants", "No passage", "To hosted-model agents"], kind: "numbers",
                rows: totals.Select(t => Layout.Row(Day(t.PeriodStart.UtcDateTime), t.Totals.Questions, t.Totals.People, t.Totals.Agents,
                    t.Totals.NoPassage, t.Totals.PassagesToHostedModels)))}
            <h2>People</h2>
            {Layout.Table(["Person", "Questions", "No passage", "Last active"], kind: "numbers",
                rows: shownPeople.Select(c => Layout.Row(c.Name, c.Questions, c.NoPassage, Layout.When(c.LastActive))))}
            {peoplePager}
            <h2>Assistants</h2>
            {Layout.Table(["Assistant", "Questions", "No passage", "Last active", "Where its model runs"], kind: "numbers",
                rows: shownAgents.Select(c => Layout.Row(c.Removed ? $"{c.Name} (removed)" : c.Name, c.Questions, c.NoPassage, Layout.When(c.LastActive), c.ModelLocation ?? "")))}
            {agentsPager}
            <h2>Most served documents</h2>
            {Layout.Table(["Document", "Title", "Times served"], kind: "paths",
                rows: served.Select(d => Layout.Row(
                    Layout.Link(M.Url("/portal/documents/view", ("path", d.Path)), d.Path), d.Title ?? "", d.Times)))}
            <h2>Asked and not answered</h2>
            <p class="note">Searches that returned no passage to whoever asked. Each one runs as you when you open it, so you see what you would be told.</p>
            {Layout.Table(["Question", "Times asked", "Last asked"],
                rows: gaps.Select(g => Layout.Row(Layout.Link(M.Url("/portal/search", ("q", g.Query)), g.Query), g.Times, Layout.When(g.LastAsked))))}
            """));
    }

    /// <summary>The window and the filter, as a form that reads again with a GET.</summary>
    /// <summary>The callers whose name holds the filter, ignoring case; all of them when there is none.</summary>
    internal static UsageByCaller[] Named(UsageByCaller[] callers, string? name) =>
        string.IsNullOrEmpty(name) ? callers : [.. callers.Where(c => c.Name.Contains(name, StringComparison.OrdinalIgnoreCase))];

    /// <summary>
    /// One page of a table, at most <see cref="PageRows"/> rows, and the line
    /// under it that says which rows these are and links to the pages either
    /// side. A page past the end shows the last one.
    /// </summary>
    internal static (IEnumerable<UsageByCaller> Rows, Markup Pager) Paged(
        UsageByCaller[] callers, int page, string table, Func<string, int, string> link)
    {
        var pages = Math.Max(1, (callers.Length + PageRows - 1) / PageRows);
        page = Math.Clamp(page, 1, pages);
        var rows = callers.Skip((page - 1) * PageRows).Take(PageRows);
        if (pages == 1) return (rows, Markup.Empty);

        var first = (page - 1) * PageRows + 1;
        var last = Math.Min(page * PageRows, callers.Length);
        return (rows, M.H($"""
            <p class="pager">{first} to {last} of {callers.Length}.
            {(page > 1 ? Layout.Link(link(table, page - 1), "Previous") : Markup.Empty)}
            {(page < pages ? Layout.Link(link(table, page + 1), "Next") : Markup.Empty)}</p>
            """));
    }

    private static int PageOf(string? text) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var page) && page > 0 ? page : 1;

    private static string Page(int page) => page.ToString(CultureInfo.InvariantCulture);

    private static Markup Picker(DateTime from, DateTime to, UsagePeriod period, bool hostedOnly, string? name) => M.H($"""
        <form method="get" action="{Path}" class="inline">
        {Layout.Field("From", "from", Day(from), "date")}
        {Layout.Field("To", "to", Day(to), "date")}
        {Layout.Select("Totals", "period", Periods, PeriodKey(period))}
        {Layout.Select("Callers", "hosted", [("", "everyone"), ("yes", "hosted-model agents only")], hostedOnly ? "yes" : "")}
        {Layout.Field("Name holds", "name", name ?? "")}
        <button type="submit">Show</button>
        </form>
        """);

    private static Markup Figure(string label, int value) =>
        M.H($"<div class=\"figure\"><dt>{label}</dt><dd>{value.ToString("N0", CultureInfo.InvariantCulture)}</dd></div>");

    private static string PeriodKey(UsagePeriod period) => period switch
    {
        UsagePeriod.Week => "week",
        UsagePeriod.Month => "month",
        _ => "day",
    };

    internal static DateTime? Date(string? text) =>
        DateTime.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day) ? day : null;

    internal static string Day(DateTime day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}

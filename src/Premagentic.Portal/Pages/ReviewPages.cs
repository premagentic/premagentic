using System.Globalization;
using Premagentic.Core.Okf;
using Premagentic.Core.Reminders;
using Premagentic.Portal.Html;
using Microsoft.AspNetCore.Http;

namespace Premagentic.Portal.Pages;

/// <summary>
/// The review queue: the documents below human-reviewed that a person's
/// sign-off would change, and the folder each lives in, filterable by source
/// and tier. Read only. The page has no form that changes anything: a person
/// reviews the file where it lives and signs it off there, and the next ingest
/// reads the sign-off.
/// <para>
/// Below the queue, what the reminders job last computed for each owner: the
/// documents past their stale date and the documents waiting for review, from
/// the sources that owner answers for. The page shows the job's latest run and
/// nothing more; it neither runs the job nor sends anything.
/// </para>
/// </summary>
internal static class ReviewPages
{
    private const int PageSize = 50;

    public static async Task<IResult> Queue(HttpContext http)
    {
        var r = PortalRequest.Of(http);
        var queue = new ReviewQueue(r.Db, r.Tenant);
        var sources = await r.Sources().ListAsync(r.Aborted);
        var sourceName = r.Query("source");
        var source = sources.FirstOrDefault(s => s.Name.Equals(sourceName, StringComparison.OrdinalIgnoreCase));
        OkfTrustTier? tier = TrustPolicy.TryParseTier(r.Query("tier"), out var t) && t < OkfTrustTier.HumanReviewed ? t : null;
        var page = int.TryParse(r.Query("page"), NumberStyles.None, CultureInfo.InvariantCulture, out var p) && p > 0 ? p : 1;

        var prefix = source is null ? null : source.PathPrefix.Length == 0 ? "" : source.PathPrefix + "/";
        var total = await queue.CountAsync(prefix, tier, r.Aborted);
        var items = await queue.ListAsync(await queue.PlacesAsync(r.Aborted), prefix, tier, PageSize + 1, (page - 1) * PageSize, r.Aborted);
        var now = r.Clock.GetUtcNow();

        var reminders = await Reminders(r);
        var tierKey = tier is { } chosen ? TrustPolicy.TierKey(chosen) : "";
        var next = items.Count > PageSize
            ? Layout.Link(M.Url("/portal/review", ("source", source?.Name), ("tier", tierKey), ("page", (page + 1).ToString(CultureInfo.InvariantCulture))), "Next page")
            : Markup.Empty;

        return Layout.Page(r, "Review queue", M.H($"""
            <p class="note">Machine-written documents below human-reviewed, which agents do not see at the default trust setting, and OKF concepts that nobody has verified.
            Nothing on this page changes anything. Review the file where it lives and sign it off there, with a <code>verified</code> entry by a <code>human:</code> actor;
            the next ingest of its source reads the sign-off.</p>
            <form method="get" action="/portal/review" class="inline">
            {Layout.Select("Source", "source", new[] { ("", "every source") }.Concat(sources.Select(s => (s.Name, s.Name))), source?.Name ?? "")}
            {Layout.Select("Tier", "tier", [("", "below human-reviewed"), (TrustPolicy.TierKey(OkfTrustTier.Unverified), "unverified"), (TrustPolicy.TierKey(OkfTrustTier.MachineConfirmed), "machine-confirmed")], tierKey)}
            <button type="submit">Show</button>
            </form>
            <p>{total} document(s).</p>
            {Layout.Table(["Path", "Source", "Declared authorship", "Tier", "Stale after", "Folder"], kind: "paths",
                rows:
                items.Take(PageSize).Select(i => Layout.Row(
                    Layout.Link(M.Url("/portal/documents/view", ("path", i.Path)), i.Path),
                    i.Source ?? (i.File is null ? "" : "(by hand)"),
                    Authorship(i.Authorship), TrustPolicy.TierKey(i.TrustTier), Stale(i.StaleAfter, now),
                    i.File is { } file ? Path.GetDirectoryName(file) ?? "" : "(no registered source or recorded run reads this prefix)")))}
            {next}
            {reminders}
            """));
    }

    /// <summary>
    /// The latest reminders run, owner by owner. The unowned documents are for
    /// administrators, who are the ones who can give their source an owner.
    /// </summary>
    private static async Task<Markup> Reminders(PortalRequest r)
    {
        var latest = await new ReminderSummaries(r.Db, r.Tenant).LatestAsync(r.Aborted);
        if (latest is not { } run)
            return M.H($"""
                <h2>Reminders</h2>
                <p class="empty">The reminders job has never run, so there is nothing to show. It runs as <code>prem reminders run</code>,
                from Task Scheduler or cron.</p>
                """);

        var rows = new List<Markup>();
        foreach (var summary in run.Summaries.OrderBy(s => s.OwnerPrincipal == ReminderSummary.Administrators).ThenBy(s => s.OwnerPrincipal, StringComparer.Ordinal))
        {
            var owner = summary.OwnerPrincipal == ReminderSummary.Administrators ? "administrators" : summary.OwnerPrincipal;
            rows.AddRange(summary.Stale.Select(i => Item(owner, "past its stale date", i)));
            rows.AddRange(summary.InReview.Select(i => Item(owner, "waiting for review", i)));
            if (r.IsAdministrator)
                rows.AddRange(summary.Unowned.Select(i => Item(owner, "from a source with no owner", i)));
        }

        return M.H($"""
            <h2>Reminders</h2>
            <p class="note">What the reminders job computed at {Layout.WhenText(run.ComputedAt)}, for the owner of each document's source.
            This page shows the last run and sends nothing.{(r.IsAdministrator ? "" : " Documents from a source with no owner are shown to administrators.")}</p>
            {Layout.Table(["Owner", "Reminder", "Document", "Since"], rows, kind: "paths")}
            """);

        static Markup Item(string owner, string why, ReminderItem item) => Layout.Row(
            owner, why,
            Layout.Link(M.Url("/portal/documents/view", ("path", item.Path)), item.Title is { Length: > 0 } title ? $"{item.Path} ({title})" : item.Path),
            // A stale date nobody can read is kept as the earliest instant, so
            // it is stale from the start; said the way the queue above says it.
            item.Since == DateTimeOffset.MinValue ? M.H($"unreadable date: always stale") : Layout.When(item.Since));
    }

    internal static string Authorship(OkfAuthorship authorship) => authorship switch
    {
        OkfAuthorship.Machine => "machine",
        OkfAuthorship.Human => "a person",
        _ => "not declared",
    };

    private static Markup Stale(DateTimeOffset? staleAfter, DateTimeOffset now) => staleAfter switch
    {
        null => M.H($"never"),
        { } s when s == DateTimeOffset.MinValue => M.H($"unreadable date: always stale"),
        { } s => M.H($"{Layout.When(s)}{(s <= now ? " (stale now)" : "")}"),
    };
}

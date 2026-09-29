using System.Globalization;
using System.Text.Json;
using Premagentic.Core.Admin;
using Premagentic.Core.Okf;
using Premagentic.Portal.Html;
using Microsoft.AspNetCore.Http;

namespace Premagentic.Portal.Pages;

/// <summary>
/// The audit trail (who or what asked, when, under which policy, and what came
/// back) and the change record (every administrator change), each newest
/// first, a page at a time.
/// </summary>
internal static class AuditPages
{
    private const int PageSize = 50;

    public static async Task<IResult> Questions(HttpContext http)
    {
        var r = PortalRequest.Of(http);
        // Null shows everything, which is the page as it was; "yes" and "no"
        // are both offered, because "what never left" is as much a question as
        // its opposite when somebody is checking a claim about a folder.
        bool? left = r.Query("left") switch { "yes" => true, "no" => false, _ => null };
        var page = await new AuditTrail(r.Db, r.Tenant).PageAsync(
            PageSize + 1, AuditCursor.Parse(r.Query("before")), leftTheNetwork: left, ct: r.Aborted);
        var users = await UserNamesAsync(r);

        var rows = page.Take(PageSize).Select(q => Layout.Row(
            Layout.WhenExact(q.At), q.Kind,
            M.H($"{Who(q, users)}<br><code>{q.AccessLabel}</code>"),
            q.Heading is null ? q.Query : $"{q.Query} ({q.Heading})",
            Policy(q),
            Where(q),
            M.H($"{q.Passages.Count}{M.Each(q.Passages.Take(5), p => M.H($"<br><code>{p.Path}</code> {p.HeadingPath}"))}")));
        var next = page.Count > PageSize
            ? Layout.Link(
                M.Url("/portal/audit",
                    ("before", new AuditCursor(page[PageSize - 1].At, page[PageSize - 1].Id).ToString()),
                    ("left", r.Query("left"))),
                "Older")
            : Markup.Empty;

        return Layout.Page(r, "Audit", M.H($"""
            <p class="note">Show: {Filter(r, null, "everything")} · {Filter(r, "yes", "what went to hosted-model agents")}
            · {Filter(r, "no", "what did not")}</p>
            {Layout.Table(["When", "Kind", "Who", "Asked", "Policy", "Where the model ran", "Passages served"], rows)}
            {next}
            """));
    }

    /// <summary>
    /// Where the model that asked was running. A read by a person has no model
    /// at all, which is not the same as a model that ran locally, so it is left
    /// blank rather than called either.
    /// </summary>
    private static Markup Where(AuditedQuestion q) => q.ModelLocation switch
    {
        null => M.H($""),
        Premagentic.Core.Identity.ModelLocations.Hosted => M.H($"<strong>to a hosted-model agent</strong>"),
        var other => M.H($"{other}"),
    };

    private static Markup Filter(PortalRequest r, string? value, string label) =>
        r.Query("left") == value || (value is null && r.Query("left") is not "yes" and not "no")
            ? M.H($"<strong>{label}</strong>")
            : Layout.Link(M.Url("/portal/audit", ("left", value)), label);

    public static async Task<IResult> Changes(HttpContext http)
    {
        var r = PortalRequest.Of(http);
        long? before = long.TryParse(r.Query("before"), NumberStyles.None, CultureInfo.InvariantCulture, out var b) ? b : null;
        var page = await new ChangeRecord(r.Db, r.Tenant).ListAsync(PageSize + 1, before, r.Aborted);
        var users = await UserNamesAsync(r);

        var rows = page.Take(PageSize).Select(e => Layout.Row(
            Layout.WhenExact(e.OccurredAt), e.Kind, e.Target, Json(e.OldValue), Json(e.NewValue), Actor(e.Actor, users)));
        var next = page.Count > PageSize
            ? Layout.Link(M.Url("/portal/changes", ("before", page[PageSize - 1].Id.ToString(CultureInfo.InvariantCulture))), "Older")
            : Markup.Empty;

        return Layout.Page(r, "Changes", M.H($"""
            <p class="note">Every administrator change, append only: the database refuses to edit or remove a row.</p>
            {Layout.Table(["When", "Kind", "What", "Before", "After", "By"], rows)}
            {next}
            """));
    }

    internal static async Task<Dictionary<Guid, string>> UserNamesAsync(PortalRequest r) =>
        (await r.Identity().ListUsersAsync(r.Aborted)).ToDictionary(u => u.Id, u => u.SignInName);

    internal static string Json(JsonElement? value) => value is { } v ? v.GetRawText() : "";

    internal static string Actor(AdminActor actor, IReadOnlyDictionary<Guid, string> users) =>
        actor.UserId is { } id
            ? $"{actor.Surface}: {(users.TryGetValue(id, out var name) ? name : id.ToString())}"
            : $"{actor.Surface}: {actor.Account ?? "unknown"}";

    /// <summary>
    /// Who asked. An agent is named as the audit stores it now, and one removed
    /// since is still named, marked removed: the list of live agents no longer
    /// holds it, and its questions stay under its name.
    /// </summary>
    private static string Who(AuditedQuestion q, IReadOnlyDictionary<Guid, string> users) =>
        (q.AgentId, q.UserId) switch
        {
            ({ } agent, { } user) => $"agent {AgentName(q, agent)} for {Name(users, user)}",
            ({ } agent, null) => $"agent {AgentName(q, agent)}",
            (null, { } user) => $"user {Name(users, user)}",
            _ => "not a registered caller",
        };

    private static string AgentName(AuditedQuestion q, Guid id) =>
        $"{q.AgentName ?? id.ToString()}{(q.AgentRemoved ? " (removed)" : "")}";

    private static string Policy(AuditedQuestion q) =>
        q.TrustMinimumTier is not { } tier
            ? "not recorded"
            : $"machine-written from {(Enum.IsDefined((OkfTrustTier)tier) ? TrustPolicy.TierKey((OkfTrustTier)tier) : tier.ToString(CultureInfo.InvariantCulture))}, " +
              $"stale {(q.IncludeStale == true ? "shown" : "left out")}, at {Layout.WhenText(q.PolicyAt)}{(q.IncludeHistorical ? ", historical" : "")}";

    private static string Name(IReadOnlyDictionary<Guid, string> names, Guid id) => names.TryGetValue(id, out var n) ? n : id.ToString();
}

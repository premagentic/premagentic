using Premagentic.Core;
using Premagentic.Core.Identity;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Premagentic.Portal.Html;

/// <summary>The page shell, the navigation, and the pieces every page is built from.</summary>
internal static class Layout
{
    /// <summary>The form field the host reads the anti-forgery token from.</summary>
    public const string AntiForgeryField = "prem_antiforgery";

    /// <summary>
    /// Same origin for everything, nothing inline, no framing, no plugins. The
    /// pages load their one stylesheet and one script from the portal itself, so
    /// they work on a network with no route out. <c>media-src</c> is there for
    /// the sign-in page's logo loop, which is served from the assembly like
    /// every other asset; without it the default of none blocks the video.
    /// </summary>
    public const string ContentSecurityPolicy =
        "default-src 'none'; script-src 'self'; style-src 'self'; img-src 'self'; font-src 'self'; connect-src 'self'; " +
        "media-src 'self'; form-action 'self'; frame-ancestors 'none'; base-uri 'none'";

    /// <summary>
    /// The navigation, in the groups the sidebar shows. The role filter decides
    /// which links a person sees, group by group; a group with nothing left in
    /// it is not drawn.
    /// </summary>
    private static readonly (string Group, (string Path, string Label, PortalNeed Need)[] Links)[] Navigation =
    [
        ("Search", [
            ("/portal/search", "Search", PortalNeed.Person),
            ("/portal/connect", "Connect an assistant", PortalNeed.Person)]),
        ("People", [
            ("/portal/users", "Users", PortalNeed.Reader),
            ("/portal/groups", "Groups", PortalNeed.Reader),
            ("/portal/agents", "Agents", PortalNeed.Reader)]),
        ("Content", [
            ("/portal/sources", "Sources", PortalNeed.Reader),
            ("/portal/documents", "Documents", PortalNeed.Reader),
            ("/portal/review", "Review", PortalNeed.Reader)]),
        ("Access", [("/portal/permissions", "Permissions", PortalNeed.Reader)]),
        ("Settings", [
            ("/portal/settings", "Settings", PortalNeed.Reader),
            ("/portal/tuning", "Tuning", PortalNeed.Reader)]),
        ("Records", [
            ("/portal/usage", "Usage", PortalNeed.Reader),
            ("/portal/audit", "Audit", PortalNeed.Reader),
            ("/portal/changes", "Changes", PortalNeed.Reader),
            ("/portal/health", "Health", PortalNeed.Reader),
            ("/portal/export", "Export", PortalNeed.Reader)]),
    ];

    /// <summary>
    /// The authorization flow's two pages, which join the Access group only
    /// while the flow is on; while it is off they do not exist, and no page
    /// points at them.
    /// </summary>
    private static readonly (string Path, string Label, PortalNeed Need)[] FlowLinks =
        [(OAuthPaths.Clients, "Clients", PortalNeed.Reader), (OAuthPaths.Grants, "Grants", PortalNeed.Reader)];

    private static (string Group, (string Path, string Label, PortalNeed Need)[] Links)[] NavigationFor(bool flowOn) =>
        !flowOn ? Navigation : Navigation.Select(g => g.Group == "Access" ? (Group: g.Group, Links: g.Links.Concat(FlowLinks).ToArray()) : g).ToArray();

    /// <summary>Whether the host registered the authorization flow, which it does only while the flow is on.</summary>
    internal static bool FlowOn(HttpContext http) => http.RequestServices.GetService<OAuthDeployment>() is not null;

    /// <summary>Every portal response says: no caching, no sniffing, no framing, no referrer, and the policy above.</summary>
    public static void SecurityHeaders(HttpResponse response)
    {
        response.Headers.ContentSecurityPolicy = ContentSecurityPolicy;
        response.Headers.XContentTypeOptions = "nosniff";
        response.Headers.XFrameOptions = "DENY";
        response.Headers["Referrer-Policy"] = "no-referrer";
        response.Headers.CacheControl = "no-store";
    }

    public static IResult Page(PortalRequest r, string title, Markup body, int status = StatusCodes.Status200OK) =>
        Html(Document(title, Header(r), Notices(r), body, r.Person.AntiForgeryToken,
            groupLabel: GroupLabel(r.Http.Request.Path.Value ?? "")), status);

    /// <summary>A page for a request the portal refuses, with the reason.</summary>
    public static IResult Refused(PortalRequest? r, int status, string why) =>
        Html(Document("Not allowed", r is null ? Markup.Empty : Header(r), Markup.Empty,
            M.H($"<p class=\"error\">{why}</p>"), r?.Person.AntiForgeryToken), status);

    public static IResult Html(Markup document, int status = StatusCodes.Status200OK) =>
        Results.Content(document.ToString(), "text/html; charset=utf-8", statusCode: status);

    /// <summary>
    /// Every page the portal draws, signed in or not, the sign-in page and a
    /// refusal included. Each ends with a link to this program's source.
    /// </summary>
    /// <param name="bodyClass">A class on the body, for a page with its own shape, such as sign-in.</param>
    /// <param name="heading">False when the page draws its own heading inside its content.</param>
    /// <param name="groupLabel">The navigation group this page belongs to, shown above its title.</param>
    public static Markup Document(string title, Markup header, Markup notices, Markup body, string? antiForgeryToken,
        string? bodyClass = null, bool heading = true, Markup groupLabel = default) => M.H($"""
        <!DOCTYPE html>
        <html lang="en">
        <head>
        <meta charset="utf-8">
        <meta name="viewport" content="width=device-width, initial-scale=1">
        <meta name="prem-antiforgery" content="{antiForgeryToken ?? ""}">
        <title>{title}: PremAgentic</title>
        <link rel="icon" href="/portal/assets/favicon.png">
        <link rel="preload" href="/portal/assets/Geist-Variable.woff2" as="font" type="font/woff2" crossorigin>
        <link rel="preload" href="/portal/assets/PixelifySans-Variable.woff2" as="font" type="font/woff2" crossorigin>
        <link rel="stylesheet" href="/portal/assets/portal.css">
        <script src="/portal/assets/portal.js" defer></script>
        </head>
        <body{(bodyClass is null ? Markup.Empty : M.H($" class=\"{bodyClass}\""))}>
        <div class="frame">
        {header}
        <main>
        {groupLabel}
        {(heading ? M.H($"<h1>{title}</h1>") : Markup.Empty)}
        {notices}
        {body}
        <footer class="source"><a href="{BuildVersion.SourceUrl}">Source</a></footer>
        </main>
        </div>
        </body>
        </html>
        """);

    /// <summary>
    /// The sidebar: the mark and the name, the navigation in its groups with
    /// the current page marked, and at the foot the signed-in person and the
    /// way out. Below 900 px the stylesheet lays the same links out as a top
    /// strip, so nothing is hidden and nothing needs a script to reach.
    /// </summary>
    private static Markup Header(PortalRequest r)
    {
        var here = r.Http.Request.Path.Value ?? "";
        var groups = NavigationFor(FlowOn(r.Http))
            .Select(group => (group.Group, Links: group.Links.Where(n => PortalRequest.Allows(r.Person.User.Role, n.Need)).ToArray()))
            .Where(group => group.Links.Length > 0)
            .Select((group, index) => M.H(
                $"<div class=\"group\"><span class=\"label\"><span class=\"num\">{index + 1:00}</span>{group.Group.ToUpperInvariant()}</span>{M.Each(group.Links, Link)}</div>"));

        Markup Link((string Path, string Label, PortalNeed Need) n) => M.H(
            $"<a href=\"{n.Path}\"{(IsHere(here, n.Path) ? M.H($" class=\"current\" aria-current=\"page\"") : Markup.Empty)}>{n.Label}</a>");

        return M.H($"""
            <div class="side">
            <a class="brand" href="/portal/search"><span class="markbox"><img class="mark" src="/portal/assets/logo-mark.png" alt="" width="28" height="28"></span><span>PremAgentic</span></a>
            <nav>{Markup.Join(groups)}</nav>
            <div class="who"><span class="name">{r.Person.User.Name}</span><span class="role">{RoleName(r.Person.User.Role)}</span>
            <button type="button" class="link" data-sign-out>Sign out</button></div>
            </div>
            """);
    }

    /// <summary>
    /// The navigation group the page being shown belongs to, numbered as the
    /// sidebar numbers it, for the line above the page's title.
    /// </summary>
    private static Markup GroupLabel(string here)
    {
        // With the flow's links always: its pages are reached only while it is on.
        var navigation = NavigationFor(flowOn: true);
        for (var index = 0; index < navigation.Length; index++)
            if (navigation[index].Links.Any(n => IsHere(here, n.Path)))
                return M.H($"<p class=\"label group-label\"><span class=\"num\">{index + 1:00}</span>{navigation[index].Group.ToUpperInvariant()}</p>");
        return Markup.Empty;
    }

    /// <summary>True when the page being shown belongs to this navigation entry.</summary>
    private static bool IsHere(string here, string path) =>
        here.Equals(path, StringComparison.Ordinal)
        || (here.StartsWith(path, StringComparison.Ordinal) && here.Length > path.Length && here[path.Length] == '/');

    /// <summary>The outcome of the last change, carried in the query of the redirect after it. Always shown as text.</summary>
    private static Markup Notices(PortalRequest r) =>
        (r.Query("done"), r.Query("error")) switch
        {
            (_, { } error) => M.H($"<p class=\"error\">{error}</p>"),
            ({ } done, _) => M.H($"<p class=\"done\">{done}</p>"),
            _ => Markup.Empty,
        };

    /// <summary>
    /// A form that changes state, for administrators only: anyone else sees
    /// nothing. It posts to the portal and carries the anti-forgery token.
    /// </summary>
    /// <param name="danger">The button reads as a destructive action.</param>
    /// <param name="secondary">The button reads as the quieter of two actions beside each other.</param>
    public static Markup Form(PortalRequest r, string action, Markup fields, string submit, bool danger = false, bool secondary = false) =>
        !r.IsAdministrator ? Markup.Empty : OwnForm(r, action, fields, submit, danger, secondary);

    /// <summary>
    /// A form any signed-in person may submit, for a change to what is theirs,
    /// such as their own agents. The endpoint it posts to decides whose the
    /// thing is; the form only carries the anti-forgery token like every other.
    /// </summary>
    public static Markup OwnForm(PortalRequest r, string action, Markup fields, string submit, bool danger = false, bool secondary = false) =>
        M.H($"""
            <form method="post" action="{action}" class="inline">
            <input type="hidden" name="{AntiForgeryField}" value="{r.Person.AntiForgeryToken ?? ""}">
            {fields}
            <button type="submit"{Kind(danger, secondary)}>{submit}</button>
            </form>
            """);

    /// <summary>
    /// A form for administrators that can send a file, as multipart/form-data,
    /// with the anti-forgery token like every other; anyone else sees nothing.
    /// </summary>
    public static Markup UploadForm(PortalRequest r, string action, Markup fields, string submit) =>
        !r.IsAdministrator ? Markup.Empty : M.H($"""
            <form method="post" action="{action}" enctype="multipart/form-data">
            <input type="hidden" name="{AntiForgeryField}" value="{r.Person.AntiForgeryToken ?? ""}">
            {fields}
            <button type="submit">{submit}</button>
            </form>
            """);

    private static Markup Kind(bool danger, bool secondary) => (danger, secondary) switch
    {
        (true, _) => M.H($" class=\"danger\""),
        (_, true) => M.H($" class=\"secondary\""),
        _ => Markup.Empty,
    };

    public static Markup Field(string label, string name, string value = "", string type = "text", bool required = false) =>
        M.H($"<label>{label} <input type=\"{type}\" name=\"{name}\" value=\"{value}\"{(required ? M.H($" required") : Markup.Empty)} autocomplete=\"off\"></label>");

    public static Markup Hidden(string name, string value) => M.H($"<input type=\"hidden\" name=\"{name}\" value=\"{value}\">");

    public static Markup Select(string label, string name, IEnumerable<(string Value, string Text)> options, string? selected = null) =>
        M.H($"<label>{label} <select name=\"{name}\">{M.Each(options, o => M.H($"<option value=\"{o.Value}\"{(o.Value == selected ? M.H($" selected") : Markup.Empty)}>{o.Text}</option>"))}</select></label>");

    public static Markup Check(string label, string name, bool isChecked = false) =>
        M.H($"<label class=\"check\"><input type=\"checkbox\" name=\"{name}\" value=\"on\"{(isChecked ? M.H($" checked") : Markup.Empty)}> {label}</label>");

    /// <param name="kind">
    /// How the stylesheet should set this table's cells: <c>paths</c> for a
    /// table of paths and folders, <c>numbers</c> for one whose later columns
    /// are counts, <c>wraps</c> for one holding long stored values. The cells
    /// themselves stay bare, since what they hold is read by tests.
    /// </param>
    public static Markup Table(IEnumerable<string> headings, IEnumerable<Markup> rows, string? kind = null)
    {
        var body = rows.ToArray();
        return body.Length == 0
            ? M.H($"<p class=\"empty\">None.</p>")
            : M.H($"<table{(kind is null ? Markup.Empty : M.H($" class=\"{kind}\""))}><thead><tr>{M.Each(headings, h => M.H($"<th>{h}</th>"))}</tr></thead><tbody>{Markup.Join(body)}</tbody></table>");
    }

    public static Markup Row(params object?[] cells) =>
        M.H($"<tr>{M.Each(cells, c => c is Markup m ? M.H($"<td>{m}</td>") : M.H($"<td>{c}</td>"))}</tr>");

    public static Markup Link(string href, string text) => M.H($"<a href=\"{href}\">{text}</a>");

    /// <summary>A link whose text is already markup, such as a moment.</summary>
    public static Markup Link(string href, Markup text) => M.H($"<a href=\"{href}\">{text}</a>");

    public static string RoleName(Role role) => role.ToString().ToLowerInvariant();

    /// <summary>
    /// A moment as a page shows it: to the minute, always UTC, always the same
    /// shape, and in the face identifiers and numbers are set in.
    /// </summary>
    public static Markup When(DateTimeOffset? instant) =>
        instant is { } t ? M.H($"<span class=\"when\">{WhenText(t)}</span>") : Markup.Empty;

    /// <summary>
    /// A moment where the order of events is the point: the same shape, to the
    /// second, so two entries in one minute can be told apart. For the tables
    /// that are a record of what happened, in the order it happened.
    /// </summary>
    public static Markup WhenExact(DateTimeOffset? instant) => instant is { } t
        ? M.H($"<span class=\"when\">{t.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss 'UTC'", System.Globalization.CultureInfo.InvariantCulture)}</span>")
        : Markup.Empty;

    /// <summary>The same moment as plain words, for a sentence rather than a cell.</summary>
    public static string WhenText(DateTimeOffset? instant) =>
        instant is { } t ? t.ToUniversalTime().ToString("yyyy-MM-dd HH:mm 'UTC'", System.Globalization.CultureInfo.InvariantCulture) : "";

    /// <summary>A redirect after a change, carrying its outcome to show as text on the next page.</summary>
    public static IResult After(string path, string? done = null, string? error = null) =>
        Results.Redirect(M.Url(path, ("done", done), ("error", error)));
}

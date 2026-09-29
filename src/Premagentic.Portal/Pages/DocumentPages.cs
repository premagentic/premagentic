using System.Globalization;
using Premagentic.Core.Admin;
using Premagentic.Core.Identity;
using Premagentic.Core.Ingestion;
using Premagentic.Core.Okf;
using Premagentic.Core.Security.Acl;
using Premagentic.Core.Sources.Registry;
using Premagentic.Portal.Html;
using Microsoft.AspNetCore.Http;

namespace Premagentic.Portal.Pages;

/// <summary>
/// The documents in the index, as an administrator needs to see them: path,
/// hash, lifecycle, trust tier, authorship, stale date, the access list and the
/// chunk count, and for a source, what its last run did not read. Never the
/// text: that reaches people only through search, under their own rights.
/// </summary>
internal static class DocumentPages
{
    private const int PageSize = 50;

    public static async Task<IResult> List(HttpContext http)
    {
        var r = PortalRequest.Of(http);
        var sources = await new SourceRegistry(r.Db, r.Tenant).ListAsync(r.Aborted);
        var sourceName = r.Query("source");
        var source = sources.FirstOrDefault(s => s.Name.Equals(sourceName, StringComparison.OrdinalIgnoreCase));
        var contains = r.Query("contains");
        var page = int.TryParse(r.Query("page"), NumberStyles.None, CultureInfo.InvariantCulture, out var p) && p > 0 ? p : 1;

        var prefix = source is null ? null : source.PathPrefix.Length == 0 ? "" : source.PathPrefix + "/";
        var documents = await new DocumentCatalog(r.Db, r.Tenant).ListAsync(prefix, contains, PageSize + 1, (page - 1) * PageSize, r.Aborted);
        var now = r.Clock.GetUtcNow();

        var notRead = Markup.Empty;
        if (source is not null && await new IngestRuns(r.Db, r.Tenant).LastAsync(source.Id, r.Aborted) is { Summary: { } s } last)
            notRead = M.H($"""
                <h2>What the last run of '{source.Name}' did not read</h2>
                <p class="note">From the run started {Layout.When(last.StartedAt)}. {s.UndeclaredAuthorship} concept(s) declare no author{(source.OkfBundle ? "" : " (the source is not read as a bundle, so none is held to one)")}.
                Whether each document declares one is not stored per document; this count is the run's.</p>
                {(s.Skipped == 0
                    ? M.H($"<p>Skipped: nothing.</p>")
                    : Layout.Table(["Skipped, by why", "Files"], s.SkippedFormats.OrderByDescending(k => k.Value).Select(k => Layout.Row(k.Key, k.Value))))}
                {(s.Unreadable == 0
                    ? M.H($"<p>Could not read: nothing.</p>")
                    : Layout.Table(["Could not read", "Reason"], s.FailureList.Select(f => Layout.Row(f.Path, f.Reason))))}
                """);

        var next = documents.Count > PageSize
            ? Layout.Link(M.Url("/portal/documents", ("source", source?.Name), ("contains", contains), ("page", (page + 1).ToString(CultureInfo.InvariantCulture))), "Next page")
            : Markup.Empty;

        return Layout.Page(r, "Documents", M.H($"""
            <form method="get" action="/portal/documents" class="inline">
            {Layout.Select("Source", "source", new[] { ("", "every source") }.Concat(sources.Select(x => (x.Name, x.Name))), source?.Name ?? "")}
            {Layout.Field("Path contains", "contains", contains ?? "")}
            <button type="submit">Show</button>
            </form>
            {notRead}
            {Layout.Table(["Path", "Lifecycle", "Trust", "Authorship", "Stale after", "Concept", "Access list", "Chunks", "Hash"], kind: "paths",
                rows:
                documents.Take(PageSize).Select(d => Layout.Row(
                    Layout.Link(M.Url("/portal/documents/view", ("path", d.Path)), d.Path), d.LifecycleStatus,
                    TrustPolicy.TierKey(d.TrustTier), d.Authorship.ToString().ToLowerInvariant(), Stale(d, now), d.ConceptId ?? "",
                    d.AclSetId is { } id ? $"#{id}{(d.AclFromRule ? " (rule)" : "")}" : "none", d.Chunks, d.ContentHash[..12])))}
            {next}
            """));
    }

    public static async Task<IResult> Show(HttpContext http)
    {
        var r = PortalRequest.Of(http);
        var path = r.Query("path");
        var document = path is null ? null : await new DocumentCatalog(r.Db, r.Tenant).FindAsync(path, r.Aborted);
        if (document is null) return Layout.Refused(r, StatusCodes.Status404NotFound, "That path is not in the index.");

        var names = new PrincipalNames(r.Identity());
        var access = document.AclText is { } text && AclSet.TryParseCanonical(text, out var set)
            ? await names.DescribeAsync(set, r.Aborted)
            : "none: nobody may read it";

        return Layout.Page(r, document.Path, M.H($"""
            <dl class="facts">
            <dt>Title</dt><dd>{document.Title ?? ""}</dd>
            <dt>Source</dt><dd>{document.SourceName ?? ""}</dd>
            <dt>Content hash</dt><dd><code>{document.ContentHash}</code></dd>
            <dt>Lifecycle</dt><dd>{document.LifecycleStatus}</dd>
            <dt>Trust tier</dt><dd>{TrustPolicy.TierKey(document.TrustTier)}</dd>
            <dt>Authorship</dt><dd>{document.Authorship.ToString().ToLowerInvariant()}</dd>
            <dt>Stale after</dt><dd>{Stale(document, r.Clock.GetUtcNow())}</dd>
            <dt>Concept id</dt><dd>{document.ConceptId ?? ""}</dd>
            <dt>Access list</dt><dd>{(document.AclSetId is { } id ? $"#{id}, " : "")}{access}{(document.AclFromRule ? " (from a folder rule)" : "")}</dd>
            <dt>Chunks</dt><dd>{document.Chunks}</dd>
            <dt>Last stored</dt><dd>{Layout.When(document.UpdatedAt)}</dd>
            </dl>
            <p>{Layout.Link(M.Url("/portal/permissions/why", ("path", document.Path)), "Why can or cannot someone read this?")}</p>
            """));
    }

    private static Markup Stale(CatalogDocument d, DateTimeOffset now) => d.StaleAfter switch
    {
        null => M.H($"never"),
        { } t when t == DateTimeOffset.MinValue => M.H($"unreadable date: always stale"),
        { } t => M.H($"{Layout.When(t)}{(t <= now ? " (stale now)" : "")}"),
    };
}

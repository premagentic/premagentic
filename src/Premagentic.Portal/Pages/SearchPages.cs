using Premagentic.Core;
using Premagentic.Core.Identity;
using Premagentic.Core.Okf;
using Premagentic.Core.Retrieval;
using Premagentic.Portal.Html;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Premagentic.Portal.Pages;

/// <summary>
/// The search page for people: a search as the signed-in person, with that
/// person's rights and trust policy, and what each passage is on every hit.
/// </summary>
internal static class SearchPages
{
    public const int TopK = 10;

    public static async Task<IResult> Search(HttpContext http)
    {
        var r = PortalRequest.Of(http);
        var query = r.Query("q");
        var historical = r.Query("historical") == "on";

        var results = Markup.Empty;
        if (query is not null)
        {
            var policy = await CallerPolicy.TrustAsync(r.Db, r.Tenant, r.Person.Caller, r.Aborted);
            try
            {
                var result = await http.RequestServices.GetRequiredService<HybridSearch>().SearchAsync(
                    r.Tenant, query, new SearchOptions(r.Person.Caller.Scope, TopK, historical, Trust: policy), r.Aborted);
                results = Hits(result.Hits);
            }
            catch (QueryTooLongException tooLong)
            {
                results = M.H($"<p class=\"error\">{tooLong.Message}</p>");
            }
        }

        return Layout.Page(r, "Search", M.H($"""
            <div class="window">
            <div class="titlebar"><span>Question</span><span>Search</span></div>
            <form method="get" action="/portal/search" class="inline">
            {Layout.Field("Question", "q", query ?? "", required: true)}
            {Layout.Check("Include superseded and archived", "historical", historical)}
            <button type="submit">Search</button>
            </form>
            </div>
            {results}
            """));
    }

    /// <summary>Hits as a list: citation, lifecycle, and the four fields that say what the passage is.</summary>
    public static Markup Hits(IReadOnlyList<SearchHit> hits) =>
        hits.Count == 0
            ? M.H($"<p class=\"empty\">Nothing this caller may read matches.</p>")
            : M.Each(hits, h => M.H($"""
                <div class="hit">
                <div class="citation"><span class="path">{h.Path}</span>{(string.IsNullOrEmpty(h.HeadingPath) ? Markup.Empty : M.H($" § {h.HeadingPath}"))}</div>
                <div class="passage">{Snippet(h.Content)}</div>
                <div class="fields">{Fields(h.TrustTier, h.Authorship, h.Stale, h.ConceptId)} <span class="tag lifecycle-{h.LifecycleStatus.ToLowerInvariant()}{Quiet(h.LifecycleStatus.Equals("active", StringComparison.OrdinalIgnoreCase))}">{h.LifecycleStatus}</span></div>
                </div>
                """));

    /// <summary>
    /// The four fields that say what a passage is, as badges. The text is what
    /// it always was; the class carries the color code, the same on every page
    /// a badge appears.
    /// </summary>
    public static Markup Fields(OkfTrustTier tier, OkfAuthorship authorship, bool stale, string? conceptId) => M.H($"""
        <span class="tag trust-{Trust(tier)}{Quiet(Trust(tier) == "unverified")}">trust: {Trust(tier)}</span>
        <span class="tag authorship-{Author(authorship)}{Quiet(Author(authorship) != "machine")}">authorship: {Author(authorship)}</span>
        <span class="tag stale-{(stale ? "yes" : "no")}{Quiet(!stale)}">stale: {(stale ? "yes" : "no")}</span>
        <span class="tag{Quiet(conceptId is null)}">concept: {conceptId ?? "-"}</span>
        """);

    // A value that means "nothing to see" is quiet: no box, no color. Only a
    // state worth acting on carries one.
    private static Markup Quiet(bool quiet) => quiet ? M.H($" quiet") : Markup.Empty;

    private static string Author(OkfAuthorship authorship) => authorship.ToString().ToLowerInvariant();

    private static string Trust(OkfTrustTier tier) => Enum.IsDefined(tier) ? TrustPolicy.TierKey(tier) : "unverified";

    private static string Snippet(string content)
    {
        var line = content.ReplaceLineEndings(" ").Trim();
        return line.Length <= 320 ? line : line[..320] + "...";
    }
}

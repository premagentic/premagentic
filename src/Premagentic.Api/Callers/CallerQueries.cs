using Premagentic.Core;
using Premagentic.Core.Identity;
using Premagentic.Core.Retrieval;
using Premagentic.Core.Storage;

namespace Premagentic.Api.Callers;

/// <summary>
/// Search and section fetch as one resolved caller, for the HTTP endpoints and
/// the MCP tools alike: the caller's own scope, and the trust policy for what
/// the caller is, a person or an agent.
/// </summary>
public sealed class CallerQueries(HybridSearch search, SectionFetcher sections, PremagenticDatabase db, Deployment deployment)
{
    public const int MaxTopK = 10;

    public async Task<SearchResult> SearchAsync(
        Caller caller, string query, int topK, bool includeHistorical, CancellationToken ct)
    {
        var options = await OptionsAsync(caller, includeHistorical, ct);
        return await search.SearchAsync(deployment.TenantId, query, options with { TopK = Math.Clamp(topK, 1, MaxTopK) }, ct);
    }

    public async Task<DocumentSectionResult?> SectionAsync(
        Caller caller, string path, string? heading, bool includeHistorical, CancellationToken ct) =>
        await sections.GetAsync(deployment.TenantId, await OptionsAsync(caller, includeHistorical, ct), path, heading, ct);

    private async Task<SearchOptions> OptionsAsync(Caller caller, bool includeHistorical, CancellationToken ct)
    {
        // The middleware refuses these before an endpoint runs; this is the
        // second line, so a refused caller can never reach a read.
        if (!caller.IsResolved)
            throw new InvalidOperationException("A caller that did not resolve may not read anything.");

        var trust = await CallerPolicy.TrustAsync(db, deployment.TenantId, caller, ct);
        return new SearchOptions(caller.Scope, IncludeHistorical: includeHistorical, Trust: trust);
    }
}

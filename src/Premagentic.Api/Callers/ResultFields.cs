using System.Text;
using Premagentic.Core;
using Premagentic.Core.Okf;
using Premagentic.Core.Retrieval;

namespace Premagentic.Api.Callers;

/// <summary>
/// How results leave the API. Every hit and every section says what it is:
/// its trust tier by the spec's name, who wrote it, whether it is stale, and
/// its concept id, whatever policy let it through.
/// </summary>
internal static class ResultFields
{
    /// <summary>What an agent is told the four fields mean, in both tool descriptions.</summary>
    public const string FieldsHelp =
        "Every result says what it is. trust: how far the passage has been confirmed, one of unverified, " +
        "machine-confirmed or human-reviewed. authorship: who wrote it, human, machine, or unknown for an " +
        "ordinary file that does not say. stale: yes when the passage is past the date its author said it " +
        "goes stale. concept: its concept id when it comes from a knowledge bundle, otherwise '-'. Treat a " +
        "machine-written, unverified or stale passage as a lead to check, not as a settled fact.";

    public static string Trust(OkfTrustTier tier) => Enum.IsDefined(tier) ? TrustPolicy.TierKey(tier) : "unverified";

    public static string Authorship(OkfAuthorship authorship) => authorship switch
    {
        OkfAuthorship.Human => "human",
        OkfAuthorship.Machine => "machine",
        _ => "unknown",
    };

    public static object Hit(SearchHit hit) => new
    {
        citation = hit.Citation,
        path = hit.Path,
        title = hit.Title,
        headingPath = hit.HeadingPath,
        content = hit.Content,
        lifecycleStatus = hit.LifecycleStatus,
        fusedScore = hit.FusedScore,
        contentHash = hit.ContentHash,
        trustTier = Trust(hit.TrustTier),
        authorship = Authorship(hit.Authorship),
        stale = hit.Stale,
        conceptId = hit.ConceptId,
    };

    public static object Section(DocumentSectionResult section) => new
    {
        path = section.Path,
        title = section.Title,
        lifecycleStatus = section.LifecycleStatus,
        lifecycleGated = section.LifecycleGated,
        trustTier = Trust(section.TrustTier),
        authorship = Authorship(section.Authorship),
        stale = section.Stale,
        conceptId = section.ConceptId,
        // A lifecycle-gated section is looked up and never read, so it has no chunks.
        chunks = section.Chunks.Select(c => new { headingPath = c.HeadingPath, content = c.Content }).ToArray(),
    };

    /// <summary>The four fields on one line, as the MCP tools print them.</summary>
    public static string Line(string lifecycle, OkfTrustTier tier, OkfAuthorship authorship, bool stale, string? conceptId) =>
        $"lifecycle: {lifecycle} | trust: {Trust(tier)} | authorship: {Authorship(authorship)} | " +
        $"stale: {(stale ? "yes" : "no")} | concept: {conceptId ?? "-"}";

    public static string SearchText(SearchResult result)
    {
        if (result.Hits.Count == 0)
            return "No results. Nothing this caller is authorized to read has evidence for this query" +
                   (result.IncludeHistorical ? "." : " (historical content was excluded; retry with includeHistorical=true only if the user wants history).");

        var sb = new StringBuilder();
        sb.AppendLine($"{result.Hits.Count} results ({result.ElapsedMs} ms, historical={result.IncludeHistorical}):");
        foreach (var hit in result.Hits)
        {
            sb.AppendLine();
            sb.AppendLine($"--- [{hit.FusedScore:F4}] {hit.Citation}");
            sb.AppendLine(Line(hit.LifecycleStatus, hit.TrustTier, hit.Authorship, hit.Stale, hit.ConceptId));
            sb.AppendLine(hit.Content);
        }
        return sb.ToString();
    }

    public static string SectionText(string path, string? heading, DocumentSectionResult? result, int maxChars)
    {
        // An unreadable document is reported as absent, never as forbidden, so
        // this cannot be used to enumerate what exists.
        if (result is null)
            return $"Document '{path}' is not available. Paths must match a search_knowledge citation exactly.";
        if (result.LifecycleGated)
            return $"Document '{path}' exists but its lifecycle status is '{result.LifecycleStatus}'. " +
                   "Retry with includeHistorical=true only if the user explicitly wants historical content.";
        if (result.Chunks.Count == 0)
            return $"No section of '{path}' matches heading '{heading}'. Retry with a shorter heading fragment, or omit it for the whole document.";

        var sb = new StringBuilder();
        sb.AppendLine($"{result.Path} (title: {result.Title ?? "-"}, {result.Chunks.Count} chunk(s)):");
        sb.AppendLine(Line(result.LifecycleStatus, result.TrustTier, result.Authorship, result.Stale, result.ConceptId));
        string? lastHeading = null;
        foreach (var chunk in result.Chunks)
        {
            if (chunk.HeadingPath != lastHeading)
            {
                sb.AppendLine();
                sb.AppendLine($"## {(chunk.HeadingPath.Length == 0 ? "(document start)" : chunk.HeadingPath)}");
                lastHeading = chunk.HeadingPath;
            }
            sb.AppendLine(chunk.Content);
            if (sb.Length > maxChars)
                return sb.ToString(0, maxChars) + "\n\n[truncated, request a narrower heading]";
        }
        return sb.ToString();
    }
}

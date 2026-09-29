using System.ComponentModel;
using Premagentic.Core.Mcp;
using Premagentic.Core.Retrieval;
using ModelContextProtocol.Server;

namespace Premagentic.Api.Callers;

/// <summary>
/// The MCP surface, served over HTTP at <c>/mcp</c> to agents presenting their
/// token. Two tools, both read-only, and nothing else: an agent reads and never
/// writes, and there is no tool that stores, remembers or changes anything.
/// Each call runs as the agent the token belongs to, with its rights and its
/// trust policy as they are at that moment.
/// </summary>
[McpServerToolType]
public sealed class PremagenticMcpTools(IHttpContextAccessor http, CallerQueries queries)
{
    private const int MaxSectionChars = 24_000;

    [McpServerTool(Name = McpToolDescriptions.SearchKnowledge, ReadOnly = true, Destructive = false, OpenWorld = false), Description(
        "Search this organization's own documents. Returns cited evidence: path, heading and content, ranked " +
        "by hybrid lexical and semantic relevance. Only documents this agent is authorized to read are " +
        "eligible, and machine-written content no person has reviewed, and stale content, are left out " +
        "unless this deployment allows them to agents. Superseded, archived, draft and expired material is " +
        "excluded unless includeHistorical is set. Read-only. " + ResultFields.FieldsHelp)]
    public async Task<string> SearchKnowledge(
        [Description("The question or search query.")] string query,
        [Description("Set true ONLY when the user explicitly wants historical, superseded or archived content.")] bool includeHistorical = false,
        [Description("Number of results to return (1-10).")] int topK = 5,
        CancellationToken ct = default)
    {
        // Over the limit, the sentence is the tool's answer, so the assistant
        // can shorten the query and ask again. The search refuses the same
        // query in the same words whoever calls it.
        if (QueryLimits.SearchRefusal(query) is { } tooLong) return tooLong;
        var result = await queries.SearchAsync(CurrentCaller(), query, topK, includeHistorical, ct);
        return ResultFields.SearchText(result);
    }

    [McpServerTool(Name = McpToolDescriptions.GetDocumentSection, ReadOnly = true, Destructive = false, OpenWorld = false), Description(
        "Fetch the full text of a section cited by search_knowledge, by document path plus heading. Use after " +
        "a search hit when the excerpt is not enough context. Omit heading for the whole document. The same " +
        "rules as search decide what this agent may read. Read-only. " + ResultFields.FieldsHelp)]
    public async Task<string> GetDocumentSection(
        [Description("Document path exactly as cited, for example 'handbook/travel.md'.")] string path,
        [Description("Heading (or 'H1 > H2' heading path) from the citation, matched case-insensitively. Omit for the whole document.")] string? heading = null,
        [Description("Set true ONLY when the citation came from a search with includeHistorical.")] bool includeHistorical = false,
        CancellationToken ct = default)
    {
        if (QueryLimits.SectionRefusal(path, heading) is { } tooLong) return tooLong;
        var result = await queries.SectionAsync(CurrentCaller(), path, heading, includeHistorical, ct);
        return ResultFields.SectionText(path, heading, result, MaxSectionChars);
    }

    private Core.Identity.Caller CurrentCaller() =>
        (http.HttpContext ?? throw new InvalidOperationException("An MCP tool ran outside an HTTP request."))
        .Caller().Caller;
}

using System.Text.RegularExpressions;
using Premagentic.Core;
using Premagentic.Core.Identity;
using Premagentic.Core.Ingestion;
using Premagentic.Core.Retrieval;
using Premagentic.Core.Security;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Premagentic.Tests;

/// <summary>
/// The principals a connector names, read through the deployment's principal
/// mapper at ingest, and what an agent then reads over MCP. MCP is for agents
/// only, and an agent's principals never pass through a sign-in adapter, so a
/// mapping reaches MCP through the documents: a file whose permissions name an
/// outside principal is readable by the group that principal is mapped to.
/// <para>
/// Both ways. With a mapper, the report bot, a service agent granted the
/// Auditors group, reads the ledger whose principal is mapped to Auditors.
/// With none, the same ledger is indexed, counted, and readable by nobody.
/// </para>
/// Requires a running Docker daemon.
/// </summary>
public sealed partial class ConnectorMappingTests(DatastoreTestDatabase server) : IClassFixture<DatastoreTestDatabase>
{
    private const string Ledger = "ext/ledger.md";
    private const string AuditorsSid = "S-1-5-21-3623811015-3361044348-30300820-1013";

    /// <summary>Maps one outside principal to the Auditors group, once the world has one.</summary>
    private sealed class AuditorsMapper(Guid auditors) : IPrincipalMapper
    {
        public string Name => "auditors";

        public Task<IReadOnlyDictionary<string, Guid>> MapAsync(PrincipalMapRequest request, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyDictionary<string, Guid>>(
                request.Principals.Where(p => p == AuditorsSid).ToDictionary(p => p, _ => auditors, StringComparer.Ordinal));
    }

    /// <summary>What the run in <see cref="WorldAsync"/> returned and said.</summary>
    private sealed class Run
    {
        public IngestSummary? Summary { get; set; }
        public List<string> Log { get; } = [];
    }

    /// <summary>
    /// The API world, with one more document ingested before the API starts:
    /// the ledger, readable only by the outside principals given, read through
    /// a mapper to Auditors or through none.
    /// </summary>
    private async Task<(ApiWorld World, Run Run)> WorldAsync(bool withMapper, params string[] principals)
    {
        var run = new Run();
        var world = await ApiWorld.NewAsync(server, beforeStart: async (db, tenant) =>
        {
            var auditors = await new IdentityStore(db, tenant).FindGroupByNameAsync("Auditors");
            var pipeline = new IngestPipeline(db, new SeededEmbeddingProvider())
            {
                PrincipalMapper = withMapper ? new AuditorsMapper(auditors!.Id) : null,
            };
            run.Summary = await pipeline.RunAsync(tenant, new DatastoreSource("ext", [
                DatastoreSource.Doc(Ledger, "## Ledger\nthe zeppelin ledger kept by the auditors", DocumentAccess.External(principals)),
            ]), run.Log.Add);
        });
        return (world, run);
    }

    [Fact]
    public async Task With_a_mapper_the_agent_granted_the_mapped_group_reads_the_document_over_mcp()
    {
        var (w, run) = await WorldAsync(withMapper: true, AuditorsSid);
        await using var _ = w;

        Assert.Equal(0, run.Summary!.DeniedToEveryone);
        Assert.Equal(0, run.Summary.UnmappedPrincipals);

        await using var bot = await OAuthApi.McpAsync(w.Host, w.BotToken);
        Assert.Contains(Ledger, await SearchPathsAsync(bot));

        // The control on the other side: the assistant acts for Alice, who is
        // not an auditor, and the mapping gives her nothing.
        await using var assistant = await OAuthApi.McpAsync(w.Host, w.AssistantToken);
        Assert.DoesNotContain(Ledger, await SearchPathsAsync(assistant));
    }

    [Fact]
    public async Task Without_a_mapper_the_same_document_reaches_nobody_over_mcp_and_the_run_says_why()
    {
        var (w, run) = await WorldAsync(withMapper: false, AuditorsSid);
        await using var _ = w;

        Assert.Equal(1, run.Summary!.DeniedToEveryone);
        Assert.Equal(1, run.Summary.UnmappedPrincipals);
        Assert.Contains(
            $"  1 principal(s) named by the source mean nothing here (no principal mapper is loaded), so what they " +
            $"alone allowed reaches nobody: {AuditorsSid}.",
            run.Log);

        await using var bot = await OAuthApi.McpAsync(w.Host, w.BotToken);
        Assert.DoesNotContain(Ledger, await SearchPathsAsync(bot));

        // The control: the ledger is indexed, so it is missing because nothing
        // may read it and not because it is absent.
        Assert.Contains(Ledger, await UnrestrictedPathsAsync(w));
    }

    /// <summary>
    /// No permissive fallback: with no mapper, a connector's principals that
    /// read like PremAgentic's own (the Auditors group's name, its principal,
    /// its id, everyone) still reach nobody. Read any one of them as a
    /// PremAgentic principal and the bot, or everybody, would have the ledger.
    /// </summary>
    [Fact]
    public async Task Without_a_mapper_principals_that_read_like_premagentic_ones_reach_nobody_over_mcp()
    {
        // The Auditors group's id exists only once the world does, so the
        // principals are written inside it, before the API starts.
        var run = new Run();
        await using var w = await ApiWorld.NewAsync(server, beforeStart: async (db, tenant) =>
        {
            var auditors = CallerResolver.IdText((await new IdentityStore(db, tenant).FindGroupByNameAsync("Auditors"))!.Id);
            run.Summary = await new IngestPipeline(db, new SeededEmbeddingProvider()).RunAsync(tenant, new DatastoreSource("ext", [
                DatastoreSource.Doc(Ledger, "## Ledger\nthe zeppelin ledger kept by the auditors",
                    DocumentAccess.External("Auditors", "group:" + auditors, auditors, "everyone")),
            ]));
        });

        Assert.Equal(1, run.Summary!.DeniedToEveryone);
        Assert.Equal(4, run.Summary.UnmappedPrincipals);

        await using var bot = await OAuthApi.McpAsync(w.Host, w.BotToken);
        Assert.DoesNotContain(Ledger, await SearchPathsAsync(bot));
        await using var assistant = await OAuthApi.McpAsync(w.Host, w.AssistantToken);
        Assert.DoesNotContain(Ledger, await SearchPathsAsync(assistant));
        Assert.Contains(Ledger, await UnrestrictedPathsAsync(w));
    }

    private static async Task<string[]> SearchPathsAsync(McpClient client)
    {
        var result = await client.CallToolAsync("search_knowledge", new Dictionary<string, object?> { ["query"] = "zeppelin", ["topK"] = 20 });
        var text = string.Concat(result.Content.OfType<TextContentBlock>().Select(b => b.Text));
        Assert.False(result.IsError ?? false, text);
        return HitLine().Matches(text).Select(m => m.Groups[1].Value).Distinct().Order(StringComparer.Ordinal).ToArray();
    }

    private static async Task<string[]> UnrestrictedPathsAsync(ApiWorld w) =>
        (await new HybridSearch(w.Db, new SeededEmbeddingProvider())
            .SearchAsync(w.Tenant, "zeppelin", new SearchOptions(AccessScope.UnrestrictedAudited("test"), TopK: 20)))
        .Hits.Select(h => h.Path).Distinct().Order(StringComparer.Ordinal).ToArray();

    [GeneratedRegex(@"^--- \[[0-9.]+\] (\S+)", RegexOptions.Multiline)]
    private static partial Regex HitLine();
}

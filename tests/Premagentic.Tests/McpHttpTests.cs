using System.ComponentModel;
using System.Text.RegularExpressions;
using Premagentic.McpServer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Premagentic.Tests;

/// <summary>
/// MCP over HTTP inside the API process, spoken by the SDK's own client: the
/// agent token decides everything, there are two read-only tools and nothing
/// else, and the stdio bridge reaches the same tools as a registered agent.
/// Requires a running Docker daemon.
/// </summary>
public sealed partial class McpHttpTests(DatastoreTestDatabase server) : IClassFixture<DatastoreTestDatabase>
{
    [Fact]
    public async Task The_server_speaks_revision_2026_07_28_statelessly()
    {
        await using var w = await ApiWorld.NewAsync(server);
        var recorder = new RecordingHandler();
        await using var client = await ConnectAsync(w, w.BotToken, recorder);

        Assert.Equal("2026-07-28", client.NegotiatedProtocolVersion);
        await client.ListToolsAsync();

        Assert.NotEmpty(recorder.RequestVersions);
        Assert.All(recorder.RequestVersions, v => Assert.Equal("2026-07-28", v));
        Assert.DoesNotContain(recorder.RequestMethods, m => m == "initialize");
        Assert.False(recorder.SawSessionId, "A 2026-07-28 server issues no Mcp-Session-Id.");
    }

    [Fact]
    public async Task Exactly_two_tools_both_read_only_each_explaining_the_four_fields()
    {
        await using var w = await ApiWorld.NewAsync(server);
        await using var client = await ConnectAsync(w, w.BotToken);

        var tools = await client.ListToolsAsync();
        Assert.Equal(["get_document_section", "search_knowledge"], tools.Select(t => t.Name).Order(StringComparer.Ordinal).ToArray());
        foreach (var tool in tools.Select(t => t.ProtocolTool))
        {
            Assert.True(tool.Annotations?.ReadOnlyHint, $"{tool.Name} is not marked read-only");
            Assert.False(tool.Annotations?.DestructiveHint ?? true, $"{tool.Name} is not marked non-destructive");
            foreach (var word in new[] { "trust:", "authorship:", "stale:", "concept:" })
                Assert.Contains(word, tool.Description);
        }
    }

    [Fact]
    public async Task Two_agents_each_see_exactly_what_the_rules_give_them_over_mcp()
    {
        await using var w = await ApiWorld.NewAsync(server);
        await using var assistant = await ConnectAsync(w, w.AssistantToken);
        await using var bot = await ConnectAsync(w, w.BotToken);

        Assert.Equal([ApiWorld.Handbook, ApiWorld.Plan], await SearchPathsAsync(assistant));
        Assert.Equal([ApiWorld.AuditLog, ApiWorld.Handbook], await SearchPathsAsync(bot));

        // The same rules for a section: absent to the one that may not read it.
        Assert.StartsWith("Document 'hr/pay.md' is not available", await SectionTextAsync(assistant, ApiWorld.Pay));
        Assert.StartsWith("Document 'audit/log.md' is not available", await SectionTextAsync(assistant, ApiWorld.AuditLog));
        Assert.StartsWith("audit/log.md", await SectionTextAsync(bot, ApiWorld.AuditLog));
    }

    [Fact]
    public async Task A_query_or_path_over_the_limit_is_answered_with_the_sentence_over_mcp()
    {
        await using var w = await ApiWorld.NewAsync(server);
        await using var client = await ConnectAsync(w, w.BotToken);
        var tooLong = new string('z', Premagentic.Core.Retrieval.QueryLimits.MaxLength + 1);

        // As the tool's answer, not an error, so the assistant reads why and can ask again.
        Assert.Equal(Premagentic.Core.Retrieval.QueryLimits.SearchRefusal(tooLong),
            await CallTextAsync(client, "search_knowledge", new() { ["query"] = tooLong }));
        Assert.Equal(Premagentic.Core.Retrieval.QueryLimits.SectionRefusal(tooLong, null), await SectionTextAsync(client, tooLong));
        // The control: at the limit, the same tool searches.
        Assert.Matches("^(No results|[0-9]+ results)", await CallTextAsync(client, "search_knowledge", new() { ["query"] = tooLong[1..] }));
    }

    [Fact]
    public async Task Every_mcp_result_says_its_trust_authorship_staleness_and_concept()
    {
        await using var w = await ApiWorld.NewAsync(server, okfBundle: true);
        await using var client = await ConnectAsync(w, w.AssistantToken);

        var text = await CallTextAsync(client, "search_knowledge", new() { ["query"] = "watering propagation seed", ["topK"] = 10 });
        var hits = HitLine().Matches(text).Count;
        var fields = FieldsLine().Matches(text).Count;
        Assert.True(hits > 0, text);
        Assert.Equal(hits, fields);

        var section = await SectionTextAsync(client, "okf/care/propagation.md");
        Assert.Contains("trust: human-reviewed | authorship: machine | stale: no | concept: care/propagation", section);
    }

    [Fact]
    public async Task No_token_a_revoked_token_or_a_session_reaches_nothing_over_mcp()
    {
        await using var w = await ApiWorld.NewAsync(server);

        await Assert.ThrowsAnyAsync<Exception>(async () => await (await ConnectAsync(w, token: null)).ListToolsAsync());

        Assert.True(await w.Identity.RevokeTokenAsync(w.BotToken["prem_agt_".Length..][..24]));
        await Assert.ThrowsAnyAsync<Exception>(async () => await (await ConnectAsync(w, w.BotToken)).ListToolsAsync());

        using var http = w.Host.Client();
        var alice = await Api.SessionAsync(http, "alice", ApiWorld.AlicePassword);
        using var cookie = await http.SendAsync(Api.Request(HttpMethod.Post, "/mcp",
            new { jsonrpc = "2.0", id = 1, method = "tools/list" }, session: alice));
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, cookie.StatusCode);

        // The control: a live token connects.
        await using var ok = await ConnectAsync(w, w.AssistantToken);
        Assert.Equal(2, (await ok.ListToolsAsync()).Count);
    }

    [Fact]
    public async Task The_rate_limit_holds_over_mcp()
    {
        await using var w = await ApiWorld.NewAsync(server, botRate: 4);
        await using var client = await ConnectAsync(w, w.BotToken);

        var failed = false;
        for (var i = 0; i < 6 && !failed; i++)
        {
            try
            {
                await CallTextAsync(client, "search_knowledge", new() { ["query"] = "zeppelin" });
            }
            catch (Exception)
            {
                failed = true;
            }
        }
        Assert.True(failed, "Six calls in one minute on a rate of four all went through.");
    }

    [Fact]
    public async Task Every_mcp_search_is_in_the_audit_trail_under_its_agent()
    {
        await using var w = await ApiWorld.NewAsync(server);
        await using var client = await ConnectAsync(w, w.BotToken);
        await CallTextAsync(client, "search_knowledge", new() { ["query"] = "audited zeppelin" });

        await using var cmd = w.Db.DataSource.CreateCommand(
            "SELECT caller_agent_id, caller_user_id FROM prem_config.retrieval_event WHERE query = 'audited zeppelin'");
        await using var reader = await cmd.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(w.Bot.Id, reader.GetGuid(0));
        Assert.True(reader.IsDBNull(1));
        Assert.False(await reader.ReadAsync());
    }

    [Fact]
    public async Task The_stdio_bridge_reaches_the_same_two_tools_as_its_agent()
    {
        await using var w = await ApiWorld.NewAsync(server);
        await using var bridge = new ApiBridge(new Uri("https://localhost"), w.AssistantToken, w.Host.Client());

        var tools = await bridge.ListToolsAsync();
        Assert.Equal(["get_document_section", "search_knowledge"], tools.Tools.Select(t => t.Name).Order(StringComparer.Ordinal).ToArray());

        var result = await bridge.CallToolAsync(new CallToolRequestParams
        {
            Name = "search_knowledge",
            Arguments = new Dictionary<string, System.Text.Json.JsonElement>
            {
                ["query"] = System.Text.Json.JsonSerializer.SerializeToElement("zeppelin"),
                ["topK"] = System.Text.Json.JsonSerializer.SerializeToElement(10),
            },
        });
        Assert.Equal([ApiWorld.Handbook, ApiWorld.Plan], Paths(Text(result)));
    }

    [Fact]
    public async Task The_bridge_passes_through_only_tools_marked_read_only()
    {
        await using var app = await FakeMcpServerAsync();
        await using var bridge = new ApiBridge(new Uri("https://localhost"), "prem_agt_fake", app.GetTestClient());

        var tools = await bridge.ListToolsAsync();
        Assert.Equal(["look"], tools.Tools.Select(t => t.Name).ToArray());

        var write = await bridge.CallToolAsync(new CallToolRequestParams { Name = "remember" });
        Assert.True(write.IsError);
        Assert.Equal(0, FakeTools.Remembered);

        var read = await bridge.CallToolAsync(new CallToolRequestParams { Name = "look" });
        Assert.Equal("looked", Text(read));
    }

    [Fact]
    public void The_bridge_takes_its_token_from_a_file_first_and_refuses_what_it_cannot_trust()
    {
        var file = Path.GetTempFileName();
        try
        {
            File.WriteAllText(file, "prem_agt_fromfile\n");
            string? Env(string key, Dictionary<string, string> values) => values.GetValueOrDefault(key);

            Assert.Equal("prem_agt_fromfile", ApiBridge.ReadToken(k => Env(k, new() { [ApiBridge.TokenFileKey] = file })));
            Assert.Equal("prem_agt_direct", ApiBridge.ReadToken(k => Env(k, new() { [ApiBridge.TokenKey] = " prem_agt_direct " })));
            Assert.Throws<InvalidOperationException>(() => ApiBridge.ReadToken(
                k => Env(k, new() { [ApiBridge.TokenFileKey] = file, [ApiBridge.TokenKey] = "prem_agt_direct" })));
            Assert.Throws<InvalidOperationException>(() => ApiBridge.ReadToken(_ => null));
            Assert.Throws<InvalidOperationException>(() => ApiBridge.ReadToken(k => Env(k, new() { [ApiBridge.TokenKey] = "not-a-token" })));

            Assert.Equal("https://prem.example.org:8443/mcp", ApiBridge.McpEndpoint(new Uri("https://prem.example.org:8443/")).ToString());
            Assert.Equal("http://localhost:5000/mcp", ApiBridge.McpEndpoint(new Uri("http://localhost:5000")).ToString());
            Assert.Throws<InvalidOperationException>(() => ApiBridge.McpEndpoint(new Uri("http://prem.example.org")));

            var bridge = ApiBridge.FromEnvironment(k => Env(k, new() { [ApiBridge.ApiUrlKey] = "https://prem.example.org", [ApiBridge.TokenFileKey] = file }));
            Assert.DoesNotContain("prem_agt_", bridge.ToString());
        }
        finally
        {
            File.Delete(file);
        }
    }

    private static async Task<McpClient> ConnectAsync(ApiWorld w, string? token, RecordingHandler? recorder = null)
    {
        var http = recorder is null ? w.Host.Client() : w.Host.Client(https: true, recorder);
        var options = new HttpClientTransportOptions { Endpoint = new Uri("https://localhost/mcp") };
        if (token is not null) options.AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = "Bearer " + token };
        var transport = new HttpClientTransport(options, http, NullLoggerFactory.Instance, ownsHttpClient: true);
        return await McpClient.CreateAsync(transport, loggerFactory: NullLoggerFactory.Instance);
    }

    private static async Task<string[]> SearchPathsAsync(McpClient client) =>
        Paths(await CallTextAsync(client, "search_knowledge", new() { ["query"] = "zeppelin", ["topK"] = 10 }));

    private static Task<string> SectionTextAsync(McpClient client, string path) =>
        CallTextAsync(client, "get_document_section", new() { ["path"] = path });

    private static async Task<string> CallTextAsync(McpClient client, string tool, Dictionary<string, object?> arguments)
    {
        var result = await client.CallToolAsync(tool, arguments);
        Assert.False(result.IsError ?? false, Text(result));
        return Text(result);
    }

    private static string Text(CallToolResult result) =>
        string.Concat(result.Content.OfType<TextContentBlock>().Select(b => b.Text));

    private static string[] Paths(string searchText) =>
        HitLine().Matches(searchText).Select(m => m.Groups[1].Value).Distinct().Order(StringComparer.Ordinal).ToArray();

    [GeneratedRegex(@"^--- \[[0-9.]+\] (\S+)", RegexOptions.Multiline)]
    private static partial Regex HitLine();

    [GeneratedRegex(@"^lifecycle: \S+ \| trust: (unverified|machine-confirmed|human-reviewed) \| authorship: (human|machine|unknown) \| stale: (yes|no) \| concept: \S+", RegexOptions.Multiline)]
    private static partial Regex FieldsLine();

    /// <summary>An MCP server that is not Premagentic, offering a write tool beside a read-only one.</summary>
    private static async Task<WebApplication> FakeMcpServerAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddMcpServer().WithHttpTransport().WithTools<FakeTools>();
        var app = builder.Build();
        app.MapMcp("/mcp");
        await app.StartAsync();
        return app;
    }

    [McpServerToolType]
    public sealed class FakeTools
    {
        public static int Remembered;

        [McpServerTool(Name = "look", ReadOnly = true), Description("Reads.")]
        public static string Look() => "looked";

        [McpServerTool(Name = "remember"), Description("Writes.")]
        public static string Remember()
        {
            Interlocked.Increment(ref Remembered);
            return "remembered";
        }
    }

    /// <summary>Records what the client sends and what the server answers, at the HTTP layer.</summary>
    private sealed class RecordingHandler : DelegatingHandler
    {
        public List<string> RequestVersions { get; } = [];
        public List<string> RequestMethods { get; } = [];
        public bool SawSessionId { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Headers.TryGetValues("MCP-Protocol-Version", out var versions)) RequestVersions.AddRange(versions);
            if (request.Headers.TryGetValues("Mcp-Method", out var methods)) RequestMethods.AddRange(methods);
            var response = await base.SendAsync(request, ct);
            SawSessionId |= response.Headers.Contains("Mcp-Session-Id");
            return response;
        }
    }
}

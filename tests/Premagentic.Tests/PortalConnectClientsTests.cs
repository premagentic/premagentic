using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Premagentic.Core.Identity;
using Premagentic.Portal.Connect;
using Premagentic.Portal.Pages;

namespace Premagentic.Tests;

/// <summary>
/// The connect page's configurations checked against each client's own
/// documentation, and the deployment's instructions shown under them. Every
/// name and text here is invented. The page tests require a running Docker
/// daemon.
/// </summary>
public sealed class PortalConnectClientsTests(DatastoreTestDatabase server) : IClassFixture<DatastoreTestDatabase>
{
    private const string Address = "https://prem.example.org:8443";
    private const string Token = "prem_agt_invented_0";

    // --- The configurations, field by field ---

    /// <summary>
    /// Claude Desktop starts a local program named in claude_desktop_config.json
    /// under "mcpServers", with "command" and "env", opened from the Claude menu
    /// in the system menu bar, Settings, Developer, Edit Config, and read after a
    /// full quit. Source: modelcontextprotocol.io/docs/develop/connect-local-servers,
    /// read 2026-09-24 (the documentation of protocol version 2026-07-28).
    /// </summary>
    [Fact]
    public void The_Claude_Desktop_configuration_has_the_shape_its_documentation_gives()
    {
        var (prose, json) = Split(ConnectSnippets.Render(ConnectSnippets.Shipped["claude-desktop"], Address, Token));

        var server = json.GetProperty("mcpServers").GetProperty("premagentic");
        Assert.True(server.TryGetProperty("command", out _));
        Assert.Equal(Address, server.GetProperty("env").GetProperty("PREM_API_URL").GetString());
        Assert.Equal(Token, server.GetProperty("env").GetProperty("PREM_AGENT_TOKEN").GetString());
        Assert.Contains("Developer", prose);
        Assert.Contains("Edit Config", prose);
        Assert.Contains("claude_desktop_config.json", prose);
        Assert.Contains("quit Claude Desktop completely", prose);
    }

    /// <summary>
    /// A custom connector added in Claude is called from Anthropic's cloud, in
    /// every Claude client including Claude Desktop, and a server on a private
    /// network, behind a VPN or a firewall does not connect. Source:
    /// support.claude.com/en/articles/11175166 (custom connectors using remote
    /// MCP), read 2026-09-24.
    /// </summary>
    [Fact]
    public void The_Claude_Desktop_text_says_a_custom_connector_cannot_reach_a_server_inside_the_network()
    {
        var text = ConnectSnippets.Render(ConnectSnippets.Shipped["claude-desktop"], Address, Token);

        Assert.Contains("not a custom connector added in Claude", text);
        Assert.Contains("called from Anthropic's cloud and cannot reach a server inside your network", text);
    }

    /// <summary>
    /// VS Code takes an HTTP server in mcp.json under "servers", with "type":
    /// "http", "url" and "headers", in the workspace's .vscode/mcp.json or the
    /// user configuration opened by MCP: Open User Configuration, and prefers
    /// an input variable to a secret written into the file. Sources:
    /// code.visualstudio.com/docs/copilot/customization/mcp-servers and
    /// code.visualstudio.com/docs/agents/reference/mcp-configuration, both dated
    /// 2026-09-16, read 2026-09-24.
    /// </summary>
    [Fact]
    public void The_VS_Code_configuration_has_the_shape_its_documentation_gives()
    {
        var (prose, json) = Split(ConnectSnippets.Render(ConnectSnippets.Shipped["copilot"], Address, Token));

        var server = json.GetProperty("servers").GetProperty("premagentic");
        Assert.Equal("http", server.GetProperty("type").GetString());
        Assert.Equal(Address + "/mcp", server.GetProperty("url").GetString());
        Assert.Equal("Bearer " + Token, server.GetProperty("headers").GetProperty("Authorization").GetString());
        Assert.Contains(".vscode/mcp.json", prose);
        Assert.Contains("MCP: Open User Configuration", prose);
        Assert.Contains("${input:premagentic-token}", prose);
        Assert.Contains("\"type\": \"promptString\"", prose);
    }

    /// <summary>
    /// ChatGPT's connectors support OAuth, no authentication, or a mix of the
    /// two, and name no header; they call the server from OpenAI's cloud. OpenAI
    /// documents Secure MCP Tunnel, an outbound-only program run inside the
    /// network that forwards requests to a private MCP server over HTTP or to a
    /// local program over stdio. So the text blames the network, not a missing
    /// authorization flow, names the tunnel without claiming it works here, and
    /// gives the bridge's program and its two settings. Sources:
    /// developers.openai.com/api/docs/guides/developer-mode and
    /// developers.openai.com/api/docs/guides/secure-mcp-tunnels, no dates on
    /// the pages, read 2026-09-24.
    /// </summary>
    [Fact]
    public void The_ChatGPT_text_says_why_it_cannot_reach_the_server_and_what_is_untried()
    {
        var text = ConnectSnippets.Render(ConnectSnippets.Shipped["chatgpt"], Address, Token);

        Assert.Contains("from OpenAI's cloud, so it cannot reach PremAgentic inside your network directly", text);
        Assert.Contains("no way to send this token as a header", text);
        Assert.Contains("Secure MCP Tunnel", text);
        Assert.Contains("This has not been tried with PremAgentic.", text);
        Assert.Contains("register this assistant with a hosted model", text);
        Assert.Contains("Premagentic.McpServer", text);
        Assert.Contains($"PREM_API_URL={Address}\n", text);
        Assert.Contains($"PREM_AGENT_TOKEN={Token}\n", text);
        // Nothing it cannot back: no claim that it works, no vendor link, and no
        // blame on an authorization flow that would not change the network.
        Assert.DoesNotContain("works with", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("openai.com", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("authorization flow", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_instructions_line_for_ChatGPT_blames_the_network_and_says_where_to_paste_them()
    {
        var line = ConnectPages.InstructionsGoTo["chatgpt"];

        Assert.Contains("cannot reach PremAgentic inside your network directly", line);
        Assert.Contains("instructions of the project", line);
        Assert.DoesNotContain("yet", line);
    }

    // --- The instructions block ---

    [Fact]
    public async Task With_no_instructions_set_the_page_shows_no_instructions_block()
    {
        await using var p = await PortalWorld.NewAsync(server);

        var page = await p.TextAsync(await p.PostAsync("/portal/connect", p.Member, Connect("alice-desk", "coding-tool")));

        Assert.Matches(Regex.Escape(AgentTokens.Prefix), page);
        Assert.DoesNotContain("Instructions from your administrator", page);
        Assert.DoesNotContain("id=\"instructions\"", page);
    }

    [Fact]
    public async Task Set_instructions_are_shown_under_the_configuration_with_a_copy_button_for_the_exact_text()
    {
        await using var p = await PortalWorld.NewAsync(server);
        const string text = "Cite the path and heading of every passage you use.\nSay so when the search returns nothing.\nNever guess a policy <or a date> & never answer from memory.";
        await new SettingsStore(p.World.Db, p.World.Tenant).SetAsync(McpSettings.Instructions, JsonSerializer.SerializeToElement(text));
        await new SettingsStore(p.World.Db, p.World.Tenant).SetAsync(AgentSettings.SelfServiceMax, JsonSerializer.SerializeToElement(AgentSettings.SelfServiceCeiling));

        foreach (var (kind, _) in ConnectSnippets.Kinds)
        {
            var page = await p.TextAsync(await p.PostAsync("/portal/connect", p.Member, Connect($"alice-{kind}", kind)));

            Assert.Contains("Instructions from your administrator", page);
            Assert.Contains(System.Net.WebUtility.HtmlEncode(ConnectPages.InstructionsGoTo[kind]), page);
            // The copy button copies the block's text, and the block holds the
            // text exactly as the administrator wrote it, line breaks and all.
            Assert.Contains("data-copy=\"instructions\"", page);
            var block = Regex.Match(page, "<pre class=\"token snippet\" id=\"instructions\">([^<]*)</pre>");
            Assert.True(block.Success, "no instructions block");
            Assert.Equal(text, System.Net.WebUtility.HtmlDecode(block.Groups[1].Value).ReplaceLineEndings("\n"));
        }

        // Where each goes: a file for a coding tool, the connect result for an MCP client.
        Assert.Contains("AGENTS.md", ConnectPages.InstructionsGoTo["coding-tool"]);
        Assert.Contains("CLAUDE.md", ConnectPages.InstructionsGoTo["coding-tool"]);
        Assert.Contains("project rules", ConnectPages.InstructionsGoTo["coding-tool"]);
        Assert.Contains("sends at connect", ConnectPages.InstructionsGoTo["local-mcp"]);
    }

    [Fact]
    public void Every_kind_says_where_the_instructions_go()
    {
        Assert.Equal(ConnectSnippets.Kinds.Select(k => k.Kind).Order(), ConnectPages.InstructionsGoTo.Keys.Order());
    }

    /// <summary>The prose before a configuration and the configuration as JSON.</summary>
    private static (string Prose, JsonElement Json) Split(string text)
    {
        var start = text.IndexOf("\n{", StringComparison.Ordinal) + 1;
        var end = text.LastIndexOf("\n}", StringComparison.Ordinal) + 2;
        Assert.True(start > 0 && end > start, "no JSON block in the text");
        return (text[..start] + text[end..], JsonDocument.Parse(text[start..end]).RootElement.Clone());
    }

    private static (string, string)[] Connect(string name, string kind) =>
        [("name", name), ("kind", kind), ("model", "hosted"), ("vendor", "Invented Models")];
}

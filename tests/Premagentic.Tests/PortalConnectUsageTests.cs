using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Premagentic.Core.Identity;
using Premagentic.Portal.Connect;
using Premagentic.Portal.Pages;

namespace Premagentic.Tests;

/// <summary>
/// The person's connect page and the manager's usage page, through the real
/// API host. Every name, question and token here is invented. Requires a
/// running Docker daemon.
/// </summary>
public sealed class PortalConnectUsageTests(DatastoreTestDatabase server) : IClassFixture<DatastoreTestDatabase>
{
    /// <summary>A window around every event a test makes, whatever the clocks say.</summary>
    private const string Year = "from=2026-01-01&to=2026-12-31";

    private static readonly Regex Token = new(Regex.Escape(AgentTokens.Prefix) + "[A-Za-z0-9_-]+");

    // --- The connect page ---

    [Fact]
    public async Task The_token_is_in_exactly_one_response_and_the_agent_it_makes_acts_as_its_person()
    {
        await using var p = await PortalWorld.NewAsync(server);

        using var made = await p.PostAsync("/portal/connect", p.Member, Connect("alice-desk", "coding-tool"));
        Assert.Equal(HttpStatusCode.OK, made.StatusCode);
        Assert.Contains("no-store", made.Headers.CacheControl!.ToString());
        var page = await made.Content.ReadAsStringAsync();
        var token = Assert.Single(Token.Matches(page).Select(m => m.Value).Distinct());
        Assert.Contains("This is the only time the token is shown.", page);

        // The token is real: it reaches what alice may read, and not what she may not.
        var paths = await Api.PathsAsync(await Api.SearchAsync(p.Client, bearer: token));
        Assert.Contains(ApiWorld.Handbook, paths);
        Assert.DoesNotContain(ApiWorld.AuditLog, paths);

        // Every other response, to every person, is without it.
        foreach (var (path, session) in new[]
        {
            ("/portal/connect", p.Member), ("/portal/connect", p.Admin), ("/portal/agents", p.Admin),
            ("/portal/changes", p.Admin), ("/portal/audit", p.Admin), ("/portal/export/changes.jsonl", p.Admin),
            ("/portal/export/config.json", p.Admin), ("/portal/search?q=zeppelin", p.Member),
        })
        {
            var text = await p.TextAsync(await p.GetAsync(path, session));
            Assert.DoesNotContain(token, text);
            Assert.DoesNotMatch(Token, text);
        }

        // The control: the name the person gave is on the page they return to, so
        // the absence above is of the token and not of the agent.
        Assert.Contains("alice-desk", await p.TextAsync(await p.GetAsync("/portal/connect", p.Member)));
    }

    [Fact]
    public async Task Each_kind_of_assistant_gets_its_snippet_with_the_servers_address_and_the_token()
    {
        // The shipped file holds exactly the kinds the form offers.
        Assert.Equal(ConnectSnippets.Kinds.Select(k => k.Kind).Order(), ConnectSnippets.Shipped.Keys.Order());

        foreach (var (kind, _) in ConnectSnippets.Kinds)
        {
            var text = ConnectSnippets.Render(ConnectSnippets.Shipped[kind], "https://prem.example.org:8443", "prem_agt_invented_0");
            Assert.Contains("https://prem.example.org:8443", text);
            Assert.Contains("prem_agt_invented_0", text);
            Assert.DoesNotContain(ConnectSnippets.AddressField, text);
            Assert.DoesNotContain(ConnectSnippets.TokenField, text);
        }

        // And through the page, where the address is the one the browser used.
        await using var p = await PortalWorld.NewAsync(server);
        await new SettingsStore(p.World.Db, p.World.Tenant).SetAsync(AgentSettings.SelfServiceMax, JsonSerializer.SerializeToElement(AgentSettings.SelfServiceCeiling));
        foreach (var (kind, label) in ConnectSnippets.Kinds.Take(3))
        {
            var page = await p.TextAsync(await p.PostAsync("/portal/connect", p.Admin, Connect($"carol-{kind}", kind)));
            Assert.Contains($"The configuration: {System.Net.WebUtility.HtmlEncode(label)}", page);
            Assert.Matches("https?://localhost", page);
            Assert.Single(Token.Matches(page).Select(m => m.Value).Distinct());
        }
    }

    [Fact]
    public async Task The_setting_overrides_a_kinds_template_and_leaves_the_others_shipped()
    {
        await using var p = await PortalWorld.NewAsync(server);
        await new SettingsStore(p.World.Db, p.World.Tenant).SetAsync(ConnectSnippets.Key,
            JsonSerializer.SerializeToElement(new Dictionary<string, string> { ["other"] = "Ask the desk: {address} with {token}" }));

        var other = await p.TextAsync(await p.PostAsync("/portal/connect", p.Member, Connect("alice-other", "other")));
        Assert.Matches("Ask the desk: https?://localhost with " + Regex.Escape(AgentTokens.Prefix), other);

        var copilot = await p.TextAsync(await p.PostAsync("/portal/connect", p.Member, Connect("alice-copilot", "copilot")));
        Assert.DoesNotContain("Ask the desk", copilot);
        Assert.Contains(".vscode/mcp.json", copilot);
    }

    [Fact]
    public async Task A_stored_override_that_cannot_be_used_is_not_used_at_all_and_says_why()
    {
        foreach (var (value, why) in new (string, string)[]
        {
            ("""["a list"]""", "takes an object"),
            ("""{"telegraph": "x {token}"}""", "no assistant kind 'telegraph'"),
            ("""{"other": ""}""", "a text of 1 to"),
            ("""{"other": "no place for it"}""", "does not say where the token goes"),
        })
        {
            Assert.False(ConnectSnippets.TryRead(JsonDocument.Parse(value).RootElement, out _, out var problem));
            Assert.Contains(why, problem);
        }
        Assert.True(ConnectSnippets.TryRead(JsonDocument.Parse("""{"other": "at {address} {token}"}""").RootElement, out var read, out _));
        Assert.Equal("at {address} {token}", read["other"]);

        await using var p = await PortalWorld.NewAsync(server);
        await new SettingsStore(p.World.Db, p.World.Tenant).SetAsync(ConnectSnippets.Key,
            JsonDocument.Parse("""{"other": "Ask the desk", "copilot": "Mine {token}"}""").RootElement);

        var admin = await p.TextAsync(await p.GetAsync("/portal/connect", p.Admin));
        Assert.Contains("setting is not used", admin);
        var copilot = await p.TextAsync(await p.PostAsync("/portal/connect", p.Member, Connect("alice-copilot", "copilot")));
        // The usable half of a bad value is not used either: all of it or none.
        Assert.DoesNotContain("Mine prem_agt_", copilot);
        Assert.Contains(".vscode/mcp.json", copilot);
    }

    [Fact]
    public async Task At_zero_the_form_is_off_and_a_post_makes_nothing()
    {
        await using var p = await PortalWorld.NewAsync(server);
        await new SettingsStore(p.World.Db, p.World.Tenant).SetAsync(AgentSettings.SelfServiceMax, JsonSerializer.SerializeToElement(0));
        var agents = await p.ScalarAsync("SELECT count(*) FROM prem_config.agent");

        var page = await p.TextAsync(await p.GetAsync("/portal/connect", p.Member));
        Assert.Contains("An administrator turned this off", page);
        Assert.DoesNotContain("action=\"/portal/connect\"", page);

        using (var post = await p.PostAsync("/portal/connect", p.Member, Connect("alice-desk", "other")))
        {
            Assert.Equal(HttpStatusCode.Redirect, post.StatusCode);
            Assert.Contains("error=", post.Headers.Location!.OriginalString);
            Assert.DoesNotMatch(Token, await post.Content.ReadAsStringAsync());
        }
        Assert.Equal(agents, await p.ScalarAsync("SELECT count(*) FROM prem_config.agent"));

        // The control: at one the form is on and the same post makes the agent.
        await new SettingsStore(p.World.Db, p.World.Tenant).SetAsync(AgentSettings.SelfServiceMax, JsonSerializer.SerializeToElement(1));
        Assert.Contains("action=\"/portal/connect\"", await p.TextAsync(await p.GetAsync("/portal/connect", p.Member)));
        using (var post = await p.PostAsync("/portal/connect", p.Member, Connect("alice-desk", "other")))
            Assert.Equal(HttpStatusCode.OK, post.StatusCode);
        Assert.Equal(agents + 1, await p.ScalarAsync("SELECT count(*) FROM prem_config.agent"));
    }

    [Fact]
    public async Task A_person_sees_and_revokes_only_their_own_agents()
    {
        await using var p = await PortalWorld.NewAsync(server);
        using (await p.PostAsync("/portal/connect", p.Admin, Connect("carol-desk", "other"))) { }
        using (await p.PostAsync("/portal/connect", p.Member, Connect("alice-desk", "other"))) { }
        var own = new SelfServeAgents(p.World.Db, p.World.Tenant);
        var carols = Assert.Single(await own.ListAsync(p.World.Carol.Id, CancellationToken.None));

        var alice = await p.TextAsync(await p.GetAsync("/portal/connect", p.Member));
        Assert.Contains("alice-desk", alice);
        Assert.DoesNotContain("carol-desk", alice);
        Assert.DoesNotContain(carols.AgentId.ToString(), alice);

        // Revoking somebody else's is refused, and theirs still works.
        using (var post = await p.PostAsync($"/portal/connect/{carols.AgentId}/revoke", p.Member))
            Assert.Contains("error=", post.Headers.Location!.OriginalString);
        Assert.False((await own.ListAsync(p.World.Carol.Id, CancellationToken.None)).Single().Revoked);

        // The control: the owner revokes it from the same route.
        using (var post = await p.PostAsync($"/portal/connect/{carols.AgentId}/revoke", p.Admin))
            Assert.Contains("done=", post.Headers.Location!.OriginalString);
        Assert.True((await own.ListAsync(p.World.Carol.Id, CancellationToken.None)).Single().Revoked);

        // Administrators see every person's agents on the agents page, as before.
        var agents = await p.TextAsync(await p.GetAsync("/portal/agents", p.Admin));
        Assert.Contains("alice-desk", agents);
        Assert.Contains("carol-desk", agents);
    }

    [Fact]
    public async Task Signed_out_or_with_an_agent_token_the_connect_page_is_refused()
    {
        await using var p = await PortalWorld.NewAsync(server);

        using (var signedOut = await p.GetAsync("/portal/connect"))
            Assert.Equal(HttpStatusCode.SeeOther, signedOut.StatusCode);
        using (var post = await p.PostAsync("/portal/connect", session: null, Connect("nobody", "other")))
            Assert.Equal(HttpStatusCode.Unauthorized, post.StatusCode);
        using (var agent = await p.Client.SendAsync(Api.Request(HttpMethod.Get, "/portal/connect", bearer: p.World.AssistantToken)))
            Assert.Equal(HttpStatusCode.Forbidden, agent.StatusCode);
        Assert.True(await PortalRoutes.IsRefusedAsync(
            await p.PostAsync("/portal/connect", p.Member, Connect("alice-desk", "other"), origin: "https://elsewhere.example")));
        Assert.True(await PortalRoutes.IsRefusedAsync(
            await p.PostAsync("/portal/connect", p.Member, Connect("alice-desk", "other"), tokenField: false)));
    }

    // --- The usage page ---

    [Fact]
    public async Task The_usage_page_shows_who_asked_what_was_served_what_was_not_found_and_what_left()
    {
        await using var p = await PortalWorld.NewAsync(server);
        await AskAsync(p);

        var page = await p.TextAsync(await p.GetAsync($"/portal/usage?{Year}", p.Auditor));
        Assert.Contains("<h2>People</h2>", page);
        Assert.Contains("<td>alice</td>", page);
        Assert.Contains("<td>assistant</td>", page);
        Assert.Contains(ApiWorld.Handbook, page);
        // A question nothing answered, as a link that asks it again as the reader.
        Assert.Contains("href=\"/portal/search?q=submarine%20maintenance%20schedule\"", page);
        // The audit trail's export is the business add-on's, so the page links none.
        Assert.DoesNotContain("/portal/export/audit.jsonl", page);

        // A hosted-model assistant alice connects, and one question from it.
        var made = await p.TextAsync(await p.PostAsync("/portal/connect", p.Member, Connect("alice-cloud", "other")));
        var token = Token.Match(made).Value;
        Assert.Equal(HttpStatusCode.OK, (await Api.SearchAsync(p.Client, bearer: token)).StatusCode);
        var all = await p.TextAsync(await p.GetAsync($"/portal/usage?{Year}", p.Auditor));
        Assert.Contains("<td>alice-cloud</td>", all);
        Assert.Matches("<dt>Passages served to hosted-model agents</dt><dd>[1-9]", all);

        // Hosted only: the local-model assistant and the person drop out of
        // every table, and the hosted one stays, so the filter narrows rather
        // than empties.
        var hosted = await p.TextAsync(await p.GetAsync($"/portal/usage?{Year}&hosted=yes", p.Auditor));
        Assert.Contains("<td>alice-cloud</td>", hosted);
        Assert.DoesNotContain("<td>assistant</td>", hosted);
        Assert.DoesNotContain("<td>alice</td>", hosted);
        Assert.DoesNotContain("submarine", hosted);
        Assert.Contains("UTC, hosted-model agents only.", hosted);

        // A window longer than the bound says why, and still shows the picker.
        var tooLong = await p.TextAsync(await p.GetAsync("/portal/usage?from=2025-01-01&to=2026-12-31", p.Auditor));
        Assert.Contains($"at most {UsagePages.MaxDays} days", tooLong);
        Assert.Contains("action=\"/portal/usage\"", tooLong);

        using (var member = await p.GetAsync("/portal/usage", p.Member))
            Assert.Equal(HttpStatusCode.Forbidden, member.StatusCode);
    }

    [Fact]
    public async Task Opening_the_usage_page_writes_nothing_anywhere()
    {
        await using var p = await PortalWorld.NewAsync(server);
        await AskAsync(p);
        var before = await EverythingAsync(p);

        foreach (var query in new[] { Year, $"{Year}&period=week", $"{Year}&period=month&hosted=yes", "from=2025-01-01&to=2026-12-31", "" })
            foreach (var session in new[] { p.Auditor, p.Admin })
            {
                var page = await p.TextAsync(await p.GetAsync($"/portal/usage?{query}", session));
                Assert.Contains("<h1>Usage</h1>", page);
                // The only form reads.
                var main = page[page.IndexOf("<main>", StringComparison.Ordinal)..];
                Assert.Equal(["<form method=\"get\" action=\"/portal/usage\" class=\"inline\">"],
                    Regex.Matches(main, "<form[^>]*>").Select(m => m.Value));
            }

        Assert.Equal(before, await EverythingAsync(p));
    }

    /// <summary>
    /// Questions from a person and from a local-model assistant, through the
    /// API, and one nothing answered. The test embedder finds a nearest passage
    /// for any question, so the unanswered one is written as the audit trail
    /// would hold it: a search by the assistant that served no passage.
    /// </summary>
    private static async Task AskAsync(PortalWorld p)
    {
        Assert.Equal(HttpStatusCode.OK, (await Api.SearchAsync(p.Client, session: p.Member)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Api.SearchAsync(p.Client, bearer: p.World.AssistantToken)).StatusCode);
        await using var cmd = p.World.Db.DataSource.CreateCommand("""
            INSERT INTO prem_config.retrieval_event(
                tenant_id, kind, query, caller_user_id, caller_agent_id, include_historical, passages, elapsed_ms, model_location, created_at)
            VALUES(@t, 'search', 'submarine maintenance schedule', @user, @agent, false, '[]'::jsonb, 1, 'local', now())
            """);
        cmd.Parameters.AddWithValue("t", p.World.Tenant);
        cmd.Parameters.AddWithValue("user", p.World.Alice.Id);
        cmd.Parameters.AddWithValue("agent", p.World.Assistant.Id);
        await cmd.ExecuteNonQueryAsync();
    }

    [Fact]
    public void The_portal_offers_exactly_the_assistant_kinds_the_setting_accepts()
    {
        Assert.Equal(SettingsCatalog.ConnectSnippetKinds.Order(), ConnectSnippets.Kinds.Select(k => k.Kind).Order());

        // And the two read a value the same way, refusal for refusal.
        foreach (var value in new[] { """["a list"]""", """{"telegraph": "x {token}"}""", """{"other": ""}""", """{"other": "no place"}""", """{"other": "at {token}"}""" })
        {
            var element = JsonDocument.Parse(value).RootElement;
            ConnectSnippets.TryRead(element, out _, out var portal);
            Assert.Equal(SettingsCatalog.ConnectSnippetsProblem(element), portal);
        }
    }

    /// <summary>
    /// Every row of every table in both schemas, as one digest per table: a
    /// write of any kind to any of them moves at least one.
    /// </summary>
    private static async Task<string> EverythingAsync(PortalWorld p)
    {
        var tables = new List<string>();
        await using (var list = p.World.Db.DataSource.CreateCommand("""
            SELECT quote_ident(table_schema) || '.' || quote_ident(table_name) FROM information_schema.tables
            WHERE table_schema IN ('prem_config', 'prem_index') AND table_type = 'BASE TABLE' ORDER BY 1
            """))
        await using (var reader = await list.ExecuteReaderAsync())
            while (await reader.ReadAsync()) tables.Add(reader.GetString(0));
        Assert.True(tables.Count > 10, $"only {tables.Count} tables were found to compare");

        var digest = new List<string>();
        foreach (var table in tables)
        {
            await using var cmd = p.World.Db.DataSource.CreateCommand(
                $"SELECT count(*)::text || ':' || md5(coalesce(string_agg(t::text, '|' ORDER BY t::text), '')) FROM {table} t");
            digest.Add($"{table}={await cmd.ExecuteScalarAsync()}");
        }
        return string.Join("\n", digest);
    }

    private static (string, string)[] Connect(string name, string kind, string model = "hosted", string vendor = "Invented Models") =>
        [("name", name), ("kind", kind), ("model", model), ("vendor", model == "local" ? "" : vendor)];
}

using System.Net;
using System.Text.RegularExpressions;
using Premagentic.Core.Identity;
using Premagentic.Portal.Pages;

namespace Premagentic.Tests;

/// <summary>
/// The portal with the authorization flow really on: the host's own consent,
/// grants and counts behind the pages, never a stand-in. The way in from an
/// assistant, a person's own grants and bound on the connect page, and the
/// health page's line for the flow. Every name is invented. Requires a running
/// Docker daemon.
/// </summary>
public sealed class PortalOAuthFlowTests(DatastoreTestDatabase server) : IClassFixture<DatastoreTestDatabase>
{
    // --- The way in ---

    [Fact]
    public async Task Signed_out_a_person_who_follows_authorize_signs_in_and_lands_on_the_consent_page()
    {
        await using var a = await OAuthApi.NewAsync(server);
        var desk = await a.RegisterAsync("Desk Tool");
        var (_, challenge) = OAuthWorld.Pkce();
        var authorize = "/oauth/authorize?" + string.Join("&", new (string Name, string Value)[]
        {
            ("response_type", "code"), ("client_id", desk), ("redirect_uri", OAuthApi.LoopbackOnAPort),
            ("code_challenge", challenge), ("code_challenge_method", "S256"), ("resource", OAuthApi.Resource), ("state", "s1"),
        }.Select(p => p.Name + "=" + Uri.EscapeDataString(p.Value)));

        // The flow hands the request to the consent page, which sends a browser
        // that is not signed in to sign in, with the consent page to come back to.
        using var forward = await a.Http.GetAsync(authorize);
        Assert.Equal(HttpStatusCode.SeeOther, forward.StatusCode);
        var consent = forward.Headers.Location!.OriginalString;
        Assert.StartsWith(OAuthPaths.Consent + "?", consent);
        using var signedOut = await a.Http.GetAsync(consent);
        Assert.Equal(HttpStatusCode.SeeOther, signedOut.StatusCode);
        var signIn = signedOut.Headers.Location!.OriginalString;
        Assert.Equal("/portal/sign-in?return=" + Uri.EscapeDataString(consent), signIn);

        // The sign-in page keeps the whole request as where to go back to, which
        // the portal's script opens once the session has started.
        using var signInPage = await a.Http.GetAsync(signIn);
        var back = WebUtility.HtmlDecode(Regex.Match(await signInPage.Content.ReadAsStringAsync(), "data-return=\"([^\"]*)\"").Groups[1].Value);
        Assert.Equal(consent, back);
        var alice = await Api.SessionAsync(a.Http, "alice", ApiWorld.AlicePassword);
        var page = await TextAsync(a.Http, alice, back);

        Assert.Contains("<h1>An assistant asks to connect</h1>", page);
        Assert.Contains("An assistant asks to search as you: <bdi class=\"client\">Desk Tool</bdi>", page);
        Assert.Equal(2, Regex.Matches(page, "<form [^>]*data-consent").Count);
    }

    // --- The connect page ---

    [Fact]
    public async Task The_connect_page_lists_a_persons_own_grants_counts_them_and_revokes_only_their_own()
    {
        await using var a = await OAuthApi.NewAsync(server);
        var desk = await a.RegisterAsync("Desk Tool");
        var alices = await a.ConnectAsync(a.World.Alice, desk);
        var carols = await a.ConnectAsync(a.World.Carol, desk);
        var alice = await Api.SessionAsync(a.Http, "alice", ApiWorld.AlicePassword);

        var page = await TextAsync(a.Http, alice, ConnectPages.Path);
        Assert.Contains("<h2>Assistants that asked to connect</h2>", page);
        Assert.Matches(GrantRow("Desk Tool", desk, "live"), page);
        Assert.Contains($"value=\"{alices.GrantId}\"", page);
        Assert.DoesNotContain(carols.GrantId, page);
        Assert.Contains("Connected now: 1 of the 2 this deployment allows each person, counting the assistants that asked to connect.", page);

        // Somebody else's grant gets the answer a grant that does not exist gets, and stays live.
        var others = await ErrorAsync(await PostFormAsync(a.Http, alice, ConnectPages.GrantRevokePath, ("grant", carols.GrantId)));
        var nobodys = await ErrorAsync(await PostFormAsync(a.Http, alice, ConnectPages.GrantRevokePath, ("grant", "no-such-grant")));
        Assert.Equal(nobodys, others);
        Assert.Equal(OAuthGrantStatus.Live, Assert.Single(await Grants(a).ListOwnAsync(a.World.Carol.Id, default)).Status);

        // Her own: the list says it is revoked and offers nothing more, and its assistant's next call reaches nothing.
        using (var revoked = await PostFormAsync(a.Http, alice, ConnectPages.GrantRevokePath, ("grant", alices.GrantId)))
        {
            Assert.Equal(HttpStatusCode.Redirect, revoked.StatusCode);
            Assert.Contains("done=", revoked.Headers.Location!.OriginalString);
        }
        page = await TextAsync(a.Http, alice, ConnectPages.Path);
        Assert.Matches(GrantRow("Desk Tool", desk, "revoked (revoked by its owner)"), page);
        Assert.DoesNotContain($"value=\"{alices.GrantId}\"", page);
        Assert.Contains("Connected now: 0 of the 2", page);
        using (var call = await a.Http.SendAsync(OAuthApi.McpPost(alices.Access)))
            Assert.Equal(HttpStatusCode.Unauthorized, call.StatusCode);
    }

    [Fact]
    public async Task The_connect_page_gives_the_servers_public_address_and_says_so_when_the_browser_used_another()
    {
        await using var a = await OAuthApi.NewAsync(server);
        var alice = await Api.SessionAsync(a.Http, "alice", ApiWorld.AlicePassword);
        const string Differs = "That is the server's public address, not the one your browser used for this page; give the assistant that one.";

        // The test's browser is at https://localhost, which is not the public address.
        var page = await TextAsync(a.Http, alice, ConnectPages.Path);
        Assert.Contains($"give it the address <code>{OAuthApi.Resource}</code>, then approve it here when it asks.", page);
        Assert.DoesNotContain("https://localhost/mcp", page);
        Assert.Contains(Differs, page);
        Assert.Contains("An assistant that can only identify itself by a web address cannot connect here on its own; your administrator can register it.", page);
        Assert.DoesNotContain("gives you the client id", page);

        // A browser at the public address itself is told nothing about it.
        using var request = Api.Request(HttpMethod.Get, ConnectPages.Path, session: alice);
        request.Headers.Host = new Uri(OAuthApi.PublicUrl).Authority;
        using var response = await a.Http.SendAsync(request);
        var there = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains($"give it the address <code>{OAuthApi.Resource}</code>", there);
        Assert.DoesNotContain(Differs, there);
    }

    [Fact]
    public async Task With_self_registration_off_the_connect_page_says_the_administrator_gives_the_client_id()
    {
        await using var a = await OAuthApi.NewAsync(server, dynamicRegistration: false);
        var alice = await Api.SessionAsync(a.Http, "alice", ApiWorld.AlicePassword);

        var page = await TextAsync(a.Http, alice, ConnectPages.Path);
        Assert.Contains("Your administrator registers each assistant that connects this way, and gives you the client id to enter in it.", page);
        Assert.Contains($"<code>{OAuthApi.Resource}</code>", page);
    }

    [Fact]
    public async Task A_grant_takes_a_slot_so_a_person_at_the_bound_is_told_so_and_cannot_connect_another()
    {
        await using var a = await OAuthApi.NewAsync(server);
        await a.ConnectAsync(a.World.Alice, await a.RegisterAsync("Desk Tool"));
        var alice = await Api.SessionAsync(a.Http, "alice", ApiWorld.AlicePassword);
        using (var made = await PostFormAsync(a.Http, alice, ConnectPages.Path,
                   ("name", "alice-desk"), ("kind", "coding-tool"), ("model", "hosted"), ("vendor", "Invented Models")))
            Assert.Equal(HttpStatusCode.OK, made.StatusCode);

        // One grant and one agent of her own: two of two, though her own table lists one.
        var page = await TextAsync(a.Http, alice, ConnectPages.Path);
        Assert.Contains("You have 2 assistant(s) connected, which is as many as this deployment allows each person (2).", page);
        Assert.DoesNotContain("action=\"/portal/connect\"", page);
        var refused = await ErrorAsync(await PostFormAsync(a.Http, alice, ConnectPages.Path,
            ("name", "alice-laptop"), ("kind", "coding-tool"), ("model", "hosted"), ("vendor", "Invented Models")));
        Assert.Contains("You have 2 connected assistants", refused);
    }

    [Fact]
    public async Task An_own_agent_says_whether_it_is_live_ended_by_a_disable_or_without_a_usable_token()
    {
        await using var p = await PortalWorld.NewAsync(server);
        var own = new SelfServeAgents(p.World.Db, p.World.Tenant);
        Task Connect(string name) =>
            own.CreateAsync(p.World.Alice.Id, new SelfServeAgentRequest(name, "hosted", "Invented Models", "coding-tool"), default);
        await Connect("alice-desk");
        await Connect("alice-laptop");

        // A disable and an enable end the desk's token for good, and an
        // administrator revokes the laptop's one token: both slots are free again,
        // so a third fits under a bound of two.
        foreach (var enabled in new[] { "off", "on" })
            using (await p.PostAsync("/portal/agents/alice-desk/enabled", p.Admin, [("enabled", enabled)])) { }
        var laptop = await p.World.Identity.FindAgentByNameAsync("alice-laptop");
        var token = Assert.Single(await p.World.Identity.ListTokensAsync(laptop!.Id, default));
        using (await p.PostAsync($"/portal/tokens/{token.Id}/revoke", p.Admin)) { }
        await Connect("alice-phone");

        var page = await p.TextAsync(await p.GetAsync(ConnectPages.Path, p.Member));
        Assert.Matches(OwnRow("alice-desk", "ended by a disable or a password change"), page);
        Assert.Matches(OwnRow("alice-laptop", "its token expired or was revoked"), page);
        Assert.Matches(OwnRow("alice-phone", "live"), page);
        // Only the live one holds a slot, and with the flow off no grant is counted or listed.
        Assert.Contains("Connected now: 1 of the 2 this deployment allows each person.", page);
        Assert.DoesNotContain("asked to connect", page);
    }

    // --- Reading nothing of the flow while it is off ---

    [Fact]
    public async Task While_off_the_connect_and_health_pages_read_no_table_of_the_flow_and_its_revoke_is_not_mapped()
    {
        var w = await ApiWorld.NewAsync(server);
        var (connection, clock) = (w.ConnectionString, w.Clock);
        await w.DisposeAsync();
        var before = await OAuthTableCounts.ReadWhenAloneAsync(connection);

        await using (var host = ApiTestHost.Start(connection, clock, new SeededEmbeddingProvider()))
        {
            using var http = host.Client(true, new PassThrough());
            var alice = await Api.SessionAsync(http, "alice", ApiWorld.AlicePassword);
            var carol = await Api.SessionAsync(http, "carol", ApiWorld.CarolPassword);

            var connect = await TextAsync(http, alice, ConnectPages.Path);
            Assert.Contains("<h2>Your assistants</h2>", connect);
            Assert.DoesNotContain("asked to connect", connect);
            var health = await TextAsync(http, carol, "/portal/health");
            Assert.Contains("<dt>Chunks</dt>", health);
            Assert.DoesNotContain("MCP authorization", health);

            using var revoke = await PostFormAsync(http, alice, ConnectPages.GrantRevokePath, ("grant", "g"));
            using var never = await PostFormAsync(http, alice, "/portal/no-such-page", ("grant", "g"));
            Assert.Equal(never.StatusCode, revoke.StatusCode);
            Assert.Equal(await never.Content.ReadAsStringAsync(), await revoke.Content.ReadAsStringAsync());
        }

        Assert.Equal(before, await OAuthTableCounts.ReadWhenAloneAsync(connection));
    }

    [Fact]
    public async Task While_on_the_same_count_sees_the_connect_page_read_the_grants_and_nothing_before_it()
    {
        var a = await OAuthApi.NewAsync(server);
        await a.ConnectAsync(a.World.Alice, await a.RegisterAsync("Desk Tool"));
        var (connection, clock) = (a.World.ConnectionString, a.World.Clock);
        await a.DisposeAsync();

        // A host with the flow on, a sign-in and a search: none of it reads a grant.
        var first = await OAuthTableCounts.ReadWhenAloneAsync(connection);
        ApiSession alice;
        await using (var host = ApiTestHost.Start(connection, clock, new SeededEmbeddingProvider()))
        {
            using var http = host.Client(true, new PassThrough());
            alice = await Api.SessionAsync(http, "alice", ApiWorld.AlicePassword);
            Assert.Contains("<h1>Search</h1>", await TextAsync(http, alice, "/portal/search"));
        }
        var second = await OAuthTableCounts.ReadWhenAloneAsync(connection);
        Assert.Equal(first["oauth_grant"], second["oauth_grant"]);

        // The connect page under the same session does.
        await using (var host = ApiTestHost.Start(connection, clock, new SeededEmbeddingProvider()))
        {
            using var http = host.Client(true, new PassThrough());
            Assert.Contains("<h2>Assistants that asked to connect</h2>", await TextAsync(http, alice, ConnectPages.Path));
        }
        var third = await OAuthTableCounts.ReadWhenAloneAsync(connection);
        Assert.True(third["oauth_grant"].Reads > second["oauth_grant"].Reads,
            $"the grant table was read {second["oauth_grant"].Reads} times before the connect page and {third["oauth_grant"].Reads} after it");
    }

    // --- The health page ---

    [Fact]
    public async Task The_health_page_says_the_flow_is_on_with_its_counts_and_the_last_refused_registration()
    {
        await using var a = await OAuthApi.NewAsync(server, settings: (OAuthSettings.RegistrationsPerHour, "2"));
        var desk = await a.RegisterAsync("Desk Tool", address: "10.0.0.9");
        await a.ConnectAsync(a.World.Alice, desk);
        var carol = await Api.SessionAsync(a.Http, "carol", ApiWorld.CarolPassword);

        var page = await TextAsync(a.Http, carol, "/portal/health");
        var line = FlowLine(page);
        Assert.StartsWith($"on, for the public address <code>{OAuthApi.PublicUrl}</code>.", line);
        Assert.Contains("1 client(s) registered, 0 of at most 100 registered", line);
        Assert.Contains("1 live grant(s).", line);
        Assert.Contains("No self-registration refused at a limit since this process started.", line);
        Assert.Contains($"href=\"{OAuthPaths.Clients}\"", line);
        Assert.Contains($"href=\"{OAuthPaths.Grants}\"", line);

        // The address's third registration in the hour is refused, and the page says when.
        await a.RegisterAsync("Other Tool", address: "10.0.0.9");
        using (var third = await a.RegisterRawAsync("Third Tool", address: "10.0.0.9"))
            Assert.Equal(HttpStatusCode.TooManyRequests, third.StatusCode);
        line = FlowLine(await TextAsync(a.Http, carol, "/portal/health"));
        Assert.Contains($"A self-registration was last refused at one of the limits at {Portal.Html.Layout.WhenText(a.World.Clock.Now)}.", line);
        Assert.DoesNotContain("No self-registration refused", line);
    }

    [Fact]
    public async Task The_health_page_says_which_way_the_settings_and_this_process_differ_and_nothing_while_both_are_off()
    {
        // Off in both: no line.
        await using (var p = await PortalWorld.NewAsync(server))
        {
            Assert.DoesNotContain("MCP authorization", await p.TextAsync(await p.GetAsync("/portal/health", p.Admin)));

            // Turned on in the settings after this process started: it runs after a restart.
            await OAuthApi.TurnOnAsync(p.World.Db, p.World.Tenant);
            var line = FlowLine(await p.TextAsync(await p.GetAsync("/portal/health", p.Admin)));
            Assert.StartsWith("on in the settings, and off in this process, which read them at start.", line);
            Assert.Contains("0 client(s) registered", line);
            Assert.DoesNotContain("href=", line);
            Assert.DoesNotContain("self-registration", line);
        }

        // Turned off in the settings while this process runs it: on until a restart.
        await using var a = await OAuthApi.NewAsync(server);
        var carol = await Api.SessionAsync(a.Http, "carol", ApiWorld.CarolPassword);
        await new SettingsStore(a.World.Db, a.World.Tenant).SetAsync(OAuthSettings.Enabled, OAuthApi.Json("false"));
        var running = FlowLine(await TextAsync(a.Http, carol, "/portal/health"));
        Assert.StartsWith("off in the settings, and still on in this process until it restarts.", running);
        Assert.DoesNotContain("client(s) registered", running);
        Assert.Contains($"href=\"{OAuthPaths.Grants}\"", running);
    }

    // --- Pieces ---

    private static OAuthGrants Grants(OAuthApi a) => new(a.World.Db, a.World.Tenant, a.Deployment, a.World.Clock);

    private static async Task<string> TextAsync(HttpClient http, ApiSession session, string path)
    {
        using var response = await http.SendAsync(Api.Request(HttpMethod.Get, path, session: session));
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{path} answered {(int)response.StatusCode}");
        return text;
    }

    /// <summary>A form post the way a portal page sends it: the anti-forgery token as the hidden field, from the portal's origin.</summary>
    private static Task<HttpResponseMessage> PostFormAsync(HttpClient http, ApiSession session, string path, params (string Name, string Value)[] fields)
    {
        var request = Api.Request(HttpMethod.Post, path, session: session, antiForgery: false);
        request.Content = new FormUrlEncodedContent(fields.Append(("prem_antiforgery", session.AntiForgeryToken))
            .Select(f => new KeyValuePair<string, string>(f.Item1, f.Item2)));
        request.Headers.Add("Origin", PortalWorld.Origin);
        return http.SendAsync(request);
    }

    /// <summary>The error a change came back with, from the redirect that carries it.</summary>
    private static async Task<string> ErrorAsync(HttpResponseMessage response)
    {
        using (response)
        {
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            return OAuthWorld.Query(response.Headers.Location!.OriginalString, "error");
        }
    }

    /// <summary>The health page's line for the flow, from its facts list.</summary>
    private static string FlowLine(string page)
    {
        var match = Regex.Match(page, "<dt>MCP authorization</dt><dd>(.*?)</dd>", RegexOptions.Singleline);
        Assert.True(match.Success, "the health page has no line for the flow");
        return Regex.Replace(match.Groups[1].Value, @"\s+", " ").Trim();
    }

    /// <summary>A row of the connect page's grant list: the client as the lists show it, then the grant's state.</summary>
    private static Regex GrantRow(string client, string id, string state) =>
        new($"<tr><td><bdi class=\"client\">{Regex.Escape(client)}</bdi><br><span class=\"note\">registered itself</span> <code>{Regex.Escape(id)}</code></td><td>{Regex.Escape(state)}</td>");

    /// <summary>A row of the connect page's own agents, with the agent's state in its second-to-last cell.</summary>
    private static Regex OwnRow(string agent, string state) =>
        new($"<tr><td>{Regex.Escape(agent)}</td>(?:<td>[^<]*(?:<span[^>]*>[^<]*</span>)?[^<]*</td>)*?<td>{Regex.Escape(state)}</td>");
}

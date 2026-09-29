using System.Net;
using System.Text.RegularExpressions;
using Premagentic.Core.Admin;
using Premagentic.Core.Identity;

namespace Premagentic.Tests;

/// <summary>
/// The Reissue and Remove buttons: a person's own on the connect page, and an
/// administrator's on the agent page, each after a page that asks. Through the
/// real API host; every name, question and token here is invented. Requires a
/// running Docker daemon.
/// </summary>
public sealed class PortalReissueRemoveTests(DatastoreTestDatabase server) : IClassFixture<DatastoreTestDatabase>
{
    /// <summary>A window around every event a test makes, whatever the clocks say.</summary>
    private const string Year = "from=2026-01-01&to=2026-12-31";

    private static readonly Regex Token = new(Regex.Escape(AgentTokens.Prefix) + "[A-Za-z0-9_-]+");

    private static readonly (string, string)[] Yes = [("confirm", "yes")];

    private const string ConfirmField = "<input type=\"hidden\" name=\"confirm\" value=\"yes\">";

    // --- The connect page ---

    [Fact]
    public async Task A_live_own_assistant_offers_a_new_key_revoke_and_remove_and_a_revoked_one_only_remove()
    {
        await using var p = await PortalWorld.NewAsync(server);
        using (await p.PostAsync("/portal/connect", p.Member, Connect("alice-live"))) { }
        using (await p.PostAsync("/portal/connect", p.Member, Connect("alice-gone"))) { }
        var own = await OwnAsync(p, p.World.Alice.Id);
        var live = own.Single(a => a.Name == "alice-live");
        var gone = own.Single(a => a.Name == "alice-gone");
        using (var revoke = await p.PostAsync($"/portal/connect/{gone.AgentId}/revoke", p.Member))
            Assert.Contains("done=", revoke.Headers.Location!.OriginalString);

        var page = await p.TextAsync(await p.GetAsync("/portal/connect", p.Member));
        Assert.Equal(["reissue", "revoke", "remove"], Actions(page, live.AgentId));
        Assert.Equal(["remove"], Actions(page, gone.AgentId));
        Assert.Contains(">Reissue key</button>", page);
        Assert.Contains(">Remove</button>", page);

        // A revoked one is removed the same way, and leaves the list.
        using (var removed = await p.PostAsync($"/portal/connect/{gone.AgentId}/remove", p.Member, Yes))
            Assert.Contains("done=", removed.Headers.Location!.OriginalString);
        Assert.Equal(["alice-live"], (await OwnAsync(p, p.World.Alice.Id)).Select(a => a.Name));
        Assert.DoesNotContain("alice-gone", await p.TextAsync(await p.GetAsync("/portal/connect", p.Member)));
    }

    [Fact]
    public async Task A_new_key_asks_first_then_is_shown_once_and_the_old_key_reaches_nothing()
    {
        await using var p = await PortalWorld.NewAsync(server);
        var old = Token.Match(await p.TextAsync(await p.PostAsync("/portal/connect", p.Member, Connect("alice-desk", "coding-tool")))).Value;
        var id = Assert.Single(await OwnAsync(p, p.World.Alice.Id)).AgentId;
        var before = await ChangesAsync(p);

        // Asked first: a page with a second form that says yes, and nothing changed.
        using (var ask = await p.PostAsync($"/portal/connect/{id}/reissue", p.Member))
        {
            Assert.Equal(HttpStatusCode.OK, ask.StatusCode);
            var text = await ask.Content.ReadAsStringAsync();
            Assert.Contains("Give 'alice-desk' a new key?", WebUtility.HtmlDecode(text));
            Assert.Contains($"action=\"/portal/connect/{id}/reissue\"", text);
            Assert.Contains(ConfirmField, text);
            Assert.DoesNotMatch(Token, text);
        }
        Assert.Equal(before, await ChangesAsync(p));
        Assert.Equal(HttpStatusCode.OK, (await Api.SearchAsync(p.Client, bearer: old)).StatusCode);

        // Said yes: the new key, once, with its configuration.
        using var yes = await p.PostAsync($"/portal/connect/{id}/reissue", p.Member, Yes);
        Assert.Equal(HttpStatusCode.OK, yes.StatusCode);
        Assert.Contains("no-store", yes.Headers.CacheControl!.ToString());
        var page = await yes.Content.ReadAsStringAsync();
        var fresh = Assert.Single(Token.Matches(page).Select(m => m.Value).Distinct());
        Assert.NotEqual(old, fresh);
        Assert.Contains("This is the only time the token is shown.", page);
        Assert.Contains("'alice-desk' has a new key", WebUtility.HtmlDecode(page));
        Assert.Contains("id=\"snippet\"", page);

        // The old key reaches nothing on its next call; the new one reaches what
        // alice may read, and not what she may not.
        Assert.Equal(HttpStatusCode.Unauthorized, (await Api.SearchAsync(p.Client, bearer: old)).StatusCode);
        var paths = await Api.PathsAsync(await Api.SearchAsync(p.Client, bearer: fresh));
        Assert.Contains(ApiWorld.Handbook, paths);
        Assert.DoesNotContain(ApiWorld.AuditLog, paths);

        // The same assistant, still live, one change recorded, and no later page shows the key.
        var after = Assert.Single(await OwnAsync(p, p.World.Alice.Id));
        Assert.Equal((id, OwnAgentState.Live), (after.AgentId, after.State));
        Assert.Equal(1, await p.ScalarAsync($"SELECT count(*) FROM prem_config.admin_event WHERE id > {before} AND kind = '{AgentLifecycle.ReissueKind}'"));
        foreach (var (path, session) in new[]
        {
            ("/portal/connect", p.Member), ("/portal/agents", p.Admin), ("/portal/agents/alice-desk", p.Admin),
            ("/portal/changes", p.Admin), ("/portal/export/changes.jsonl", p.Admin), ("/portal/audit", p.Admin),
        })
        {
            var text = await p.TextAsync(await p.GetAsync(path, session));
            Assert.DoesNotMatch(Token, text);
        }
        // The control: the page alice returns to names the assistant.
        Assert.Contains("alice-desk", await p.TextAsync(await p.GetAsync("/portal/connect", p.Member)));
    }

    [Fact]
    public async Task A_remove_asks_first_then_takes_the_row_off_and_the_history_still_names_it()
    {
        await using var p = await PortalWorld.NewAsync(server);
        var token = Token.Match(await p.TextAsync(await p.PostAsync("/portal/connect", p.Member, Connect("alice-cloud")))).Value;
        Assert.Equal(HttpStatusCode.OK, (await Api.SearchAsync(p.Client, bearer: token)).StatusCode);
        var id = Assert.Single(await OwnAsync(p, p.World.Alice.Id)).AgentId;
        var before = await ChangesAsync(p);
        // The control for the marks below: before the removal, the history names it unmarked.
        Assert.Contains("agent alice-cloud for alice", await p.TextAsync(await p.GetAsync("/portal/audit", p.Auditor)));
        Assert.Contains("<td>alice-cloud</td>", await p.TextAsync(await p.GetAsync($"/portal/usage?{Year}", p.Auditor)));

        using (var ask = await p.PostAsync($"/portal/connect/{id}/remove", p.Member))
        {
            Assert.Equal(HttpStatusCode.OK, ask.StatusCode);
            var text = await ask.Content.ReadAsStringAsync();
            Assert.Contains("Remove 'alice-cloud'?", WebUtility.HtmlDecode(text));
            Assert.Contains($"action=\"/portal/connect/{id}/remove\"", text);
            Assert.Contains(ConfirmField, text);
        }
        Assert.Equal(before, await ChangesAsync(p));
        Assert.Single(await OwnAsync(p, p.World.Alice.Id));
        Assert.Equal(HttpStatusCode.OK, (await Api.SearchAsync(p.Client, bearer: token)).StatusCode);

        using (var yes = await p.PostAsync($"/portal/connect/{id}/remove", p.Member, Yes))
            Assert.Contains("done=", yes.Headers.Location!.OriginalString);
        Assert.Empty(await OwnAsync(p, p.World.Alice.Id));
        Assert.DoesNotContain("alice-cloud", await p.TextAsync(await p.GetAsync("/portal/connect", p.Member)));
        Assert.Equal(HttpStatusCode.Unauthorized, (await Api.SearchAsync(p.Client, bearer: token)).StatusCode);

        // The history still names it, marked removed, and the removed view says whose it was and who removed it.
        Assert.Contains("agent alice-cloud (removed) for alice", await p.TextAsync(await p.GetAsync("/portal/audit", p.Auditor)));
        Assert.Contains("<td>alice-cloud (removed)</td>", await p.TextAsync(await p.GetAsync($"/portal/usage?{Year}", p.Auditor)));
        var removed = await p.TextAsync(await p.GetAsync("/portal/agents?view=removed", p.Admin));
        Assert.Contains("<td>alice-cloud</td><td>alice</td>", removed);
        Assert.Contains("portal (user alice)", removed);
        Assert.Equal(1, await p.ScalarAsync($"SELECT count(*) FROM prem_config.admin_event WHERE id > {before} AND kind = '{AgentLifecycle.RemoveKind}'"));
    }

    [Fact]
    public async Task A_person_can_neither_reissue_nor_remove_another_persons_assistant()
    {
        await using var p = await PortalWorld.NewAsync(server);
        var carolsToken = Token.Match(await p.TextAsync(await p.PostAsync("/portal/connect", p.Admin, Connect("carol-desk")))).Value;
        using (await p.PostAsync("/portal/connect", p.Member, Connect("alice-desk"))) { }
        var carols = Assert.Single(await OwnAsync(p, p.World.Carol.Id));
        var alices = Assert.Single(await OwnAsync(p, p.World.Alice.Id));
        var before = await ChangesAsync(p);
        var rows = await CredentialRowsAsync(p, carols.AgentId, alices.AgentId);

        // Asked or said yes, a reissue or a remove of another person's assistant
        // is refused with the one sentence, and never names it.
        foreach (var (who, session, id) in new[] { ("alice", p.Member, carols.AgentId), ("carol", p.Admin, alices.AgentId) })
            foreach (var action in new[] { "reissue", "remove" })
                foreach (var fields in new[] { Array.Empty<(string, string)>(), Yes })
                {
                    using var post = await p.PostAsync($"/portal/connect/{id}/{action}", session, fields);
                    var location = Uri.UnescapeDataString(post.Headers.Location?.OriginalString ?? "");
                    Assert.True(location.Contains(SelfServeAgents.NotYours, StringComparison.Ordinal),
                        $"{who} posting {action} {(fields.Length == 0 ? "unasked" : "said yes")} gave {(int)post.StatusCode} {location}");
                    var body = await post.Content.ReadAsStringAsync();
                    Assert.DoesNotContain("carol-desk", body);
                    Assert.DoesNotContain("alice-desk", body);
                }

        // Nothing written, exactly: no change recorded, and both agents, their
        // tokens and their people's credential generations as they were.
        Assert.Equal(before, await ChangesAsync(p));
        Assert.Equal(rows, await CredentialRowsAsync(p, carols.AgentId, alices.AgentId));
        Assert.Equal(OwnAgentState.Live, Assert.Single(await OwnAsync(p, p.World.Carol.Id)).State);
        Assert.Equal(OwnAgentState.Live, Assert.Single(await OwnAsync(p, p.World.Alice.Id)).State);
        Assert.Equal(HttpStatusCode.OK, (await Api.SearchAsync(p.Client, bearer: carolsToken)).StatusCode);

        // The control: the owner, on the same route, is asked.
        using var own = await p.PostAsync($"/portal/connect/{carols.AgentId}/reissue", p.Admin);
        Assert.Equal(HttpStatusCode.OK, own.StatusCode);
        Assert.Contains("Give 'carol-desk' a new key?", WebUtility.HtmlDecode(await own.Content.ReadAsStringAsync()));
    }

    [Fact]
    public async Task Signed_out_from_another_site_without_the_form_token_or_as_an_agent_the_connect_buttons_are_refused()
    {
        await using var p = await PortalWorld.NewAsync(server);
        var token = Token.Match(await p.TextAsync(await p.PostAsync("/portal/connect", p.Member, Connect("alice-desk")))).Value;
        var id = Assert.Single(await OwnAsync(p, p.World.Alice.Id)).AgentId;
        var before = await ChangesAsync(p);

        foreach (var action in new[] { "reissue", "remove" })
        {
            var path = $"/portal/connect/{id}/{action}";
            using (var signedOut = await p.PostAsync(path, session: null, Yes))
                Assert.Equal(HttpStatusCode.Unauthorized, signedOut.StatusCode);
            Assert.True(await PortalRoutes.IsRefusedAsync(await p.PostAsync(path, p.Member, Yes, origin: "https://elsewhere.example")));
            Assert.True(await PortalRoutes.IsRefusedAsync(await p.PostAsync(path, p.Member, Yes, tokenField: false)));
            var request = Api.Request(HttpMethod.Post, path, bearer: token);
            request.Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["confirm"] = "yes" });
            request.Headers.Add("Origin", PortalWorld.Origin);
            using (var agent = await p.Client.SendAsync(request))
                Assert.Equal(HttpStatusCode.Forbidden, agent.StatusCode);
        }

        Assert.Equal(before, await ChangesAsync(p));
        Assert.Equal(OwnAgentState.Live, Assert.Single(await OwnAsync(p, p.World.Alice.Id)).State);
        Assert.Equal(HttpStatusCode.OK, (await Api.SearchAsync(p.Client, bearer: token)).StatusCode);
    }

    // --- The agent page ---

    [Fact]
    public async Task An_administrator_sees_reissue_beside_each_usable_token_and_remove_agent_and_a_reader_sees_neither()
    {
        await using var p = await PortalWorld.NewAsync(server);
        var first = Assert.Single(await p.World.Identity.ListTokensAsync(p.World.Bot.Id));

        var page = await p.TextAsync(await p.GetAsync("/portal/agents/report-bot", p.Admin));
        Assert.Contains($"action=\"/portal/tokens/{first.Id}/reissue\"", page);
        Assert.Contains($"action=\"/portal/tokens/{first.Id}/revoke\"", page);
        Assert.Contains("action=\"/portal/agents/report-bot/remove\"", page);
        Assert.Contains(">Reissue</button>", page);
        Assert.Contains(">Remove agent</button>", page);

        // A reader is shown neither, and a post from a reader or a member is refused with nothing changed.
        var auditor = await p.TextAsync(await p.GetAsync("/portal/agents/report-bot", p.Auditor));
        Assert.DoesNotContain("/reissue\"", auditor);
        Assert.DoesNotContain("/remove\"", auditor);
        var before = await ChangesAsync(p);
        foreach (var session in new[] { p.Member, p.Auditor })
            foreach (var path in new[] { $"/portal/tokens/{first.Id}/reissue", "/portal/agents/report-bot/remove" })
                foreach (var fields in new[] { Array.Empty<(string, string)>(), Yes })
                {
                    using var post = await p.PostAsync(path, session, fields);
                    Assert.True(post.StatusCode == HttpStatusCode.Forbidden, $"POST {path} gave {(int)post.StatusCode}");
                }
        Assert.Equal(before, await ChangesAsync(p));
        Assert.Equal(HttpStatusCode.OK, (await Api.SearchAsync(p.Client, bearer: p.World.BotToken)).StatusCode);

        // A revoked token has no Reissue beside it; the live one beside it has.
        using (await p.PostAsync("/portal/agents/report-bot/tokens", p.Admin, [("days", "7")])) { }
        var second = (await p.World.Identity.ListTokensAsync(p.World.Bot.Id)).Single(t => t.Id != first.Id);
        using (await p.PostAsync($"/portal/tokens/{first.Id}/revoke", p.Admin)) { }
        page = await p.TextAsync(await p.GetAsync("/portal/agents/report-bot", p.Admin));
        Assert.DoesNotContain($"/portal/tokens/{first.Id}/reissue\"", page);
        Assert.Contains($"/portal/tokens/{second.Id}/reissue\"", page);

        // Nor has a token of a disabled agent, whose Remove agent stays.
        using (await p.PostAsync("/portal/agents/report-bot/enabled", p.Admin, [("enabled", "off")])) { }
        page = await p.TextAsync(await p.GetAsync("/portal/agents/report-bot", p.Admin));
        Assert.DoesNotContain("/reissue\"", page);
        Assert.Contains("action=\"/portal/agents/report-bot/remove\"", page);

        // Nor a token of an agent whose person is disabled.
        var assistant = Assert.Single(await p.World.Identity.ListTokensAsync(p.World.Assistant.Id));
        Assert.Contains($"/portal/tokens/{assistant.Id}/reissue\"", await p.TextAsync(await p.GetAsync("/portal/agents/assistant", p.Admin)));
        using (await p.PostAsync("/portal/users/alice/enabled", p.Admin, [("enabled", "off")])) { }
        page = await p.TextAsync(await p.GetAsync("/portal/agents/assistant", p.Admin));
        Assert.DoesNotContain("/reissue\"", page);
        Assert.Contains($"/portal/tokens/{assistant.Id}/revoke\"", page);
    }

    [Fact]
    public async Task A_token_reissue_asks_first_then_shows_the_new_one_once_and_the_old_one_reaches_nothing()
    {
        await using var p = await PortalWorld.NewAsync(server);
        var old = Assert.Single(await p.World.Identity.ListTokensAsync(p.World.Bot.Id));
        var before = await ChangesAsync(p);

        using (var ask = await p.PostAsync($"/portal/tokens/{old.Id}/reissue", p.Admin))
        {
            Assert.Equal(HttpStatusCode.OK, ask.StatusCode);
            var text = await ask.Content.ReadAsStringAsync();
            Assert.Contains($"Reissue token {old.Id} of 'report-bot'?", WebUtility.HtmlDecode(text));
            Assert.Contains($"action=\"/portal/tokens/{old.Id}/reissue\"", text);
            Assert.Contains(ConfirmField, text);
            Assert.DoesNotMatch(Token, text);
        }
        Assert.Equal(before, await ChangesAsync(p));
        Assert.Equal(HttpStatusCode.OK, (await Api.SearchAsync(p.Client, bearer: p.World.BotToken)).StatusCode);

        using var yes = await p.PostAsync($"/portal/tokens/{old.Id}/reissue", p.Admin, Yes);
        Assert.Equal(HttpStatusCode.OK, yes.StatusCode);
        Assert.Contains("no-store", yes.Headers.CacheControl!.ToString());
        var page = await yes.Content.ReadAsStringAsync();
        var fresh = Assert.Single(Token.Matches(page).Select(m => m.Value).Distinct());
        Assert.NotEqual(p.World.BotToken, fresh);
        Assert.Contains("This is the only time the token is shown.", page);
        Assert.Contains("A new token for 'report-bot'", WebUtility.HtmlDecode(page));
        Assert.Contains($"Token <code>{old.Id}</code> is revoked", page);

        Assert.Equal(HttpStatusCode.Unauthorized, (await Api.SearchAsync(p.Client, bearer: p.World.BotToken)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Api.SearchAsync(p.Client, bearer: fresh)).StatusCode);
        var tokens = await p.World.Identity.ListTokensAsync(p.World.Bot.Id);
        Assert.Equal(2, tokens.Count);
        Assert.NotNull(tokens.Single(t => t.Id == old.Id).RevokedAt);
        Assert.Equal(1, await p.ScalarAsync($"SELECT count(*) FROM prem_config.admin_event WHERE id > {before} AND kind = '{AgentLifecycle.ReissueKind}'"));
        foreach (var path in new[] { "/portal/agents/report-bot", "/portal/agents", "/portal/changes", "/portal/export/changes.jsonl" })
            Assert.DoesNotMatch(Token, await p.TextAsync(await p.GetAsync(path, p.Admin)));

        // Said yes again, the replaced token is refused with the reason, and a
        // token that is not there is refused too; neither writes anything.
        var after = await ChangesAsync(p);
        using (var again = await p.PostAsync($"/portal/tokens/{old.Id}/reissue", p.Admin, Yes))
            Assert.Contains(AgentLifecycle.TokenRevoked(old.Id), Uri.UnescapeDataString(again.Headers.Location!.OriginalString));
        using (var none = await p.PostAsync("/portal/tokens/prem-nothing-here/reissue", p.Admin, Yes))
            Assert.Contains(AgentLifecycle.NoSuchToken, Uri.UnescapeDataString(none.Headers.Location!.OriginalString));
        Assert.Equal(after, await ChangesAsync(p));
    }

    [Fact]
    public async Task Remove_agent_asks_first_then_takes_it_off_the_list_and_the_removed_view_and_the_history_name_it()
    {
        await using var p = await PortalWorld.NewAsync(server);
        Assert.Equal(HttpStatusCode.OK, (await Api.SearchAsync(p.Client, bearer: p.World.BotToken)).StatusCode);
        var before = await ChangesAsync(p);

        using (var ask = await p.PostAsync("/portal/agents/report-bot/remove", p.Admin))
        {
            Assert.Equal(HttpStatusCode.OK, ask.StatusCode);
            var text = await ask.Content.ReadAsStringAsync();
            Assert.Contains("Remove agent 'report-bot'?", WebUtility.HtmlDecode(text));
            Assert.Contains("action=\"/portal/agents/report-bot/remove\"", text);
            Assert.Contains(ConfirmField, text);
        }
        Assert.Equal(before, await ChangesAsync(p));
        Assert.Contains("href=\"/portal/agents/report-bot\"", await p.TextAsync(await p.GetAsync("/portal/agents", p.Admin)));
        Assert.Equal(HttpStatusCode.OK, (await Api.SearchAsync(p.Client, bearer: p.World.BotToken)).StatusCode);

        using (var yes = await p.PostAsync("/portal/agents/report-bot/remove", p.Admin, Yes))
            Assert.Contains("done=", yes.Headers.Location!.OriginalString);
        var list = await p.TextAsync(await p.GetAsync("/portal/agents", p.Admin));
        Assert.DoesNotContain("report-bot", list);
        Assert.Contains("href=\"/portal/agents/assistant\"", list);
        using (var gone = await p.GetAsync("/portal/agents/report-bot", p.Admin))
            Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Api.SearchAsync(p.Client, bearer: p.World.BotToken)).StatusCode);
        Assert.Equal(1, await p.ScalarAsync($"SELECT count(*) FROM prem_config.admin_event WHERE id > {before} AND kind = '{AgentLifecycle.RemoveKind}'"));

        // The removed view: its name, whose it was, when and by whom; no link and no form.
        foreach (var session in new[] { p.Admin, p.Auditor })
        {
            var removed = await p.TextAsync(await p.GetAsync("/portal/agents?view=removed", session));
            var main = removed[removed.IndexOf("<main>", StringComparison.Ordinal)..];
            Assert.Contains("<td>report-bot</td><td>carol</td>", main);
            Assert.Contains("portal (user carol)", main);
            Assert.Contains("<strong>the removed ones</strong>", main);
            Assert.DoesNotContain("href=\"/portal/agents/report-bot", main);
            Assert.DoesNotContain("<form", main);
            Assert.DoesNotContain("href=\"/portal/agents/assistant\"", main);
        }

        // The history still names it, marked removed.
        Assert.Contains("agent report-bot (removed)", await p.TextAsync(await p.GetAsync("/portal/audit", p.Auditor)));
        Assert.Contains("<td>report-bot (removed)</td>", await p.TextAsync(await p.GetAsync($"/portal/usage?{Year}", p.Auditor)));

        // Said yes again, there is nothing to remove, and nothing is written.
        var after = await ChangesAsync(p);
        using (var again = await p.PostAsync("/portal/agents/report-bot/remove", p.Admin, Yes))
            Assert.Contains("error=", again.Headers.Location!.OriginalString);
        Assert.Equal(after, await ChangesAsync(p));
    }

    /// <summary>
    /// The agents' rows, their tokens' rows and their people's credential
    /// generations, as one text: every column that says whether a credential
    /// still works, and never a token's hash.
    /// </summary>
    private static async Task<string> CredentialRowsAsync(PortalWorld p, params Guid[] agents)
    {
        await using var cmd = p.World.Db.DataSource.CreateCommand("""
            SELECT coalesce((SELECT string_agg(concat_ws(',', a.id, a.name, a.disabled, a.credential_generation, a.deleted_at, a.deleted_by), '|' ORDER BY a.id)
                             FROM prem_config.agent a WHERE a.id = ANY(@agents)), '')
                || '#' || coalesce((SELECT string_agg(concat_ws(',', t.id, t.agent_id, t.created_at, t.expires_at, t.revoked_at, t.last_used_at,
                                                                 t.user_generation, t.agent_generation), '|' ORDER BY t.id)
                                    FROM prem_config.agent_token t WHERE t.agent_id = ANY(@agents)), '')
                || '#' || coalesce((SELECT string_agg(concat_ws(',', u.id, u.disabled, u.credential_generation), '|' ORDER BY u.id)
                                    FROM prem_config.app_user u WHERE u.id IN (SELECT owner_user_id FROM prem_config.agent WHERE id = ANY(@agents))), '')
            """);
        cmd.Parameters.AddWithValue("agents", agents);
        var text = (string)(await cmd.ExecuteScalarAsync())!;
        // The control: the snapshot holds both agents and a token of each, so an equal pair compares something.
        Assert.Equal(agents.Length, text.Split('#')[0].Split('|').Length);
        Assert.True(text.Split('#')[1].Split('|').Length >= agents.Length);
        return text;
    }

    private static Task<long> ChangesAsync(PortalWorld p) => p.ScalarAsync("SELECT coalesce(max(id), 0) FROM prem_config.admin_event");

    /// <summary>
    /// A person's own assistants, read on the world's clock as the host reads
    /// them: the system clock would find every key of the world expired.
    /// </summary>
    private static Task<IReadOnlyList<OwnAgent>> OwnAsync(PortalWorld p, Guid userId) =>
        new SelfServeAgents(p.World.Db, p.World.Tenant, oauth: null, time: p.World.Clock).ListAsync(userId, CancellationToken.None);

    /// <summary>The actions a connect page row offers for one assistant, in the order they appear.</summary>
    private static string[] Actions(string page, Guid id) =>
        [.. Regex.Matches(page, $"action=\"/portal/connect/{id}/([a-z]+)\"").Select(m => m.Groups[1].Value)];

    private static (string, string)[] Connect(string name, string kind = "other") =>
        [("name", name), ("kind", kind), ("model", "hosted"), ("vendor", "Invented Models")];
}

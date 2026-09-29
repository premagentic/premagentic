using System.Text.RegularExpressions;
using Premagentic.Core.Identity;

namespace Premagentic.Tests;

/// <summary>
/// The agents page says who made each agent and can show only the ones people
/// made for themselves. Every name is invented. Requires a running Docker daemon.
/// </summary>
public sealed class PortalAgentsOriginTests(DatastoreTestDatabase server) : IClassFixture<DatastoreTestDatabase>
{
    [Fact]
    public async Task The_agents_page_says_who_made_each_agent_and_filters_to_the_self_made()
    {
        await using var p = await PortalWorld.NewAsync(server);
        await new SelfServeAgents(p.World.Db, p.World.Tenant).CreateAsync(
            p.World.Alice.Id, new SelfServeAgentRequest("alice-desk", "hosted", "Invented Models", "coding-tool"), CancellationToken.None);
        using (await p.PostAsync("/portal/agents", p.Admin,
            [("name", "carol-helper"), ("owner", "alice"), ("mode", "service"), ("rate", "30"), ("minTier", ""), ("model", "local")])) { }

        var every = await p.TextAsync(await p.GetAsync("/portal/agents", p.Auditor));
        Assert.Matches(Row("alice-desk", "by alice, for themself"), every);
        Assert.Matches(Row("carol-helper", "in the portal, by carol"), every);
        Assert.Contains("report-bot", every);

        var selfMade = await p.TextAsync(await p.GetAsync("/portal/agents?made=self", p.Auditor));
        Assert.Matches(Row("alice-desk", "by alice, for themself"), selfMade);
        Assert.DoesNotContain("carol-helper", selfMade);
        Assert.DoesNotContain(">report-bot<", selfMade);
        Assert.Contains("<strong>the ones people made for themselves</strong>", selfMade);
    }

    [Fact]
    public async Task An_assistant_approved_through_the_flow_is_made_through_it_and_its_page_points_at_its_grant()
    {
        await using var a = await OAuthApi.NewAsync(server);
        var desk = await a.RegisterAsync("Desk Tool");
        var connection = await a.ConnectAsync(a.World.Alice, desk);
        var agent = (await a.World.Identity.FindAgentAsync(connection.AgentId, CancellationToken.None))!;
        var carol = await Api.SessionAsync(a.Http, "carol", ApiWorld.CarolPassword);
        async Task<string> Page(string path)
        {
            using var response = await a.Http.SendAsync(Api.Request(HttpMethod.Get, path, session: carol));
            return await response.Content.ReadAsStringAsync();
        }

        Assert.Equal("oauth:" + desk, agent.Origin!.Text);
        Assert.Matches(Row(agent.Name, $"through the assistant {desk}"), await Page("/portal/agents"));
        Assert.Matches(Row(agent.Name, $"through the assistant {desk}"), await Page("/portal/agents?made=self"));

        // Its page says where its credentials are, links the grants, and issues nothing.
        var page = await Page("/portal/agents/" + agent.Name);
        Assert.Contains("This assistant's credentials belong to its grant, and end when the grant is revoked or ends.", page);
        Assert.Contains($"<a href=\"{OAuthPaths.Grants}\">See the grants</a>", page);
        Assert.DoesNotContain("Issue a token", page);
        Assert.DoesNotContain("<th>Token id</th>", page);

        // The control: an agent made another way still lists its tokens and can be issued one.
        var assistant = await Page("/portal/agents/assistant");
        Assert.Contains("Issue a token", assistant);
        Assert.Contains("<th>Token id</th>", assistant);
        Assert.DoesNotContain("belong to its grant", assistant);
    }

    [Fact]
    public void Each_origin_reads_as_words_and_an_unrecorded_one_says_so()
    {
        var alice = new UserAccount(Guid.NewGuid(), "alice", "Alice", Role.Member, false, true, DateTimeOffset.UnixEpoch);
        var users = new Dictionary<Guid, UserAccount> { [alice.Id] = alice };

        Assert.Equal("by alice, for themself", Portal.Pages.AgentPages.MadeBy(AgentOrigin.Self(alice.Id), users));
        Assert.Equal("in the portal, by alice", Portal.Pages.AgentPages.MadeBy(AgentOrigin.Portal(alice.Id), users));
        Assert.Equal("at the command line, by ops", Portal.Pages.AgentPages.MadeBy(AgentOrigin.Cli("ops"), users));
        Assert.Equal("by the profile starter", Portal.Pages.AgentPages.MadeBy(AgentOrigin.Profile("starter"), users));
        Assert.Equal("not recorded", Portal.Pages.AgentPages.MadeBy(AgentOrigin.Unknown, users));
        Assert.Equal("not recorded", Portal.Pages.AgentPages.MadeBy(null, users));
        Assert.Equal("by a user who no longer signs in, for themself", Portal.Pages.AgentPages.MadeBy(AgentOrigin.Self(Guid.NewGuid()), users));
    }

    /// <summary>An agents table row naming the agent, with who made it in its last cell.</summary>
    private static Regex Row(string agent, string madeBy) =>
        new($"<tr><td><a href=\"[^\"]*\">{Regex.Escape(agent)}</a></td>.*?<td>{Regex.Escape(madeBy)}</td></tr>");
}

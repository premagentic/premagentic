using Premagentic.Core.Extensions;
using Premagentic.Core.Identity;
using Premagentic.Core.Security;
using Premagentic.Core.Security.Acl;
using Premagentic.Core.Sources;
using Premagentic.Core.Sources.Registry;

namespace Premagentic.Tests;

/// <summary>
/// Portal surfaces: the audit filter over what left the
/// network, the health page's account of what is loaded and what this
/// deployment was configured from, the source owner and the hosted-model
/// switch, and moving an agent's model. Every data item is invented. Requires
/// a running Docker daemon.
/// </summary>
public sealed class PortalAuditHealthAndSourcesTests(DatastoreTestDatabase server) : IClassFixture<DatastoreTestDatabase>
{
    private static async Task<(Agent Agent, string Token)> AgentAsync(
        PortalWorld p, string name, ModelLocation where, string? vendor = null)
    {
        var owner = (await p.World.Identity.FindUserByNameAsync("alice"))!;
        var agent = await p.World.Identity.CreateAgentAsync(name, owner.Id, AgentMode.ActsForUser, 600, null, where, vendor);
        var token = await p.World.Identity.IssueTokenAsync(agent.Id, TimeSpan.FromDays(30));
        return (agent, token.PlainText);
    }

    [Fact]
    public async Task The_audit_filter_separates_what_left_the_network_from_what_did_not()
    {
        await using var p = await PortalWorld.NewAsync(server);
        var (_, hostedToken) = await AgentAsync(p, "desk-hosted", ModelLocation.Hosted, "a vendor");
        var (_, localToken) = await AgentAsync(p, "shop-local", ModelLocation.Local);

        using (await Api.SearchAsync(p.Client, "zeppelin", bearer: hostedToken)) { }
        using (await Api.SearchAsync(p.Client, "zeppelin", bearer: localToken)) { }

        // Unfiltered, both reads are there, and the column says which is which.
        var all = await p.TextAsync(await p.GetAsync("/portal/audit", p.Auditor));
        Assert.Contains("desk-hosted", all);
        Assert.Contains("shop-local", all);
        Assert.Contains("to a hosted-model agent", all);

        // A row past the filter, and a row inside it. Both directions, because
        // "what never left" is as much a question as its opposite when somebody
        // is checking a claim about a folder.
        var left = await p.TextAsync(await p.GetAsync("/portal/audit?left=yes", p.Auditor));
        Assert.Contains("desk-hosted", left);
        Assert.DoesNotContain("shop-local", left);

        var stayed = await p.TextAsync(await p.GetAsync("/portal/audit?left=no", p.Auditor));
        Assert.Contains("shop-local", stayed);
        Assert.DoesNotContain("desk-hosted", stayed);
    }

    [Fact]
    public async Task Paging_a_filtered_audit_keeps_the_filter_so_older_rows_are_still_the_filtered_ones()
    {
        await using var p = await PortalWorld.NewAsync(server);
        var (_, hostedToken) = await AgentAsync(p, "desk-hosted", ModelLocation.Hosted, "a vendor");
        var (_, localToken) = await AgentAsync(p, "shop-local", ModelLocation.Local);

        // More than one page of reads that left the network, and one that did not.
        for (var i = 0; i < 51; i++)
            using (await Api.SearchAsync(p.Client, "zeppelin", bearer: hostedToken)) { }
        using (await Api.SearchAsync(p.Client, "zeppelin", bearer: localToken)) { }

        var filtered = await p.TextAsync(await p.GetAsync("/portal/audit?left=yes", p.Auditor));

        // The link to older rows carries the filter. Without it, page two would
        // quietly answer a different question from page one.
        Assert.Contains("left=yes", filtered);
        Assert.Contains("before=", filtered);
        Assert.DoesNotContain("shop-local", filtered);
    }

    [Fact]
    public async Task The_health_page_tells_apart_no_folder_an_empty_folder_and_a_refusal()
    {
        // The host this API composes when nobody configured a folder. The
        // fourth state, a process that composed no host at all, is defensive:
        // the API always registers one, so it cannot be reached from here.
        await using (var noFolder = await PortalWorld.NewAsync(server))
            Assert.Contains("No extensions folder is configured",
                await noFolder.TextAsync(await noFolder.GetAsync("/portal/health", noFolder.Auditor)));

        // A folder that holds nothing, which is not the same as not having one.
        var empty = Directory.CreateTempSubdirectory("prem-empty-extensions-").FullName;
        await using (var emptyFolder = await PortalWorld.NewAsync(
                         server, options: new ApiHostOptions { Extensions = ExtensionHost.Load(empty, []) }))
            Assert.Contains("Nothing found in", await emptyFolder.TextAsync(await emptyFolder.GetAsync("/portal/health", emptyFolder.Auditor)));

        // A folder whose extension was refused: the state that must never read
        // as "nothing loaded", because somebody's allow list is wrong.
        var refusing = Directory.CreateTempSubdirectory("prem-refused-extensions-").FullName;
        SampleExtension.InstallInto(refusing);
        await using var refused = await PortalWorld.NewAsync(
            server, options: new ApiHostOptions { Extensions = ExtensionHost.Load(refusing, []) });
        var page = await refused.TextAsync(await refused.GetAsync("/portal/health", refused.Auditor));
        Assert.Contains("Refused", page);
        Assert.Contains("not allowed", page);
        Assert.Contains(SampleExtension.Name, page);
    }

    [Fact]
    public async Task The_health_page_names_the_profile_and_says_why_when_it_cannot_compare()
    {
        await using var p = await PortalWorld.NewAsync(server);
        Assert.Contains("No profile has been applied",
            await p.TextAsync(await p.GetAsync("/portal/health", p.Auditor)));

        // Applied from a folder that is then taken away: the page must say it
        // cannot compare, not that nothing has drifted.
        var folder = Directory.CreateTempSubdirectory("prem-vanishing-profile-").FullName;
        File.WriteAllText(Path.Combine(folder, "profile.json"), """{ "name": "office", "version": "1" }""");
        Assert.True(Core.Profiles.ProfileReader.TryRead(folder, out var profile, out _));
        var (plan, _) = await Core.Profiles.ProfilePlanner.BuildAsync(
            profile!, p.World.Db, p.World.Tenant, Path.GetTempPath());
        await Core.Profiles.ProfileApply.RunAsync(plan!, p.World.Db, p.World.Tenant, new Core.Admin.AdminActor("cli", "tester"));

        Assert.Contains("none: the settings, sources and rules match the profile",
            await p.TextAsync(await p.GetAsync("/portal/health", p.Auditor)));

        Directory.Delete(folder, recursive: true);
        Assert.Contains("not compared:", await p.TextAsync(await p.GetAsync("/portal/health", p.Auditor)));
    }

    [Fact]
    public async Task The_source_page_shows_and_turns_the_hosted_model_switch()
    {
        await using var p = await PortalWorld.NewAsync(server);
        var corpus = SourcesTests.Folder(("rota.md", "# Rota\n\n## Weekend\nTwo people open the yard on Saturdays.\n"));
        using (await p.PostAsync("/portal/sources", p.Admin,
                   [("name", "yard"), ("folder", corpus), ("prefix", "yard"), ("chunker", "markdown")])) { }
        using (await p.PostAsync("/portal/permissions/rules", p.Admin,
                   [("source", "filesystem"), ("prefix", "yard"), ("entries", "allow everyone")])) { }

        Assert.Contains("may be served to hosted models", await p.TextAsync(await p.GetAsync("/portal/sources/yard", p.Auditor)));

        // The form sends nothing when the box is clear, which is the switch off.
        using (var off = await p.PostAsync("/portal/sources/yard/hosted", p.Admin))
            Assert.DoesNotContain("error=", off.Headers.Location?.OriginalString ?? "");

        Assert.Contains("never served to hosted-model agents", await p.TextAsync(await p.GetAsync("/portal/sources/yard", p.Auditor)));
        // A hold beside the rule, and the rule left exactly as it was written.
        Assert.Contains(new HostedHold("filesystem", "yard"), await HostedHolds.ListAsync(p.World.Db, p.World.Tenant));
        var rule = (await new AclStore(p.World.Db, p.World.Tenant).ListRulesAsync())
            .Single(x => x.Rule.PathPrefix == "yard");
        Assert.Equal(["allow everyone"], rule.Rule.Acl.Entries.Select(e => e.ToString()));

        // And back, which releases the hold.
        using (await p.PostAsync("/portal/sources/yard/hosted", p.Admin, [("hosted", "on")])) { }
        Assert.Contains("may be served to hosted models", await p.TextAsync(await p.GetAsync("/portal/sources/yard", p.Auditor)));
        Assert.Empty(await HostedHolds.ListAsync(p.World.Db, p.World.Tenant));
    }

    [Fact]
    public async Task The_source_page_names_an_owner_and_the_owner_decides_nothing()
    {
        await using var p = await PortalWorld.NewAsync(server);
        var corpus = SourcesTests.Folder(("rota.md", "# Rota\n\n## Weekend\nTwo people open the yard on Saturdays.\n"));
        using (await p.PostAsync("/portal/sources", p.Admin,
                   [("name", "yard"), ("folder", corpus), ("prefix", "yard"), ("chunker", "markdown")])) { }
        using (await p.PostAsync("/portal/permissions/rules", p.Admin,
                   [("source", "filesystem"), ("prefix", "yard"), ("entries", "allow everyone")])) { }
        var alice = (await p.World.Identity.FindUserByNameAsync("alice"))!;

        Assert.Contains("<dt>Owner</dt><dd>nobody</dd>", await p.TextAsync(await p.GetAsync("/portal/sources/yard", p.Auditor)));

        using (await p.PostAsync("/portal/sources/yard/set", p.Admin, [("chunker", "markdown"), ("owner", alice.Id.ToString())])) { }

        var source = (await new SourceRegistry(p.World.Db, p.World.Tenant).FindAsync("yard"))!;
        Assert.Equal(alice.Id, source.OwnerUserId);
        Assert.Contains($"<dt>Owner</dt><dd>{alice.Name}</dd>", await p.TextAsync(await p.GetAsync("/portal/sources/yard", p.Auditor)));
        // The rule is untouched: an owner is a person to ask, not a permission.
        Assert.Equal("allow everyone\n",
            (await new AclStore(p.World.Db, p.World.Tenant).ListRulesAsync()).Single(x => x.Rule.PathPrefix == "yard").Rule.Acl.CanonicalText);
    }

    [Fact]
    public async Task Moving_an_agents_model_moves_it_in_and_out_of_the_reserved_group()
    {
        await using var p = await PortalWorld.NewAsync(server);
        var (agent, _) = await AgentAsync(p, "desk", ModelLocation.Local);

        using (var hosted = await p.PostAsync(
                   "/portal/agents/desk/model", p.Admin, [("model", "hosted"), ("vendor", "a vendor")]))
            Assert.DoesNotContain("error=", hosted.Headers.Location?.OriginalString ?? "");

        Assert.Equal(ModelLocation.Hosted, (await p.World.Identity.FindAgentAsync(agent.Id))!.ModelLocation);
        Assert.Contains(SystemGroups.HostedModelAgentsName,
            (await p.World.Identity.SystemGroupsOfAgentAsync(agent.Id)).Select(g => g.Name));

        // A local model has no vendor, and saying it has one is refused rather
        // than quietly dropped.
        using (var refused = await p.PostAsync(
                   "/portal/agents/desk/model", p.Admin, [("model", "local"), ("vendor", "a vendor")]))
            Assert.Contains("error=", refused.Headers.Location?.OriginalString ?? "");
        Assert.Equal(ModelLocation.Hosted, (await p.World.Identity.FindAgentAsync(agent.Id))!.ModelLocation);

        using (await p.PostAsync("/portal/agents/desk/model", p.Admin, [("model", "local")])) { }

        Assert.Equal(ModelLocation.Local, (await p.World.Identity.FindAgentAsync(agent.Id))!.ModelLocation);
        Assert.DoesNotContain(SystemGroups.HostedModelAgentsName,
            (await p.World.Identity.SystemGroupsOfAgentAsync(agent.Id)).Select(g => g.Name));
    }
}

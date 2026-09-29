using System.Net;

namespace Premagentic.Tests;

/// <summary>
/// An agent already in the state asked for is not written: the store reports
/// it as no change, so the portal's agent form, which records on a change,
/// puts no row in the change record for it, as <c>prem agents enable</c> and
/// <c>disable</c> do.
/// Requires a running Docker daemon.
/// </summary>
public sealed class PortalAgentCompareFirstTests(DatastoreTestDatabase server) : IClassFixture<DatastoreTestDatabase>
{
    [Fact]
    public async Task Enabling_an_enabled_agent_or_disabling_a_disabled_one_records_nothing()
    {
        await using var portal = await PortalWorld.NewAsync(server);
        var identity = portal.World.Identity;

        // The store: a no-op is no change, a real change is one.
        Assert.False(await identity.SetAgentDisabledAsync(portal.World.Assistant.Id, false));
        Assert.True(await identity.SetAgentDisabledAsync(portal.World.Assistant.Id, true));
        Assert.False(await identity.SetAgentDisabledAsync(portal.World.Assistant.Id, true));
        Assert.True(await identity.SetAgentDisabledAsync(portal.World.Assistant.Id, false));

        // The form: the assistant is enabled; disable the bot through the store
        // so that disabling it again through the form is a no-op.
        await identity.SetAgentDisabledAsync(portal.World.Bot.Id, true);
        await PostEnabledAsync(portal, "assistant", "on");
        await PostEnabledAsync(portal, "report-bot", "off");
        Assert.Equal(0, await RowsAsync(portal, "agent.enable"));
        Assert.Equal(0, await RowsAsync(portal, "agent.disable"));

        // The control: a real change through the form is made and recorded, once each way.
        await PostEnabledAsync(portal, "report-bot", "on");
        await PostEnabledAsync(portal, "assistant", "off");
        Assert.Equal(1, await RowsAsync(portal, "agent.enable"));
        Assert.Equal(1, await RowsAsync(portal, "agent.disable"));
        Assert.True((await identity.FindAgentAsync(portal.World.Assistant.Id))!.Disabled);
        Assert.False((await identity.FindAgentAsync(portal.World.Bot.Id))!.Disabled);
    }

    private static async Task PostEnabledAsync(PortalWorld portal, string name, string enabled)
    {
        using var response = await portal.PostAsync($"/portal/agents/{name}/enabled", portal.Admin, [("enabled", enabled)]);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
    }

    private static Task<long> RowsAsync(PortalWorld portal, string kind) =>
        portal.ScalarAsync($"SELECT count(*) FROM prem_config.admin_event WHERE kind = '{kind}'");
}

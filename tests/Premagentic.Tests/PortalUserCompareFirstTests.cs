using System.Net;

namespace Premagentic.Tests;

/// <summary>
/// The portal's user form compares before it writes, as <c>prem users enable</c>
/// and <c>disable</c> do: an account already in the state asked for is not
/// written and puts no row in the change record.
/// Requires a running Docker daemon.
/// </summary>
public sealed class PortalUserCompareFirstTests(DatastoreTestDatabase server) : IClassFixture<DatastoreTestDatabase>
{
    [Fact]
    public async Task Enabling_an_enabled_user_or_disabling_a_disabled_one_records_nothing()
    {
        await using var portal = await PortalWorld.NewAsync(server);

        // Alice is enabled and Eve is disabled in the shared world.
        await PostEnabledAsync(portal, "alice", "on");
        await PostEnabledAsync(portal, "eve", "off");
        Assert.Equal(0, await RowsAsync(portal, "user.enable"));
        Assert.Equal(0, await RowsAsync(portal, "user.disable"));

        // The control: a real change is made and recorded, once each way.
        await PostEnabledAsync(portal, "bob", "off");
        await PostEnabledAsync(portal, "bob", "on");
        Assert.Equal(1, await RowsAsync(portal, "user.disable"));
        Assert.Equal(1, await RowsAsync(portal, "user.enable"));
        Assert.False((await portal.World.Identity.FindUserAsync(portal.World.Bob.Id))!.Disabled);
    }

    private static async Task PostEnabledAsync(PortalWorld portal, string name, string enabled)
    {
        using var response = await portal.PostAsync($"/portal/users/{name}/enabled", portal.Admin, [("enabled", enabled)]);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
    }

    private static Task<long> RowsAsync(PortalWorld portal, string kind) =>
        portal.ScalarAsync($"SELECT count(*) FROM prem_config.admin_event WHERE kind = '{kind}'");
}

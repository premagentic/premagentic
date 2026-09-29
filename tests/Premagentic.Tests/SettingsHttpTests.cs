using Premagentic.Core.Admin;
using Premagentic.Core.Okf;

namespace Premagentic.Tests;

/// <summary>
/// The trust settings as the API applies them: a change made in the database
/// changes the very next search over HTTP, for an agent presenting a token and
/// for a person signed in, in both directions, with no restart and no re-ingest.
/// Requires a running Docker daemon.
/// </summary>
public sealed class SettingsHttpTests(DatastoreTestDatabase server) : IClassFixture<DatastoreTestDatabase>
{
    // Agent-written and unverified, in the bundle ApiWorld reads under "okf",
    // where everyone may read it.
    private const string Pest = "okf/care/pest-scouting.md";
    private const string Query = "yellow cards every Monday count caught";

    private static readonly AdminActor Admin = new("cli", "test-account");

    [Fact]
    public async Task A_setting_change_changes_the_next_search_over_http_for_an_agent_and_for_a_person()
    {
        await using var world = await ApiWorld.NewAsync(server, okfBundle: true);
        using var client = world.Host.Client();
        var alice = await Api.SessionAsync(client, "alice", ApiWorld.AlicePassword);
        var settings = new TrustSettingsStore(world.Db, world.Tenant);

        async Task<bool> AgentSees() =>
            (await Api.PathsAsync(await Api.SearchAsync(client, Query, bearer: world.AssistantToken))).Contains(Pest);
        async Task<bool> PersonSees() =>
            (await Api.PathsAsync(await Api.SearchAsync(client, Query, session: alice))).Contains(Pest);
        async Task<short> LastAuditedTierAsync()
        {
            await using var cmd = world.Db.DataSource.CreateCommand(
                "SELECT trust_minimum_tier FROM prem_config.retrieval_event ORDER BY created_at DESC LIMIT 1");
            return (short)(await cmd.ExecuteScalarAsync())!;
        }

        // The defaults: the agent does not see it, the person does.
        Assert.False(await AgentSees());
        Assert.True(await PersonSees());

        await settings.SetAsync(TrustSettingsStore.AgentsMinimumTier, "unverified", Admin);
        Assert.True(await AgentSees());
        Assert.Equal((short)OkfTrustTier.Unverified, await LastAuditedTierAsync());

        await settings.SetAsync(TrustSettingsStore.PeopleMinimumTier, "human-reviewed", Admin);
        Assert.False(await PersonSees());
        Assert.Equal((short)OkfTrustTier.HumanReviewed, await LastAuditedTierAsync());

        await settings.SetAsync(TrustSettingsStore.AgentsMinimumTier, "human-reviewed", Admin);
        Assert.False(await AgentSees());

        await settings.SetAsync(TrustSettingsStore.PeopleMinimumTier, "unverified", Admin);
        Assert.True(await PersonSees());
    }
}

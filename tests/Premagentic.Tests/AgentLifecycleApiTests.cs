using System.Net;
using Premagentic.Core.Admin;

namespace Premagentic.Tests;

/// <summary>
/// A removed assistant over the wire: its token reaches nothing on its next
/// call, over the HTTP API and over MCP, and a reissued key replaces the old
/// one there too. Requires a running Docker daemon.
/// </summary>
public sealed class AgentLifecycleApiTests(DatastoreTestDatabase server) : IClassFixture<DatastoreTestDatabase>
{
    private static HttpRequestMessage Mcp(string token) =>
        Api.Request(HttpMethod.Post, "/mcp", new { jsonrpc = "2.0", id = 1, method = "tools/list" }, bearer: token);

    [Fact]
    public async Task A_removed_assistants_token_reaches_nothing_on_its_next_call_over_http_and_mcp()
    {
        await using var w = await ApiWorld.NewAsync(server);
        using var client = w.Host.Client();

        // The control: the token is taken on both surfaces before the removal.
        using (var search = await Api.SearchAsync(client, bearer: w.BotToken)) Assert.Equal(HttpStatusCode.OK, search.StatusCode);
        using (var mcp = await client.SendAsync(Mcp(w.BotToken))) Assert.NotEqual(HttpStatusCode.Unauthorized, mcp.StatusCode);

        await new AdminChanges(w.Db, w.Tenant, w.Clock).RunAsync(AdminActor.Cli(), change => AgentLifecycle.RemoveAsync(change, w.Bot.Id));

        using (var search = await Api.SearchAsync(client, bearer: w.BotToken)) Assert.Equal(HttpStatusCode.Unauthorized, search.StatusCode);
        using (var mcp = await client.SendAsync(Mcp(w.BotToken))) Assert.Equal(HttpStatusCode.Unauthorized, mcp.StatusCode);
        // Another agent's token is untouched.
        using (var other = await Api.SearchAsync(client, bearer: w.AssistantToken)) Assert.Equal(HttpStatusCode.OK, other.StatusCode);
    }

    [Fact]
    public async Task A_reissued_key_is_taken_over_http_and_the_one_it_replaced_is_not()
    {
        await using var w = await ApiWorld.NewAsync(server);
        using var client = w.Host.Client();
        var oldId = w.BotToken["prem_agt_".Length..][..24];

        var reissued = await new AdminChanges(w.Db, w.Tenant, w.Clock).RunAsync(AdminActor.Cli(),
            change => AgentLifecycle.ReissueAsync(change, oldId));

        using (var fresh = await Api.SearchAsync(client, bearer: reissued.New.PlainText)) Assert.Equal(HttpStatusCode.OK, fresh.StatusCode);
        using (var stale = await Api.SearchAsync(client, bearer: w.BotToken)) Assert.Equal(HttpStatusCode.Unauthorized, stale.StatusCode);
        using (var mcp = await client.SendAsync(Mcp(w.BotToken))) Assert.Equal(HttpStatusCode.Unauthorized, mcp.StatusCode);
    }
}

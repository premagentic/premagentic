using Premagentic.Cli.Admin;
using Premagentic.Core.Admin;
using Premagentic.Core.Identity;
using Premagentic.Core.Storage;

namespace Premagentic.Tests;

/// <summary>
/// <c>prem tokens reissue</c> and <c>prem agents remove</c> as an operator
/// uses them: the new token printed once, the agent form refused unless it
/// names one live token, and the removed agents listed apart. Requires a
/// running Docker daemon.
/// </summary>
[Collection(ConsoleCollection.Name)]
public sealed class AgentLifecycleCliTests(DatastoreTestDatabase server) : IClassFixture<DatastoreTestDatabase>
{
    private async Task<(PremagenticDatabase Db, Guid Tenant, IdentityStore Store, Agent Svc)> NewAsync()
    {
        var db = new PremagenticDatabase(await server.CreateDatabaseAsync());
        await db.InitializeAsync();
        var tenant = await db.EnsureTenantAsync("t", "T");
        var store = new IdentityStore(db, tenant);
        var alice = await store.CreateUserAsync("alice", "Alice", Role.Member);
        var svc = await store.CreateAgentAsync("svc", alice.Id, AgentMode.Service, 60, null, ModelLocation.Local);
        return (db, tenant, store, svc);
    }

    [Fact]
    public async Task A_reissue_by_agent_name_prints_the_new_token_once_and_needs_exactly_one_live_token()
    {
        var (db, tenant, store, svc) = await NewAsync();
        await using var _ = db;
        var old = await store.IssueTokenAsync(svc.Id, TimeSpan.FromDays(30));

        var run = await ConsoleCapture.RunAsync(() => AdminCommands.RunAsync(["tokens", "reissue", "--agent", "svc", "--days", "7"], db, tenant));

        Assert.True(run.Exit == 0, run.Err);
        var tokens = (await store.ListTokensAsync(svc.Id)).ToList();
        var fresh = Assert.Single(tokens, t => t.RevokedAt is null);
        Assert.Contains($"Token {fresh.Id} for agent 'svc' replaces token {old.Record.Id}, now revoked", run.Out, StringComparison.Ordinal);
        Assert.Equal(1, run.Out.Split('\n').Count(line => line.StartsWith("prem_agt_", StringComparison.Ordinal)));
        Assert.Equal(TimeSpan.FromDays(7), fresh.ExpiresAt - fresh.CreatedAt);

        // Two live tokens: the name alone does not say which.
        await store.IssueTokenAsync(svc.Id, TimeSpan.FromDays(30));
        var two = await ConsoleCapture.RunAsync(() => AdminCommands.RunAsync(["tokens", "reissue", "--agent", "svc"], db, tenant));
        Assert.Equal(1, two.Exit);
        Assert.Contains("Agent 'svc' has 2 live tokens. Give the token id.", two.Err, StringComparison.Ordinal);

        // A refusal from the lifecycle reaches the operator as it is.
        var revoked = await ConsoleCapture.RunAsync(() => AdminCommands.RunAsync(["tokens", "reissue", old.Record.Id], db, tenant));
        Assert.Equal(1, revoked.Exit);
        Assert.Contains(AgentLifecycle.TokenRevoked(old.Record.Id), revoked.Err, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_removed_agent_leaves_the_list_and_is_listed_apart_with_when_and_by_whom()
    {
        var (db, tenant, store, _) = await NewAsync();
        await using var __ = db;

        var removed = await ConsoleCapture.RunAsync(() => AdminCommands.RunAsync(["agents", "remove", "svc"], db, tenant));
        Assert.True(removed.Exit == 0, removed.Err);
        Assert.Contains("Agent 'svc' removed; 0 token(s) revoked.", removed.Out, StringComparison.Ordinal);

        var live = await ConsoleCapture.RunAsync(() => AdminCommands.RunAsync(["agents", "list"], db, tenant));
        Assert.DoesNotContain("svc", live.Out, StringComparison.Ordinal);
        var gone = await ConsoleCapture.RunAsync(() => AdminCommands.RunAsync(["agents", "list", "--removed"], db, tenant));
        Assert.Contains("svc", gone.Out, StringComparison.Ordinal);
        Assert.Contains($"by {AdminActor.Cli().Describe()}", gone.Out, StringComparison.Ordinal);

        var again = await ConsoleCapture.RunAsync(() => AdminCommands.RunAsync(["agents", "remove", "svc"], db, tenant));
        Assert.Equal(1, again.Exit);
        Assert.Contains("There is no agent named 'svc'.", again.Err, StringComparison.Ordinal);
    }
}

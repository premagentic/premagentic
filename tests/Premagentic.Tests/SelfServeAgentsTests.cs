using System.Text.Json;
using Premagentic.Core.Admin;
using Premagentic.Core.Evaluation;
using Premagentic.Core.Identity;
using Premagentic.Core.Storage;

namespace Premagentic.Tests;

/// <summary>
/// The agents a person makes for themself on the connect page: each acts as
/// that person and nothing more, the number is bounded by a setting, a person
/// sees and revokes only their own, and every creation and revocation is in
/// the change record with the person as the actor. Requires a running Docker
/// daemon.
/// </summary>
public sealed class SelfServeAgentsTests(DatastoreTestDatabase server) : IClassFixture<DatastoreTestDatabase>
{
    private sealed record World(
        PremagenticDatabase Db, Guid Tenant, IdentityStore Store, SelfServeAgents Agents, User Alice, User Bob, Group Staff)
        : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }

    private static SelfServeAgentRequest Request(string name, string location = "local", string? vendor = null) =>
        new(name, location, vendor, "coding tool");

    private async Task<World> NewAsync()
    {
        var db = new PremagenticDatabase(await server.CreateDatabaseAsync());
        await db.InitializeAsync();
        var tenant = await db.EnsureTenantAsync("t", "T");
        var store = new IdentityStore(db, tenant);
        var alice = await store.CreateUserAsync("alice", "Alice", Role.Member);
        var bob = await store.CreateUserAsync("bob", "Bob", Role.Member);
        var staff = await store.CreateGroupAsync("Staff");
        await store.AddMemberAsync(staff.Id, alice.Id);
        return new World(db, tenant, store, new SelfServeAgents(db, tenant), alice, bob, staff);
    }

    private static async Task<List<(string Kind, string Target, Guid? Actor)>> RecordAsync(PremagenticDatabase db)
    {
        await using var cmd = db.DataSource.CreateCommand("SELECT kind, target, actor_user_id FROM prem_config.admin_event ORDER BY id");
        var rows = new List<(string, string, Guid?)>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            rows.Add((reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetGuid(2)));
        return rows;
    }

    private static Task SetBoundAsync(World w, int bound) =>
        new TuningSettingsStore(w.Db, w.Tenant).SetAsync(
            AgentSettings.SelfServiceMax, JsonSerializer.SerializeToElement(bound), AdminActor.Cli());

    [Fact]
    public async Task A_member_makes_an_agent_that_holds_exactly_what_they_hold()
    {
        await using var w = await NewAsync();
        var resolver = new CallerResolver(w.Store);

        var created = await w.Agents.CreateAsync(w.Alice.Id, Request("alice-laptop"), default);

        var asAgent = await resolver.ResolveAsync(CallerIdentity.AgentToken(created.Token));
        var asAlice = await resolver.ResolveAsync(CallerIdentity.SignedInUser(w.Alice.Id));
        Assert.Equal(CallerStatus.Resolved, asAgent.Status);
        Assert.Equal(
            asAlice.Principals.Select(p => p.ToString()).Order(StringComparer.Ordinal),
            asAgent.Principals.Select(p => p.ToString()).Order(StringComparer.Ordinal));
        Assert.Equal(w.Alice.Id, asAgent.UserId);
        Assert.Equal(created.AgentId, asAgent.AgentId);

        var agent = (await w.Store.FindAgentAsync(created.AgentId))!;
        Assert.Equal(AgentMode.ActsForUser, agent.Mode);
        Assert.Equal(w.Alice.Id, agent.OwnerUserId);
        Assert.Equal(SelfServeAgents.RatePerMinute, agent.RequestsPerMinute);
        Assert.DoesNotContain(created.Token, created.ToString());
    }

    [Fact]
    public async Task The_agent_cannot_be_made_to_act_for_a_group()
    {
        await using var w = await NewAsync();
        var created = await w.Agents.CreateAsync(w.Alice.Id, Request("alice-laptop"), default);

        // The request has no field for a group; the one route to one, a grant,
        // is refused for an agent that acts for a person.
        await Assert.ThrowsAsync<InvalidOperationException>(() => w.Store.GrantGroupAsync(created.AgentId, w.Staff.Id));
        Assert.Empty(await w.Store.GroupsGrantedToAgentAsync(created.AgentId));
    }

    [Fact]
    public async Task A_member_cannot_make_an_agent_for_another_person()
    {
        await using var w = await NewAsync();

        var created = await w.Agents.CreateAsync(w.Bob.Id, Request("bob-desk"), default);

        // Whoever is acting owns what they make; there is no owner to name.
        Assert.Equal(w.Bob.Id, (await w.Store.FindAgentAsync(created.AgentId))!.OwnerUserId);
        Assert.Empty(await w.Agents.ListAsync(w.Alice.Id, default));
    }

    [Fact]
    public async Task The_bound_refuses_one_more_and_zero_turns_the_form_off()
    {
        await using var w = await NewAsync();
        await SetBoundAsync(w, 2);
        await w.Agents.CreateAsync(w.Alice.Id, Request("one"), default);
        await w.Agents.CreateAsync(w.Alice.Id, Request("two"), default);
        var records = (await RecordAsync(w.Db)).Count;

        var over = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            w.Agents.CreateAsync(w.Alice.Id, Request("three"), default));
        Assert.Contains("allows 2 each", over.Message);
        Assert.Null(await w.Store.FindAgentByNameAsync("three"));
        Assert.Equal(records, (await RecordAsync(w.Db)).Count);

        // The bound is per person: Bob still has room.
        await w.Agents.CreateAsync(w.Bob.Id, Request("bob-one"), default);

        // A revoked agent frees its place.
        var first = (await w.Agents.ListAsync(w.Alice.Id, default)).Single(a => a.Name == "one");
        await w.Agents.RevokeAsync(w.Alice.Id, first.AgentId, default);
        await w.Agents.CreateAsync(w.Alice.Id, Request("three"), default);

        await SetBoundAsync(w, 0);
        var off = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            w.Agents.CreateAsync(w.Bob.Id, Request("bob-two"), default));
        Assert.Contains("turned off", off.Message);
    }

    [Fact]
    public async Task An_agent_an_administrator_registered_does_not_count_toward_the_bound_or_show_on_the_list()
    {
        await using var w = await NewAsync();
        await SetBoundAsync(w, 1);
        await w.Store.CreateAgentAsync("admin-made", w.Alice.Id, AgentMode.ActsForUser, 600, null, ModelLocation.Local);

        await w.Agents.CreateAsync(w.Alice.Id, Request("mine"), default);

        Assert.Equal(["mine"], (await w.Agents.ListAsync(w.Alice.Id, default)).Select(a => a.Name));
        var adminMade = (await w.Store.FindAgentByNameAsync("admin-made"))!;
        await Assert.ThrowsAsync<InvalidOperationException>(() => w.Agents.RevokeAsync(w.Alice.Id, adminMade.Id, default));
    }

    [Fact]
    public async Task A_revoked_token_reaches_nothing_on_the_next_call()
    {
        await using var w = await NewAsync();
        var resolver = new CallerResolver(w.Store);
        var created = await w.Agents.CreateAsync(w.Alice.Id, Request("alice-laptop"), default);
        Assert.True((await resolver.ResolveAsync(CallerIdentity.AgentToken(created.Token))).IsResolved);

        await w.Agents.RevokeAsync(w.Alice.Id, created.AgentId, default);

        var after = await resolver.ResolveAsync(CallerIdentity.AgentToken(created.Token));
        Assert.False(after.IsResolved);
        Assert.Empty(after.Principals);
        var listed = Assert.Single(await w.Agents.ListAsync(w.Alice.Id, default));
        Assert.True(listed.Revoked);
    }

    [Fact]
    public async Task A_member_cannot_list_or_revoke_another_persons_agent()
    {
        await using var w = await NewAsync();
        var resolver = new CallerResolver(w.Store);
        var bobs = await w.Agents.CreateAsync(w.Bob.Id, Request("bob-desk"), default);

        Assert.Empty(await w.Agents.ListAsync(w.Alice.Id, default));
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            w.Agents.RevokeAsync(w.Alice.Id, bobs.AgentId, default));
        // The same sentence as for an id that does not exist.
        var missing = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            w.Agents.RevokeAsync(w.Alice.Id, Guid.NewGuid(), default));
        Assert.Equal(missing.Message, refused.Message);

        Assert.True((await resolver.ResolveAsync(CallerIdentity.AgentToken(bobs.Token))).IsResolved);
        Assert.False(Assert.Single(await w.Agents.ListAsync(w.Bob.Id, default)).Revoked);
    }

    [Fact]
    public async Task Creation_and_revocation_are_recorded_with_the_person_as_the_actor()
    {
        await using var w = await NewAsync();

        var created = await w.Agents.CreateAsync(w.Alice.Id, Request("alice-cloud", "hosted", "a vendor"), default);
        await w.Agents.RevokeAsync(w.Alice.Id, created.AgentId, default);
        var afterFirst = (await RecordAsync(w.Db)).Count;
        await w.Agents.RevokeAsync(w.Alice.Id, created.AgentId, default);

        Assert.Equal(
            [
                ("agent.add", "alice-cloud", (Guid?)w.Alice.Id),
                ("token.issue", "alice-cloud", w.Alice.Id),
                ("token.revoke", "alice-cloud", w.Alice.Id),
                ("agent.disable", "alice-cloud", w.Alice.Id),
            ],
            await RecordAsync(w.Db));
        Assert.Equal(afterFirst, (await RecordAsync(w.Db)).Count);

        var listed = Assert.Single(await w.Agents.ListAsync(w.Alice.Id, default));
        Assert.Equal("hosted", listed.ModelLocation);
        Assert.Equal("a vendor", listed.ModelVendor);
        Assert.Equal("coding tool", listed.AssistantKind);
    }

    [Theory]
    [InlineData("hosted", null, "Name who runs the hosted model")]
    [InlineData("somewhere", null, "Say where the assistant's model runs")]
    [InlineData("local", "a vendor", "A local model has no vendor")]
    public async Task An_incomplete_request_is_refused_and_writes_nothing(string location, string? vendor, string said)
    {
        await using var w = await NewAsync();

        var refused = await Assert.ThrowsAsync<ArgumentException>(() =>
            w.Agents.CreateAsync(w.Alice.Id, Request("x", location, vendor), default));

        Assert.Contains(said, refused.Message);
        Assert.Empty(await w.Store.ListAgentsAsync());
        Assert.Empty(await RecordAsync(w.Db));
    }

    [Fact]
    public async Task A_disabled_person_cannot_make_an_agent()
    {
        await using var w = await NewAsync();
        await w.Store.SetUserDisabledAsync(w.Alice.Id, true);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            w.Agents.CreateAsync(w.Alice.Id, Request("x"), default));
        Assert.Empty(await w.Store.ListAgentsAsync());
    }

    [Fact]
    public async Task A_person_reissues_and_removes_only_their_own_assistant()
    {
        await using var w = await NewAsync();
        var resolver = new CallerResolver(w.Store);
        var own = await w.Agents.CreateAsync(w.Alice.Id, Request("alice-laptop"), default);
        var rows = (await RecordAsync(w.Db)).Count;

        // Bob gets the answer an assistant that does not exist gets, and nothing is written.
        foreach (var attempt in new Func<Task>[]
                 {
                     () => w.Agents.ReissueAsync(w.Bob.Id, own.AgentId, default),
                     () => w.Agents.RemoveAsync(w.Bob.Id, own.AgentId, default),
                 })
            Assert.Equal(SelfServeAgents.NotYours, (await Assert.ThrowsAsync<InvalidOperationException>(attempt)).Message);
        Assert.Equal(rows, (await RecordAsync(w.Db)).Count);
        Assert.Equal(CallerStatus.Resolved, (await resolver.ResolveAsync(CallerIdentity.AgentToken(own.Token))).Status);

        var reissued = await w.Agents.ReissueAsync(w.Alice.Id, own.AgentId, default);
        Assert.Equal(CallerStatus.Resolved, (await resolver.ResolveAsync(CallerIdentity.AgentToken(reissued.New.PlainText))).Status);
        Assert.Equal(CallerStatus.TokenRevoked, (await resolver.ResolveAsync(CallerIdentity.AgentToken(own.Token))).Status);
        Assert.Equal((AgentLifecycle.ReissueKind, "alice-laptop", (Guid?)w.Alice.Id), (await RecordAsync(w.Db))[^1]);

        await w.Agents.RemoveAsync(w.Alice.Id, own.AgentId, default);
        Assert.Empty(await w.Agents.ListAsync(w.Alice.Id, default));
        Assert.Equal(CallerStatus.TokenRevoked, (await resolver.ResolveAsync(CallerIdentity.AgentToken(reissued.New.PlainText))).Status);
        Assert.Equal((AgentLifecycle.RemoveKind, "alice-laptop", (Guid?)w.Alice.Id), (await RecordAsync(w.Db))[^1]);
    }

    [Fact]
    public async Task A_person_cannot_reissue_a_revoked_assistant_or_touch_one_an_administrator_made_but_removes_one_in_any_state()
    {
        await using var w = await NewAsync();

        // Revoked on the connect page: no live key, and no way back through a reissue.
        var revoked = await w.Agents.CreateAsync(w.Alice.Id, Request("old-laptop"), default);
        await w.Agents.RevokeAsync(w.Alice.Id, revoked.AgentId, default);
        Assert.Equal(SelfServeAgents.NoLiveKey,
            (await Assert.ThrowsAsync<InvalidOperationException>(() => w.Agents.ReissueAsync(w.Alice.Id, revoked.AgentId, default))).Message);

        // Two live keys, one an administrator issued: the person does not choose between them.
        var two = await w.Agents.CreateAsync(w.Alice.Id, Request("desk"), default);
        await w.Store.IssueTokenAsync(two.AgentId, TimeSpan.FromDays(30));
        Assert.Equal(SelfServeAgents.MoreThanOneKey,
            (await Assert.ThrowsAsync<InvalidOperationException>(() => w.Agents.ReissueAsync(w.Alice.Id, two.AgentId, default))).Message);

        // An agent an administrator made for Alice is not hers to change here.
        var made = await w.Store.CreateAgentAsync("made-for-alice", w.Alice.Id, AgentMode.ActsForUser, 60, null, ModelLocation.Local);
        await w.Store.IssueTokenAsync(made.Id, TimeSpan.FromDays(30));
        foreach (var attempt in new Func<Task>[]
                 {
                     () => w.Agents.ReissueAsync(w.Alice.Id, made.Id, default),
                     () => w.Agents.RemoveAsync(w.Alice.Id, made.Id, default),
                 })
            Assert.Equal(SelfServeAgents.NotYours, (await Assert.ThrowsAsync<InvalidOperationException>(attempt)).Message);
        Assert.NotNull(await w.Store.FindAgentAsync(made.Id));

        // The revoked one she can still remove, and it leaves her page.
        await w.Agents.RemoveAsync(w.Alice.Id, revoked.AgentId, default);
        Assert.DoesNotContain(await w.Agents.ListAsync(w.Alice.Id, default), a => a.AgentId == revoked.AgentId);
    }
}

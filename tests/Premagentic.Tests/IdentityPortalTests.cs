using Premagentic.Core.Identity;
using Premagentic.Core.Security;
using Premagentic.Core.Security.Acl;
using Premagentic.Core.Storage;

namespace Premagentic.Tests;

/// <summary>
/// What the administration portal needs from identity and access: changes that
/// join a transaction the portal owns, role and agent-limit changes, and an
/// agent viewed as by an administrator.
/// Requires a running Docker daemon.
/// </summary>
public sealed class IdentityPortalTests(DatastoreTestDatabase server) : IClassFixture<DatastoreTestDatabase>
{
    private async Task<(PremagenticDatabase Db, Guid Tenant, IdentityStore Identity)> NewAsync()
    {
        var db = new PremagenticDatabase(await server.CreateDatabaseAsync());
        await db.InitializeAsync();
        var tenant = await db.EnsureTenantAsync("t", "T");
        return (db, tenant, new IdentityStore(db, tenant));
    }

    [Fact]
    public async Task Identity_and_rule_changes_join_a_transaction_the_caller_owns()
    {
        var (db, tenant, identity) = await NewAsync();
        await using var _ = db;
        var user = await identity.CreateUserAsync("frankie", "Frankie", Role.Member);

        foreach (var commit in new[] { false, true })
        {
            await using var conn = await db.DataSource.OpenConnectionAsync();
            await using var tx = await conn.BeginTransactionAsync();
            var inTx = new IdentityStore(db, tenant, transaction: tx);
            var group = await inTx.CreateGroupAsync("Readers");
            await inTx.AddMemberAsync(group.Id, user.Id);
            await inTx.SetUserDisabledAsync(user.Id, true);
            var names = new PrincipalNames(inTx);
            await new AclStore(db, tenant, tx).SetRuleAsync(new FolderRule("filesystem", "r", await names.ToAclSetAsync(["allow group:Readers"])));
            Assert.True(await inTx.DeleteGroupAsync(group.Id));

            if (commit) await tx.CommitAsync(); else await tx.RollbackAsync();

            // Nothing is visible outside until the owner commits, and all of it after.
            Assert.Equal(commit, (await identity.FindUserAsync(user.Id))!.Disabled);
            Assert.Equal(commit ? 1 : 0, (await new AclStore(db, tenant).ListRulesAsync()).Count);
            if (commit) Assert.Null(await identity.FindGroupByNameAsync("Readers"));
        }
    }

    [Fact]
    public async Task The_last_enabled_administrator_keeps_the_role()
    {
        var (db, _, identity) = await NewAsync();
        await using var __ = db;
        var ada = await identity.CreateUserAsync("ada", "Ada", Role.Administrator);
        var ben = await identity.CreateUserAsync("ben", "Ben", Role.Member);

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => identity.SetUserRoleAsync(ada.Id, Role.Member));
        Assert.Contains("no enabled administrator", refused.Message);
        Assert.Equal(Role.Administrator, (await identity.FindUserAsync(ada.Id))!.Role);

        // With a second administrator, the first can step down; a disabled one
        // does not count as the second.
        Assert.True(await identity.SetUserRoleAsync(ben.Id, Role.Administrator));
        await identity.SetUserDisabledAsync(ben.Id, true);
        await Assert.ThrowsAsync<InvalidOperationException>(() => identity.SetUserRoleAsync(ada.Id, Role.Auditor));
        await identity.SetUserDisabledAsync(ben.Id, false);
        Assert.True(await identity.SetUserRoleAsync(ada.Id, Role.Auditor));
        Assert.Equal(Role.Auditor, (await identity.FindUserAsync(ada.Id))!.Role);

        Assert.False(await identity.SetUserRoleAsync(Guid.NewGuid(), Role.Member));
    }

    [Fact]
    public async Task An_agents_limits_change_and_its_tier_is_stored_as_the_spec_names_it()
    {
        var (db, _, identity) = await NewAsync();
        await using var __ = db;
        var owner = await identity.CreateUserAsync("ada", "Ada", Role.Administrator);
        var agent = await identity.CreateAgentAsync("helper", owner.Id, AgentMode.ActsForUser, 60, null, ModelLocation.Local);

        Assert.True(await identity.SetAgentLimitsAsync(agent.Id, 5, "Machine_Confirmed"));
        var changed = (await identity.FindAgentAsync(agent.Id))!;
        Assert.Equal(5, changed.RequestsPerMinute);
        Assert.Equal("machine-confirmed", changed.MinimumTrustTier);

        Assert.True(await identity.SetAgentLimitsAsync(agent.Id, 7, " "));
        Assert.Null((await identity.FindAgentAsync(agent.Id))!.MinimumTrustTier);

        await Assert.ThrowsAsync<ArgumentException>(() => identity.SetAgentLimitsAsync(agent.Id, 7, "trusted"));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => identity.SetAgentLimitsAsync(agent.Id, 0, null));
        Assert.Equal(7, (await identity.FindAgentAsync(agent.Id))!.RequestsPerMinute);
        Assert.False(await identity.SetAgentLimitsAsync(Guid.NewGuid(), 5, null));
    }

    [Fact]
    public async Task An_agent_resolves_by_id_for_view_as_like_its_token_would_and_records_no_use()
    {
        var (db, _, identity) = await NewAsync();
        await using var __ = db;
        var owner = await identity.CreateUserAsync("ada", "Ada", Role.Administrator);
        var readers = await identity.CreateGroupAsync("Readers");
        await identity.AddMemberAsync(readers.Id, owner.Id);
        var helper = await identity.CreateAgentAsync("helper", owner.Id, AgentMode.ActsForUser, 60, null, ModelLocation.Local);
        var token = await identity.IssueTokenAsync(helper.Id, TimeSpan.FromDays(1));

        var byId = await CallerAccess.ResolveAgentAsync(identity, helper.Id);
        var byToken = await CallerAccess.ResolveAgentTokenAsync(identity, token.PlainText);
        Assert.True(byId.IsResolved);
        Assert.Equal(byToken.Scope.Principals, byId.Scope.Principals);
        Assert.Equal(byToken.Scope.NarrowedBy, byId.Scope.NarrowedBy);
        Assert.Equal(helper.Id, byId.Agent!.Id);
        Assert.Null(byId.Resolved.TokenId);

        // Only the token call counts as a use.
        var used = (await identity.ListTokensAsync(helper.Id)).Single().LastUsedAt;
        await CallerAccess.ResolveAgentAsync(identity, helper.Id);
        Assert.Equal(used, (await identity.ListTokensAsync(helper.Id)).Single().LastUsedAt);

        // A disabled agent, or one whose owner is disabled, holds nothing either way.
        await identity.SetAgentDisabledAsync(helper.Id, true);
        Assert.Equal(CallerStatus.AgentDisabled, (await CallerAccess.ResolveAgentAsync(identity, helper.Id)).Resolved.Status);
        await identity.SetAgentDisabledAsync(helper.Id, false);
        await identity.SetUserDisabledAsync(owner.Id, true);
        Assert.Equal(CallerStatus.OwnerDisabled, (await CallerAccess.ResolveAgentAsync(identity, helper.Id)).Resolved.Status);
        Assert.Equal(CallerStatus.UnknownAgent, (await CallerAccess.ResolveAgentAsync(identity, Guid.NewGuid())).Resolved.Status);
    }

    [Fact]
    public async Task A_scope_viewed_as_by_an_administrator_holds_the_same_and_says_who_looked()
    {
        var (db, tenant, identity) = await NewAsync();
        await using var __ = db;
        var ada = await identity.CreateUserAsync("ada", "Ada", Role.Administrator);
        var ben = await identity.CreateUserAsync("ben", "Ben", Role.Member);
        var scope = await PermittedSetReader.ResolveAsync(db, tenant, await CallerAccess.ForUserAsync(identity, ben.Id));

        var viewed = scope.ViewedAsBy(ada.Id);

        Assert.Equal($"view-as:user:{ada.Id:D} user:{ben.Id:D}", viewed.AuditLabel);
        Assert.Equal(scope.Holds, viewed.Holds);
        Assert.Equal(scope.Principals, viewed.Principals);
        Assert.Equal(scope.UserId, viewed.UserId);
        Assert.Equal(scope.PermittedSetIds, viewed.PermittedSetIds);
        Assert.False(viewed.Unrestricted);
    }
}

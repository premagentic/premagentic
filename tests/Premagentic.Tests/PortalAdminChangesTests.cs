using Premagentic.Core.Admin;
using Premagentic.Core.Security.Acl;
using Premagentic.Core.Storage;
using Npgsql;

namespace Premagentic.Tests;

/// <summary>
/// An administrator change to users, groups, agents, tokens or rules and its
/// row in the change record commit together or not at all.
/// Requires a running Docker daemon.
/// </summary>
public sealed class PortalAdminChangesTests(DatastoreTestDatabase server) : IClassFixture<DatastoreTestDatabase>
{
    [Fact]
    public async Task An_identity_change_whose_record_cannot_be_written_is_not_made()
    {
        await using var db = new PremagenticDatabase(await server.CreateDatabaseAsync());
        await db.InitializeAsync();
        var tenant = await db.EnsureTenantAsync("t", "T");
        var changes = new AdminChanges(db, tenant);
        // No such user, so the record's foreign key refuses the row, after the change was made.
        var nobody = new AdminActor("portal", null, Guid.NewGuid());

        await Assert.ThrowsAsync<PostgresException>(() => changes.RunAsync(nobody, async change =>
        {
            var group = await change.Identity.CreateGroupAsync("Ghosts");
            change.Record("group.add", group.Name, null, new { name = group.Name });
            return group;
        }));
        await Assert.ThrowsAsync<PostgresException>(() => changes.RunAsync(nobody, async change =>
        {
            await change.Rules.SetRuleAsync(new FolderRule("filesystem", "ghosts", AclSet.Of(AclEntry.Allow(Principal.Everyone))));
            change.Record("rule.set", "filesystem:ghosts", null, new { entries = "allow everyone" });
            return true;
        }));

        var identity = new Core.Identity.IdentityStore(db, tenant);
        Assert.Null(await identity.FindGroupByNameAsync("Ghosts"));
        Assert.Empty(await new Core.Security.AclStore(db, tenant).ListRulesAsync());
        Assert.Empty(await new ChangeRecord(db, tenant).ListAsync());

        // The control: the same change by an actor the record accepts is made, and recorded.
        await changes.RunAsync(new AdminActor("cli", "test-account"), async change =>
        {
            var group = await change.Identity.CreateGroupAsync("Ghosts");
            change.Record("group.add", group.Name, null, new { name = group.Name });
            return group;
        });
        Assert.NotNull(await identity.FindGroupByNameAsync("Ghosts"));
        Assert.Single(await new ChangeRecord(db, tenant).ListAsync());
    }
}

using Premagentic.Cli.Admin;
using Premagentic.Core.Identity;
using Premagentic.Core.Security;
using Premagentic.Core.Security.Acl;
using Premagentic.Core.Storage;

namespace Premagentic.Tests;

/// <summary>
/// <c>prem groups remove</c> refuses a group a rule names unless
/// <c>--force</c> is given, because a deny entry for a removed group matches
/// nobody and its former members fall through to what the list allows after
/// it. The refusal changes nothing and records nothing. The portal's confirm
/// page has its own test; this is the command line's.
/// Requires a running Docker daemon.
/// </summary>
[Collection(ConsoleCollection.Name)]
public sealed class CliGroupRemoveRefusalTests(DatastoreTestDatabase server) : IClassFixture<DatastoreTestDatabase>
{
    [Fact]
    public async Task A_group_a_rule_names_is_removed_only_with_force_and_the_refusal_records_nothing()
    {
        await using var db = new PremagenticDatabase(await server.CreateDatabaseAsync());
        await db.InitializeAsync();
        var tenant = await db.EnsureTenantAsync("t", "T");
        var store = new IdentityStore(db, tenant);
        await store.CreateGroupAsync("Contractors");
        await store.CreateGroupAsync("Unnamed");
        await new AclStore(db, tenant).SetRuleAsync(new FolderRule(AdminCommands.FileSystemSource, "staff",
            await new PrincipalNames(store).ToAclSetAsync(["deny group:Contractors", "allow everyone"])));

        var refused = await ConsoleCapture.RunAsync(() => AdminCommands.RunAsync(["groups", "remove", "Contractors"], db, tenant));
        Assert.Equal(1, refused.Exit);
        Assert.Contains("is named by 1 rule(s)", refused.Err);
        Assert.Contains("--force", refused.Err);
        Assert.NotNull(await store.FindGroupByNameAsync("Contractors"));
        await using (var any = db.DataSource.CreateCommand("SELECT count(*) FROM prem_config.admin_event"))
            Assert.Equal(0L, (long)(await any.ExecuteScalarAsync())!);

        // The control: a group no rule names goes without being forced.
        var plain = await ConsoleCapture.RunAsync(() => AdminCommands.RunAsync(["groups", "remove", "Unnamed"], db, tenant));
        Assert.True(plain.Exit == 0, plain.Err);
        Assert.Null(await store.FindGroupByNameAsync("Unnamed"));

        var forced = await ConsoleCapture.RunAsync(() => AdminCommands.RunAsync(["groups", "remove", "Contractors", "--force"], db, tenant));
        Assert.True(forced.Exit == 0, forced.Err);
        Assert.Null(await store.FindGroupByNameAsync("Contractors"));
        Assert.Equal(2, await RowsAsync(db));
        Assert.Equal(1, await RowsAsync(db, "AND old_value->>'name' = 'Contractors' AND (old_value->>'rules_naming_it')::int = 1"));
    }

    private static async Task<long> RowsAsync(PremagenticDatabase db, string where = "")
    {
        await using var cmd = db.DataSource.CreateCommand($"SELECT count(*) FROM prem_config.admin_event WHERE kind = 'group.remove' {where}");
        return (long)(await cmd.ExecuteScalarAsync())!;
    }
}

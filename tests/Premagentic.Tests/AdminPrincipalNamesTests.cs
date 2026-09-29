using Premagentic.Core.Identity;
using Premagentic.Core.Security.Acl;
using Premagentic.Core.Storage;

namespace Premagentic.Tests;

/// <summary>
/// Principals as administrators write them, by name, against principals as
/// access lists store them, by id. Requires a running Docker daemon.
/// </summary>
public sealed class AdminPrincipalNamesTests(DatastoreTestDatabase server) : IClassFixture<DatastoreTestDatabase>
{
    private async Task<(PremagenticDatabase Db, IdentityStore Store, PrincipalNames Names)> NewAsync()
    {
        var db = new PremagenticDatabase(await server.CreateDatabaseAsync());
        await db.InitializeAsync();
        var store = new IdentityStore(db, await db.EnsureTenantAsync("t", "T"));
        return (db, store, new PrincipalNames(store));
    }

    [Fact]
    public async Task Names_become_ids_and_ids_read_back_as_names()
    {
        var (db, store, names) = await NewAsync();
        await using var _ = db;
        var alice = await store.CreateUserAsync("alice", "Alice", Role.Member);
        var staff = await store.CreateGroupAsync("Staff");
        var bot = await store.CreateAgentAsync("report-bot", alice.Id, AgentMode.Service, 60, null, ModelLocation.Local);

        var set = await names.ToAclSetAsync(["deny agent:Report-Bot", "allow group:STAFF", "allow user:Alice", "allow everyone"]);

        Assert.Equal(
            $"deny agent:{CallerResolver.IdText(bot.Id)}\nallow group:{CallerResolver.IdText(staff.Id)}\n" +
            $"allow user:{CallerResolver.IdText(alice.Id)}\nallow everyone\n",
            set.CanonicalText);
        Assert.Equal("deny agent:report-bot; allow group:Staff; allow user:alice; allow everyone", await names.DescribeAsync(set));
    }

    [Theory]
    [InlineData("group:Nobody")]
    [InlineData("user:nobody")]
    [InlineData("agent:nobody")]
    [InlineData("role:admin")]
    [InlineData("Staff")]
    [InlineData("group:")]
    [InlineData("")]
    public async Task A_name_that_matches_nothing_is_an_error_never_a_guess(string text)
    {
        var (db, _, names) = await NewAsync();
        await using var _ = db;
        await Assert.ThrowsAsync<ArgumentException>(() => names.ToPrincipalAsync(text));
    }

    [Fact]
    public async Task Source_system_principals_pass_through_unchanged()
    {
        var (db, _, names) = await NewAsync();
        await using var _ = db;
        Assert.Equal(Principal.Parse("sid:S-1-5-21-1-2-3-512"), await names.ToPrincipalAsync("sid:S-1-5-21-1-2-3-512"));
        Assert.Equal(Principal.Parse("gid:100"), await names.ToPrincipalAsync("gid:100"));
    }

    [Fact]
    public async Task A_list_keeps_the_order_it_was_written_in()
    {
        var (db, store, names) = await NewAsync();
        await using var _ = db;
        await store.CreateGroupAsync("Staff");
        await store.CreateGroupAsync("Contractors");

        var denyFirst = await names.ToAclSetAsync(["deny group:Contractors", "allow group:Staff"]);
        var allowFirst = await names.ToAclSetAsync(["allow group:Staff", "deny group:Contractors"]);
        Assert.NotEqual(denyFirst, allowFirst);
        Assert.Equal(AclEffect.Deny, denyFirst.Entries[0].Effect);
        await Assert.ThrowsAsync<ArgumentException>(() => names.ToAclSetAsync(["grant group:Staff"]));
    }

    [Fact]
    public async Task A_deleted_group_reads_back_as_deleted_not_as_its_successor()
    {
        var (db, store, names) = await NewAsync();
        await using var _ = db;
        var old = await store.CreateGroupAsync("Staff");
        var set = await names.ToAclSetAsync(["allow group:Staff"]);
        await store.DeleteGroupAsync(old.Id);
        await store.CreateGroupAsync("Staff");

        var described = await names.DescribeAsync(set);
        Assert.Contains(CallerResolver.IdText(old.Id), described);
        Assert.Contains("deleted group 'Staff'", described);
        Assert.Equal("(nobody)", await names.DescribeAsync(AclSet.Empty));
    }
}

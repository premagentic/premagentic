using Premagentic.Core.Identity;
using Premagentic.Core.Storage;

namespace Premagentic.Tests;

/// <summary>
/// The mapping table, <c>prem_config.identity_mapping</c>, as the core keeps
/// it: the core reads and writes no mapping, which an extension's principal
/// mapper does, but the table is the core's, and removing a group takes every
/// row mapped to it. Asked of the table in SQL, joined to nothing, because
/// every reader skips a deleted group and so cannot tell a removed row from
/// one left behind. Requires a running Docker daemon.
/// </summary>
public sealed class IdentityMappingTests(DatastoreTestDatabase server) : IClassFixture<DatastoreTestDatabase>
{
    private const string FinanceSid = "S-1-5-21-1004336348-1177238915-682003330-512";

    [Fact]
    public async Task A_deleted_group_takes_what_was_mapped_to_it()
    {
        var db = new PremagenticDatabase(await server.CreateDatabaseAsync());
        await using var _ = db;
        await db.InitializeAsync();
        var tenant = await db.EnsureTenantAsync("t", "T");
        var identity = new IdentityStore(db, tenant);
        var finance = await identity.CreateGroupAsync("Finance");
        var ops = await identity.CreateGroupAsync("Ops");

        await InsertAsync(db, tenant, FinanceSid, finance.Id);
        await InsertAsync(db, tenant, "S-1-5-21-7", ops.Id);
        Assert.Equal(1, await RowsAsync(db, tenant, FinanceSid));

        Assert.True(await identity.DeleteGroupAsync(finance.Id));

        Assert.Equal(0, await RowsAsync(db, tenant, FinanceSid));
        // The control: a row mapped to a group that stays, stays.
        Assert.Equal(1, await RowsAsync(db, tenant, "S-1-5-21-7"));
    }

    private static async Task InsertAsync(PremagenticDatabase db, Guid tenant, string principal, Guid group)
    {
        await using var cmd = db.DataSource.CreateCommand(
            "INSERT INTO prem_config.identity_mapping(tenant_id, external_principal, group_id) VALUES(@tenant, @principal, @group)");
        cmd.Parameters.AddWithValue("tenant", tenant);
        cmd.Parameters.AddWithValue("principal", principal);
        cmd.Parameters.AddWithValue("group", group);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>Rows in the mapping table for this principal, joined to nothing.</summary>
    private static async Task<long> RowsAsync(PremagenticDatabase db, Guid tenant, string principal)
    {
        await using var cmd = db.DataSource.CreateCommand(
            "SELECT count(*) FROM prem_config.identity_mapping WHERE tenant_id = @tenant AND external_principal = @principal");
        cmd.Parameters.AddWithValue("tenant", tenant);
        cmd.Parameters.AddWithValue("principal", principal);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }
}

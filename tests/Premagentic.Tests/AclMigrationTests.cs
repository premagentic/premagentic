using Premagentic.Core.Identity;
using Premagentic.Core.Storage;
using Npgsql;

namespace Premagentic.Tests;

/// <summary>
/// The identity and access migrations, 0002 to 0009, against stock PostgreSQL.
/// Requires a running Docker daemon.
/// </summary>
public sealed class AclMigrationTests(DatastoreTestDatabase server) : IClassFixture<DatastoreTestDatabase>
{
    private static async Task<string[]> DocumentColumnsAsync(PremagenticDatabase db)
    {
        await using var cmd = db.DataSource.CreateCommand("""
            SELECT column_name FROM information_schema.columns
            WHERE table_schema = 'prem_index' AND table_name = 'document' ORDER BY column_name
            """);
        var columns = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync()) columns.Add(reader.GetString(0));
        return columns.ToArray();
    }

    [Fact]
    public async Task The_document_has_one_statement_of_access_and_the_old_columns_are_gone()
    {
        await using var db = new PremagenticDatabase(await server.CreateDatabaseAsync());
        await db.MigrateAsync();

        var columns = await DocumentColumnsAsync(db);
        Assert.Contains("acl_set_id", columns);
        Assert.Contains("acl_from_rule", columns);
        Assert.DoesNotContain("is_public", columns);
        Assert.DoesNotContain("allowed_principals", columns);
    }

    [Fact]
    public async Task A_dropped_index_comes_back_with_the_access_column_and_config_is_untouched()
    {
        await using var db = new PremagenticDatabase(await server.CreateDatabaseAsync());
        await db.MigrateAsync();
        var tenant = await db.EnsureTenantAsync("t", "T");
        var alice = await new IdentityStore(db, tenant).CreateUserAsync("alice", "Alice", Role.Member);

        await using (var drop = db.DataSource.CreateCommand("DROP SCHEMA prem_index CASCADE"))
            await drop.ExecuteNonQueryAsync();
        var applied = await db.MigrateAsync();

        Assert.All(applied, a => Assert.Equal("prem_index", a.Schema));
        Assert.Contains(applied, a => a.Version == 4);
        Assert.Contains("acl_set_id", await DocumentColumnsAsync(db));
        Assert.DoesNotContain("is_public", await DocumentColumnsAsync(db));
        Assert.NotNull(await new IdentityStore(db, tenant).FindUserAsync(alice.Id));
    }

    [Fact]
    public void No_config_section_in_this_range_references_the_index()
    {
        var mine = Migration.LoadEmbedded().Where(m => m.Version is >= 2 and <= 9).ToArray();
        Assert.NotEmpty(mine);
        foreach (var section in mine.SelectMany(m => m.Sections).Where(s => s.Schema == Migration.ConfigSchema))
            Assert.DoesNotContain("prem_index", section.Sql);
    }

    [Fact]
    public async Task A_document_cannot_point_at_another_tenants_access_list()
    {
        await using var db = new PremagenticDatabase(await server.CreateDatabaseAsync());
        await db.MigrateAsync();
        var a = await db.EnsureTenantAsync("a", "A");
        var b = await db.EnsureTenantAsync("b", "B");
        var setOfA = await new Core.Security.AclStore(db, a).EnsureSetAsync(Core.Security.Acl.AclSet.Empty);

        await using var cmd = db.DataSource.CreateCommand("""
            INSERT INTO prem_index.document(tenant_id, path, content_hash, acl_set_id) VALUES(@b, 'x.md', 'h', @set)
            """);
        cmd.Parameters.AddWithValue("b", b);
        cmd.Parameters.AddWithValue("set", setOfA);
        var ex = await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync());
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, ex.SqlState);
    }
}

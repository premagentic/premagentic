using Premagentic.Cli.Admin;
using Premagentic.Core.Admin;
using Premagentic.Core.Identity;
using Premagentic.Core.Storage;

namespace Premagentic.Tests;

/// <summary>
/// Who made an agent (<c>created_by</c>): each path that makes one writes its
/// own origin, the store reads it back on the listing, the database refuses a
/// row that does not say, and the connect page counts and lists by it rather
/// than by the assistant kind. Requires a running Docker daemon.
/// </summary>
[Collection(ConsoleCollection.Name)]
public sealed class AgentOriginTests(DatastoreTestDatabase server) : IClassFixture<DatastoreTestDatabase>
{
    private async Task<(PremagenticDatabase Db, Guid Tenant, IdentityStore Store, User Alice)> NewAsync()
    {
        var db = new PremagenticDatabase(await server.CreateDatabaseAsync());
        await db.InitializeAsync();
        var tenant = await db.EnsureTenantAsync("t", "T");
        var store = new IdentityStore(db, tenant);
        return (db, tenant, store, await store.CreateUserAsync("alice", "Alice", Role.Member));
    }

    private static async Task<string> CreatedByAsync(PremagenticDatabase db, string name)
    {
        await using var cmd = db.DataSource.CreateCommand("SELECT created_by FROM prem_config.agent WHERE name = @name");
        cmd.Parameters.AddWithValue("name", name);
        return (string)(await cmd.ExecuteScalarAsync())!;
    }

    [Fact]
    public async Task The_command_line_writes_its_account_as_the_origin()
    {
        var (db, tenant, store, _) = await NewAsync();
        await using var _ = db;

        var run = await ConsoleCapture.RunAsync(() => AdminCommands.RunAsync(
            ["agents", "add", "helper", "--owner", "alice", "--mode", "service", "--model", "local"], db, tenant));

        Assert.Equal(0, run.Exit);
        Assert.Equal($"cli:{AdminActor.Cli().Account}", await CreatedByAsync(db, "helper"));
        Assert.Equal(new AgentOrigin("cli", AdminActor.Cli().Account), (await store.FindAgentByNameAsync("helper"))!.Origin);
    }

    [Fact]
    public async Task The_connect_page_writes_self_and_the_listing_reads_it_back()
    {
        var (db, tenant, store, alice) = await NewAsync();
        await using var _ = db;

        var created = await new SelfServeAgents(db, tenant).CreateAsync(
            alice.Id, new SelfServeAgentRequest("alice-laptop", "local", null, "coding tool"), default);

        Assert.Equal($"self:{CallerResolver.IdText(alice.Id)}", await CreatedByAsync(db, "alice-laptop"));
        var listed = Assert.Single(await store.ListAgentsAsync(), a => a.Id == created.AgentId);
        Assert.Equal(AgentOrigin.Self(alice.Id), listed.Origin);
    }

    [Fact]
    public async Task The_portal_writes_the_administrator_who_registered_it()
    {
        await using var p = await PortalWorld.NewAsync(server);

        using (var made = await p.PostAsync("/portal/agents", p.Admin,
                   [("name", "helper"), ("owner", "alice"), ("mode", "service"), ("rate", "30"), ("minTier", ""), ("model", "local")]))
            Assert.DoesNotContain("error=", made.Headers.Location?.OriginalString ?? "", StringComparison.Ordinal);

        Assert.Equal(1, await p.ScalarAsync("""
            SELECT count(*) FROM prem_config.agent a
            JOIN prem_config.app_user u ON a.created_by = 'portal:' || u.id::text
            WHERE a.name = 'helper' AND u.role = 'administrator'
            """));
    }

    [Fact]
    public async Task The_database_refuses_an_agent_that_does_not_say_who_made_it()
    {
        var (db, tenant, _, alice) = await NewAsync();
        await using var _ = db;

        foreach (var createdBy in new[] { "someone", "self:", "cli" })
        {
            await using var cmd = db.DataSource.CreateCommand("""
                INSERT INTO prem_config.agent(tenant_id, name, owner_user_id, mode, requests_per_minute, model_location, created_by)
                VALUES(@t, @name, @owner, 'service', 60, 'local', @by)
                """);
            cmd.Parameters.AddWithValue("t", tenant);
            cmd.Parameters.AddWithValue("name", "x-" + createdBy.Replace(":", ""));
            cmd.Parameters.AddWithValue("owner", alice.Id);
            cmd.Parameters.AddWithValue("by", createdBy);
            await Assert.ThrowsAsync<Npgsql.PostgresException>(() => cmd.ExecuteNonQueryAsync());
        }

        // And there is no default to fall back on when a path leaves it out.
        await using var missing = db.DataSource.CreateCommand("""
            INSERT INTO prem_config.agent(tenant_id, name, owner_user_id, mode, requests_per_minute, model_location)
            VALUES(@t, 'no-origin', @owner, 'service', 60, 'local')
            """);
        missing.Parameters.AddWithValue("t", tenant);
        missing.Parameters.AddWithValue("owner", alice.Id);
        await Assert.ThrowsAsync<Npgsql.PostgresException>(() => missing.ExecuteNonQueryAsync());
    }

    [Fact]
    public async Task An_agent_with_an_assistant_kind_but_not_made_on_the_connect_page_is_not_the_persons()
    {
        var (db, tenant, store, alice) = await NewAsync();
        await using var _ = db;
        var agents = new SelfServeAgents(db, tenant);

        // An administrator's agent that happens to carry an assistant kind: the
        // old convention would have counted it as hers. Who made it decides now.
        var admins = await store.CreateAgentAsync("admin-made", alice.Id, AgentMode.ActsForUser, 60, null, ModelLocation.Local,
            origin: AgentOrigin.Cli("an-admin"));
        await using (var kind = db.DataSource.CreateCommand("UPDATE prem_config.agent SET assistant_kind = 'coding tool' WHERE id = @id"))
        {
            kind.Parameters.AddWithValue("id", admins.Id);
            await kind.ExecuteNonQueryAsync();
        }

        Assert.Empty(await agents.ListAsync(alice.Id, default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => agents.RevokeAsync(alice.Id, admins.Id, default));
    }

    [Theory]
    [InlineData("cli:host\\pat", "cli", "host\\pat")]
    [InlineData("portal:0f1e2d3c-0000-0000-0000-000000000001", "portal", "0f1e2d3c-0000-0000-0000-000000000001")]
    [InlineData("profile:starter", "profile", "starter")]
    [InlineData("unknown", "unknown", null)]
    [InlineData("robot:x", "unknown", null)]
    [InlineData("self:", "unknown", null)]
    public void The_stored_text_reads_back_as_the_origin_it_names(string text, string kind, string? who)
    {
        var origin = AgentOrigin.Parse(text);

        Assert.Equal(new AgentOrigin(kind, who), origin);
        if (kind != "unknown") Assert.Equal(text, origin.Text);
    }
}

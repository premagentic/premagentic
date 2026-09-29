using Premagentic.Core;
using Premagentic.Cli.Admin;
using Premagentic.Core.Admin;
using Premagentic.Core.Identity;
using Premagentic.Core.Ingestion;
using Premagentic.Core.Retrieval;
using Premagentic.Core.Security;
using Premagentic.Core.Security.Acl;
using Premagentic.Core.Storage;
using Npgsql;

namespace Premagentic.Tests;

/// <summary>
/// Where an agent's model runs, and what follows from it: the reserved group
/// that holds every hosted-model agent, the folder rule that keeps those agents
/// out, and the location written on every audit row.
/// <para>
/// Every case asserts both directions. A hosted agent is kept out of the folder
/// that denies the group and let into the one that does not, and a local agent
/// reads both, so a gate that refused everybody would fail here as surely as
/// one that leaked.
/// </para>
/// Requires a running Docker daemon.
/// </summary>
public sealed class ModelLocationTests(DatastoreTestDatabase server) : IClassFixture<DatastoreTestDatabase>
{
    private const string Source = "test-datastore";
    private const string Marketing = "marketing/newsletter.md";
    private const string Clients = "clients/matter.md";

    private sealed record World(
        PremagenticDatabase Db, Guid Tenant, IdentityStore Identity, AclStore Rules, HybridSearch Search,
        User Owner, Group Reserved);

    /// <summary>
    /// One person, two folders, and the rule that makes the difference:
    /// <c>marketing</c> is readable by anyone the deployment authenticates, and
    /// <c>clients</c> is the same rule with one deny in front of it, naming the
    /// reserved group. Nothing else separates the two.
    /// </summary>
    private async Task<World> NewWorldAsync()
    {
        var db = new PremagenticDatabase(await server.CreateDatabaseAsync());
        await db.InitializeAsync();
        var tenant = await db.EnsureTenantAsync("t", "T");
        var identity = new IdentityStore(db, tenant);
        var rules = new AclStore(db, tenant);

        var owner = await identity.CreateUserAsync("dana", "Dana", Role.Member);
        var reserved = await identity.FindSystemGroupAsync(SystemGroups.HostedModelAgents);
        Assert.NotNull(reserved);

        await rules.SetRuleAsync(new FolderRule(Source, "marketing", AclSet.Of(AclEntry.Allow(Principal.Everyone))));
        await rules.SetRuleAsync(new FolderRule(Source, "clients", AclSet.Of(
            AclEntry.Deny(Principal.Group(CallerResolver.IdText(reserved.Id))),
            AclEntry.Allow(Principal.Everyone))));

        var corpus = new DatastoreSource("", [
            DatastoreSource.Doc(Marketing, "## Newsletter\nthe zeppelin newsletter draft", DocumentAccess.FolderRules),
            DatastoreSource.Doc(Clients, "## Matter\nthe zeppelin client matter", DocumentAccess.FolderRules),
        ]);
        var embedder = new SeededEmbeddingProvider();
        var summary = await new IngestPipeline(db, embedder).RunAsync(tenant, corpus);
        Assert.Equal(2, summary.Ingested);

        return new World(db, tenant, identity, rules, new HybridSearch(db, embedder), owner, reserved);
    }

    private static async Task<string[]> PathsAsync(World w, AccessScope scope) =>
        (await w.Search.SearchAsync(w.Tenant, "zeppelin", new SearchOptions(scope, TopK: 20)))
        .Hits.Select(h => h.Path).Distinct().Order(StringComparer.Ordinal).ToArray();

    private static async Task<AccessScope> WithTokenAsync(World w, Agent agent) =>
        await CallerAccess.ForAgentTokenAsync(w.Identity, (await w.Identity.IssueTokenAsync(agent.Id, TimeSpan.FromDays(30))).PlainText);

    private static async Task<string[]> GroupNamesAsync(World w, Agent agent) =>
        (await w.Identity.SystemGroupsOfAgentAsync(agent.Id)).Select(g => g.Name).ToArray();

    [Fact]
    public async Task Membership_of_the_reserved_group_follows_the_agents_model_both_ways()
    {
        var w = await NewWorldAsync();
        await using var _ = w.Db;

        var hosted = await w.Identity.CreateAgentAsync(
            "desk", w.Owner.Id, AgentMode.ActsForUser, 60, null, ModelLocation.Hosted, "a vendor");
        var local = await w.Identity.CreateAgentAsync("shop", w.Owner.Id, AgentMode.Service, 60, null, ModelLocation.Local);

        // Registering one way or the other decides it.
        Assert.Equal([SystemGroups.HostedModelAgentsName], await GroupNamesAsync(w, hosted));
        Assert.Empty(await GroupNamesAsync(w, local));

        // And changing it moves the agent, in both directions.
        Assert.True(await w.Identity.SetAgentModelAsync(hosted.Id, ModelLocation.Local));
        Assert.True(await w.Identity.SetAgentModelAsync(local.Id, ModelLocation.Hosted, "a vendor"));
        Assert.Empty(await GroupNamesAsync(w, hosted));
        Assert.Equal([SystemGroups.HostedModelAgentsName], await GroupNamesAsync(w, local));

        // The attribute moved with it, and the vendor went when the model did.
        var movedIn = await w.Identity.FindAgentAsync(local.Id);
        var movedOut = await w.Identity.FindAgentAsync(hosted.Id);
        Assert.Equal(ModelLocation.Hosted, movedIn!.ModelLocation);
        Assert.Equal("a vendor", movedIn.ModelVendor);
        Assert.Equal(ModelLocation.Local, movedOut!.ModelLocation);
        Assert.Null(movedOut.ModelVendor);

        // Setting the same value twice is not an error and does not double the row.
        Assert.True(await w.Identity.SetAgentModelAsync(local.Id, ModelLocation.Hosted, "a vendor"));
        Assert.Equal([SystemGroups.HostedModelAgentsName], await GroupNamesAsync(w, local));
    }

    [Fact]
    public async Task A_local_model_has_no_vendor_to_name()
    {
        var w = await NewWorldAsync();
        await using var _ = w.Db;

        await Assert.ThrowsAsync<ArgumentException>(() => w.Identity.CreateAgentAsync(
            "desk", w.Owner.Id, AgentMode.Service, 60, null, ModelLocation.Local, "a vendor"));

        // The control: the same call with a hosted model is accepted.
        var hosted = await w.Identity.CreateAgentAsync(
            "desk", w.Owner.Id, AgentMode.Service, 60, null, ModelLocation.Hosted, "a vendor");
        Assert.Equal("a vendor", hosted.ModelVendor);
    }

    [Fact]
    public async Task The_reserved_group_cannot_be_renamed_deleted_or_written_by_hand()
    {
        var w = await NewWorldAsync();
        await using var _ = w.Db;
        var agent = await w.Identity.CreateAgentAsync("shop", w.Owner.Id, AgentMode.Service, 60, null, ModelLocation.Local);
        var ordinary = await w.Identity.CreateGroupAsync("Marketing");

        await Assert.ThrowsAsync<InvalidOperationException>(() => w.Identity.RenameGroupAsync(w.Reserved.Id, "something else"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => w.Identity.DeleteGroupAsync(w.Reserved.Id));
        await Assert.ThrowsAsync<InvalidOperationException>(() => w.Identity.AddMemberAsync(w.Reserved.Id, w.Owner.Id));
        await Assert.ThrowsAsync<InvalidOperationException>(() => w.Identity.GrantGroupAsync(agent.Id, w.Reserved.Id));
        await Assert.ThrowsAsync<InvalidOperationException>(() => w.Identity.RevokeGroupAsync(agent.Id, w.Reserved.Id));

        // It is still there, still named the same, and still found by its key.
        var again = await w.Identity.FindSystemGroupAsync(SystemGroups.HostedModelAgents);
        Assert.Equal(w.Reserved.Id, again!.Id);
        Assert.Equal(SystemGroups.HostedModelAgentsName, again.Name);
        Assert.True(again.IsSystem);

        // The control: an ordinary group takes every one of those changes.
        Assert.True(await w.Identity.RenameGroupAsync(ordinary.Id, "Marketing team"));
        Assert.True(await w.Identity.AddMemberAsync(ordinary.Id, w.Owner.Id));
        Assert.True(await w.Identity.GrantGroupAsync(agent.Id, ordinary.Id));
        Assert.True(await w.Identity.RevokeGroupAsync(agent.Id, ordinary.Id));
        Assert.True(await w.Identity.DeleteGroupAsync(ordinary.Id));
    }

    [Fact]
    public async Task The_database_refuses_the_reserved_group_even_when_no_command_is_looking()
    {
        var w = await NewWorldAsync();
        await using var _ = w.Db;

        await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(w,
            "UPDATE prem_config.app_group SET name = 'renamed' WHERE id = @id", w.Reserved.Id));
        await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(w,
            "UPDATE prem_config.app_group SET deleted_at = now() WHERE id = @id", w.Reserved.Id));
        await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(w,
            "UPDATE prem_config.app_group SET system_key = NULL WHERE id = @id", w.Reserved.Id));
        await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(w,
            "DELETE FROM prem_config.app_group WHERE id = @id", w.Reserved.Id));

        // The control: the same four statements go through on an ordinary group.
        var ordinary = await w.Identity.CreateGroupAsync("Marketing");
        await ExecuteAsync(w, "UPDATE prem_config.app_group SET name = 'renamed' WHERE id = @id", ordinary.Id);
        await ExecuteAsync(w, "UPDATE prem_config.app_group SET deleted_at = now() WHERE id = @id", ordinary.Id);
        await ExecuteAsync(w, "UPDATE prem_config.app_group SET system_key = NULL WHERE id = @id", ordinary.Id);
        await ExecuteAsync(w, "DELETE FROM prem_config.app_group WHERE id = @id", ordinary.Id);
    }

    [Fact]
    public async Task A_rule_that_denies_the_reserved_group_keeps_a_hosted_agent_out_and_lets_a_local_one_in()
    {
        var w = await NewWorldAsync();
        await using var _ = w.Db;

        var hostedDesk = await w.Identity.CreateAgentAsync(
            "hosted-desk", w.Owner.Id, AgentMode.ActsForUser, 60, null, ModelLocation.Hosted, "a vendor");
        var localDesk = await w.Identity.CreateAgentAsync(
            "local-desk", w.Owner.Id, AgentMode.ActsForUser, 60, null, ModelLocation.Local);
        var hostedShop = await w.Identity.CreateAgentAsync(
            "hosted-shop", w.Owner.Id, AgentMode.Service, 60, null, ModelLocation.Hosted, "a vendor");
        var localShop = await w.Identity.CreateAgentAsync(
            "local-shop", w.Owner.Id, AgentMode.Service, 60, null, ModelLocation.Local);

        // Both modes: the hosted agent reads the folder with no deny and not the
        // one with it; the local agent reads both.
        Assert.Equal([Marketing], await PathsAsync(w, await WithTokenAsync(w, hostedDesk)));
        Assert.Equal([Marketing], await PathsAsync(w, await WithTokenAsync(w, hostedShop)));
        Assert.Equal([Clients, Marketing], await PathsAsync(w, await WithTokenAsync(w, localDesk)));
        Assert.Equal([Clients, Marketing], await PathsAsync(w, await WithTokenAsync(w, localShop)));

        // The person the agents act for reads both, so the deny is about where
        // the model runs and nothing else.
        Assert.Equal([Clients, Marketing], await PathsAsync(w, await CallerAccess.ForUserAsync(w.Identity, w.Owner.Id)));

        // And it follows the switch: move the hosted desk to a local model and
        // the client matter is there on the next call.
        await w.Identity.SetAgentModelAsync(hostedDesk.Id, ModelLocation.Local);
        Assert.Equal([Clients, Marketing], await PathsAsync(w, await WithTokenAsync(w, hostedDesk)));
    }

    [Fact]
    public async Task A_rule_that_allows_the_reserved_group_gives_nothing_to_anybody()
    {
        var w = await NewWorldAsync();
        await using var _ = w.Db;
        var reserved = Principal.Group(CallerResolver.IdText(w.Reserved.Id));

        var hosted = await w.Identity.CreateAgentAsync(
            "hosted-desk", w.Owner.Id, AgentMode.ActsForUser, 60, null, ModelLocation.Hosted, "a vendor");
        var hostedService = await w.Identity.CreateAgentAsync(
            "hosted-shop", w.Owner.Id, AgentMode.Service, 60, null, ModelLocation.Hosted, "a vendor");

        // The client folder now allows the reserved group in front of the two
        // denies that would otherwise decide. If the group were a principal an
        // agent HELD, that first entry would match first and carry both agents
        // past a deny written about them: the person one acts for, and the
        // other by its own name.
        await w.Rules.SetRuleAsync(new FolderRule(Source, "clients", AclSet.Of(
            AclEntry.Allow(reserved),
            AclEntry.Deny(Principal.User(CallerResolver.IdText(w.Owner.Id))),
            AclEntry.Deny(Principal.Agent(CallerResolver.IdText(hostedService.Id))),
            AclEntry.Allow(Principal.Everyone))));

        Assert.Equal([Marketing], await PathsAsync(w, await WithTokenAsync(w, hosted)));
        Assert.Equal([Marketing], await PathsAsync(w, await WithTokenAsync(w, hostedService)));
        Assert.Equal([Marketing], await PathsAsync(w, await CallerAccess.ForUserAsync(w.Identity, w.Owner.Id)));

        // The control: everything is there to be found.
        Assert.Equal([Clients, Marketing], await PathsAsync(w, AccessScope.UnrestrictedAudited("test")));
    }

    [Fact]
    public async Task The_explanation_names_the_reserved_group_as_the_reason()
    {
        var w = await NewWorldAsync();
        await using var _ = w.Db;
        var hosted = await w.Identity.CreateAgentAsync(
            "hosted-desk", w.Owner.Id, AgentMode.ActsForUser, 60, null, ModelLocation.Hosted, "a vendor");
        var local = await w.Identity.CreateAgentAsync(
            "local-desk", w.Owner.Id, AgentMode.ActsForUser, 60, null, ModelLocation.Local);

        var refused = await ExplainAsync(w, hosted, Clients);
        Assert.False(refused.Access.Passes);
        Assert.Contains("model runs outside the network", refused.Access.Reason, StringComparison.Ordinal);

        // The control: the same document, the same agent but for its model, is
        // allowed and the explanation says nothing of the kind.
        var allowed = await ExplainAsync(w, local, Clients);
        Assert.True(allowed.Access.Passes);
        Assert.DoesNotContain("model runs outside the network", allowed.Access.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Every_audit_row_says_where_the_model_ran()
    {
        var w = await NewWorldAsync();
        await using var _ = w.Db;
        var hosted = await w.Identity.CreateAgentAsync(
            "hosted-desk", w.Owner.Id, AgentMode.ActsForUser, 60, null, ModelLocation.Hosted, "a vendor");
        var local = await w.Identity.CreateAgentAsync(
            "local-desk", w.Owner.Id, AgentMode.ActsForUser, 60, null, ModelLocation.Local);

        await PathsAsync(w, await WithTokenAsync(w, hosted));
        await PathsAsync(w, await WithTokenAsync(w, local));
        await PathsAsync(w, await CallerAccess.ForUserAsync(w.Identity, w.Owner.Id));

        var trail = await new AuditTrail(w.Db, w.Tenant).PageAsync(10);
        Assert.Equal(3, trail.Count);
        Assert.Equal(ModelLocations.Hosted, trail.Single(q => q.AgentId == hosted.Id).ModelLocation);
        Assert.Equal(ModelLocations.Local, trail.Single(q => q.AgentId == local.Id).ModelLocation);
        Assert.Equal(ModelLocations.Local, trail.Single(q => q.AgentId is null).ModelLocation);

        // One row, and only one, is the record of something leaving.
        Assert.Equal([hosted.Id], trail.Where(q => q.LeftTheNetwork).Select(q => q.AgentId).ToArray());

        // The export carries it too, since an auditor reads that and not this.
        var lines = new List<string>();
        await foreach (var line in new AuditTrail(w.Db, w.Tenant).JsonLinesAsync()) lines.Add(line);
        Assert.Equal(2, lines.Count(l => l.Contains("\"model_location\":\"local\"", StringComparison.Ordinal)));
        Assert.Equal(1, lines.Count(l => l.Contains("\"model_location\":\"hosted\"", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task The_row_keeps_saying_what_was_true_when_the_passages_were_served()
    {
        var w = await NewWorldAsync();
        await using var _ = w.Db;
        var agent = await w.Identity.CreateAgentAsync(
            "desk", w.Owner.Id, AgentMode.ActsForUser, 60, null, ModelLocation.Hosted, "a vendor");

        await PathsAsync(w, await WithTokenAsync(w, agent));
        await w.Identity.SetAgentModelAsync(agent.Id, ModelLocation.Local);
        await PathsAsync(w, await WithTokenAsync(w, agent));

        // A row that recorded nothing reads as "(none)" rather than being
        // compared as a null, so a gap fails this loudly instead of quietly.
        var trail = await new AuditTrail(w.Db, w.Tenant).PageAsync(10);
        Assert.Equal([ModelLocations.Local, ModelLocations.Hosted], trail.Select(q => q.ModelLocation ?? "(none)").ToArray());
    }

    [Fact]
    public async Task An_agent_that_was_registered_before_this_migration_is_read_as_hosted_and_joins_the_group()
    {
        // The schema as it stood before 0040, then one agent written into it the
        // way the old code wrote them, then the rest of the migrations.
        var connection = await server.CreateDatabaseAsync();
        await using var source = NpgsqlDataSource.Create(connection);
        var before = Migration.LoadEmbedded().Where(m => m.Version < 40).ToList();
        await new MigrationRunner(source, before).MigrateAsync();

        Guid tenant, agentId;
        await using (var cmd = source.CreateCommand("""
            WITH t AS (INSERT INTO prem_config.tenant(key, name) VALUES('t', 'T') RETURNING id),
            u AS (INSERT INTO prem_config.app_user(tenant_id, sign_in_name, display_name, role)
                  SELECT t.id, 'dana', 'Dana', 'member' FROM t RETURNING tenant_id, id),
            a AS (INSERT INTO prem_config.agent(tenant_id, name, owner_user_id, mode, requests_per_minute)
                  SELECT u.tenant_id, 'old', u.id, 'service', 60 FROM u RETURNING tenant_id, id)
            SELECT tenant_id, id FROM a
            """))
        await using (var reader = await cmd.ExecuteReaderAsync())
        {
            Assert.True(await reader.ReadAsync());
            (tenant, agentId) = (reader.GetGuid(0), reader.GetGuid(1));
        }

        await new MigrationRunner(source).MigrateAsync();

        await using var db = new PremagenticDatabase(connection);
        var identity = new IdentityStore(db, tenant);
        var agent = await identity.FindAgentAsync(agentId);

        // Nothing is known about it, so it is read as hosted, which is the
        // answer that keeps it out of a folder marked never-leaves.
        Assert.Equal(ModelLocation.Hosted, agent!.ModelLocation);
        Assert.Null(agent.ModelVendor);
        Assert.Equal([SystemGroups.HostedModelAgentsName], (await identity.SystemGroupsOfAgentAsync(agentId)).Select(g => g.Name));

        // And the assumption is not baked into the column: an agent written now
        // has to say, because the migration dropped the default.
        await using var missing = source.CreateCommand(
            "INSERT INTO prem_config.agent(tenant_id, name, owner_user_id, mode, requests_per_minute) " +
            "SELECT tenant_id, 'newer', owner_user_id, mode, requests_per_minute FROM prem_config.agent WHERE id = @id");
        missing.Parameters.AddWithValue("id", agentId);
        await Assert.ThrowsAsync<PostgresException>(() => missing.ExecuteNonQueryAsync());
    }

    [Fact]
    public async Task Registering_a_hosted_agent_with_no_group_to_join_fails_rather_than_half_happening()
    {
        var w = await NewWorldAsync();
        await using var _ = w.Db;

        // A tenant row written straight into the database, so it has no reserved
        // group: the one state in which the membership could silently not be
        // written. Nothing a person can do reaches it, which is why it is worth
        // pinning: it is the shape of the mistake, not a supported state.
        Guid bare;
        await using (var cmd = w.Db.DataSource.CreateCommand(
            "INSERT INTO prem_config.tenant(key, name) VALUES('bare', 'Bare') RETURNING id"))
            bare = (Guid)(await cmd.ExecuteScalarAsync())!;

        var store = new IdentityStore(w.Db, bare);
        var user = await store.CreateUserAsync("dana", "Dana", Role.Member);
        Assert.Null(await store.FindSystemGroupAsync(SystemGroups.HostedModelAgents));

        await Assert.ThrowsAsync<PostgresException>(() => store.CreateAgentAsync(
            "desk", user.Id, AgentMode.Service, 60, null, ModelLocation.Hosted, "a vendor"));
        Assert.Empty(await store.ListAgentsAsync());

        // The control: a local agent has nothing to join, so it registers, and
        // moving it to a hosted model is what fails.
        var local = await store.CreateAgentAsync("shop", user.Id, AgentMode.Service, 60, null, ModelLocation.Local);
        await Assert.ThrowsAsync<PostgresException>(() => store.SetAgentModelAsync(local.Id, ModelLocation.Hosted, "a vendor"));
        Assert.Equal(ModelLocation.Local, (await store.FindAgentAsync(local.Id))!.ModelLocation);
    }

    [Fact]
    public async Task A_tenant_made_after_the_migration_gets_the_group_too()
    {
        var w = await NewWorldAsync();
        await using var _ = w.Db;

        var later = await w.Db.EnsureTenantAsync("later", "Later");
        var group = await new IdentityStore(w.Db, later).FindSystemGroupAsync(SystemGroups.HostedModelAgents);

        Assert.NotNull(group);
        Assert.NotEqual(w.Reserved.Id, group.Id);

        // Ensuring the same tenant again does not make a second one.
        Assert.Equal(later, await w.Db.EnsureTenantAsync("later", "Later"));
        Assert.Single(await new IdentityStore(w.Db, later).ListGroupsAsync());
    }

    private static async Task<AccessExplanation> ExplainAsync(World w, Agent agent, string path)
    {
        var caller = await CallerAccess.ResolveAgentAsync(w.Identity, agent.Id);
        var document = await new DocumentCatalog(w.Db, w.Tenant).FindAsync(path);
        Assert.NotNull(document);
        var matcher = await w.Rules.LoadMatcherAsync();
        var policy = await CallerPolicy.TrustAsync(w.Db, w.Tenant, caller);
        return AccessExplainer.Explain(
            document, matcher.Match(document.SourceName ?? "", path), caller.Scope, policy,
            DateTimeOffset.UtcNow, includeHistorical: false);
    }

    private static async Task ExecuteAsync(World w, string sql, Guid id)
    {
        await using var cmd = w.Db.DataSource.CreateCommand(sql);
        cmd.Parameters.AddWithValue("id", id);
        await cmd.ExecuteNonQueryAsync();
    }
}

/// <summary>
/// Every surface that registers an agent refuses one that does not say where
/// its model runs, rather than choosing for it. A registration that guessed
/// would put the agent in the reserved group, or keep it out, on nobody's
/// authority, and the audit trail would then say passages left the network, or
/// did not, on the same nobody's authority.
/// <para>
/// There are three such surfaces: the command line, the portal and the store
/// itself. The HTTP API registers no agents, so there is nothing to assert
/// there.
/// </para>
/// Requires a running Docker daemon.
/// </summary>
[Collection(ConsoleCollection.Name)]
public sealed class ModelLocationSurfaceTests(DatastoreTestDatabase server) : IClassFixture<DatastoreTestDatabase>
{
    [Fact]
    public async Task The_command_line_refuses_an_agent_that_does_not_say_where_its_model_runs()
    {
        await using var db = new PremagenticDatabase(await server.CreateDatabaseAsync());
        await db.InitializeAsync();
        var tenant = await db.EnsureTenantAsync("t", "T");
        await new IdentityStore(db, tenant).CreateUserAsync("dana", "Dana", Role.Member);

        string[] add = ["agents", "add", "helper", "--owner", "dana", "--mode", "service"];

        var missing = await ConsoleCapture.RunAsync(() => AdminCommands.RunAsync(add, db, tenant));
        Assert.Equal(1, missing.Exit);
        Assert.Contains("--model", missing.Err, StringComparison.Ordinal);

        var unknown = await ConsoleCapture.RunAsync(() => AdminCommands.RunAsync([.. add, "--model", "somewhere"], db, tenant));
        Assert.Equal(1, unknown.Exit);
        Assert.Contains("--model", unknown.Err, StringComparison.Ordinal);

        // Nothing was registered by either attempt.
        Assert.Empty(await new IdentityStore(db, tenant).ListAgentsAsync());

        // The control: the same command with an answer registers the agent.
        var good = await ConsoleCapture.RunAsync(() => AdminCommands.RunAsync([.. add, "--model", "local"], db, tenant));
        Assert.Equal(0, good.Exit);
        Assert.Equal(ModelLocation.Local, Assert.Single(await new IdentityStore(db, tenant).ListAgentsAsync()).ModelLocation);
    }

    [Fact]
    public async Task The_portal_refuses_an_agent_that_does_not_say_where_its_model_runs()
    {
        await using var p = await PortalWorld.NewAsync(server);
        (string, string)[] fields = [("name", "helper"), ("owner", "alice"), ("mode", "service"), ("rate", "30"), ("minTier", "")];

        foreach (var attempt in new[] { fields, [.. fields, ("model", "somewhere")] })
        {
            using var refused = await p.PostAsync("/portal/agents", p.Admin, attempt);
            var location = Uri.UnescapeDataString(refused.Headers.Location?.OriginalString ?? "");
            Assert.Contains("error=", location, StringComparison.Ordinal);
            Assert.Contains("runs inside your network", location, StringComparison.Ordinal);
            Assert.Equal(0, await p.ScalarAsync("SELECT count(*) FROM prem_config.agent WHERE name = 'helper'"));
        }

        // The control: the same form with an answer registers the agent.
        using (var good = await p.PostAsync("/portal/agents", p.Admin, [.. fields, ("model", "hosted"), ("vendor", "a vendor")]))
            Assert.DoesNotContain("error=", good.Headers.Location?.OriginalString ?? "", StringComparison.Ordinal);
        Assert.Equal(1, await p.ScalarAsync(
            "SELECT count(*) FROM prem_config.agent WHERE name = 'helper' AND model_location = 'hosted' AND model_vendor = 'a vendor'"));
    }

    [Fact]
    public async Task The_store_refuses_a_model_location_it_has_no_name_for()
    {
        await using var db = new PremagenticDatabase(await server.CreateDatabaseAsync());
        await db.InitializeAsync();
        var tenant = await db.EnsureTenantAsync("t", "T");
        var store = new IdentityStore(db, tenant);
        var dana = await store.CreateUserAsync("dana", "Dana", Role.Member);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.CreateAgentAsync(
            "helper", dana.Id, AgentMode.Service, 60, null, (ModelLocation)0));
        Assert.Empty(await store.ListAgentsAsync());

        // The control: the same call with a location it knows goes through, and
        // then refuses to be moved to one it does not.
        var agent = await store.CreateAgentAsync("helper", dana.Id, AgentMode.Service, 60, null, ModelLocation.Local);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.SetAgentModelAsync(agent.Id, (ModelLocation)7));
        Assert.Equal(ModelLocation.Local, (await store.FindAgentAsync(agent.Id))!.ModelLocation);
    }
}

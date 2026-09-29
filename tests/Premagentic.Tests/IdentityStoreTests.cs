using System.Security.Cryptography;
using System.Text.Json;
using Premagentic.Core.Identity;
using Premagentic.Core.Security;
using Premagentic.Core.Security.Acl;
using Premagentic.Core.Storage;

namespace Premagentic.Tests;

/// <summary>
/// The identity and access-list stores on the database. Requires a running Docker daemon.
/// </summary>
public sealed class IdentityStoreTests(DatastoreTestDatabase server) : IClassFixture<DatastoreTestDatabase>
{
    private async Task<(PremagenticDatabase Db, Guid Tenant, IdentityStore Store)> NewAsync(string tenantKey = "t")
    {
        var db = new PremagenticDatabase(await server.CreateDatabaseAsync());
        await db.InitializeAsync();
        var tenant = await db.EnsureTenantAsync(tenantKey, tenantKey);
        return (db, tenant, new IdentityStore(db, tenant));
    }

    [Fact]
    public async Task Sign_in_names_are_unique_and_found_case_insensitively_among_live_users()
    {
        var (db, _, store) = await NewAsync();
        await using var _ = db;

        var alice = await store.CreateUserAsync("Alice", "Alice A", Role.Member);
        Assert.Equal(alice.Id, (await store.FindUserByNameAsync("ALICE"))!.Id);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.CreateUserAsync("alice", "Other", Role.Member));
        Assert.Null(await store.FindUserByNameAsync("alice "));
        Assert.Null(await store.FindUserByNameAsync("bob"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(" alice")]
    [InlineData("alice ")]
    [InlineData("ali\nce")]
    public async Task Malformed_names_are_refused(string name)
    {
        var (db, _, store) = await NewAsync();
        await using var _ = db;
        await Assert.ThrowsAsync<ArgumentException>(() => store.CreateUserAsync(name, "x", Role.Member));
        await Assert.ThrowsAsync<ArgumentException>(() => store.CreateGroupAsync(name));
    }

    [Fact]
    public async Task A_deleted_group_keeps_its_id_and_a_new_group_with_its_name_gets_a_new_one()
    {
        var (db, _, store) = await NewAsync();
        await using var _ = db;
        var alice = await store.CreateUserAsync("alice", "Alice", Role.Member);
        var old = await store.CreateGroupAsync("Staff");
        await store.AddMemberAsync(old.Id, alice.Id);

        Assert.True(await store.DeleteGroupAsync(old.Id));
        Assert.False(await store.DeleteGroupAsync(old.Id));
        Assert.Empty(await store.GroupsOfUserAsync(alice.Id));
        Assert.Null(await store.FindGroupByNameAsync("staff"));
        var found = await store.FindGroupAsync(old.Id);
        Assert.Equal(old, found!.Value.Group);
        Assert.True(found.Value.Deleted);

        var reused = await store.CreateGroupAsync("staff");
        Assert.NotEqual(old.Id, reused.Id);
        Assert.Empty(await store.ListMembersAsync(reused.Id));
        Assert.False(await store.AddMemberAsync(old.Id, alice.Id));
    }

    [Fact]
    public async Task The_directory_sees_live_rows_only_and_a_rename_keeps_the_id()
    {
        var (db, _, store) = await NewAsync();
        await using var _ = db;
        var alice = await store.CreateUserAsync("alice", "Alice", Role.Member);
        var staff = await store.CreateGroupAsync("Staff");
        var gone = await store.CreateGroupAsync("Gone");
        await store.AddMemberAsync(staff.Id, alice.Id);
        await store.AddMemberAsync(gone.Id, alice.Id);
        await store.DeleteGroupAsync(gone.Id);

        Assert.True(await store.RenameGroupAsync(staff.Id, "Employees"));
        Assert.Equal([new Group(staff.Id, "Employees")], await store.GroupsOfUserAsync(alice.Id));
        var other = await store.CreateGroupAsync("Other");
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.RenameGroupAsync(other.Id, "employees"));
    }

    [Fact]
    public async Task Tenants_see_nothing_of_each_other()
    {
        var (db, _, a) = await NewAsync("a");
        await using var _ = db;
        var tenantB = await db.EnsureTenantAsync("b", "B");
        var b = new IdentityStore(db, tenantB);

        var alice = await a.CreateUserAsync("alice", "Alice", Role.Member);
        var staff = await a.CreateGroupAsync("Staff");
        var agent = await a.CreateAgentAsync("bot", alice.Id, AgentMode.Service, 60, null, ModelLocation.Local);
        var token = await a.IssueTokenAsync(agent.Id, TimeSpan.FromDays(1));

        Assert.Null(await b.FindUserAsync(alice.Id));
        Assert.Null(await b.FindUserByNameAsync("alice"));
        Assert.Null(await b.FindGroupByNameAsync("Staff"));
        Assert.Null(await b.FindAgentAsync(agent.Id));
        Assert.Null(await b.FindTokenAsync(token.Record.Id));
        Assert.Equal("refused:UnknownToken", (await CallerAccess.ForAgentTokenAsync(b, token.PlainText)).AuditLabel);

        // Tenant b cannot join a's user to its own group, or own a's user.
        var bGroup = await b.CreateGroupAsync("Staff");
        Assert.False(await b.AddMemberAsync(bGroup.Id, alice.Id));
        await Assert.ThrowsAsync<InvalidOperationException>(() => b.CreateAgentAsync("bot", alice.Id, AgentMode.Service, 60, null, ModelLocation.Local));

        // The same names are free in b.
        Assert.NotEqual(alice.Id, (await b.CreateUserAsync("alice", "Alice", Role.Member)).Id);
    }

    [Fact]
    public async Task An_agent_acting_for_a_user_cannot_be_granted_groups()
    {
        var (db, _, store) = await NewAsync();
        await using var _ = db;
        var alice = await store.CreateUserAsync("alice", "Alice", Role.Member);
        var staff = await store.CreateGroupAsync("Staff");
        var assistant = await store.CreateAgentAsync("assistant", alice.Id, AgentMode.ActsForUser, 60, null, ModelLocation.Local);
        var bot = await store.CreateAgentAsync("bot", alice.Id, AgentMode.Service, 60, "human_reviewed", ModelLocation.Local);

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.GrantGroupAsync(assistant.Id, staff.Id));
        Assert.True(await store.GrantGroupAsync(bot.Id, staff.Id));
        Assert.False(await store.GrantGroupAsync(bot.Id, staff.Id));
        Assert.Equal([staff], await store.GroupsGrantedToAgentAsync(bot.Id));
        Assert.Equal("human_reviewed", (await store.FindAgentAsync(bot.Id))!.MinimumTrustTier);
        Assert.Null((await store.FindAgentAsync(assistant.Id))!.MinimumTrustTier);
    }

    [Fact]
    public async Task A_token_is_stored_only_as_the_hash_of_its_secret()
    {
        var (db, _, store) = await NewAsync();
        await using var _ = db;
        var alice = await store.CreateUserAsync("alice", "Alice", Role.Member);
        var bot = await store.CreateAgentAsync("bot", alice.Id, AgentMode.Service, 60, null, ModelLocation.Local);
        var issued = await store.IssueTokenAsync(bot.Id, TimeSpan.FromDays(7));
        var secret = issued.PlainText[(AgentTokens.Prefix.Length + 25)..];

        await using var cmd = db.DataSource.CreateCommand("SELECT t::text, secret_sha256 FROM prem_config.agent_token t WHERE id = @id");
        cmd.Parameters.AddWithValue("id", issued.Record.Id);
        await using var reader = await cmd.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.DoesNotContain(secret, reader.GetString(0));
        Assert.Equal(SHA256.HashData(System.Buffers.Text.Base64Url.DecodeFromChars(secret)), reader.GetFieldValue<byte[]>(1));
    }

    [Fact]
    public async Task Revoking_a_token_is_recorded_once()
    {
        var (db, _, store) = await NewAsync();
        await using var _ = db;
        var alice = await store.CreateUserAsync("alice", "Alice", Role.Member);
        var bot = await store.CreateAgentAsync("bot", alice.Id, AgentMode.Service, 60, null, ModelLocation.Local);
        var issued = await store.IssueTokenAsync(bot.Id, TimeSpan.FromDays(7));

        Assert.True(await store.RevokeTokenAsync(issued.Record.Id));
        Assert.False(await store.RevokeTokenAsync(issued.Record.Id));
        Assert.NotNull((await store.FindTokenAsync(issued.Record.Id))!.RevokedAt);
        Assert.Single(await store.ListTokensAsync(bot.Id));
        Assert.Empty(await store.ListTokensAsync(alice.Id));
    }

    [Fact]
    public async Task A_password_is_stored_only_as_a_hash()
    {
        var (db, _, store) = await NewAsync();
        await using var _ = db;
        var alice = await store.CreateUserAsync("alice", "Alice", Role.Member);

        await Assert.ThrowsAsync<ArgumentException>(() => store.SetPasswordHashAsync(alice.Id, "correct horse"));
        Assert.True(await store.SetPasswordHashAsync(alice.Id, new PasswordHasher().Hash("correct horse")));
        Assert.True(Assert.Single(await store.ListUsersAsync()).HasPassword);
    }

    [Fact]
    public async Task An_access_list_is_stored_once_per_text_and_never_rewritten()
    {
        var (db, tenant, _) = await NewAsync();
        await using var _ = db;
        var sets = new AclStore(db, tenant);
        var denyFirst = AclSet.Of(AclEntry.Deny(Principal.Group("c")), AclEntry.Allow(Principal.Group("s")));
        var allowFirst = AclSet.Of(AclEntry.Allow(Principal.Group("s")), AclEntry.Deny(Principal.Group("c")));

        var id = await sets.EnsureSetAsync(denyFirst);
        Assert.Equal(id, await sets.EnsureSetAsync(AclSet.Of(AclEntry.Deny(Principal.Group("c")), AclEntry.Allow(Principal.Group("s")))));
        Assert.NotEqual(id, await sets.EnsureSetAsync(allowFirst));

        var stored = Assert.Single(await sets.LoadSetsAsync(), s => s.Id == id);
        Assert.Equal(denyFirst, stored.Set);

        // Another tenant's identical list is its own row.
        var other = await db.EnsureTenantAsync("other", "Other");
        Assert.NotEqual(id, await new AclStore(db, other).EnsureSetAsync(denyFirst));
    }

    [Fact]
    public async Task A_stored_list_that_does_not_parse_reads_as_the_empty_list()
    {
        var (db, tenant, _) = await NewAsync();
        await using var _ = db;
        await using (var cmd = db.DataSource.CreateCommand(
            "INSERT INTO prem_config.acl_set(tenant_id, sha256, canonical_text) VALUES(@t, repeat('a', 64), 'allow role:x' || chr(10))"))
        {
            cmd.Parameters.AddWithValue("t", tenant);
            await cmd.ExecuteNonQueryAsync();
        }

        var stored = Assert.Single(await new AclStore(db, tenant).LoadSetsAsync());
        Assert.Same(AclSet.Empty, stored.Set);
    }

    [Fact]
    public async Task Settings_round_trip_per_tenant()
    {
        var (db, tenant, _) = await NewAsync();
        await using var _ = db;
        var settings = new SettingsStore(db, tenant);
        var other = new SettingsStore(db, await db.EnsureTenantAsync("other", "Other"));

        Assert.Null(await settings.GetAsync("retrieval.rrf_k"));
        await settings.SetAsync("retrieval.rrf_k", JsonDocument.Parse("50").RootElement);
        Assert.Equal(50, (await settings.GetAsync("retrieval.rrf_k"))!.Value.GetInt32());
        Assert.Null(await other.GetAsync("retrieval.rrf_k"));
        Assert.True(await settings.RemoveAsync("retrieval.rrf_k"));
        Assert.False(await settings.RemoveAsync("retrieval.rrf_k"));
        await Assert.ThrowsAsync<ArgumentException>(() => settings.GetAsync("Portal Size"));
    }

    [Fact]
    public async Task The_store_refuses_a_key_the_catalog_does_not_define_for_every_verb()
    {
        var (db, tenant, _) = await NewAsync();
        await using var _ = db;
        var settings = new SettingsStore(db, tenant);
        var value = JsonDocument.Parse("50").RootElement;

        // A well-formed key that no setting defines: a store that wrote one
        // would hold a value no command lists, reads or removes.
        var set = await Assert.ThrowsAsync<ArgumentException>(() => settings.SetAsync("portal.page_size", value));
        Assert.Contains(nameof(SettingsCatalog), set.Message);
        await Assert.ThrowsAsync<ArgumentException>(() => settings.GetAsync("portal.page_size"));
        await Assert.ThrowsAsync<ArgumentException>(() => settings.GetManyAsync(["retrieval.rrf_k", "portal.page_size"]));
        await Assert.ThrowsAsync<ArgumentException>(() => settings.RemoveAsync("portal.page_size"));

        // The control: a key the catalog defines is written and read.
        await settings.SetAsync("retrieval.rrf_k", value);
        Assert.Equal(50, (await settings.GetAsync("retrieval.rrf_k"))!.Value.GetInt32());
    }

    [Fact]
    public async Task The_general_store_refuses_to_change_a_trust_setting_and_still_reads_one()
    {
        var (db, tenant, _) = await NewAsync();
        await using var _ = db;
        var settings = new SettingsStore(db, tenant);
        var value = JsonDocument.Parse("\"unverified\"").RootElement;

        await using (var cmd = db.DataSource.CreateCommand(
            "INSERT INTO prem_config.setting(tenant_id, key, value) VALUES(@tenant, 'trust.agents_minimum_tier', '\"human-reviewed\"')"))
        {
            cmd.Parameters.AddWithValue("tenant", tenant);
            await cmd.ExecuteNonQueryAsync();
        }

        var set = await Assert.ThrowsAsync<InvalidOperationException>(() => settings.SetAsync("trust.agents_minimum_tier", value));
        Assert.Contains("prem settings set", set.Message);
        await Assert.ThrowsAsync<InvalidOperationException>(() => settings.RemoveAsync("trust.agents_minimum_tier"));
        // A trust key no setting defines is refused as well, before its prefix is looked at.
        await Assert.ThrowsAsync<ArgumentException>(() => settings.SetAsync("trust.new_key", value));

        // Nothing changed, and reading is still allowed.
        Assert.Equal("human-reviewed", (await settings.GetAsync("trust.agents_minimum_tier"))!.Value.GetString());
        // The control: a key outside trust. is written.
        await settings.SetAsync("evaluation.golden_set_path", value);
        Assert.Equal("unverified", (await settings.GetAsync("evaluation.golden_set_path"))!.Value.GetString());
    }
}

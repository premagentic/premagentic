using Premagentic.Core;
using Premagentic.Core.Identity;
using Premagentic.Core.Ingestion;
using Premagentic.Core.Retrieval;
using Premagentic.Core.Security;
using Premagentic.Core.Storage;

namespace Premagentic.Tests;

/// <summary>
/// The access gate end to end on the database: Premagentic's own users, groups,
/// agents, tokens and folder rules deciding what each search returns.
/// <para>
/// Every case asserts both directions. The world below gives each caller
/// something it may read and something it may not, so a gate that returned
/// nothing to anybody would fail here as surely as one that leaked.
/// </para>
/// Requires a running Docker daemon.
/// </summary>
public sealed class IdentityGateTests(DatastoreTestDatabase server) : IClassFixture<DatastoreTestDatabase>
{
    private const string Source = "test-datastore";
    private const string Handbook = "pub/handbook.md";
    private const string Plan = "staff/plan.md";
    private const string AuditLog = "audit/log.md";
    private const string Pay = "hr/pay.md";
    private const string Draft = "none/draft.md";

    private sealed record World(
        PremagenticDatabase Db, Guid Tenant, TestClock Clock, IdentityStore Identity, AclStore Rules, PrincipalNames Names,
        IngestPipeline Pipeline, HybridSearch Search, SectionFetcher Sections, DatastoreSource Corpus,
        User Alice, User Bob, User Carol, Group Staff, Group Contractors, Group Auditors,
        Agent Assistant, Agent Bot, string AssistantToken, string BotToken);

    /// <summary>
    /// Alice is staff. Bob is staff and a contractor. Carol owns a service agent
    /// and belongs to nothing. Alice's assistant acts for her; the report bot is
    /// a service agent holding the auditors group.
    /// <list type="bullet">
    /// <item><c>pub</c>: allow everyone</item>
    /// <item><c>staff</c>: deny contractors, then allow staff</item>
    /// <item><c>audit</c>: allow auditors</item>
    /// <item><c>hr</c>: deny the assistant, then allow Alice</item>
    /// <item><c>none</c>: no rule</item>
    /// </list>
    /// </summary>
    private async Task<World> NewWorldAsync()
    {
        var db = new PremagenticDatabase(await server.CreateDatabaseAsync());
        await db.InitializeAsync();
        var tenant = await db.EnsureTenantAsync("t", "T");
        var clock = new TestClock(new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.Zero));
        var identity = new IdentityStore(db, tenant, clock);
        var rules = new AclStore(db, tenant);
        var names = new PrincipalNames(identity);

        var alice = await identity.CreateUserAsync("alice", "Alice", Role.Member);
        var bob = await identity.CreateUserAsync("bob", "Bob", Role.Member);
        var carol = await identity.CreateUserAsync("carol", "Carol", Role.Administrator);
        var staff = await identity.CreateGroupAsync("Staff");
        var contractors = await identity.CreateGroupAsync("Contractors");
        var auditors = await identity.CreateGroupAsync("Auditors");
        await identity.AddMemberAsync(staff.Id, alice.Id);
        await identity.AddMemberAsync(staff.Id, bob.Id);
        await identity.AddMemberAsync(contractors.Id, bob.Id);

        var assistant = await identity.CreateAgentAsync("assistant", alice.Id, AgentMode.ActsForUser, 60, null, ModelLocation.Local);
        var bot = await identity.CreateAgentAsync("report-bot", carol.Id, AgentMode.Service, 60, null, ModelLocation.Local);
        await identity.GrantGroupAsync(bot.Id, auditors.Id);
        var assistantToken = (await identity.IssueTokenAsync(assistant.Id, TimeSpan.FromDays(30))).PlainText;
        var botToken = (await identity.IssueTokenAsync(bot.Id, TimeSpan.FromDays(30))).PlainText;

        await SetRuleAsync(names, rules, "pub", "allow everyone");
        await SetRuleAsync(names, rules, "staff", "deny group:Contractors", "allow group:Staff");
        await SetRuleAsync(names, rules, "audit", "allow group:Auditors");
        await SetRuleAsync(names, rules, "hr", "deny agent:assistant", "allow user:alice");

        var corpus = new DatastoreSource("", [
            DatastoreSource.Doc(Handbook, "## Handbook\nthe zeppelin handbook for everyone", DocumentAccess.FolderRules),
            DatastoreSource.Doc(Plan, "## Plan\nthe zeppelin staff plan", DocumentAccess.FolderRules),
            DatastoreSource.Doc(AuditLog, "## Log\nthe zeppelin audit log", DocumentAccess.FolderRules),
            DatastoreSource.Doc(Pay, "## Pay\nthe zeppelin pay bands", DocumentAccess.FolderRules),
            DatastoreSource.Doc(Draft, "## Draft\nthe zeppelin draft nobody may read", DocumentAccess.FolderRules),
        ]);
        var embedder = new SeededEmbeddingProvider();
        var pipeline = new IngestPipeline(db, embedder);
        var summary = await pipeline.RunAsync(tenant, corpus);
        Assert.Equal(5, summary.Ingested);
        Assert.Equal(1, summary.DeniedToEveryone);

        return new World(db, tenant, clock, identity, rules, names, pipeline, new HybridSearch(db, embedder),
            new SectionFetcher(db), corpus, alice, bob, carol, staff, contractors, auditors, assistant, bot,
            assistantToken, botToken);
    }

    private static async Task<int> SetRuleAsync(PrincipalNames names, AclStore rules, string prefix, params string[] entries) =>
        await rules.SetRuleAsync(new Core.Security.Acl.FolderRule(Source, prefix, await names.ToAclSetAsync(entries)));

    private static async Task<string[]> PathsAsync(World w, AccessScope scope) =>
        (await w.Search.SearchAsync(w.Tenant, "zeppelin", new SearchOptions(scope, TopK: 20)))
        .Hits.Select(h => h.Path).Distinct().Order(StringComparer.Ordinal).ToArray();

    private static Task<AccessScope> AsUserAsync(World w, User user) => CallerAccess.ForUserAsync(w.Identity, user.Id);

    private static Task<AccessScope> WithTokenAsync(World w, string token) => CallerAccess.ForAgentTokenAsync(w.Identity, token);

    private static async Task<Dictionary<string, Guid[]>> ChunkIdsAsync(World w)
    {
        var ids = new Dictionary<string, List<Guid>>();
        await using var cmd = w.Db.DataSource.CreateCommand(
            "SELECT d.path, c.id FROM prem_index.chunk c JOIN prem_index.document d ON d.id = c.document_id ORDER BY c.id");
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var path = reader.GetString(0);
            if (!ids.TryGetValue(path, out var list)) ids[path] = list = [];
            list.Add(reader.GetGuid(1));
        }
        return ids.ToDictionary(kv => kv.Key, kv => kv.Value.ToArray());
    }

    [Fact]
    public async Task Two_users_and_two_agents_each_see_exactly_what_the_rules_give_them()
    {
        var w = await NewWorldAsync();
        await using var _ = w.Db;

        Assert.Equal([AuditLog, Handbook], await PathsAsync(w, await WithTokenAsync(w, w.BotToken)));
        Assert.Equal([Pay, Handbook, Plan], await PathsAsync(w, await AsUserAsync(w, w.Alice)));
        Assert.Equal([Handbook, Plan], await PathsAsync(w, await WithTokenAsync(w, w.AssistantToken)));
        Assert.Equal([Handbook], await PathsAsync(w, await AsUserAsync(w, w.Bob)));
        Assert.Equal([Handbook], await PathsAsync(w, await AsUserAsync(w, w.Carol)));
        Assert.Equal([Handbook], await PathsAsync(w, AccessScope.PublicOnly));

        // The control: everything is there to be found.
        Assert.Equal([AuditLog, Pay, Draft, Handbook, Plan], await PathsAsync(w, AccessScope.UnrestrictedAudited("test")));
    }

    [Fact]
    public async Task Removing_a_group_membership_changes_the_next_query()
    {
        var w = await NewWorldAsync();
        await using var _ = w.Db;
        Assert.Contains(Plan, await PathsAsync(w, await AsUserAsync(w, w.Alice)));
        Assert.Contains(Plan, await PathsAsync(w, await WithTokenAsync(w, w.AssistantToken)));

        await w.Identity.RemoveMemberAsync(w.Staff.Id, w.Alice.Id);

        Assert.DoesNotContain(Plan, await PathsAsync(w, await AsUserAsync(w, w.Alice)));
        Assert.DoesNotContain(Plan, await PathsAsync(w, await WithTokenAsync(w, w.AssistantToken)));
        // Only that group went: the rest of what she may read is untouched.
        Assert.Contains(Pay, await PathsAsync(w, await AsUserAsync(w, w.Alice)));
    }

    [Fact]
    public async Task A_rule_change_repoints_documents_and_changes_the_next_query_with_no_reembedding()
    {
        var w = await NewWorldAsync();
        await using var _ = w.Db;
        var before = await ChunkIdsAsync(w);
        Assert.DoesNotContain(AuditLog, await PathsAsync(w, await AsUserAsync(w, w.Alice)));

        var moved = await SetRuleAsync(w.Names, w.Rules, "audit", "allow group:Staff");

        Assert.Equal(1, moved);
        Assert.Contains(AuditLog, await PathsAsync(w, await AsUserAsync(w, w.Alice)));
        Assert.DoesNotContain(AuditLog, await PathsAsync(w, await WithTokenAsync(w, w.BotToken)));

        // No chunk was replaced, and the next ingest finds nothing to redo.
        Assert.Equal(before.Keys.Order(), (await ChunkIdsAsync(w)).Keys.Order());
        foreach (var (path, ids) in await ChunkIdsAsync(w))
            Assert.Equal(before[path], ids);
        var again = await w.Pipeline.RunAsync(w.Tenant, w.Corpus);
        Assert.Equal(0, again.Ingested);
        Assert.Equal(5, again.Unchanged);
    }

    [Fact]
    public async Task A_renamed_group_keeps_its_deny()
    {
        var w = await NewWorldAsync();
        await using var _ = w.Db;
        Assert.DoesNotContain(Plan, await PathsAsync(w, await AsUserAsync(w, w.Bob)));

        await w.Identity.RenameGroupAsync(w.Contractors.Id, "Vendors");

        Assert.DoesNotContain(Plan, await PathsAsync(w, await AsUserAsync(w, w.Bob)));
        Assert.Contains(Plan, await PathsAsync(w, await AsUserAsync(w, w.Alice)));
    }

    [Fact]
    public async Task A_deleted_group_leaves_its_rules_matching_nobody_and_a_new_group_with_its_name_inherits_nothing()
    {
        var w = await NewWorldAsync();
        await using var _ = w.Db;

        await w.Identity.DeleteGroupAsync(w.Auditors.Id);
        var reused = await w.Identity.CreateGroupAsync("Auditors");
        await w.Identity.AddMemberAsync(reused.Id, w.Carol.Id);
        Assert.NotEqual(w.Auditors.Id, reused.Id);

        Assert.DoesNotContain(AuditLog, await PathsAsync(w, await AsUserAsync(w, w.Carol)));
        Assert.DoesNotContain(AuditLog, await PathsAsync(w, await WithTokenAsync(w, w.BotToken)));
        Assert.Contains(Handbook, await PathsAsync(w, await AsUserAsync(w, w.Carol)));
    }

    [Fact]
    public async Task The_rules_that_name_a_group_can_be_found_before_it_is_deleted()
    {
        var w = await NewWorldAsync();
        await using var _ = w.Db;

        var naming = await w.Rules.RulesNamingAsync(Core.Security.Acl.Principal.Group(CallerResolver.IdText(w.Contractors.Id)));
        Assert.Equal(["staff"], naming.Select(r => r.Rule.PathPrefix).ToArray());
        Assert.Empty(await w.Rules.RulesNamingAsync(Core.Security.Acl.Principal.Group(CallerResolver.IdText(Guid.NewGuid()))));

        // Why the question matters: deleting a group a deny names lets its members through.
        await w.Identity.DeleteGroupAsync(w.Contractors.Id);
        Assert.Contains(Plan, await PathsAsync(w, await AsUserAsync(w, w.Bob)));
    }

    [Fact]
    public async Task A_document_with_no_access_list_reaches_nobody()
    {
        var w = await NewWorldAsync();
        await using var _ = w.Db;
        await using (var cmd = w.Db.DataSource.CreateCommand("UPDATE prem_index.document SET acl_set_id = NULL WHERE path = @p"))
        {
            cmd.Parameters.AddWithValue("p", Handbook);
            Assert.Equal(1, await cmd.ExecuteNonQueryAsync());
        }

        Assert.DoesNotContain(Handbook, await PathsAsync(w, AccessScope.PublicOnly));
        Assert.DoesNotContain(Handbook, await PathsAsync(w, await AsUserAsync(w, w.Alice)));
        Assert.Null(await w.Sections.GetAsync(w.Tenant, await AsUserAsync(w, w.Alice), Handbook, null, false));
        // The control: it is still indexed, and Alice still reads her own.
        Assert.Contains(Handbook, await PathsAsync(w, AccessScope.UnrestrictedAudited("test")));
        Assert.Contains(Pay, await PathsAsync(w, await AsUserAsync(w, w.Alice)));
    }

    [Fact]
    public async Task A_disabled_owner_stops_both_kinds_of_agent()
    {
        var w = await NewWorldAsync();
        await using var _ = w.Db;
        Assert.NotEmpty(await PathsAsync(w, await WithTokenAsync(w, w.AssistantToken)));
        Assert.NotEmpty(await PathsAsync(w, await WithTokenAsync(w, w.BotToken)));

        await w.Identity.SetUserDisabledAsync(w.Alice.Id, true);
        var assistant = await WithTokenAsync(w, w.AssistantToken);
        Assert.Equal("refused:OwnerDisabled", assistant.AuditLabel);
        Assert.Empty(await PathsAsync(w, assistant));
        Assert.NotEmpty(await PathsAsync(w, await WithTokenAsync(w, w.BotToken)));

        await w.Identity.SetUserDisabledAsync(w.Carol.Id, true);
        var bot = await WithTokenAsync(w, w.BotToken);
        Assert.Equal("refused:OwnerDisabled", bot.AuditLabel);
        Assert.Empty(await PathsAsync(w, bot));
    }

    [Fact]
    public async Task An_agent_acting_for_a_user_is_narrowed_by_an_entry_that_names_it()
    {
        var w = await NewWorldAsync();
        await using var _ = w.Db;
        var alice = await AsUserAsync(w, w.Alice);
        var assistant = await WithTokenAsync(w, w.AssistantToken);

        // It holds exactly Alice's principals, yet the deny naming it holds it back.
        Assert.Equal(alice.Holds.OrderBy(p => p.ToString()).Select(p => p.ToString()),
                     assistant.Holds.OrderBy(p => p.ToString()).Select(p => p.ToString()));
        Assert.Contains(Pay, await PathsAsync(w, alice));
        Assert.DoesNotContain(Pay, await PathsAsync(w, assistant));
        Assert.NotNull(await w.Sections.GetAsync(w.Tenant, alice, Pay, null, false));
        Assert.Null(await w.Sections.GetAsync(w.Tenant, assistant, Pay, null, false));

        // An entry naming the agent can only take away: allowing it where Alice
        // is not allowed gives it nothing.
        await SetRuleAsync(w.Names, w.Rules, "audit", "allow agent:assistant");
        Assert.DoesNotContain(AuditLog, await PathsAsync(w, await WithTokenAsync(w, w.AssistantToken)));
    }

    [Fact]
    public async Task An_expired_or_revoked_token_resolves_to_no_access()
    {
        var w = await NewWorldAsync();
        await using var _ = w.Db;
        var tokenId = w.BotToken.Substring(AgentTokens.Prefix.Length, 24);

        w.Clock.Now = w.Clock.Now.AddDays(30);
        var expired = await WithTokenAsync(w, w.BotToken);
        Assert.Equal("refused:TokenExpired", expired.AuditLabel);
        Assert.Empty(await PathsAsync(w, expired));
        Assert.Null((await w.Identity.FindTokenAsync(tokenId))!.LastUsedAt);

        w.Clock.Now = w.Clock.Now.AddDays(-29);
        Assert.Contains(AuditLog, await PathsAsync(w, await WithTokenAsync(w, w.BotToken)));
        Assert.Equal(w.Clock.Now, (await w.Identity.FindTokenAsync(tokenId))!.LastUsedAt);

        Assert.True(await w.Identity.RevokeTokenAsync(tokenId));
        var revoked = await WithTokenAsync(w, w.BotToken);
        Assert.Equal("refused:TokenRevoked", revoked.AuditLabel);
        Assert.Empty(await PathsAsync(w, revoked));
    }

    [Fact]
    public async Task The_audit_trail_records_which_user_and_agent_asked()
    {
        var w = await NewWorldAsync();
        await using var _ = w.Db;

        await PathsAsync(w, await WithTokenAsync(w, w.AssistantToken));
        var assistantEvent = await LastEventAsync(w);
        Assert.Equal(w.Alice.Id, assistantEvent.User);
        Assert.Equal(w.Assistant.Id, assistantEvent.Agent);
        Assert.StartsWith($"agent:{w.Assistant.Id}", assistantEvent.Label);

        await PathsAsync(w, await WithTokenAsync(w, w.BotToken));
        var botEvent = await LastEventAsync(w);
        Assert.Null(botEvent.User);
        Assert.Equal(w.Bot.Id, botEvent.Agent);

        await PathsAsync(w, await AsUserAsync(w, w.Bob));
        var bobEvent = await LastEventAsync(w);
        Assert.Equal(w.Bob.Id, bobEvent.User);
        Assert.Null(bobEvent.Agent);
        Assert.Equal($"user:{w.Bob.Id}", bobEvent.Label);

        await PathsAsync(w, AccessScope.PublicOnly);
        var anonymous = await LastEventAsync(w);
        Assert.Null(anonymous.User);
        Assert.Null(anonymous.Agent);
    }

    [Fact]
    public async Task A_new_rule_reaches_documents_no_rule_covered_and_removing_it_returns_them_to_nobody()
    {
        var w = await NewWorldAsync();
        await using var _ = w.Db;
        Assert.DoesNotContain(Draft, await PathsAsync(w, AccessScope.PublicOnly));

        Assert.Equal(1, await SetRuleAsync(w.Names, w.Rules, "none", "allow everyone"));
        Assert.Contains(Draft, await PathsAsync(w, AccessScope.PublicOnly));

        var (removed, moved) = await w.Rules.RemoveRuleAsync(Source, "none");
        Assert.True(removed);
        Assert.Equal(1, moved);
        Assert.DoesNotContain(Draft, await PathsAsync(w, AccessScope.PublicOnly));
        Assert.Contains(Handbook, await PathsAsync(w, AccessScope.PublicOnly));
    }

    [Fact]
    public async Task A_rule_for_a_shorter_prefix_never_overrides_a_longer_one()
    {
        var w = await NewWorldAsync();
        await using var _ = w.Db;

        // A whole-source rule reaches only the one folder with no rule of its own.
        Assert.Equal(1, await SetRuleAsync(w.Names, w.Rules, "", "allow everyone"));
        Assert.Contains(Draft, await PathsAsync(w, await AsUserAsync(w, w.Bob)));
        Assert.DoesNotContain(Plan, await PathsAsync(w, await AsUserAsync(w, w.Bob)));
        Assert.DoesNotContain(Pay, await PathsAsync(w, await AsUserAsync(w, w.Bob)));

        // Removing the staff rule drops its folder to the whole-source rule.
        var (_, moved) = await w.Rules.RemoveRuleAsync(Source, "staff");
        Assert.Equal(1, moved);
        Assert.Contains(Plan, await PathsAsync(w, await AsUserAsync(w, w.Bob)));
    }

    [Fact]
    public async Task A_rule_change_never_moves_a_document_its_connector_decided_or_a_path_the_rules_cannot_judge()
    {
        var w = await NewWorldAsync();
        await using var _ = w.Db;
        const string decided = "pub/decided.md";
        const string odd = "pub//odd.md";
        var corpus = new DatastoreSource("", [
            DatastoreSource.Doc(decided, "## Decided\nthe zeppelin connector note", DocumentAccess.NoOne),
            DatastoreSource.Doc(odd, "## Odd\nthe zeppelin odd path", DocumentAccess.FolderRules),
            DatastoreSource.Doc(Handbook, "## Handbook\nthe zeppelin handbook for everyone", DocumentAccess.FolderRules),
        ]);
        await w.Pipeline.RunAsync(w.Tenant, corpus);
        Assert.Equal([Handbook], await PathsAsync(w, AccessScope.PublicOnly));

        await SetRuleAsync(w.Names, w.Rules, "pub", "allow everyone", "allow group:Staff");
        await SetRuleAsync(w.Names, w.Rules, "", "allow everyone");

        var visible = await PathsAsync(w, await AsUserAsync(w, w.Alice));
        Assert.Contains(Handbook, visible);
        Assert.DoesNotContain(decided, visible);
        Assert.DoesNotContain(odd, visible);
    }

    private static async Task<(Guid? User, Guid? Agent, string Label)> LastEventAsync(World w)
    {
        await using var cmd = w.Db.DataSource.CreateCommand(
            "SELECT caller_user_id, caller_agent_id, access_label FROM prem_config.retrieval_event ORDER BY created_at DESC LIMIT 1");
        await using var reader = await cmd.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return (reader.IsDBNull(0) ? null : reader.GetGuid(0), reader.IsDBNull(1) ? null : reader.GetGuid(1), reader.GetString(2));
    }
}

/// <summary>A clock a test can set.</summary>
internal sealed class TestClock(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now;
}

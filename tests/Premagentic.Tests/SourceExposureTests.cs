using Premagentic.Core;
using Premagentic.Core.Admin;
using Premagentic.Core.Embeddings;
using Premagentic.Core.Identity;
using Premagentic.Core.Ingestion;
using Premagentic.Core.Retrieval;
using Premagentic.Core.Security;
using Premagentic.Core.Security.Acl;
using Premagentic.Core.Sources;
using Premagentic.Core.Storage;

namespace Premagentic.Tests;

/// <summary>
/// The "may be served to hosted models" switch: off is a hold on the source's
/// folder, kept beside the folder rules. The tests that matter run through the
/// real gate, because the switch is only worth anything if a search honors it.
/// The rules that cannot undo a hold are in <see cref="HostedHoldTests"/>.
/// Every file here is invented. Requires a running Docker daemon.
/// </summary>
public sealed class SourceExposureTests(DatastoreTestDatabase server) : IClassFixture<DatastoreTestDatabase>
{
    private static readonly AdminActor Tester = new("cli", "test-account");
    private const string Handbook = "handbook/rota.md";

    private sealed record World(
        PremagenticDatabase Db, Guid Tenant, IdentityStore Identity, AclStore Rules,
        SourceExposureStore Exposure, ChangeRecord Record, HybridSearch Search, User Owner, Group Reserved)
        : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }

    /// <summary>One folder anyone may read, indexed, with nothing held back yet.</summary>
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

        await rules.SetRuleAsync(new FolderRule(
            SourceExposureStore.RuleSource, "handbook", AclSet.Of(AclEntry.Allow(Principal.Everyone))));

        // Ingested by the real file system connector, because a folder rule is
        // keyed on the CONNECTOR's name and the switch writes its rule under
        // that name. A test corpus from another connector would sit under no
        // rule at all, and every caller would read nothing for a reason that
        // has nothing to do with the switch.
        var embedder = new SeededEmbeddingProvider();
        var corpus = SourcesTests.Folder(
            ("rota.md", "# Rota\n\n## Weekend\nTwo people open the zeppelin shop on Saturdays.\n"));
        var summary = await new IngestPipeline(db, embedder).RunAsync(
            tenant, new FileSystemSource(corpus, DocumentAccess.FolderRules, "handbook"));
        Assert.Equal(1, summary.Ingested);
        Assert.Equal(0, summary.DeniedToEveryone);

        return new World(db, tenant, identity, rules, new SourceExposureStore(db, tenant),
            new ChangeRecord(db, tenant), new HybridSearch(db, embedder), owner, reserved);
    }

    private static async Task<string[]> PathsAsync(World w, Agent agent) =>
        (await w.Search.SearchAsync(w.Tenant, "zeppelin", new SearchOptions(
            await CallerAccess.ForAgentTokenAsync(
                w.Identity, (await w.Identity.IssueTokenAsync(agent.Id, TimeSpan.FromDays(30))).PlainText),
            TopK: 20))).Hits.Select(h => h.Path).Distinct().ToArray();

    private static AclSet Rule(World w) =>
        w.Rules.ListRulesAsync().GetAwaiter().GetResult()
            .Single(r => r.Rule.PathPrefix == "handbook").Rule.Acl;

    /// <summary>
    /// Marks a hold legacy, as migration 0141 marks the holds it makes from a
    /// switch turned off before it.
    /// </summary>
    internal static async Task MarkLegacyAsync(PremagenticDatabase db, Guid tenant, string source, string prefix)
    {
        await using var cmd = db.DataSource.CreateCommand(
            "UPDATE prem_config.hosted_hold SET legacy = true WHERE tenant_id = @tenant AND source = @source AND path_prefix = @prefix");
        cmd.Parameters.AddWithValue("tenant", tenant);
        cmd.Parameters.AddWithValue("source", source);
        cmd.Parameters.AddWithValue("prefix", prefix);
        Assert.Equal(1, await cmd.ExecuteNonQueryAsync());
    }

    /// <summary>The list a document stores, as its canonical text.</summary>
    internal static async Task<string?> StoredListAsync(PremagenticDatabase db, Guid tenant, string path)
    {
        await using var cmd = db.DataSource.CreateCommand("""
            SELECT s.canonical_text FROM prem_index.document d
            JOIN prem_config.acl_set s ON s.tenant_id = d.tenant_id AND s.id = d.acl_set_id
            WHERE d.tenant_id = @tenant AND d.path = @path
            """);
        cmd.Parameters.AddWithValue("tenant", tenant);
        cmd.Parameters.AddWithValue("path", path);
        return await cmd.ExecuteScalarAsync() as string;
    }

    [Fact]
    public async Task With_the_switch_off_a_hosted_agent_gets_nothing_and_a_local_agent_gets_the_documents()
    {
        await using var w = await NewWorldAsync();
        var hosted = await w.Identity.CreateAgentAsync(
            "desk", w.Owner.Id, AgentMode.ActsForUser, 60, null, ModelLocation.Hosted, "a vendor");
        var local = await w.Identity.CreateAgentAsync(
            "shop", w.Owner.Id, AgentMode.ActsForUser, 60, null, ModelLocation.Local);

        // Before the switch, the folder is readable by anyone the deployment
        // authenticates, so both read it. That is the control: without it, a
        // hosted agent reading nothing afterwards would prove nothing.
        Assert.Equal([Handbook], await PathsAsync(w, hosted));
        Assert.Equal([Handbook], await PathsAsync(w, local));

        var (before, after, changed) = await w.Exposure.SetAsync("handbook", mayBeServed: false, Tester);

        Assert.True(changed);
        Assert.Equal(SourceExposureState.MayBeServed, before);
        Assert.Equal(SourceExposureState.NeverLeaves, after);
        Assert.Empty(await PathsAsync(w, hosted));
        Assert.Equal([Handbook], await PathsAsync(w, local));
    }

    [Fact]
    public async Task Turning_the_switch_leaves_the_rule_as_written_and_stamps_what_the_documents_store()
    {
        await using var w = await NewWorldAsync();
        var written = AclSet.Of(
            AclEntry.Deny(Principal.User(CallerResolver.IdText(w.Owner.Id))),
            AclEntry.Allow(Principal.Everyone));
        await w.Rules.SetRuleAsync(new FolderRule("filesystem", "handbook", written));

        await w.Exposure.SetAsync("handbook", mayBeServed: false, Tester);

        // The rule reads as its author wrote it. The document's list carries
        // the denial first and the rule's entries after it in their order,
        // because the order of an access list is what it means.
        Assert.Equal(written.CanonicalText, Rule(w).CanonicalText);
        Assert.Equal(
            $"deny group:{CallerResolver.IdText(w.Reserved.Id)}\n{written.CanonicalText}",
            await StoredListAsync(w.Db, w.Tenant, Handbook));

        await w.Exposure.SetAsync("handbook", mayBeServed: true, Tester);

        Assert.Equal(written.CanonicalText, Rule(w).CanonicalText);
        Assert.Equal(written.CanonicalText, await StoredListAsync(w.Db, w.Tenant, Handbook));
    }

    /// <summary>
    /// A rule from before migration 0141: the switch's entry at its top, and
    /// the legacy hold the migration made of it. Turning the switch on removes
    /// that entry, keeps a denial somebody wrote lower down, and records the
    /// rule before and after.
    /// </summary>
    [Fact]
    public async Task Turning_the_switch_on_removes_the_entry_it_wrote_before_0141_and_nothing_else()
    {
        await using var w = await NewWorldAsync();
        var byHand = AclEntry.Deny(Principal.User(CallerResolver.IdText(w.Owner.Id)));
        var old = AclSet.Of(SourceExposure.Denial(w.Reserved.Id), byHand, AclEntry.Allow(Principal.Everyone));
        await w.Rules.SetRuleAsync(new FolderRule("filesystem", "handbook", old));
        await w.Rules.HoldAsync("filesystem", "handbook");
        await MarkLegacyAsync(w.Db, w.Tenant, "filesystem", "handbook");
        Assert.Equal(SourceExposureState.NeverLeaves, await w.Exposure.ReadAsync("handbook"));

        var (_, after, changed) = await w.Exposure.SetAsync("handbook", mayBeServed: true, Tester);

        Assert.True(changed);
        Assert.Equal(SourceExposureState.MayBeServed, after);
        Assert.Equal([byHand.ToString(), "allow everyone"], Rule(w).Entries.Select(e => e.ToString()));
        Assert.Equal(Rule(w).CanonicalText, await StoredListAsync(w.Db, w.Tenant, Handbook));
        var entry = Assert.Single(await w.Record.ListAsync(100), e => e.Kind == SourceExposureStore.ChangeKind);
        Assert.True(entry.NewValue!.Value.GetProperty("legacy_entry_removed").GetBoolean());
        Assert.Equal(old.CanonicalText, entry.OldValue!.Value.GetProperty("rule_entries").GetString());
        Assert.Equal(Rule(w).CanonicalText, entry.NewValue!.Value.GetProperty("rule_entries").GetString());
    }

    /// <summary>
    /// The same entry at the top of the rule of a folder held since 0141, not
    /// by the migration: somebody wrote it, so releasing the hold leaves it,
    /// and the folder reads as denied by hand.
    /// </summary>
    [Fact]
    public async Task Turning_the_switch_on_leaves_the_top_entry_when_the_hold_is_not_a_legacy_one()
    {
        await using var w = await NewWorldAsync();
        await w.Rules.SetRuleAsync(new FolderRule("filesystem", "handbook", AclSet.Of(
            SourceExposure.Denial(w.Reserved.Id), AclEntry.Allow(Principal.Everyone))));
        await w.Exposure.SetAsync("handbook", mayBeServed: false, Tester);

        var (_, after, changed) = await w.Exposure.SetAsync("handbook", mayBeServed: true, Tester);

        Assert.True(changed);
        Assert.Equal(SourceExposureState.DeniedByHand, after);
        Assert.Equal(SourceExposure.Denial(w.Reserved.Id), Rule(w).Entries[0]);
        var released = (await w.Record.ListAsync(100)).First(e => e.Kind == SourceExposureStore.ChangeKind);
        Assert.False(released.NewValue!.Value.GetProperty("legacy_entry_removed").GetBoolean());
    }

    /// <summary>
    /// The same entry at the top of a rule nobody held was written by hand
    /// after 0141. Turning the switch on is not a reason to take it out.
    /// </summary>
    [Fact]
    public async Task Turning_the_switch_on_keeps_a_denial_written_at_the_top_by_hand()
    {
        await using var w = await NewWorldAsync();
        await w.Rules.SetRuleAsync(new FolderRule("filesystem", "handbook", AclSet.Of(
            SourceExposure.Denial(w.Reserved.Id), AclEntry.Allow(Principal.Everyone))));

        var (_, after, changed) = await w.Exposure.SetAsync("handbook", mayBeServed: true, Tester);

        Assert.False(changed);
        Assert.Equal(SourceExposureState.DeniedByHand, after);
        Assert.Equal(SourceExposure.Denial(w.Reserved.Id), Rule(w).Entries[0]);
    }

    /// <summary>
    /// Turning the switch off on a folder already held, where a document was
    /// stored without the denial: it is stamped again, and that is recorded,
    /// though the switch itself did not change.
    /// </summary>
    [Fact]
    public async Task Turning_the_switch_off_again_stamps_a_document_stored_without_the_denial_and_records_it()
    {
        await using var w = await NewWorldAsync();
        await w.Exposure.SetAsync("handbook", mayBeServed: false, Tester);
        await using (var cmd = w.Db.DataSource.CreateCommand("""
            UPDATE prem_index.document SET acl_set_id = (
                SELECT id FROM prem_config.acl_set WHERE tenant_id = @tenant AND canonical_text = @plain)
            WHERE tenant_id = @tenant AND path = @path
            """))
        {
            cmd.Parameters.AddWithValue("tenant", w.Tenant);
            cmd.Parameters.AddWithValue("plain", "allow everyone\n");
            cmd.Parameters.AddWithValue("path", Handbook);
            Assert.Equal(1, await cmd.ExecuteNonQueryAsync());
        }
        var before = (await w.Record.ListAsync(100)).Count;

        var turn = await w.Exposure.SetAsync("handbook", mayBeServed: false, Tester);

        Assert.False(turn.Changed);
        Assert.Equal(1, turn.Moved);
        Assert.StartsWith("deny group:", await StoredListAsync(w.Db, w.Tenant, Handbook));
        var records = await w.Record.ListAsync(100);
        Assert.Equal(before + 1, records.Count);
        var entry = records.First(e => e.Kind == SourceExposureStore.ChangeKind);
        Assert.False(entry.OldValue!.Value.GetProperty("hosted_models").GetBoolean());
        Assert.False(entry.NewValue!.Value.GetProperty("hosted_models").GetBoolean());
        Assert.Equal(1, entry.NewValue!.Value.GetProperty("documents_moved").GetInt32());
    }

    [Fact]
    public async Task Turning_the_switch_twice_the_same_way_changes_nothing_and_records_nothing()
    {
        await using var w = await NewWorldAsync();
        await w.Exposure.SetAsync("handbook", mayBeServed: false, Tester);
        var after = (await w.Record.ListAsync(100)).Count;

        var (_, _, changed) = await w.Exposure.SetAsync("handbook", mayBeServed: false, Tester);

        Assert.False(changed);
        Assert.Equal(after, (await w.Record.ListAsync(100)).Count);
    }

    [Fact]
    public async Task The_switch_is_written_to_the_change_record_because_the_rules_store_writes_none()
    {
        await using var w = await NewWorldAsync();

        await w.Exposure.SetAsync("handbook", mayBeServed: false, Tester);

        var entry = Assert.Single(await w.Record.ListAsync(100), e => e.Kind == SourceExposureStore.ChangeKind);
        Assert.Equal("source.hosted", entry.Kind);
        // The target the rules page uses, so both kinds of row read alike.
        Assert.Equal("filesystem:handbook", entry.Target);
        Assert.True(entry.OldValue!.Value.GetProperty("hosted_models").GetBoolean());
        Assert.False(entry.NewValue!.Value.GetProperty("hosted_models").GetBoolean());
        Assert.Equal(1, entry.NewValue!.Value.GetProperty("documents_moved").GetInt32());
        // A hold is not a rule change, so it writes no rule.set row.
        Assert.DoesNotContain(await w.Record.ListAsync(100), e => e.Kind == "rule.set");
    }

    [Fact]
    public async Task Holding_back_a_folder_with_no_rule_holds_it_and_invents_no_rule()
    {
        await using var w = await NewWorldAsync();

        var (before, after, changed) = await w.Exposure.SetAsync("nowhere", mayBeServed: false, Tester);

        Assert.True(changed);
        Assert.Equal(SourceExposureState.MayBeServed, before);
        Assert.Equal(SourceExposureState.NeverLeaves, after);
        Assert.Contains(new HostedHold("filesystem", "nowhere"), await HostedHolds.ListAsync(w.Db, w.Tenant));
        Assert.DoesNotContain(await w.Rules.ListRulesAsync(), r => r.Rule.PathPrefix == "nowhere");
    }

    [Fact]
    public async Task Naming_an_owner_records_it_and_decides_nothing_about_who_may_read()
    {
        await using var w = await NewWorldAsync();
        var registry = new Core.Sources.Registry.SourceRegistry(w.Db, w.Tenant);
        var folder = SourcesTests.Folder(("rota.md", "# Rota\n\n## Weekend\nOpen on Saturdays.\n"));
        await registry.AddAsync("handbook", folder, "handbook", false, false, Tester);

        var hosted = await w.Identity.CreateAgentAsync(
            "desk", w.Owner.Id, AgentMode.ActsForUser, 60, null, ModelLocation.Hosted, "a vendor");
        var before = await PathsAsync(w, hosted);

        var (_, after, changed) = await registry.SetOwnerAsync("handbook", w.Owner.Id, Tester);

        Assert.True(changed);
        Assert.Equal(w.Owner.Id, after.OwnerUserId);
        Assert.Contains(await w.Record.ListAsync(100), e => e.Kind == "source.set" && e.Target == "handbook");
        // An owner is a person to ask, not a permission. Naming one changes
        // nothing about what anybody, including an agent, may read.
        Assert.Equal(before, await PathsAsync(w, hosted));

        var (_, cleared, clearedChanged) = await registry.SetOwnerAsync("handbook", null, Tester);
        Assert.True(clearedChanged);
        Assert.Null(cleared.OwnerUserId);
    }

    /// <summary>
    /// A denial written by hand, below an allow. It is not the switch, and
    /// calling it "may be served" would be false; calling it "held back" would
    /// be false too, because the allow above it decides first: a hosted-model
    /// agent acting for the person it allows reads the folder. The words say
    /// exactly that much.
    /// </summary>
    [Fact]
    public async Task A_denial_somebody_wrote_by_hand_is_reported_as_that_and_not_as_may_be_served()
    {
        await using var w = await NewWorldAsync();
        await w.Rules.SetRuleAsync(new FolderRule("filesystem", "handbook", AclSet.Of(
            AclEntry.Allow(Principal.User(CallerResolver.IdText(w.Owner.Id))),
            SourceExposure.Denial(w.Reserved.Id),
            AclEntry.Allow(Principal.Everyone))));
        var hosted = await w.Identity.CreateAgentAsync(
            "desk", w.Owner.Id, AgentMode.ActsForUser, 60, null, ModelLocation.Hosted, "a vendor");

        Assert.Equal(SourceExposureState.DeniedByHand, await w.Exposure.ReadAsync("handbook"));
        Assert.Equal([Handbook], await PathsAsync(w, hosted));
        var words = SourceExposure.Describe(SourceExposureState.DeniedByHand);
        Assert.StartsWith("not held by the switch", words);
        Assert.Contains("decides only where no entry before it", words);
        Assert.DoesNotContain("held back", words);
    }
}

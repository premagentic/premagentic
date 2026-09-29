using System.Net;
using System.Text.RegularExpressions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Premagentic.Cli.Admin;
using Premagentic.Core;
using Premagentic.Core.Admin;
using Premagentic.Core.Identity;
using Premagentic.Core.Ingestion;
using Premagentic.Core.Retrieval;
using Premagentic.Core.Security;
using Premagentic.Core.Security.Acl;
using Premagentic.Core.Sources;
using Premagentic.Core.Sources.Registry;
using Premagentic.Core.Storage;
using Role = Premagentic.Core.Identity.Role;

namespace Premagentic.Tests;

/// <summary>
/// A source's "may be served to hosted models" switch, turned off, against
/// every rule write that could undo it: a longer rule beneath the folder, a
/// rule written at the folder itself, a rule removed, an ingest after the
/// hold, and a list some writer failed to stamp. Through the CLI and the
/// portal's own forms, and read back through the real gate over the API and
/// MCP.
/// <para>
/// Each world holds a second folder, <c>policies</c>, under the same rule as
/// the held one and not held. A hosted-model agent reading it is the control
/// on precision: the exclusion takes out every list a held document stores,
/// so a writer that stored a held document's list unstamped would take the
/// shared list, and this folder, away with it.
/// </para>
/// Every file here is invented. Requires a running Docker daemon.
/// </summary>
[Collection(ConsoleCollection.Name)]
public sealed partial class HostedHoldTests(DatastoreTestDatabase server) : IClassFixture<DatastoreTestDatabase>
{
    private static readonly AdminActor Tester = new("cli", "test-account");
    private const string Rota = "handbook/rota.md";
    private const string Shift = "handbook/weekend/shift.md";
    private const string Leave = "policies/leave.md";

    private sealed record World(
        PremagenticDatabase Db, Guid Tenant, IdentityStore Identity, AclStore Rules, SourceExposureStore Exposure,
        HybridSearch Search, SeededEmbeddingProvider Embedder, string Handbook, Guid Reserved, string Hosted, string Local)
        : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Db.DisposeAsync();

        public Task<IngestSummary> IngestHandbookAsync() =>
            new IngestPipeline(Db, Embedder).RunAsync(Tenant, new FileSystemSource(Handbook, DocumentAccess.FolderRules, "handbook"));
    }

    /// <summary>
    /// Two folders anyone may read, <c>handbook</c> (with a folder beneath it)
    /// and <c>policies</c>, indexed, and a hosted-model agent and a local one
    /// acting for the same person. Held back when <paramref name="hold"/>.
    /// </summary>
    private async Task<World> NewWorldAsync(bool hold = true)
    {
        var db = new PremagenticDatabase(await server.CreateDatabaseAsync());
        await db.InitializeAsync();
        var tenant = await db.EnsureTenantAsync("t", "T");
        var identity = new IdentityStore(db, tenant);
        var rules = new AclStore(db, tenant);
        var owner = await identity.CreateUserAsync("dana", "Dana", Role.Member);
        await identity.CreateUserAsync("erin", "Erin", Role.Member);
        var reserved = (await identity.FindSystemGroupAsync(SystemGroups.HostedModelAgents))!.Id;

        var everyone = AclSet.Of(AclEntry.Allow(Principal.Everyone));
        await rules.SetRuleAsync(new FolderRule("filesystem", "handbook", everyone));
        await rules.SetRuleAsync(new FolderRule("filesystem", "policies", everyone));

        var embedder = new SeededEmbeddingProvider();
        var handbook = SourcesTests.Folder(
            ("rota.md", "# Rota\n\n## Weekend\nTwo people open the zeppelin shop on Saturdays.\n"),
            ("weekend/shift.md", "# Shift\n\n## Sunday\nThe zeppelin shop closes at four on Sundays.\n"));
        var policies = SourcesTests.Folder(("leave.md", "# Leave\n\n## Notice\nAsk two weeks ahead for zeppelin leave.\n"));
        var pipeline = new IngestPipeline(db, embedder);
        Assert.Equal(2, (await pipeline.RunAsync(tenant, new FileSystemSource(handbook, DocumentAccess.FolderRules, "handbook"))).Ingested);
        Assert.Equal(1, (await pipeline.RunAsync(tenant, new FileSystemSource(policies, DocumentAccess.FolderRules, "policies"))).Ingested);

        var hosted = await identity.CreateAgentAsync(
            "desk", owner.Id, AgentMode.ActsForUser, 600, null, ModelLocation.Hosted, "a vendor");
        var local = await identity.CreateAgentAsync(
            "shop", owner.Id, AgentMode.ActsForUser, 600, null, ModelLocation.Local);
        var w = new World(db, tenant, identity, rules, new SourceExposureStore(db, tenant),
            new HybridSearch(db, embedder), embedder, handbook, reserved,
            (await identity.IssueTokenAsync(hosted.Id, TimeSpan.FromDays(30))).PlainText,
            (await identity.IssueTokenAsync(local.Id, TimeSpan.FromDays(30))).PlainText);

        // The control before anything is held: both agents read all three.
        Assert.Equal([Rota, Shift, Leave], await PathsAsync(w, w.Hosted));
        if (hold)
        {
            await w.Exposure.SetAsync("handbook", mayBeServed: false, Tester);
            Assert.Equal([Leave], await PathsAsync(w, w.Hosted));
        }
        return w;
    }

    private static async Task<string[]> PathsAsync(World w, string token) =>
        [.. (await w.Search.SearchAsync(w.Tenant, "zeppelin", new SearchOptions(
                await CallerAccess.ForAgentTokenAsync(w.Identity, token), TopK: 20)))
            .Hits.Select(h => h.Path).Distinct().Order(StringComparer.Ordinal)];

    private static async Task<(int Exit, string Out, string Err)> CliAsync(World w, params string[] args) =>
        await ConsoleCapture.RunAsync(() => AdminCommands.RunAsync(args, w.Db, w.Tenant));

    private static string Stamped(Guid reserved, string list) => $"deny group:{CallerResolver.IdText(reserved)}\n{list}";

    /// <summary>
    /// A longer rule beneath a held folder. It decides the documents beneath
    /// it, so the hold has to reach them whatever that rule says.
    /// </summary>
    [Fact]
    public async Task A_rule_beneath_a_held_folder_set_at_the_command_line_does_not_undo_the_hold()
    {
        await using var w = await NewWorldAsync();

        var set = await CliAsync(w, "rules", "set", "--prefix", "handbook/weekend", "--public");
        Assert.True(set.Exit == 0, set.Err);

        // The folder beneath is still held, and the unheld folder under the
        // same list is still read: the list stored under the new rule is the
        // stamped one, not the plain one the policies folder shares.
        Assert.Equal([Leave], await PathsAsync(w, w.Hosted));
        Assert.Equal([Rota, Shift, Leave], await PathsAsync(w, w.Local));
        Assert.Equal(Stamped(w.Reserved, "allow everyone\n"), await SourceExposureTests.StoredListAsync(w.Db, w.Tenant, Shift));
        Assert.Equal("allow everyone\n", await SourceExposureTests.StoredListAsync(w.Db, w.Tenant, Leave));

        // And the rule taken away again: the documents fall to the folder's
        // rule, held still.
        var removed = await CliAsync(w, "rules", "remove", "--prefix", "handbook/weekend");
        Assert.True(removed.Exit == 0, removed.Err);
        Assert.Equal([Leave], await PathsAsync(w, w.Hosted));
        Assert.Equal(Stamped(w.Reserved, "allow everyone\n"), await SourceExposureTests.StoredListAsync(w.Db, w.Tenant, Shift));
    }

    /// <summary>
    /// The held folder's own rule written again with other entries. The hold
    /// is not an entry of that rule, so the write leaves it where it is.
    /// </summary>
    [Fact]
    public async Task The_held_folders_own_rule_written_again_at_the_command_line_keeps_the_hold()
    {
        await using var w = await NewWorldAsync();

        var set = await CliAsync(w, "rules", "set", "--prefix", "handbook", "--entry", "deny user:erin", "--entry", "allow everyone");
        Assert.True(set.Exit == 0, set.Err);

        Assert.Equal([Leave], await PathsAsync(w, w.Hosted));
        Assert.Equal([Rota, Shift, Leave], await PathsAsync(w, w.Local));
        Assert.Equal(SourceExposureState.NeverLeaves, await w.Exposure.ReadAsync("handbook"));

        // The rule is what was written, and what the documents store is that
        // list with the denial first.
        var rule = (await w.Rules.ListRulesAsync()).Single(r => r.Rule.PathPrefix == "handbook").Rule.Acl;
        Assert.Equal(2, rule.Entries.Count);
        Assert.Equal(Stamped(w.Reserved, rule.CanonicalText), await SourceExposureTests.StoredListAsync(w.Db, w.Tenant, Rota));
    }

    [Fact]
    public async Task The_rules_list_names_the_hold_beside_every_rule_it_reaches_and_no_other()
    {
        await using var w = await NewWorldAsync();
        Assert.Equal(0, (await CliAsync(w, "rules", "set", "--prefix", "handbook/weekend", "--public")).Exit);

        var (exit, output, _) = await CliAsync(w, "rules", "list");

        Assert.Equal(0, exit);
        var lines = output.ReplaceLineEndings("\n").Split('\n');
        const string Held = "held back from hosted-model agents by the source's switch: filesystem:handbook";
        // The line beneath each rule of the held folder, and none beneath the
        // policies folder's rule, which reads the same entries.
        foreach (var folder in new[] { "filesystem:handbook ", "filesystem:handbook/weekend " })
        {
            var at = Array.FindIndex(lines, l => l.StartsWith(folder, StringComparison.Ordinal));
            Assert.True(at >= 0, output);
            Assert.Contains(Held, lines[at + 1]);
        }
        var policies = Array.FindIndex(lines, l => l.StartsWith("filesystem:policies ", StringComparison.Ordinal));
        Assert.True(policies >= 0, output);
        Assert.DoesNotContain("held back", policies + 1 < lines.Length ? lines[policies + 1] : "");
    }

    /// <summary>A rule written first and the folder held after it: holding stamps what is already there.</summary>
    [Fact]
    public async Task Holding_a_folder_stamps_the_documents_rules_beneath_it_already_decide()
    {
        await using var w = await NewWorldAsync(hold: false);
        Assert.Equal(0, (await CliAsync(w, "rules", "set", "--prefix", "handbook/weekend", "--public")).Exit);

        var (_, _, changed) = await w.Exposure.SetAsync("handbook", mayBeServed: false, Tester);

        Assert.True(changed);
        Assert.Equal([Leave], await PathsAsync(w, w.Hosted));
        Assert.Equal(Stamped(w.Reserved, "allow everyone\n"), await SourceExposureTests.StoredListAsync(w.Db, w.Tenant, Shift));
        Assert.Equal(Stamped(w.Reserved, "allow everyone\n"), await SourceExposureTests.StoredListAsync(w.Db, w.Tenant, Rota));
    }

    /// <summary>A document ingested into a held folder after the hold is stored stamped.</summary>
    [Fact]
    public async Task A_document_ingested_beneath_a_held_folder_is_stored_stamped()
    {
        await using var w = await NewWorldAsync();
        File.WriteAllText(Path.Combine(w.Handbook, "weekend", "holiday.md"),
            "# Holiday\n\n## Closed\nThe zeppelin shop closes on the first of May.\n");

        Assert.Equal(1, (await w.IngestHandbookAsync()).Ingested);

        Assert.Equal(Stamped(w.Reserved, "allow everyone\n"),
            await SourceExposureTests.StoredListAsync(w.Db, w.Tenant, "handbook/weekend/holiday.md"));
        Assert.Equal([Leave], await PathsAsync(w, w.Hosted));
        Assert.Contains("handbook/weekend/holiday.md", await PathsAsync(w, w.Local));
    }

    /// <summary>
    /// The second enforcement alone: a held document whose stored list is
    /// the plain one, as if a writer had failed to stamp it, is still not
    /// read by a hosted-model agent. It takes the shared list with it, which
    /// is the cost of failing closed.
    /// </summary>
    [Fact]
    public async Task A_held_document_stored_without_its_stamp_is_still_kept_from_a_hosted_model_agent()
    {
        await using var w = await NewWorldAsync();
        await UnstampAsync(w, Shift);

        Assert.DoesNotContain(Shift, await PathsAsync(w, w.Hosted));
        // The control: the local agent reads it, so it is indexed and readable.
        Assert.Contains(Shift, await PathsAsync(w, w.Local));
    }

    /// <summary>
    /// The same for a hold on a whole source, which has no folder to range
    /// over: a document of the source stored without its stamp is still not
    /// read by a hosted-model agent.
    /// </summary>
    [Fact]
    public async Task A_document_of_a_whole_held_source_stored_without_its_stamp_is_still_kept_from_a_hosted_model_agent()
    {
        await using var w = await NewWorldAsync(hold: false);
        const string Memo = "ext/memo.md";
        await new IngestPipeline(w.Db, w.Embedder).RunAsync(w.Tenant, new DatastoreSource("ext", [
            DatastoreSource.Doc(Memo, "## Memo\nthe zeppelin memo", DocumentAccess.Everyone)]));
        await w.Rules.HoldAsync("test-datastore", "");
        await UnstampAsync(w, Memo);

        Assert.DoesNotContain(Memo, await PathsAsync(w, w.Hosted));
        Assert.Contains(Memo, await PathsAsync(w, w.Local));
    }

    /// <summary>
    /// A folder whose name only starts like a held one is not held: not
    /// stamped at ingest, and not taken out by the exclusion, whose range
    /// in byte order lets such a folder in before the segment test drops it.
    /// </summary>
    [Fact]
    public async Task A_folder_whose_name_starts_like_a_held_one_is_not_held()
    {
        await using var w = await NewWorldAsync();
        await w.Rules.SetRuleAsync(new FolderRule("filesystem", "handbook-old", AclSet.Of(AclEntry.Allow(Principal.Everyone))));
        var old = SourcesTests.Folder(("notes.md", "# Notes\n\n## Old\nThe old zeppelin rota.\n"));
        Assert.Equal(1, (await new IngestPipeline(w.Db, w.Embedder)
            .RunAsync(w.Tenant, new FileSystemSource(old, DocumentAccess.FolderRules, "handbook-old"))).Ingested);

        Assert.Equal("allow everyone\n", await SourceExposureTests.StoredListAsync(w.Db, w.Tenant, "handbook-old/notes.md"));
        Assert.Equal(["handbook-old/notes.md", Leave], await PathsAsync(w, w.Hosted));
    }

    /// <summary>
    /// Gives a document the list the policies folder stores, the plain one,
    /// as a writer that failed to stamp it would have.
    /// </summary>
    private static async Task UnstampAsync(World w, string path)
    {
        await using var cmd = w.Db.DataSource.CreateCommand("""
            UPDATE prem_index.document d SET acl_set_id = (
                SELECT p.acl_set_id FROM prem_index.document p WHERE p.tenant_id = d.tenant_id AND p.path = @plain)
            WHERE d.tenant_id = @tenant AND d.path = @held
            """);
        cmd.Parameters.AddWithValue("tenant", w.Tenant);
        cmd.Parameters.AddWithValue("plain", Leave);
        cmd.Parameters.AddWithValue("held", path);
        Assert.Equal(1, await cmd.ExecuteNonQueryAsync());
        Assert.Equal("allow everyone\n", await SourceExposureTests.StoredListAsync(w.Db, w.Tenant, path));
    }

    [Fact]
    public async Task Turning_the_switch_back_on_gives_every_rule_decided_document_its_rules_list()
    {
        await using var w = await NewWorldAsync();
        Assert.Equal(0, (await CliAsync(w, "rules", "set", "--prefix", "handbook/weekend", "--public")).Exit);

        var (_, after, changed) = await w.Exposure.SetAsync("handbook", mayBeServed: true, Tester);

        Assert.True(changed);
        Assert.Equal(SourceExposureState.MayBeServed, after);
        Assert.Equal([Rota, Shift, Leave], await PathsAsync(w, w.Hosted));
        Assert.Equal("allow everyone\n", await SourceExposureTests.StoredListAsync(w.Db, w.Tenant, Shift));
        Assert.Equal("allow everyone\n", await SourceExposureTests.StoredListAsync(w.Db, w.Tenant, Rota));
    }

    /// <summary>
    /// A document its connector decided: stamped at ingest while held, and,
    /// once released, stamped still until its next ingest decides it again,
    /// which fails closed. Held again after that ingest, it is stamped by the
    /// hold itself.
    /// </summary>
    [Fact]
    public async Task A_connector_decided_document_keeps_its_stamp_until_its_next_ingest()
    {
        await using var w = await NewWorldAsync(hold: false);
        var memo = DatastoreSource.Doc("ext/memo.md", "## Memo\nthe zeppelin memo", DocumentAccess.Everyone);
        var pipeline = new IngestPipeline(w.Db, w.Embedder);
        var (held, _) = await w.Rules.HoldAsync("test-datastore", "ext");
        Assert.True(held);

        await pipeline.RunAsync(w.Tenant, new DatastoreSource("ext", [memo]));
        Assert.Equal(Stamped(w.Reserved, "allow everyone\n"), await SourceExposureTests.StoredListAsync(w.Db, w.Tenant, "ext/memo.md"));
        Assert.DoesNotContain("ext/memo.md", await PathsAsync(w, w.Hosted));

        await w.Rules.ReleaseAsync("test-datastore", "ext");
        Assert.Equal(Stamped(w.Reserved, "allow everyone\n"), await SourceExposureTests.StoredListAsync(w.Db, w.Tenant, "ext/memo.md"));

        await pipeline.RunAsync(w.Tenant, new DatastoreSource("ext", [memo]));
        Assert.Equal("allow everyone\n", await SourceExposureTests.StoredListAsync(w.Db, w.Tenant, "ext/memo.md"));
        Assert.Contains("ext/memo.md", await PathsAsync(w, w.Hosted));

        // Held again with the document already indexed: the hold stamps the
        // list its connector decided, as it stamps a rule's.
        await w.Rules.HoldAsync("test-datastore", "ext");
        Assert.Equal(Stamped(w.Reserved, "allow everyone\n"), await SourceExposureTests.StoredListAsync(w.Db, w.Tenant, "ext/memo.md"));
    }

    /// <summary>
    /// The portal's own rule form, beneath a held folder and at it, and the answer
    /// read back over the API and over MCP, as a hosted-model agent sees it.
    /// </summary>
    [Fact]
    public async Task Rules_set_in_the_portal_beneath_and_at_a_held_folder_leave_it_held_over_the_api_and_mcp()
    {
        await using var p = await PortalWorld.NewAsync(server);
        var corpus = SourcesTests.Folder(
            ("rota.md", "# Rota\n\n## Weekend\nTwo people open the zeppelin yard on Saturdays.\n"),
            ("weekend/shift.md", "# Shift\n\n## Sunday\nThe zeppelin yard closes at four on Sundays.\n"));
        using (await p.PostAsync("/portal/sources", p.Admin,
                   [("name", "yard"), ("folder", corpus), ("prefix", "yard"), ("chunker", "markdown")])) { }
        using (await p.PostAsync("/portal/permissions/rules", p.Admin,
                   [("source", "filesystem"), ("prefix", "yard"), ("entries", "allow everyone")])) { }
        Assert.Equal(2, (await new IngestPipeline(p.World.Db, new SeededEmbeddingProvider())
            .RunAsync(p.World.Tenant, new FileSystemSource(corpus, DocumentAccess.FolderRules, "yard"))).Ingested);
        using (var off = await p.PostAsync("/portal/sources/yard/hosted", p.Admin))
            Assert.DoesNotContain("error=", off.Headers.Location?.OriginalString ?? "");

        var owner = (await p.World.Identity.FindUserByNameAsync("alice"))!;
        var hosted = await p.World.Identity.CreateAgentAsync(
            "desk-hosted", owner.Id, AgentMode.ActsForUser, 600, null, ModelLocation.Hosted, "a vendor");
        var token = (await p.World.Identity.IssueTokenAsync(hosted.Id, TimeSpan.FromDays(30))).PlainText;
        string[] yard = ["yard/rota.md", "yard/weekend/shift.md"];

        // A rule beneath the held folder, then the folder's own rule written
        // again with other entries.
        foreach (var (prefix, entries) in new[] { ("yard/weekend", "allow everyone"), ("yard", "deny user:bob\nallow everyone") })
        {
            using var set = await p.PostAsync("/portal/permissions/rules", p.Admin,
                [("source", "filesystem"), ("prefix", prefix), ("entries", entries)]);
            Assert.Equal(HttpStatusCode.Found, set.StatusCode);
            Assert.Contains("done=", set.Headers.Location?.OriginalString ?? "");
        }

        // Both writes happened: the rule beneath, and the folder's own rule
        // with the entries written, the denial of bob first.
        var rules = await new AclStore(p.World.Db, p.World.Tenant).ListRulesAsync();
        Assert.Equal(["allow everyone"],
            rules.Single(x => x.Rule.PathPrefix == "yard/weekend").Rule.Acl.Entries.Select(e => e.ToString()));
        Assert.Equal([$"deny user:{CallerResolver.IdText(p.World.Bob.Id)}", "allow everyone"],
            rules.Single(x => x.Rule.PathPrefix == "yard").Rule.Acl.Entries.Select(e => e.ToString()));

        // Over the API: the handbook everyone may read, under the list the
        // yard's documents would share unstamped, is still read.
        var overApi = await Api.PathsAsync(await Api.SearchAsync(p.Client, "zeppelin", bearer: token, topK: 20));
        Assert.Contains(ApiWorld.Handbook, overApi);
        Assert.Empty(overApi.Intersect(yard));

        await using (var mcp = await OAuthApi.McpAsync(p.World.Host, token))
        {
            var overMcp = await McpPathsAsync(mcp);
            Assert.Contains(ApiWorld.Handbook, overMcp);
            Assert.Empty(overMcp.Intersect(yard));
        }

        // The control: a person reads the yard, so it is indexed and readable.
        var asPerson = await Api.PathsAsync(await Api.SearchAsync(p.Client, "zeppelin", p.Member, topK: 20));
        Assert.Equal(yard, asPerson.Intersect(yard).Order(StringComparer.Ordinal));

        // The Permissions page names the hold beside both of the yard's rules.
        var page = await p.TextAsync(await p.GetAsync("/portal/permissions", p.Auditor));
        Assert.Equal(2, Regex.Count(page, "switch: filesystem:yard"));
    }

    /// <summary>
    /// The exclusion alone, as an installed deployment runs it: the API
    /// connected as the application role, which the row policy binds, and
    /// reading through the search role. The permitted lists are computed
    /// before any caller session opens, as the application role, which setup
    /// lists in index_writer, so the policy lets it see the held document and
    /// take its list out. The held document is stored plain, as a writer that
    /// failed to stamp it would leave it: a hosted-model agent is refused it,
    /// and the local assistant acting for the same person reads it.
    /// </summary>
    [Fact]
    public async Task Through_the_api_as_the_application_role_the_exclusion_alone_refuses_a_hosted_model_agent()
    {
        await using var r = await RlsDatabase.NewAsync(server);
        var agent = await r.Identity.CreateAgentAsync(
            "desk", r.Alice.Id, AgentMode.ActsForUser, 600, null, ModelLocation.Hosted, "a vendor");
        var hosted = (await r.Identity.IssueTokenAsync(agent.Id, TimeSpan.FromDays(30))).PlainText;
        await using var host = ApiTestHost.Start(r.AppConnection, new TestClock(ApiWorld.Start), r.Embedder,
            new ApiHostOptions { SearchRoleConnection = r.SearchConnection });
        using var client = host.Client();

        // The control: nothing held, the hosted-model agent reads the handbook.
        Assert.Contains(RlsDatabase.Handbook, await Api.PathsAsync(await Api.SearchAsync(client, bearer: hosted)));

        // Held as the application role, then the stamp undone.
        await new AclStore(r.App, r.Tenant).HoldAsync("test-datastore", "pub");
        await RlsDatabase.ExecAsync(r.OwnerConnection, $"""
            UPDATE prem_index.document SET acl_set_id = (
                SELECT id FROM prem_config.acl_set WHERE tenant_id = '{r.Tenant}' AND canonical_text = E'allow everyone\n')
            WHERE tenant_id = '{r.Tenant}' AND path = '{RlsDatabase.Handbook}'
            """);
        Assert.Equal(["allow everyone\n"], await RlsDatabase.ReadAsync(r.AppConnection, $"""
            SELECT s.canonical_text FROM prem_index.document d
            JOIN prem_config.acl_set s ON s.tenant_id = d.tenant_id AND s.id = d.acl_set_id
            WHERE d.tenant_id = '{r.Tenant}' AND d.path = '{RlsDatabase.Handbook}'
            """));

        Assert.DoesNotContain(RlsDatabase.Handbook, await Api.PathsAsync(await Api.SearchAsync(client, bearer: hosted)));
        Assert.Contains(RlsDatabase.Handbook, await Api.PathsAsync(await Api.SearchAsync(client, bearer: r.AssistantToken)));
    }

    /// <summary>
    /// A held source removed from the registry keeps its documents indexed,
    /// so its hold stays and keeps them from hosted models. The sources list
    /// names the hold, and hosted-release lets it go, recorded; it refuses a
    /// folder a registered source still reads.
    /// </summary>
    [Fact]
    public async Task A_hold_whose_source_was_removed_stays_is_listed_and_is_released_by_hosted_release()
    {
        await using var w = await NewWorldAsync(hold: false);
        var registry = new SourceRegistry(w.Db, w.Tenant);
        await registry.AddAsync("handbook", w.Handbook, "handbook", false, false, Tester);
        var off = await ConsoleCapture.RunAsync(() => SourcesCommands.RunAsync(["sources", "set", "handbook", "--hosted", "no"], w.Db, w.Tenant));
        Assert.True(off.Exit == 0, off.Err);

        var refused = await ConsoleCapture.RunAsync(() => SourcesCommands.RunAsync(
            ["sources", "hosted-release", "--prefix", "handbook"], w.Db, w.Tenant));
        Assert.Equal(1, refused.Exit);
        Assert.Contains("prem sources set handbook --hosted yes", refused.Out + refused.Err);

        Assert.NotNull(await registry.RemoveAsync("handbook", Tester));
        Assert.Equal([Leave], await PathsAsync(w, w.Hosted));
        var listed = await ConsoleCapture.RunAsync(() => SourcesCommands.RunAsync(["sources", "list"], w.Db, w.Tenant));
        Assert.Contains("filesystem:handbook is held back from hosted-model agents and no source reads it", listed.Out);

        var released = await ConsoleCapture.RunAsync(() => SourcesCommands.RunAsync(
            ["sources", "hosted-release", "--prefix", "handbook"], w.Db, w.Tenant));

        Assert.True(released.Exit == 0, released.Err);
        Assert.Empty(await HostedHolds.ListAsync(w.Db, w.Tenant));
        Assert.Equal([Rota, Shift, Leave], await PathsAsync(w, w.Hosted));
        var entry = (await new ChangeRecord(w.Db, w.Tenant).ListAsync(100)).First(e => e.Kind == SourceExposureStore.ChangeKind);
        Assert.Equal("filesystem:handbook", entry.Target);
        Assert.True(entry.NewValue!.Value.GetProperty("hosted_models").GetBoolean());
    }

    /// <summary>
    /// A source whose folder lies beneath a held one reads as held, in words
    /// that name the hold above, and turning its own switch on cannot release
    /// what is not its own.
    /// </summary>
    [Fact]
    public async Task A_folder_beneath_a_held_one_reads_as_held_by_the_hold_above_and_names_it()
    {
        await using var w = await NewWorldAsync();
        var registry = new SourceRegistry(w.Db, w.Tenant);
        await registry.AddAsync("weekend", Path.Combine(w.Handbook, "weekend"), "handbook/weekend", false, false, Tester);

        var (state, words) = await w.Exposure.ExplainAsync("handbook/weekend");
        Assert.Equal(SourceExposureState.NeverLeaves, state);
        Assert.EndsWith("by the switch on filesystem:handbook", words);
        var listed = await ConsoleCapture.RunAsync(() => SourcesCommands.RunAsync(["sources", "list"], w.Db, w.Tenant));
        Assert.Contains("never served to hosted-model agents, by the switch on filesystem:handbook", listed.Out);

        var (_, after, changed) = await w.Exposure.SetAsync("handbook/weekend", mayBeServed: true, Tester);
        Assert.False(changed);
        Assert.Equal(SourceExposureState.NeverLeaves, after);
        Assert.Equal([Leave], await PathsAsync(w, w.Hosted));
    }

    /// <summary>
    /// Turning the switch off at the command line on a folder already held,
    /// where a document was stored without the denial, says it stamped it.
    /// </summary>
    [Fact]
    public async Task Turning_the_switch_off_again_at_the_command_line_says_it_stamped_a_document_again()
    {
        await using var w = await NewWorldAsync(hold: false);
        await new SourceRegistry(w.Db, w.Tenant).AddAsync("handbook", w.Handbook, "handbook", false, false, Tester);
        string[] off = ["sources", "set", "handbook", "--hosted", "no"];
        Assert.Equal(0, (await ConsoleCapture.RunAsync(() => SourcesCommands.RunAsync(off, w.Db, w.Tenant))).Exit);
        await UnstampAsync(w, Shift);

        var again = await ConsoleCapture.RunAsync(() => SourcesCommands.RunAsync(off, w.Db, w.Tenant));

        Assert.True(again.Exit == 0, again.Err);
        Assert.Contains("1 indexed document(s) under it stored without the denial were stamped again, and that is recorded", again.Out);
        Assert.StartsWith("deny group:", await SourceExposureTests.StoredListAsync(w.Db, w.Tenant, Shift));
    }

    private static async Task<string[]> McpPathsAsync(McpClient client)
    {
        var result = await client.CallToolAsync("search_knowledge", new Dictionary<string, object?> { ["query"] = "zeppelin", ["topK"] = 20 });
        var text = string.Concat(result.Content.OfType<TextContentBlock>().Select(b => b.Text));
        Assert.False(result.IsError ?? false, text);
        return [.. HitLine().Matches(text).Select(m => m.Groups[1].Value).Distinct().Order(StringComparer.Ordinal)];
    }

    [GeneratedRegex(@"^--- \[[0-9.]+\] (\S+)", RegexOptions.Multiline)]
    private static partial Regex HitLine();
}

using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Premagentic.Core.Admin;
using Premagentic.Core.Identity;
using Premagentic.Core.Ingestion;
using Premagentic.Core.Okf;
using Premagentic.Core.Sources.Registry;

namespace Premagentic.Tests;

/// <summary>
/// What the portal's pages do, through the real API host: the group-delete
/// question, a token shown once, a looser setting asked twice, search as the
/// person, documents without their text, run now and its history, health and
/// export without secrets, view as and why. Every data item is invented.
/// Requires a running Docker daemon.
/// </summary>
public sealed class PortalPagesTests(DatastoreTestDatabase server) : IClassFixture<DatastoreTestDatabase>
{
    [Fact]
    public async Task Removing_a_group_a_rule_names_asks_first_and_shows_the_rules()
    {
        await using var p = await PortalWorld.NewAsync(server);

        // "staff" denies Contractors, then allows Staff: removing Contractors would let its members through.
        var question = await p.TextAsync(await p.PostAsync("/portal/groups/Contractors/remove", p.Admin));
        Assert.Contains("Remove group &#x27;Contractors&#x27;?", question);
        Assert.Contains("deny", question);
        Assert.Contains("value=\"yes\"", question);
        Assert.Equal(1, await p.ScalarAsync("SELECT count(*) FROM prem_config.app_group WHERE name = 'Contractors' AND deleted_at IS NULL"));
        Assert.Equal(0, await p.ScalarAsync("SELECT count(*) FROM prem_config.admin_event"));

        using (var yes = await p.PostAsync("/portal/groups/Contractors/remove", p.Admin, [("confirm", "yes")]))
            Assert.Equal(HttpStatusCode.Redirect, yes.StatusCode);
        Assert.Equal(0, await p.ScalarAsync("SELECT count(*) FROM prem_config.app_group WHERE name = 'Contractors' AND deleted_at IS NULL"));
        Assert.Equal(1, await p.ScalarAsync(
            $"SELECT count(*) FROM prem_config.admin_event WHERE kind = 'group.remove' AND target = 'Contractors' AND actor_user_id = '{p.World.Carol.Id}'"));

        // The control: a group no rule names goes at once, with no question.
        await p.World.Identity.CreateGroupAsync("Visitors");
        using (var direct = await p.PostAsync("/portal/groups/Visitors/remove", p.Admin))
            Assert.Equal(HttpStatusCode.Redirect, direct.StatusCode);
        Assert.Equal(0, await p.ScalarAsync("SELECT count(*) FROM prem_config.app_group WHERE name = 'Visitors' AND deleted_at IS NULL"));
    }

    [Fact]
    public async Task A_token_is_shown_once_on_a_page_that_keeps_no_copy()
    {
        await using var p = await PortalWorld.NewAsync(server);

        using var issued = await p.PostAsync("/portal/agents/report-bot/tokens", p.Admin, [("days", "7")]);
        Assert.Equal(HttpStatusCode.OK, issued.StatusCode);
        Assert.Equal("no-store", issued.Headers.CacheControl?.ToString());
        var page = await issued.Content.ReadAsStringAsync();
        var token = Regex.Match(page, "prem_agt_[A-Za-z0-9_-]+").Value;
        Assert.NotEmpty(token);
        Assert.True((await CallerAccess.ResolveAgentTokenAsync(p.World.Identity, token)).IsResolved);

        // Nowhere else: not the agent's page, the change record, or any export.
        foreach (var path in new[] { "/portal/agents/report-bot", "/portal/changes", "/portal/export/changes.jsonl", "/portal/export/config.json" })
            Assert.DoesNotContain(token, await p.TextAsync(await p.GetAsync(path, p.Admin)));
        using (var withHashes = await p.PostAsync("/portal/export/config.json", p.Admin, [("secrets", "yes")]))
            Assert.DoesNotContain(token, await withHashes.Content.ReadAsStringAsync());
        Assert.Equal(1, await p.ScalarAsync("SELECT count(*) FROM prem_config.admin_event WHERE kind = 'token.issue'"));
        Assert.Equal(0, await p.ScalarAsync($"SELECT count(*) FROM prem_config.admin_event WHERE new_value::text LIKE '%{token[7..]}%'"));
    }

    [Fact]
    public async Task A_looser_setting_is_asked_again_before_it_is_saved_and_a_stricter_one_is_not()
    {
        await using var p = await PortalWorld.NewAsync(server);
        var settings = new TrustSettingsStore(p.World.Db, p.World.Tenant);

        var warning = await p.TextAsync(await p.PostAsync("/portal/settings", p.Admin,
            [("key", TrustSettingsStore.AgentsMinimumTier), ("value", "unverified")]));
        Assert.Contains("is looser than human-reviewed", warning);
        Assert.Equal(TrustSettingSource.Default, (await settings.ReadAsync(TrustSettingsStore.AgentsMinimumTier)).Source);
        Assert.Equal(0, await p.ScalarAsync("SELECT count(*) FROM prem_config.admin_event"));

        using (var confirmed = await p.PostAsync("/portal/settings", p.Admin,
                   [("key", TrustSettingsStore.AgentsMinimumTier), ("value", "unverified"), ("confirm", "yes")]))
            Assert.Equal(HttpStatusCode.Redirect, confirmed.StatusCode);
        Assert.Equal("unverified", (await settings.ReadAsync(TrustSettingsStore.AgentsMinimumTier)).Value);

        using (var stricter = await p.PostAsync("/portal/settings", p.Admin,
                   [("key", TrustSettingsStore.AgentsMinimumTier), ("value", "human-reviewed")]))
            Assert.Equal(HttpStatusCode.Redirect, stricter.StatusCode);
        Assert.Equal("human-reviewed", (await settings.ReadAsync(TrustSettingsStore.AgentsMinimumTier)).Value);
        Assert.Equal(2, await p.ScalarAsync(
            $"SELECT count(*) FROM prem_config.admin_event WHERE kind = 'setting.set' AND actor_user_id = '{p.World.Carol.Id}'"));
    }

    [Fact]
    public async Task The_search_page_searches_as_the_person_and_shows_the_four_fields()
    {
        await using var p = await PortalWorld.NewAsync(server);
        var bob = await Api.SessionAsync(p.Client, "bob", ApiWorld.BobPassword);

        var alice = await p.TextAsync(await p.GetAsync("/portal/search?q=zeppelin", p.Member));
        Assert.Contains(ApiWorld.Handbook, alice);
        Assert.Contains(ApiWorld.Plan, alice);
        Assert.DoesNotContain(ApiWorld.AuditLog, alice);
        Assert.Contains("trust: ", alice);
        Assert.Contains("authorship: ", alice);
        Assert.Contains("stale: ", alice);
        Assert.Contains("concept: ", alice);

        // Bob is staff and a contractor, and "staff" denies contractors first.
        var asBob = await p.TextAsync(await p.GetAsync("/portal/search?q=zeppelin", bob));
        Assert.Contains(ApiWorld.Handbook, asBob);
        Assert.DoesNotContain(ApiWorld.Plan, asBob);
    }

    [Fact]
    public async Task A_question_over_the_limit_is_answered_on_the_search_page_with_the_sentence()
    {
        await using var p = await PortalWorld.NewAsync(server);
        var tooLong = string.Join(' ', Enumerable.Repeat("zeppelin", 445));

        using var response = await p.GetAsync("/portal/search?q=" + Uri.EscapeDataString(tooLong), p.Member);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains($"<p class=\"error\">{Premagentic.Core.Retrieval.QueryLimits.SearchRefusal(tooLong)}</p>", await p.TextAsync(response));
    }

    [Fact]
    public async Task The_documents_page_shows_what_a_document_is_and_never_its_text()
    {
        await using var p = await PortalWorld.NewAsync(server);

        var list = await p.TextAsync(await p.GetAsync("/portal/documents", p.Auditor));
        Assert.Contains(ApiWorld.Handbook, list);
        Assert.Contains("okf/care/pest-scouting.md", list);
        Assert.Contains("machine", list);
        Assert.DoesNotContain("zeppelin handbook for everyone", list);

        var one = await p.TextAsync(await p.GetAsync("/portal/documents/view?path=" + Uri.EscapeDataString(ApiWorld.Plan), p.Auditor));
        Assert.Contains("Content hash", one);
        Assert.Contains("deny group:Contractors", one);
        Assert.DoesNotContain("zeppelin staff plan", one);
    }

    [Fact]
    public async Task Run_now_ingests_a_registered_source_and_its_run_shows_every_count()
    {
        await using var p = await PortalWorld.NewAsync(server);
        var folder = SourcesTests.Folder(
            ("rota.md", "# Rota\n\n## Weekend\nTwo people open the potting shed on Saturdays.\n"),
            ("plan.pdf", "%PDF invented"),
            ("blank.md", ""));

        using (await p.PostAsync("/portal/sources", p.Admin, [("name", "shed"), ("folder", folder), ("prefix", "shed")])) { }
        using (await p.PostAsync("/portal/permissions/rules", p.Admin, [("source", "filesystem"), ("prefix", "shed"), ("entries", "allow everyone")])) { }
        using (var started = await p.PostAsync("/portal/sources/shed/run", p.Admin))
            Assert.DoesNotContain("error=", started.Headers.Location?.OriginalString ?? "");

        var runs = new IngestRuns(p.World.Db, p.World.Tenant);
        var source = (await new SourceRegistry(p.World.Db, p.World.Tenant).FindAsync("shed"))!;
        IngestRunRecord? run = null;
        for (var i = 0; i < 300 && run is not { Outcome: not IngestRunOutcome.Running }; i++)
        {
            await Task.Delay(100);
            run = await runs.LastAsync(source.Id);
        }
        Assert.Equal(IngestRunOutcome.Completed, run!.Outcome);

        var page = await p.TextAsync(await p.GetAsync("/portal/runs/" + run.Id, p.Auditor));
        Assert.Contains("<dt>Scanned</dt><dd>2</dd>", page);
        Assert.Contains("<dt>Ingested</dt><dd>1</dd>", page);
        // The PDF was never indexed, so nothing was kept without a reader.
        Assert.Contains("<dt>Kept without a reader</dt><dd>0</dd>", page);
        Assert.Contains("<dt>Of those, removed because now skipped</dt><dd>0</dd>", page);
        Assert.Contains(".pdf", page);
        Assert.Contains("(empty)", page);
        Assert.Equal(1, await p.ScalarAsync(
            $"SELECT count(*) FROM prem_config.admin_event WHERE kind = 'source.run' AND target = 'shed' AND actor_user_id = '{p.World.Carol.Id}'"));

        // A run that started long ago and never ended says so, and one that has just started blocks another.
        await using (var old = p.World.Db.DataSource.CreateCommand("""
            INSERT INTO prem_config.ingest_run(tenant_id, source_id, connector, folder, path_prefix, okf_bundle, undeclared_is_machine, started_at)
            VALUES(@t, @s, 'filesystem', 'x', 'shed', false, false, @at)
            """))
        {
            old.Parameters.AddWithValue("t", p.World.Tenant);
            old.Parameters.AddWithValue("s", source.Id);
            old.Parameters.AddWithValue("at", p.World.Clock.GetUtcNow().AddHours(-13));
            await old.ExecuteNonQueryAsync();
        }
        Assert.Contains("did not finish", await p.TextAsync(await p.GetAsync("/portal/sources/shed", p.Auditor)));

        await using (var current = p.World.Db.DataSource.CreateCommand("""
            INSERT INTO prem_config.ingest_run(tenant_id, source_id, connector, folder, path_prefix, okf_bundle, undeclared_is_machine)
            VALUES(@t, @s, 'filesystem', 'x', 'shed', false, false)
            """))
        {
            current.Parameters.AddWithValue("t", p.World.Tenant);
            current.Parameters.AddWithValue("s", source.Id);
            await current.ExecuteNonQueryAsync();
        }
        var runsBefore = await p.ScalarAsync("SELECT count(*) FROM prem_config.ingest_run");
        using (var refused = await p.PostAsync("/portal/sources/shed/run", p.Admin))
            Assert.Contains("still%20in%20progress", refused.Headers.Location?.OriginalString ?? "");
        await Task.Delay(500);
        Assert.Equal(runsBefore, await p.ScalarAsync("SELECT count(*) FROM prem_config.ingest_run"));
    }

    [Fact]
    public async Task Health_and_the_support_bundle_carry_no_secret_no_question_and_no_text()
    {
        await using var p = await PortalWorld.NewAsync(server);
        using (await Api.SearchAsync(p.Client, "zeppelin", session: p.Member)) { }
        // A chunker this process lacks, registered by another's registry, so the bundle cannot be showing a default.
        await new SourceRegistry(p.World.Db, p.World.Tenant, new ChunkerRegistry(new LineChunker()))
            .AddAsync("shed", SourcesTests.Folder(("a.md", "# A\n\n## Body\nText.\n")), "shed", false, false,
                new AdminActor("cli", "test-account"), LineChunker.DefaultName);

        var health = await p.TextAsync(await p.GetAsync("/portal/health", p.Auditor));
        Assert.Contains("Schema", health);
        Assert.Contains("current", health);

        using var bundle = await p.GetAsync("/portal/health/support-bundle.json", p.Auditor);
        var text = await bundle.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(text);
        Assert.True(json.RootElement.GetProperty("assemblies").GetArrayLength() > 0);
        Assert.Equal("human-reviewed", json.RootElement.GetProperty("settings").GetProperty(TrustSettingsStore.AgentsMinimumTier).GetProperty("value").GetString());
        Assert.Equal(LineChunker.DefaultName, json.RootElement.GetProperty("sources").EnumerateArray()
            .Single(s => s.GetProperty("name").GetString() == "shed").GetProperty("chunker").GetString());
        Assert.DoesNotContain("zeppelin", text);
        Assert.DoesNotContain("password", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("prem_agt_", text);
        Assert.DoesNotContain("Password=", text);
    }

    [Fact]
    public async Task Export_leaves_out_hashes_unless_an_administrator_asks_and_the_asking_is_recorded()
    {
        await using var p = await PortalWorld.NewAsync(server);

        var plain = await p.TextAsync(await p.GetAsync("/portal/export/config.json", p.Auditor));
        using (var json = JsonDocument.Parse(plain))
            Assert.True(json.RootElement.GetProperty("tables").GetProperty("app_user").GetArrayLength() > 0);
        Assert.DoesNotContain("password_hash", plain);
        Assert.DoesNotContain("secret_sha256", plain);
        Assert.DoesNotContain("user_session", plain);

        Assert.True(await PortalRoutes.IsRefusedAsync(await p.PostAsync("/portal/export/config.json", p.Auditor, [("secrets", "yes")])));
        var withHashes = await p.TextAsync(await p.PostAsync("/portal/export/config.json", p.Admin, [("secrets", "yes")]));
        Assert.Contains("password_hash", withHashes);
        Assert.Equal(1, await p.ScalarAsync(
            $"SELECT count(*) FROM prem_config.admin_event WHERE kind = 'export.secrets' AND actor_user_id = '{p.World.Carol.Id}'"));

        var lines = (await p.TextAsync(await p.GetAsync("/portal/export/changes.jsonl", p.Auditor))).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.NotEmpty(lines);
        Assert.All(lines, line => JsonDocument.Parse(line).Dispose());
    }

    [Fact]
    public async Task The_audit_trails_export_is_the_business_add_ons_and_its_address_says_so()
    {
        await using var p = await PortalWorld.NewAsync(server);

        foreach (var path in new[]
                 {
                     "/portal/export/audit.jsonl", "/portal/export/audit.jsonl?from=2026-01-01&to=2026-01-31&hosted=yes",
                     // The portal's routes ignore case, and so does the sentence.
                     "/portal/export/AUDIT.jsonl",
                 })
        {
            using var response = await p.GetAsync(path, p.Auditor);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            var said = await response.Content.ReadAsStringAsync();
            Assert.Contains("/portal/export/audit.jsonl", said);
            Assert.Contains(" comes with PremAgentic for Teams, which is not installed here.", said);
        }

        // The export page is the core's backup, and no page links the audit trail's export.
        var export = await p.TextAsync(await p.GetAsync("/portal/export", p.Auditor));
        Assert.Contains("href=\"/portal/export/config.json\"", export);
        Assert.Contains("href=\"/portal/export/changes.jsonl\"", export);
        foreach (var page in new[] { "/portal/export", "/portal/usage", "/portal/audit" })
            Assert.DoesNotContain("audit.jsonl", await p.TextAsync(await p.GetAsync(page, p.Auditor)));
    }

    [Fact]
    public async Task With_the_add_on_loaded_the_audit_trails_address_names_the_command_that_gives_its_rows()
    {
        // An extension under the add-on's name, allowed through the settings
        // before the server starts, as an installed deployment allows one.
        var extensions = Directory.CreateTempSubdirectory("prem-addon-portal-").FullName;
        var allowed = GreetingSample.InstallInto(extensions, Premagentic.Core.Extensions.BusinessAddOn.ExtensionName);
        await using var p = await PortalWorld.NewAsync(server, beforeStart: async (db, tenant) =>
        {
            var settings = new SettingsStore(db, tenant);
            await settings.SetAsync(Premagentic.Core.Extensions.ExtensionSettings.Folder, JsonSerializer.SerializeToElement(extensions));
            await settings.SetAsync(Premagentic.Core.Extensions.ExtensionSettings.Allowed,
                Premagentic.Core.Extensions.ExtensionSettings.ToStored([allowed]));
        });

        using var response = await p.GetAsync("/portal/export/audit.jsonl", p.Auditor);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var said = await response.Content.ReadAsStringAsync();
        Assert.Contains("The audit trail download is not offered in the portal in this version;", said);
        Assert.Contains("prem audit export", said);
        Assert.Contains("gives the same rows.", said);
        Assert.DoesNotContain("does not have it", said);
        Assert.DoesNotContain("not installed", said);
    }

    [Fact]
    public async Task View_as_searches_as_the_target_and_the_audit_row_names_the_administrator()
    {
        await using var p = await PortalWorld.NewAsync(server);

        var page = await p.TextAsync(await p.GetAsync("/portal/permissions/view-as?who=user%3Abob&q=zeppelin", p.Admin));
        Assert.Contains(ApiWorld.Handbook, page);
        Assert.DoesNotContain(ApiWorld.Plan, page);

        await using var cmd = p.World.Db.DataSource.CreateCommand(
            "SELECT access_label, caller_user_id FROM prem_config.retrieval_event ORDER BY created_at DESC LIMIT 1");
        await using var reader = await cmd.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.StartsWith($"view-as:user:{CallerResolver.IdText(p.World.Carol.Id)} ", reader.GetString(0));
        Assert.Equal(p.World.Bob.Id, reader.GetGuid(1));
        await reader.DisposeAsync();

        // As an agent, with no token: the assistant acts for Alice, and "hr" denies it before allowing her.
        var asAgent = await p.TextAsync(await p.GetAsync("/portal/permissions/view-as?who=agent%3Aassistant&q=zeppelin", p.Admin));
        Assert.Contains(ApiWorld.Plan, asAgent);
        Assert.DoesNotContain(ApiWorld.Pay, asAgent);
        await using var agentRow = p.World.Db.DataSource.CreateCommand(
            "SELECT access_label, caller_agent_id FROM prem_config.retrieval_event ORDER BY created_at DESC LIMIT 1");
        await using var agentReader = await agentRow.ExecuteReaderAsync();
        Assert.True(await agentReader.ReadAsync());
        Assert.StartsWith($"view-as:user:{CallerResolver.IdText(p.World.Carol.Id)} agent:", agentReader.GetString(0));
        Assert.Equal(p.World.Assistant.Id, agentReader.GetGuid(1));
    }

    [Fact]
    public async Task Why_names_the_rule_and_the_entry_that_decided()
    {
        await using var p = await PortalWorld.NewAsync(server);

        var bob = await p.TextAsync(await p.GetAsync($"/portal/permissions/why?who=user%3Abob&path={Uri.EscapeDataString(ApiWorld.Plan)}", p.Auditor));
        Assert.Contains("user:bob cannot read", bob);
        Assert.Contains("test-datastore:staff", bob);
        Assert.Contains("decided", bob);

        var alice = await p.TextAsync(await p.GetAsync($"/portal/permissions/why?who=user%3Aalice&path={Uri.EscapeDataString(ApiWorld.Plan)}", p.Auditor));
        Assert.Contains("user:alice can read", alice);
    }
}

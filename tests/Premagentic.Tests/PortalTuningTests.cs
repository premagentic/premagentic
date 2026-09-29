using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Premagentic.Core.Admin;
using Premagentic.Core.Evaluation;
using Premagentic.Core.Okf;
using Premagentic.Core.Retrieval;

namespace Premagentic.Tests;

/// <summary>
/// The tuning page through the real API host: the retrieval values with where
/// each came from, changes checked by the same rules as <c>prem settings</c>,
/// the golden set path shown and never changed, and each settings page's
/// history holding only its own keys. Requires a running Docker daemon.
/// </summary>
public sealed class PortalTuningTests(DatastoreTestDatabase server) : IClassFixture<DatastoreTestDatabase>
{
    private const string RrfK = RetrievalSettings.RrfK;

    private static string MainOf(string page) => page[page.IndexOf("<main>", StringComparison.Ordinal)..];

    private static async Task<string> OutcomeAsync(Task<HttpResponseMessage> post)
    {
        using var response = await post;
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        return Uri.UnescapeDataString(response.Headers.Location!.OriginalString).Replace('+', ' ');
    }

    [Fact]
    public async Task The_page_shows_each_value_and_its_source_and_a_change_is_checked_stored_recorded_and_undone()
    {
        await using var p = await PortalWorld.NewAsync(server);
        var store = new TuningSettingsStore(p.World.Db, p.World.Tenant);

        var fresh = MainOf(await p.TextAsync(await p.GetAsync("/portal/tuning", p.Admin)));
        foreach (var key in RetrievalSettings.Keys)
            Assert.Contains($"<code>{key}</code>", fresh);
        Assert.Contains("<td><code>60</code></td><td>default</td><td>a whole number from 1 to 1000</td>", fresh);
        Assert.Contains("Read by the golden-set evaluation; a search is not filtered by it.", fresh);
        Assert.Contains("No golden set is set.", fresh);
        Assert.Contains($"prem settings set {TuningSettingsStore.GoldenSetPath} &lt;absolute path&gt;", fresh);
        Assert.DoesNotContain("Use the default", fresh);

        Assert.Contains($"{RrfK} is now 40. It applies to the next search.",
            await OutcomeAsync(p.PostAsync("/portal/tuning", p.Admin, [("key", RrfK), ("value", "40")])));
        Assert.Equal(40, (await store.ReadAsync(RrfK)).Value.GetInt32());
        var set = MainOf(await p.TextAsync(await p.GetAsync("/portal/tuning", p.Admin)));
        Assert.Contains("<td><code>40</code></td><td>set</td>", set);
        Assert.Contains("Use the default", set);
        Assert.Contains($"<td>{RrfK}</td><td></td><td>40</td>", set);

        Assert.Contains($"{RrfK} is back to its default, 60.",
            await OutcomeAsync(p.PostAsync("/portal/tuning/unset", p.Admin, [("key", RrfK)])));
        Assert.Equal(TuningSettingSource.Default, (await store.ReadAsync(RrfK)).Source);
        Assert.Contains($"<td>{RrfK}</td><td>40</td><td>unset, so the default applies</td>",
            await p.TextAsync(await p.GetAsync("/portal/tuning", p.Auditor)));

        Assert.Equal(1, await p.ScalarAsync(
            $"SELECT count(*) FROM prem_config.admin_event WHERE kind = 'setting.set' AND target = '{RrfK}' AND actor_user_id = '{p.World.Carol.Id}'"));
        Assert.Equal(1, await p.ScalarAsync(
            $"SELECT count(*) FROM prem_config.admin_event WHERE kind = 'setting.unset' AND target = '{RrfK}' AND actor_user_id = '{p.World.Carol.Id}'"));

        // An auditor reads the page and gets no form that changes anything.
        var auditor = MainOf(await p.TextAsync(await p.GetAsync("/portal/tuning", p.Auditor)));
        Assert.DoesNotMatch(new Regex("<form[^>]*method=\"post\""), auditor);
    }

    [Theory]
    [InlineData(RetrievalSettings.RrfK, "0", "a whole number from 1 to 1000")]
    [InlineData(RetrievalSettings.RrfK, "forty", "takes a whole number from 1 to 1000, not 'forty'")]
    [InlineData(RetrievalSettings.FallbackRrfWeight, "-0.1", "a number from 0 to 1")]
    [InlineData(RetrievalSettings.Authority, """{"default": 1.0, "by_class": {"runbook": 0}}""", "a weight greater than 0 for every class")]
    [InlineData(TuningSettingsStore.GoldenSetPath, "C:\\eval\\golden.json", "is set on the command line only")]
    [InlineData("retrieval.top_k", "5", "There is no retrieval setting 'retrieval.top_k'")]
    public async Task A_refused_change_says_why_and_stores_and_records_nothing(string key, string value, string said)
    {
        await using var p = await PortalWorld.NewAsync(server);

        var outcome = await OutcomeAsync(p.PostAsync("/portal/tuning", p.Admin, [("key", key), ("value", value)]));

        Assert.Contains("error=", outcome);
        Assert.Contains(said, outcome);
        Assert.Equal(0, await p.ScalarAsync(
            $"SELECT count(*) FROM prem_config.setting WHERE key LIKE 'retrieval.%' OR key = '{TuningSettingsStore.GoldenSetPath}'"));
        Assert.Equal(0, await p.ScalarAsync("SELECT count(*) FROM prem_config.admin_event"));
    }

    [Fact]
    public async Task The_golden_set_path_is_shown_but_cannot_be_changed_or_unset_here()
    {
        await using var p = await PortalWorld.NewAsync(server);
        var store = new TuningSettingsStore(p.World.Db, p.World.Tenant);
        var path = Path.Combine(Path.GetTempPath(), "prem-eval", "golden.json");
        await store.SetAsync(TuningSettingsStore.GoldenSetPath, JsonSerializer.SerializeToElement(path), AdminActor.Cli());
        var records = await p.ScalarAsync("SELECT count(*) FROM prem_config.admin_event");

        var page = MainOf(await p.TextAsync(await p.GetAsync("/portal/tuning", p.Auditor)));
        Assert.Contains($"The golden set is <code>{System.Text.Encodings.Web.HtmlEncoder.Default.Encode(path)}</code>.", page);

        Assert.Contains("is set on the command line only",
            await OutcomeAsync(p.PostAsync("/portal/tuning/unset", p.Admin, [("key", TuningSettingsStore.GoldenSetPath)])));
        Assert.Equal(path, (await store.ReadGoldenSetPathAsync()).Path);
        Assert.Equal(records, await p.ScalarAsync("SELECT count(*) FROM prem_config.admin_event"));
    }

    [Fact]
    public async Task A_stored_value_that_cannot_be_used_is_shown_with_why_and_its_default()
    {
        await using var p = await PortalWorld.NewAsync(server);
        await using (var cmd = p.World.Db.DataSource.CreateCommand(
            "INSERT INTO prem_config.setting(tenant_id, key, value) VALUES(@t, @key, '1500'::jsonb)"))
        {
            cmd.Parameters.AddWithValue("t", p.World.Tenant);
            cmd.Parameters.AddWithValue("key", RrfK);
            await cmd.ExecuteNonQueryAsync();
        }

        var page = MainOf(await p.TextAsync(await p.GetAsync("/portal/tuning", p.Admin)));
        Assert.Contains("<td><code>60</code></td><td><span class=\"tag bad\">not used: retrieval.rrf_k takes a whole number from 1 to 1000, not 1500. The default applies.</span></td>", page);
        // The edit form starts from what is stored, so it can be corrected, and the default can be restored.
        Assert.Contains("value=\"1500\"", page);
        Assert.Contains("Use the default", page);
    }

    [Fact]
    public async Task Each_settings_page_shows_the_history_of_its_own_keys_only()
    {
        await using var p = await PortalWorld.NewAsync(server);
        await OutcomeAsync(p.PostAsync("/portal/tuning", p.Admin, [("key", RrfK), ("value", "40")]));
        await OutcomeAsync(p.PostAsync("/portal/settings", p.Admin,
            [("key", TrustSettingsStore.Stale), ("value", "hidden-from-everyone")]));

        var trust = MainOf(await p.TextAsync(await p.GetAsync("/portal/settings", p.Auditor)));
        var tuning = MainOf(await p.TextAsync(await p.GetAsync("/portal/tuning", p.Auditor)));
        var trustHistory = trust[trust.IndexOf("<h2>History</h2>", StringComparison.Ordinal)..];
        var tuningHistory = tuning[tuning.IndexOf("<h2>History</h2>", StringComparison.Ordinal)..];

        Assert.Contains($"<td>{TrustSettingsStore.Stale}</td>", trustHistory);
        Assert.DoesNotContain(RrfK, trustHistory);
        Assert.Contains($"<td>{RrfK}</td>", tuningHistory);
        Assert.DoesNotContain(TrustSettingsStore.Stale, tuningHistory);
        // The record itself holds both.
        Assert.Equal(2, await p.ScalarAsync("SELECT count(*) FROM prem_config.admin_event WHERE kind = 'setting.set'"));
    }
}

using System.Net;
using System.Text.Json;
using Premagentic.Core;
using Premagentic.Core.Admin;
using Premagentic.Core.Embeddings;
using Premagentic.Core.Evaluation;
using Premagentic.Core.Identity;
using Premagentic.Core.Retrieval;

namespace Premagentic.Tests;

/// <summary>
/// "Run the golden set": the evaluation run in process under the settings
/// saved now, each question judged by the settings its own search ranked
/// under, one run at a time, bounded in time, and recorded in the change
/// record and nowhere else. The one-at-a-time lock is process wide, so every
/// class that runs the golden set in process is in the collection
/// <see cref="GoldenSetRunGate"/>. Every question and file is invented.
/// Requires a running Docker daemon.
/// </summary>
// GoldenSetRun's gate is one per process: never beside another class that runs it.
[Collection(GoldenSetRunGate.Name)]
public sealed class PortalGoldenRunTests(DatastoreTestDatabase server) : IClassFixture<DatastoreTestDatabase>
{
    private const string Floor = RetrievalSettings.NoAnswerDistanceFloor;

    // One question that finds its document, one that expects a document nobody
    // wrote, and one with no answer in the corpus at all.
    private const string GoldenJson = $$"""
        [
          { "id": "finds-the-handbook", "question": "zeppelin", "category": "lookup",
            "expectedSourcePaths": ["{{ApiWorld.Handbook}}"], "forbiddenSourcePaths": [], "notes": "" },
          { "id": "expects-a-missing-file", "question": "zeppelin", "category": "lookup",
            "expectedSourcePaths": ["nowhere/missing.md"], "forbiddenSourcePaths": [], "notes": "" },
          { "id": "has-no-answer", "question": "xylophone quartz marmalade", "category": "no-answer",
            "expectedSourcePaths": [], "forbiddenSourcePaths": [], "expectNoAnswer": true, "notes": "" }
        ]
        """;

    private static async Task<string> GoldenFileAsync(string json)
    {
        var path = Path.Combine(Path.GetTempPath(), $"prem-golden-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(path, json);
        return path;
    }

    private static async Task<string> OutcomeAsync(Task<HttpResponseMessage> post)
    {
        using var response = await post;
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        return Uri.UnescapeDataString(response.Headers.Location!.OriginalString).Replace('+', ' ');
    }

    private static async Task<List<JsonElement>> RunsAsync(PortalWorld p)
    {
        await using var cmd = p.World.Db.DataSource.CreateCommand(
            $"SELECT new_value::text FROM prem_config.admin_event WHERE kind = '{GoldenSetRun.Kind}' ORDER BY id");
        var runs = new List<JsonElement>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            runs.Add(JsonDocument.Parse(reader.GetString(0)).RootElement.Clone());
        return runs;
    }

    private static Task SetAsync(PortalWorld p, string key, object value) =>
        new TuningSettingsStore(p.World.Db, p.World.Tenant).SetAsync(key, JsonSerializer.SerializeToElement(value), AdminActor.Cli());

    [Fact]
    public async Task A_run_judges_each_question_by_the_saved_settings_records_its_summary_and_sits_beside_the_run_before()
    {
        await using var p = await PortalWorld.NewAsync(server);
        var golden = await GoldenFileAsync(GoldenJson);
        try
        {
            await SetAsync(p, TuningSettingsStore.GoldenSetPath, golden);

            // A floor so low that no passage found only by meaning counts as an answer.
            await SetAsync(p, Floor, 0.0001);
            Assert.Contains("The golden set ran: 2 of 3 passed.",
                await OutcomeAsync(p.PostAsync("/portal/tuning/run", p.Admin)));

            // A floor so high that any passage found by meaning counts as one, so the no-answer question now fails.
            await SetAsync(p, Floor, 1.99);
            Assert.Contains("The golden set ran: 1 of 3 passed.",
                await OutcomeAsync(p.PostAsync("/portal/tuning/run", p.Admin)));

            var runs = await RunsAsync(p);
            Assert.Equal(2, runs.Count);
            var (low, high) = (runs[0], runs[1]);
            Assert.Equal("completed", low.GetProperty("outcome").GetString());
            Assert.Equal((3, 2, 1, 1), (low.GetProperty("cases").GetInt32(), low.GetProperty("passed").GetInt32(),
                low.GetProperty("no_answer_cases").GetInt32(), low.GetProperty("no_answer_correct").GetInt32()));
            Assert.Equal(["expects-a-missing-file"], low.GetProperty("failed").EnumerateArray().Select(f => f.GetString()));
            Assert.Equal(0.0001, low.GetProperty("settings").GetProperty(Floor).GetDouble());
            Assert.False(low.GetProperty("settings_changed").GetBoolean());
            Assert.Equal(0, high.GetProperty("no_answer_correct").GetInt32());
            Assert.Equal(["expects-a-missing-file", "has-no-answer"], high.GetProperty("failed").EnumerateArray().Select(f => f.GetString()));
            Assert.Equal(1.99, high.GetProperty("settings").GetProperty(Floor).GetDouble());
            Assert.Equal(60, high.GetProperty("settings").GetProperty(RetrievalSettings.RrfK).GetInt32());
            Assert.Equal(1.0, high.GetProperty("settings").GetProperty(RetrievalSettings.Authority).GetProperty("default").GetDouble());
            Assert.Equal(2, await p.ScalarAsync(
                $"SELECT count(*) FROM prem_config.admin_event WHERE kind = '{GoldenSetRun.Kind}' AND target = '{golden.Replace("'", "''")}' AND actor_user_id = '{p.World.Carol.Id}'"));

            var page = await p.TextAsync(await p.GetAsync("/portal/tuning", p.Auditor));
            Assert.Contains("<th>Latest run</th><th>The run before</th>", page);
            Assert.Contains("<td>Passed</td><td>1 of 3</td><td>2 of 3</td>", page);
            Assert.Contains("<td>No-answer floor</td><td>1.99</td><td>0.0001</td>", page);
            Assert.Contains("<td>Failed</td><td>expects-a-missing-file, has-no-answer</td><td>expects-a-missing-file</td>", page);
            // The auditor sees the runs and gets no button that starts one.
            Assert.DoesNotContain("/portal/tuning/run", page);
        }
        finally
        {
            File.Delete(golden);
        }
    }

    [Fact]
    public async Task A_run_is_for_administrators_and_a_second_one_while_one_runs_is_told_so_and_starts_nothing()
    {
        await using var p = await PortalWorld.NewAsync(server);
        var golden = await GoldenFileAsync(GoldenJson);
        try
        {
            await SetAsync(p, TuningSettingsStore.GoldenSetPath, golden);

            foreach (var session in new[] { p.Auditor, p.Member })
                using (var refused = await p.PostAsync("/portal/tuning/run", session))
                    Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);

            Assert.True(await GoldenSetRun.InProgress.WaitAsync(0));
            try
            {
                Assert.Contains("A golden-set run is already in progress.",
                    await OutcomeAsync(p.PostAsync("/portal/tuning/run", p.Admin)));
            }
            finally
            {
                GoldenSetRun.InProgress.Release();
            }
            Assert.Empty(await RunsAsync(p));

            // The control: with nothing in progress, the same request runs.
            Assert.Contains("The golden set ran:", await OutcomeAsync(p.PostAsync("/portal/tuning/run", p.Admin)));
            Assert.Single(await RunsAsync(p));
        }
        finally
        {
            File.Delete(golden);
        }
    }

    [Fact]
    public async Task While_the_gate_is_held_a_run_is_busy_before_its_golden_set_is_judged_and_records_nothing()
    {
        await using var p = await PortalWorld.NewAsync(server);
        Task<string> RunAsync() => OutcomeAsync(p.PostAsync("/portal/tuning/run", p.Admin));
        var changes = await p.ScalarAsync("SELECT count(*) FROM prem_config.admin_event");

        // No golden set is set, so a run that reached the judgment would be
        // refused for it. With the gate held, as another run in this process
        // holds it, the answer is busy instead: the gate comes first. A test
        // class that runs the golden set beside this one would turn every
        // refusal here the same way, which is why they share a collection.
        Assert.True(await GoldenSetRun.InProgress.WaitAsync(0));
        try
        {
            var busy = await RunAsync();
            Assert.Contains("A golden-set run is already in progress.", busy);
            Assert.DoesNotContain("No golden set is set.", busy);
        }
        finally
        {
            GoldenSetRun.InProgress.Release();
        }
        Assert.Empty(await RunsAsync(p));
        Assert.Equal(changes, await p.ScalarAsync("SELECT count(*) FROM prem_config.admin_event"));

        // The control: released, the same request reaches the judgment and is refused for the golden set.
        Assert.Contains("No golden set is set.", await RunAsync());
        Assert.Empty(await RunsAsync(p));
        Assert.Equal(changes, await p.ScalarAsync("SELECT count(*) FROM prem_config.admin_event"));
    }

    [Fact]
    public async Task A_run_past_its_limit_is_stopped_and_the_stop_is_recorded()
    {
        await using var p = await PortalWorld.NewAsync(server);
        var golden = await GoldenFileAsync(GoldenJson);
        try
        {
            await SetAsync(p, TuningSettingsStore.GoldenSetPath, golden);
            var run = new GoldenSetRun(p.World.Db, p.World.Tenant, new HybridSearch(p.World.Db, new SeededEmbeddingProvider()),
                new PrincipalNames(new IdentityStore(p.World.Db, p.World.Tenant)));

            var result = await run.RunAsync(AdminActor.Cli(), TimeSpan.Zero);

            Assert.Equal(GoldenRunOutcome.TimedOut, result.Outcome);
            var recorded = Assert.Single(await RunsAsync(p));
            Assert.Equal("timed_out", recorded.GetProperty("outcome").GetString());
            Assert.Equal(3, recorded.GetProperty("cases").GetInt32());
            Assert.Contains("<td>Outcome</td><td>stopped at the limit of 0 seconds</td>",
                await p.TextAsync(await p.GetAsync("/portal/tuning", p.Auditor)));
            // The lock is released after a stopped run.
            Assert.True(await GoldenSetRun.InProgress.WaitAsync(0));
            GoldenSetRun.InProgress.Release();
        }
        finally
        {
            File.Delete(golden);
        }
    }

    [Fact]
    public async Task A_run_is_refused_in_plain_words_when_the_golden_set_is_unset_relative_or_unreadable_and_nothing_is_recorded()
    {
        await using var p = await PortalWorld.NewAsync(server);
        Task<string> RunAsync() => OutcomeAsync(p.PostAsync("/portal/tuning/run", p.Admin));

        Assert.Contains("No golden set is set.", await RunAsync());

        await using (var cmd = p.World.Db.DataSource.CreateCommand(
            "INSERT INTO prem_config.setting(tenant_id, key, value) VALUES(@t, @key, '\"golden.json\"'::jsonb)"))
        {
            cmd.Parameters.AddWithValue("t", p.World.Tenant);
            cmd.Parameters.AddWithValue("key", TuningSettingsStore.GoldenSetPath);
            await cmd.ExecuteNonQueryAsync();
        }
        Assert.Contains("takes an absolute path", await RunAsync());

        await SetAsync(p, TuningSettingsStore.GoldenSetPath, Path.Combine(Path.GetTempPath(), $"prem-absent-{Guid.NewGuid():N}.json"));
        Assert.Contains("cannot be read by this process", await RunAsync());

        foreach (var (json, said) in new[]
                 {
                     ("{ not json at all", "is not a JSON list of golden questions"),
                     ("[]", "holds no questions"),
                     ("""[{ "id": "q", "question": "zeppelin" }]""", "without an id, a question, expectedSourcePaths or forbiddenSourcePaths"),
                 })
        {
            var file = await GoldenFileAsync(json);
            try
            {
                await SetAsync(p, TuningSettingsStore.GoldenSetPath, file);
                var outcome = await RunAsync();
                Assert.Contains(said, outcome);
                // Nothing of the parser or the file is echoed back.
                Assert.DoesNotContain("LineNumber", outcome);
                Assert.DoesNotContain("not json", outcome);
            }
            finally
            {
                File.Delete(file);
            }
        }
        Assert.Empty(await RunsAsync(p));
    }

    [Fact]
    public void A_summary_flags_settings_that_changed_between_questions_and_compares_weights_by_value()
    {
        static RetrievalSettingsReading Reading(double floor, double runbook) => new(
            RetrievalTuning.Default with { NoAnswerDistanceFloor = floor },
            new AuthorityWeights(new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase) { ["runbook"] = runbook }),
            []);
        static EvalCaseResult Case(string id, RetrievalSettingsReading settings) => new(
            new GoldenCase(id, "q", "c", [], null, [], false, false, ""),
            new EvalVerdict(true, true, true, false, true, true), null, "eval",
            new SearchResult("q", false, [], 1, settings));

        // Equal values in distinct instances are the same settings.
        var steady = new EvalSummary([Case("a", Reading(0.5, 1.2)), Case("b", Reading(0.5, 1.2))]);
        Assert.False(steady.SettingsChanged);
        Assert.False(GoldenSetRun.SummaryValue(steady).ContainsKey("settings_by_case"));
        Assert.True(RetrievalReadings.Same(Reading(0.5, 1.2), new RetrievalSettingsReading(
            RetrievalTuning.Default with { NoAnswerDistanceFloor = 0.5 },
            new AuthorityWeights(new Dictionary<string, double> { ["Runbook"] = 1.2 }), [])));

        foreach (var changed in new[]
                 {
                     new EvalSummary([Case("a", Reading(0.5, 1.2)), Case("b", Reading(0.6, 1.2))]),
                     new EvalSummary([Case("a", Reading(0.5, 1.2)), Case("b", Reading(0.5, 1.3))]),
                 })
        {
            Assert.True(changed.SettingsChanged);
            var value = JsonSerializer.SerializeToElement(GoldenSetRun.SummaryValue(changed));
            Assert.True(value.GetProperty("settings_changed").GetBoolean());
            Assert.Equal(["a", "b"], value.GetProperty("settings_by_case").EnumerateArray().Select(c => c.GetProperty("id").GetString()));
        }
    }
}

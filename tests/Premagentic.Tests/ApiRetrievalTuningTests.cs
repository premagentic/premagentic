using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Premagentic.Core.Identity;
using Premagentic.Core.Retrieval;

namespace Premagentic.Tests;

/// <summary>
/// The API reads the retrieval tuning the deployment stores, for every search,
/// and logs a stored value it cannot use once, not once per request. Requires a
/// running Docker daemon.
/// </summary>
public sealed class ApiRetrievalTuningTests(DatastoreTestDatabase server) : IClassFixture<DatastoreTestDatabase>
{
    [Fact]
    public async Task A_stored_rrf_k_changes_the_next_search_and_an_unusable_one_changes_nothing_and_is_logged_once()
    {
        var logs = new List<string>();
        await using var w = await ApiWorld.NewAsync(server, new ApiHostOptions { Logs = logs });
        using var client = w.Host.Client();
        var settings = new SettingsStore(w.Db, w.Tenant);

        async Task<double> TopScoreAsync()
        {
            var response = await Api.SearchAsync(client, bearer: w.AssistantToken);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            return body.GetProperty("hits").EnumerateArray().Max(h => h.GetProperty("fusedScore").GetDouble());
        }

        // At the default k of 60 no passage can score above two ranks of 1/61.
        var before = await TopScoreAsync();
        Assert.InRange(before, double.Epsilon, 2.0 / 61);

        // At k = 1 the passage ranked first scores at least 1/2.
        await settings.SetAsync(RetrievalSettings.RrfK, JsonDocument.Parse("1").RootElement);
        Assert.InRange(await TopScoreAsync(), 0.5, 1.0);

        // A value the key does not allow: the default applies, the search answers,
        // and the log says so once however many searches follow.
        await settings.RemoveAsync(RetrievalSettings.RrfK);
        await using (var cmd = w.Db.DataSource.CreateCommand(
            "INSERT INTO prem_config.setting(tenant_id, key, value) VALUES(@tenant, @key, '0'::jsonb)"))
        {
            cmd.Parameters.AddWithValue("tenant", w.Tenant);
            cmd.Parameters.AddWithValue("key", RetrievalSettings.RrfK);
            await cmd.ExecuteNonQueryAsync();
        }
        for (var i = 0; i < 3; i++)
            Assert.Equal(before, await TopScoreAsync());

        string[] warned;
        lock (logs) warned = [.. logs.Where(l => l.Contains(RetrievalSettings.RrfK, StringComparison.Ordinal))];
        Assert.Single(warned);
        Assert.StartsWith("Warning ", warned[0]);
        Assert.Contains(RetrievalSettings.Allowed(RetrievalSettings.RrfK), warned[0]);
    }
}

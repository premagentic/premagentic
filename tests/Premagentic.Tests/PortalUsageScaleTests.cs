using System.Text.RegularExpressions;
using Premagentic.Core.Identity;
using Premagentic.Portal.Pages;

namespace Premagentic.Tests;

/// <summary>
/// The usage page with more people than one page holds: the people table in
/// pages of fifty and a name filter, the figures unchanged. Every person and
/// question is invented. Requires a running Docker daemon.
/// </summary>
public sealed class PortalUsageScaleTests(DatastoreTestDatabase server) : IClassFixture<DatastoreTestDatabase>
{
    private const string Year = "from=2026-01-01&to=2026-12-31";
    private const int People = 120;

    [Fact]
    public async Task A_hundred_and_twenty_people_are_three_pages_of_fifty_and_the_name_filter_narrows_them()
    {
        await using var p = await PortalWorld.NewAsync(server);
        await CrowdAsync(p);

        var first = await p.TextAsync(await p.GetAsync($"/portal/usage?{Year}", p.Auditor));
        Assert.Equal(UsagePages.PageRows, PeopleRows(first).Length);
        Assert.Contains($"1 to {UsagePages.PageRows} of {People}.", first);
        Assert.Contains("people=2", first);
        // The figure counts everyone in the window, not the rows on the page.
        Assert.Matches($"<dt>People</dt><dd>{People}</dd>", first);

        var third = await p.TextAsync(await p.GetAsync($"/portal/usage?{Year}&people=3", p.Auditor));
        Assert.Equal(People - 2 * UsagePages.PageRows, PeopleRows(third).Length);
        Assert.Contains($"101 to {People} of {People}.", third);
        // No page shows a person twice, and the three pages hold everyone.
        var second = await p.TextAsync(await p.GetAsync($"/portal/usage?{Year}&people=2", p.Auditor));
        var all = PeopleRows(first).Concat(PeopleRows(second)).Concat(PeopleRows(third)).ToArray();
        Assert.Equal(People, all.Distinct().Count());

        // A name filter narrows the table, on one page, and says nothing of pages.
        var filtered = await p.TextAsync(await p.GetAsync($"/portal/usage?{Year}&name=VISITOR-11", p.Auditor));
        Assert.Equal(Enumerable.Range(110, 10).Select(i => $"visitor-{i:000}"), PeopleRows(filtered).Order());
        Assert.DoesNotContain("class=\"pager\"", filtered);
        Assert.Matches($"<dt>People</dt><dd>{People}</dd>", filtered);
    }

    /// <summary>The names in the people table, in the order shown.</summary>
    private static string[] PeopleRows(string page)
    {
        var start = page.IndexOf("<h2>People</h2>", StringComparison.Ordinal);
        var end = page.IndexOf("<h2>Assistants</h2>", StringComparison.Ordinal);
        Assert.True(start > 0 && end > start, "no people table");
        return [.. Regex.Matches(page[start..end], "<tr><td>([^<]+)</td>").Select(m => m.Groups[1].Value)];
    }

    /// <summary>A hundred and twenty invented people, one question each, today.</summary>
    private static async Task CrowdAsync(PortalWorld p)
    {
        for (var i = 0; i < People; i++)
        {
            var person = await p.World.Identity.CreateUserAsync($"visitor-{i:000}", $"Visitor {i}", Role.Member);
            await using var cmd = p.World.Db.DataSource.CreateCommand("""
                INSERT INTO prem_config.retrieval_event(
                    tenant_id, kind, query, caller_user_id, caller_agent_id, include_historical, passages, elapsed_ms, model_location, created_at)
                VALUES(@t, 'search', 'opening hours', @user, NULL, false, '[]'::jsonb, 1, NULL, now())
                """);
            cmd.Parameters.AddWithValue("t", p.World.Tenant);
            cmd.Parameters.AddWithValue("user", person.Id);
            await cmd.ExecuteNonQueryAsync();
        }
    }
}

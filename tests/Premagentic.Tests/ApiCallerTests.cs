using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Premagentic.Core.Identity;

namespace Premagentic.Tests;

/// <summary>
/// Every search over HTTP runs as a real caller: people by session or trusted
/// header, agents by token, each reaching exactly what the rules give them,
/// with the trust policy for what they are, and every result saying what it is.
/// Requires a running Docker daemon.
/// </summary>
public sealed class ApiCallerTests(DatastoreTestDatabase server) : IClassFixture<DatastoreTestDatabase>
{
    private const string Header = "X-Test-Signed-In-As";

    [Fact]
    public async Task Two_users_and_two_agents_each_see_exactly_what_the_rules_give_them_over_http()
    {
        await using var w = await ApiWorld.NewAsync(server);
        using var client = w.Host.Client();
        var alice = await Api.SessionAsync(client, "alice", ApiWorld.AlicePassword);
        var bob = await Api.SessionAsync(client, "bob", ApiWorld.BobPassword);

        Assert.Equal([ApiWorld.Pay, ApiWorld.Handbook, ApiWorld.Plan], await Api.PathsAsync(await Api.SearchAsync(client, session: alice)));
        Assert.Equal([ApiWorld.Handbook], await Api.PathsAsync(await Api.SearchAsync(client, session: bob)));
        Assert.Equal([ApiWorld.Handbook, ApiWorld.Plan], await Api.PathsAsync(await Api.SearchAsync(client, bearer: w.AssistantToken)));
        Assert.Equal([ApiWorld.AuditLog, ApiWorld.Handbook], await Api.PathsAsync(await Api.SearchAsync(client, bearer: w.BotToken)));

        // Section fetch obeys the same rules: the assistant is denied the pay
        // bands its owner may read, and it is told the path is not there.
        Assert.Equal(HttpStatusCode.OK, (await Section(client, ApiWorld.Pay, session: alice)).StatusCode);
        using var denied = await Section(client, ApiWorld.Pay, bearer: w.AssistantToken);
        Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);
        using var absent = await Section(client, "hr/no-such-file.md", bearer: w.AssistantToken);
        Assert.Equal(await denied.Content.ReadAsStringAsync(), await absent.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_bearer_token_reaches_the_agents_rights_and_who_am_i_names_the_agent()
    {
        await using var w = await ApiWorld.NewAsync(server);
        using var client = w.Host.Client();

        using var me = await client.SendAsync(Api.Request(HttpMethod.Get, "/api/session", bearer: w.BotToken));
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        var body = await me.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("agent", body.GetProperty("kind").GetString());
        Assert.Equal("report-bot", body.GetProperty("agent").GetString());
        Assert.Equal("service", body.GetProperty("mode").GetString());

        // An agent has no session to end.
        using var signOut = await client.SendAsync(Api.Request(HttpMethod.Delete, "/api/session", bearer: w.BotToken));
        Assert.Equal(HttpStatusCode.BadRequest, signOut.StatusCode);
    }

    [Fact]
    public async Task An_expired_revoked_malformed_or_foreign_token_reaches_nothing()
    {
        await using var w = await ApiWorld.NewAsync(server);
        using var client = w.Host.Client();
        Assert.Equal(HttpStatusCode.OK, (await Api.SearchAsync(client, bearer: w.BotToken)).StatusCode);

        var expiring = (await w.Identity.IssueTokenAsync(w.Bot.Id, TimeSpan.FromHours(1))).PlainText;
        Assert.Equal(HttpStatusCode.OK, (await Api.SearchAsync(client, bearer: expiring)).StatusCode);
        w.Clock.Now += TimeSpan.FromHours(1);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Api.SearchAsync(client, bearer: expiring)).StatusCode);

        var tokenId = w.BotToken["prem_agt_".Length..][..24];
        Assert.True(await w.Identity.RevokeTokenAsync(tokenId));
        using var revoked = await Api.SearchAsync(client, bearer: w.BotToken);
        Assert.Equal(HttpStatusCode.Unauthorized, revoked.StatusCode);
        Assert.DoesNotContain("zeppelin", await revoked.Content.ReadAsStringAsync());

        foreach (var bad in new[] { "prem_agt_nonsense", w.AssistantToken[..^2] + "AA", "Basic " + w.AssistantToken })
            Assert.Equal(HttpStatusCode.Unauthorized, (await Api.SearchAsync(client, bearer: bad)).StatusCode);

        var basic = Api.Request(HttpMethod.Post, "/api/search", new { query = "zeppelin" });
        basic.Headers.TryAddWithoutValidation("Authorization", "Basic YWxpY2U6eA==");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(basic)).StatusCode);

        // The control: the other agent's token still works.
        Assert.Equal(HttpStatusCode.OK, (await Api.SearchAsync(client, bearer: w.AssistantToken)).StatusCode);
    }

    [Fact]
    public async Task A_bad_token_is_refused_even_with_a_good_session_beside_it()
    {
        await using var w = await ApiWorld.NewAsync(server);
        using var client = w.Host.Client();
        var alice = await Api.SessionAsync(client, "alice", ApiWorld.AlicePassword);

        using var response = await Api.SearchAsync(client, session: alice, bearer: "prem_agt_nonsense");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task An_agent_is_held_to_its_requests_per_minute()
    {
        await using var w = await ApiWorld.NewAsync(server, botRate: 3);
        using var client = w.Host.Client();

        for (var i = 0; i < 3; i++)
            Assert.Equal(HttpStatusCode.OK, (await Api.SearchAsync(client, bearer: w.BotToken)).StatusCode);

        using var limited = await Api.SearchAsync(client, bearer: w.BotToken);
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.True(int.Parse(limited.Headers.GetValues("Retry-After").Single()) is > 0 and <= 60);

        // Another agent has its own allowance, and the window slides.
        Assert.Equal(HttpStatusCode.OK, (await Api.SearchAsync(client, bearer: w.AssistantToken)).StatusCode);
        w.Clock.Now += TimeSpan.FromSeconds(61);
        Assert.Equal(HttpStatusCode.OK, (await Api.SearchAsync(client, bearer: w.BotToken)).StatusCode);
    }

    [Fact]
    public async Task With_the_header_mode_off_the_header_is_ignored()
    {
        await using var w = await ApiWorld.NewAsync(server);
        using var client = w.Host.Client();

        foreach (var header in new[] { "X-Prem-Sign-In", Header, "X-Prem-Principals" })
            Assert.Equal(HttpStatusCode.Unauthorized, (await Api.SearchAsync(client, header: (header, "alice"))).StatusCode);
    }

    [Fact]
    public async Task With_the_header_mode_on_the_header_names_a_user_whose_groups_Premagentic_holds()
    {
        await using var w = await ApiWorld.NewAsync(server, new ApiHostOptions { SignInHeader = Header });
        using var client = w.Host.Client();

        Assert.Equal([ApiWorld.Pay, ApiWorld.Handbook, ApiWorld.Plan], await Api.PathsAsync(await Api.SearchAsync(client, header: (Header, "alice"))));
        Assert.Equal([ApiWorld.Handbook], await Api.PathsAsync(await Api.SearchAsync(client, header: (Header, "BOB"))));

        // An unknown name, a disabled one, and principals in the old style all hold nothing.
        foreach (var value in new[] { "mallory", "eve", "group:Staff", "user:alice,group:Staff" })
            Assert.Equal(HttpStatusCode.Unauthorized, (await Api.SearchAsync(client, header: (Header, value))).StatusCode);

        using var me = await client.SendAsync(Api.Request(HttpMethod.Get, "/api/session", header: (Header, "alice")));
        var body = await me.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("trusted-header", body.GetProperty("via").GetString());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("antiForgeryToken").ValueKind);
    }

    [Fact]
    public async Task With_the_header_mode_on_a_body_a_cross_site_form_can_send_is_refused()
    {
        // The header mode has no session to bind an anti-forgery token to. What
        // keeps a form on another site from posting through the proxy is that
        // every route that changes state reads JSON, which a form cannot send.
        await using var w = await ApiWorld.NewAsync(server, new ApiHostOptions { SignInHeader = Header });
        using var client = w.Host.Client();

        foreach (var (content, type) in new[]
        {
            ("{\"query\":\"zeppelin\"}", "text/plain"),
            ("query=zeppelin", "application/x-www-form-urlencoded"),
            ("{\"query\":\"zeppelin\"}", "multipart/form-data; boundary=x"),
        })
        {
            var request = Api.Request(HttpMethod.Post, "/api/search", header: (Header, "alice"));
            request.Content = new StringContent(content, System.Text.Encoding.UTF8);
            request.Content.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse(type);
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
        }

        // The control: the same caller with a JSON body is served.
        Assert.Equal(HttpStatusCode.OK, (await Api.SearchAsync(client, header: (Header, "alice"))).StatusCode);
    }

    [Fact]
    public async Task The_retired_principal_header_setting_stops_the_api_from_starting()
    {
        var connectionString = await server.CreateDatabaseAsync();
        var ex = Assert.ThrowsAny<Exception>(() => ApiTestHost.Start(
            connectionString, new TestClock(ApiWorld.Start), new SeededEmbeddingProvider(),
            new ApiHostOptions { RetiredPrincipalHeader = "X-Principals" }));
        Assert.Contains("PREM_SIGN_IN_HEADER", Flatten(ex));
    }

    [Fact]
    public async Task Every_hit_and_section_says_its_trust_authorship_staleness_and_concept()
    {
        await using var w = await ApiWorld.NewAsync(server, okfBundle: true);
        using var client = w.Host.Client();
        var alice = await Api.SessionAsync(client, "alice", ApiWorld.AlicePassword);

        using var response = await Api.SearchAsync(client, "watering humidity propagation scouting fertilizer", session: alice);
        var hits = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("hits").EnumerateArray().ToArray();
        Assert.Equal(10, hits.Length);
        foreach (var hit in hits)
            foreach (var field in new[] { "trustTier", "authorship", "stale", "conceptId" })
                Assert.True(hit.TryGetProperty(field, out _), $"{hit.GetProperty("path")} has no {field}");

        await AssertSection(client, alice, "okf/care/watering.md", "human-reviewed", "human", stale: false, "care/watering");
        await AssertSection(client, alice, "okf/care/humidity.md", "machine-confirmed", "machine", stale: false, "care/humidity");
        await AssertSection(client, alice, "okf/care/pest-scouting.md", "unverified", "machine", stale: false, "care/pest-scouting");
        await AssertSection(client, alice, "okf/supplies/fertilizer-prices.md", "unverified", "human", stale: true, "supplies/fertilizer-prices");
        await AssertSection(client, alice, ApiWorld.Handbook, "unverified", "unknown", stale: false, null);
    }

    [Fact]
    public async Task An_agent_is_not_served_what_a_person_sees_flagged()
    {
        await using var w = await ApiWorld.NewAsync(server, okfBundle: true);
        using var client = w.Host.Client();
        var alice = await Api.SessionAsync(client, "alice", ApiWorld.AlicePassword);

        // Machine-written and not reviewed by a person, or past its stale date:
        // a person sees it with its flags on, an agent does not see it.
        foreach (var path in new[] { "okf/care/humidity.md", "okf/care/pest-scouting.md", "okf/supplies/fertilizer-prices.md" })
        {
            Assert.Equal(HttpStatusCode.OK, (await Section(client, path, session: alice)).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await Section(client, path, bearer: w.AssistantToken)).StatusCode);
        }

        // Reviewed by a person, and never stale: both see it.
        foreach (var path in new[] { "okf/care/watering.md", "okf/care/propagation.md", "okf/supplies/seed-order.md" })
        {
            Assert.Equal(HttpStatusCode.OK, (await Section(client, path, session: alice)).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await Section(client, path, bearer: w.AssistantToken)).StatusCode);
        }
    }

    [Fact]
    public async Task An_agents_own_minimum_trust_tier_loosens_or_tightens_it_alone()
    {
        await using var w = await ApiWorld.NewAsync(server, okfBundle: true);
        await using (var cmd = w.Db.DataSource.CreateCommand("UPDATE prem_config.agent SET minimum_trust_tier = 'machine-confirmed' WHERE id = @id"))
        {
            cmd.Parameters.AddWithValue("id", w.Assistant.Id);
            await cmd.ExecuteNonQueryAsync();
        }
        using var client = w.Host.Client();

        Assert.Equal(HttpStatusCode.OK, (await Section(client, "okf/care/humidity.md", bearer: w.AssistantToken)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Section(client, "okf/care/pest-scouting.md", bearer: w.AssistantToken)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Section(client, "okf/care/humidity.md", bearer: w.BotToken)).StatusCode);
    }

    private static async Task AssertSection(
        HttpClient client, ApiSession session, string path, string tier, string authorship, bool stale, string? conceptId)
    {
        using var response = await Section(client, path, session: session);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(tier, body.GetProperty("trustTier").GetString());
        Assert.Equal(authorship, body.GetProperty("authorship").GetString());
        Assert.Equal(stale, body.GetProperty("stale").GetBoolean());
        Assert.Equal(conceptId, body.GetProperty("conceptId").GetString());
        Assert.NotEmpty(body.GetProperty("chunks").EnumerateArray());
    }

    private static Task<HttpResponseMessage> Section(HttpClient client, string path, ApiSession? session = null, string? bearer = null) =>
        client.SendAsync(Api.Request(HttpMethod.Post, "/api/section", new { path }, session, bearer: bearer));

    private static string Flatten(Exception ex) =>
        ex.InnerException is null ? ex.Message : ex.Message + " | " + Flatten(ex.InnerException);
}

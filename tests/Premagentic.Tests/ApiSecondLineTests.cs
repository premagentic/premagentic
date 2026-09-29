using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Premagentic.Api;
using Premagentic.Api.Callers;
using Premagentic.Core.Identity;
using Premagentic.Core.Storage;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace Premagentic.Tests;

/// <summary>
/// The API's side of the second line and of the portal's needs: reads go
/// through the search role when there is one, a role that would read the whole
/// index stops the start, forms carry their anti-forgery token in a field, and
/// pages for people send a signed-out browser to sign in.
/// Requires a running Docker daemon.
/// </summary>
public sealed class ApiSecondLineTests(DatastoreTestDatabase server) : IClassFixture<DatastoreTestDatabase>
{
    [Fact]
    public async Task The_api_reads_through_the_search_role_when_one_is_configured()
    {
        await using var r = await RlsDatabase.NewAsync(server);
        await using var host = ApiTestHost.Start(r.AppConnection, new TestClock(ApiWorld.Start), r.Embedder,
            new ApiHostOptions { SearchRoleConnection = r.SearchConnection });
        using var client = host.Client();

        using (var health = await client.GetAsync("/health"))
            Assert.True((await health.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("rowLevelSecurity").GetBoolean());
        Assert.Equal([RlsDatabase.Handbook, RlsDatabase.Plan], await Api.PathsAsync(await Api.SearchAsync(client, bearer: r.AssistantToken)));
        Assert.Equal([RlsDatabase.AuditLog, RlsDatabase.Handbook], await Api.PathsAsync(await Api.SearchAsync(client, bearer: r.BotToken)));

        // Take the read away from the search role alone: the API's searches
        // fail, which only happens if they connect as that role.
        Assert.Equal(HttpStatusCode.OK, (await Section(client, r.AssistantToken)).StatusCode);
        await RlsDatabase.ExecAsync(r.OwnerConnection, $"REVOKE SELECT ON prem_index.chunk FROM {r.SearchRoleName}");
        using (var failed = await Api.SearchAsync(client, bearer: r.AssistantToken))
            Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
        using (var failed = await Section(client, r.AssistantToken))
            Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
        await RlsDatabase.ExecAsync(r.OwnerConnection, $"GRANT SELECT ON prem_index.chunk TO {r.SearchRoleName}");
        Assert.Equal([RlsDatabase.Handbook, RlsDatabase.Plan], await Api.PathsAsync(await Api.SearchAsync(client, bearer: r.AssistantToken)));
        Assert.Equal(HttpStatusCode.OK, (await Section(client, r.AssistantToken)).StatusCode);
    }

    [Fact]
    public async Task The_api_refuses_to_start_with_a_search_role_that_reads_the_whole_index()
    {
        await using var r = await RlsDatabase.NewAsync(server);

        var ex = Assert.ThrowsAny<Exception>(() => ApiTestHost.Start(r.AppConnection, new TestClock(ApiWorld.Start), r.Embedder,
            new ApiHostOptions { SearchRoleConnection = r.AppConnection }));
        Assert.Contains("index_writer", Flatten(ex));
        // A refusal, so the host prints the sentence and exits 2 instead of a stack trace.
        Assert.True(Chain(ex).Any(e => e is StartupRefusedException), "The refusal is not a StartupRefusedException: " + ex.GetType());
    }

    [Fact]
    public async Task A_form_posted_under_a_session_may_carry_its_anti_forgery_token_in_a_field()
    {
        await using var w = await ApiWorld.NewAsync(server);
        using var client = w.Host.Client();
        var alice = await Api.SessionAsync(client, "alice", ApiWorld.AlicePassword);

        async Task<HttpStatusCode> PostForm(string? field, bool header)
        {
            var values = new Dictionary<string, string> { ["query"] = "zeppelin" };
            if (field is not null) values[CallerMiddleware.AntiForgeryField] = field;
            var request = Api.Request(HttpMethod.Post, "/api/search", session: alice, antiForgery: header);
            request.Content = new FormUrlEncodedContent(values);
            using var response = await client.SendAsync(request);
            return response.StatusCode;
        }

        // The search route reads JSON, so a form that passes the caller check
        // reaches routing's 415; one that fails it stops at the 403 first.
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, await PostForm(alice.AntiForgeryToken, header: false));
        Assert.Equal(HttpStatusCode.Forbidden, await PostForm("not-the-token", header: false));
        Assert.Equal(HttpStatusCode.Forbidden, await PostForm(null, header: false));
        // The header, when present, is the one that counts.
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, await PostForm("not-the-token", header: true));
    }

    [Fact]
    public async Task A_page_for_people_sends_a_signed_out_browser_to_sign_in_and_refuses_an_agent()
    {
        await using var w = await ApiWorld.NewAsync(server);
        var sessions = new SessionStore(w.Db, w.Tenant, w.Clock);
        var alice = await sessions.StartAsync(w.Alice.Id);
        var page = CallerRequirement.PeopleOnly("/portal/sign-in");

        Assert.Equal((303, "/portal/sign-in?return=%2Fportal%2Fusers%3Fpage%3D2", false),
            await RunAsync(w, sessions, page, "GET", "/portal/users", "?page=2"));
        Assert.Equal((401, null, false), await RunAsync(w, sessions, page, "POST", "/portal/users"));
        Assert.Equal((401, null, false), await RunAsync(w, sessions, CallerRequirement.PeopleOnly(), "GET", "/portal/users"));
        Assert.Equal((403, null, false), await RunAsync(w, sessions, page, "GET", "/portal/users", bearer: w.AssistantToken));
        Assert.Equal((200, null, true), await RunAsync(w, sessions, page, "GET", "/portal/users", session: alice.Value));

        // An ended session is signed out too, and the redirect never leaves the site.
        await sessions.EndAsync(alice.Value);
        Assert.Equal(303, (await RunAsync(w, sessions, page, "GET", "/portal/users", session: alice.Value)).Status);
        Assert.Throws<ArgumentException>(() => CallerRequirement.PeopleOnly("https://elsewhere.example/sign-in"));
        Assert.Throws<ArgumentException>(() => CallerRequirement.PeopleOnly("//elsewhere.example/sign-in"));
    }

    /// <summary>One request through the caller middleware alone, to an endpoint with <paramref name="requirement"/>.</summary>
    private static async Task<(int Status, string? Location, bool Reached)> RunAsync(
        ApiWorld w, SessionStore sessions, CallerRequirement requirement, string method, string path, string query = "",
        string? bearer = null, string? session = null)
    {
        var reached = false;
        var http = new DefaultHttpContext();
        http.Request.Method = method;
        http.Request.Path = path;
        http.Request.QueryString = new QueryString(query);
        http.Response.Body = new MemoryStream();
        if (bearer is not null) http.Request.Headers.Authorization = "Bearer " + bearer;
        if (session is not null) http.Request.Headers.Cookie = $"{CallerMiddleware.SessionCookie}={session}";
        http.SetEndpoint(new Endpoint(_ => Task.CompletedTask, new EndpointMetadataCollection(requirement), "test page"));

        // The settings decide which sign-in adapters exist, so they are read
        // here and handed to the adapters, where the middleware now asks them.
        var settings = ApiSettings.From(new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());
        var middleware = new CallerMiddleware(
            _ => { reached = true; return Task.CompletedTask; },
            new AgentRateLimiter(w.Clock),
            new UnmappedPrincipalLog(NullLogger<UnmappedPrincipalLog>.Instance),
            NullLogger<CallerMiddleware>.Instance);
        await middleware.InvokeAsync(
            http, new IdentityStore(w.Db, w.Tenant, w.Clock), new SignInAdapters(settings, sessions),
            new PrincipalMapping(w.Db, w.Tenant, null));

        var status = reached ? 200 : http.Response.StatusCode;
        var location = http.Response.Headers.Location.Count > 0 ? http.Response.Headers.Location.ToString() : null;
        return (status, location, reached);
    }

    private static Task<HttpResponseMessage> Section(HttpClient client, string bearer) =>
        client.SendAsync(Api.Request(HttpMethod.Post, "/api/section", new { path = RlsDatabase.Plan }, bearer: bearer));

    private static string Flatten(Exception ex) =>
        ex.InnerException is null ? ex.Message : ex.Message + " | " + Flatten(ex.InnerException);

    private static IEnumerable<Exception> Chain(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException) yield return e;
    }
}

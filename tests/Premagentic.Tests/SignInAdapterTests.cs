using System.Net;
using Premagentic.Api.Callers;
using Premagentic.Core.Identity;
using Premagentic.Core.Identity.SignIn;
using Premagentic.Core.Storage;

namespace Premagentic.Tests;

/// <summary>
/// The sign-in adapter seam, and the two ways of signing in that Premagentic
/// ships expressed through it. What each adapter does is unchanged; the point
/// of these cases is the contract a third one will be written against.
/// <para>
/// Three answers, not two: no resolution at all means the request was not this
/// adapter's and the next is asked, a resolution with a name means that
/// account, and a resolution with no name means the request WAS this adapter's
/// and proved nobody, which refuses it. That last one is why an expired cookie
/// cannot quietly become an anonymous request.
/// </para>
/// Requires a running Docker daemon.
/// </summary>
public sealed class SignInAdapterTests(DatastoreTestDatabase server) : IClassFixture<DatastoreTestDatabase>
{
    private const string Cookie = "__Host-prem-session";
    private const string Header = "X-Signed-In-As";

    /// <summary>A request carrying whatever a case needs, and nothing else.</summary>
    private sealed class FakeRequest(
        IReadOnlyDictionary<string, string>? headers = null, IReadOnlyDictionary<string, string>? cookies = null)
        : ISignInRequest
    {
        public string Header(string name) =>
            headers is not null && headers.TryGetValue(name, out var value) ? value : "";

        public string? Cookie(string name) =>
            cookies is not null && cookies.TryGetValue(name, out var value) ? value : null;
    }

    private async Task<(PremagenticDatabase Db, IdentityStore Identity, SessionStore Sessions, User Dana)> NewWorldAsync()
    {
        var db = new PremagenticDatabase(await server.CreateDatabaseAsync());
        await db.InitializeAsync();
        var tenant = await db.EnsureTenantAsync("t", "T");
        var identity = new IdentityStore(db, tenant);
        var dana = await identity.CreateUserAsync("dana", "Dana", Role.Member);
        return (db, identity, new SessionStore(db, tenant), dana);
    }

    [Fact]
    public async Task The_trusted_header_claims_a_request_only_when_the_header_names_somebody()
    {
        var adapter = new TrustedHeaderSignInAdapter(Header);

        Assert.Null(await adapter.ResolveAsync(new FakeRequest()));
        Assert.Null(await adapter.ResolveAsync(new FakeRequest(headers: new Dictionary<string, string> { [Header] = "" })));
        Assert.Null(await adapter.ResolveAsync(new FakeRequest(headers: new Dictionary<string, string> { [Header] = "   " })));

        // The control: a header with a name in it is claimed, trimmed, and
        // carries no groups of its own.
        var resolved = await adapter.ResolveAsync(new FakeRequest(headers: new Dictionary<string, string> { [Header] = "  dana  " }));
        Assert.NotNull(resolved);
        Assert.Equal("dana", resolved.SignInName);
        Assert.Empty(resolved.ExternalGroups);

        // A name no account has is still a claim. The middleware refuses it;
        // the adapter does not get to decide who exists.
        var unknown = await adapter.ResolveAsync(new FakeRequest(headers: new Dictionary<string, string> { [Header] = "nobody-here" }));
        Assert.Equal("nobody-here", unknown?.SignInName);
    }

    [Fact]
    public async Task A_dead_session_claims_the_request_and_proves_nobody()
    {
        var (db, _, sessions, dana) = await NewWorldAsync();
        await using var _db = db;
        var adapter = new SessionSignInAdapter(sessions, Cookie);
        var issued = await sessions.StartAsync(dana.Id);

        // No cookie at all: not this adapter's request.
        Assert.Null(await adapter.ResolveAsync(new FakeRequest()));

        // A live session: the account it belongs to.
        var live = await adapter.ResolveAsync(new FakeRequest(cookies: new Dictionary<string, string> { [Cookie] = issued.Value }));
        Assert.Equal("dana", live?.SignInName);

        // A cookie that resolves to nothing is claimed all the same, with no
        // name, which is what refuses the request instead of passing it on.
        foreach (var dead in new[] { "not-a-session", issued.Value + "x" })
        {
            var refused = await adapter.ResolveAsync(new FakeRequest(cookies: new Dictionary<string, string> { [Cookie] = dead }));
            Assert.NotNull(refused);
            Assert.Null(refused.SignInName);
        }

        // And once the session ends, the cookie that worked a moment ago is the
        // same kind of claim: this adapter's, and nobody's.
        Assert.True(await sessions.EndAsync(issued.Value));
        var ended = await adapter.ResolveAsync(new FakeRequest(cookies: new Dictionary<string, string> { [Cookie] = issued.Value }));
        Assert.NotNull(ended);
        Assert.Null(ended.SignInName);
    }

    [Fact]
    public async Task An_adapter_never_makes_an_account_and_never_carries_a_role()
    {
        var (db, identity, sessions, dana) = await NewWorldAsync();
        await using var _db = db;
        var before = (await identity.ListUsersAsync()).Count;

        var header = new TrustedHeaderSignInAdapter(Header);
        await header.ResolveAsync(new FakeRequest(headers: new Dictionary<string, string> { [Header] = "someone-new" }));
        await new SessionSignInAdapter(sessions, Cookie).ResolveAsync(
            new FakeRequest(cookies: new Dictionary<string, string> { [Cookie] = "not-a-session" }));

        Assert.Equal(before, (await identity.ListUsersAsync()).Count);
        Assert.Null(await identity.FindUserByNameAsync("someone-new"));

        // The control: the account that does exist is reachable, and its role is
        // the one an administrator gave it, not anything an adapter said.
        Assert.Equal(Role.Member, (await identity.FindUserByNameAsync("dana"))!.Role);
        Assert.Equal(dana.Id, (await identity.FindUserByNameAsync("dana"))!.Id);
    }

    /// <summary>Claims every request as one account, standing in for an extension's adapter.</summary>
    private sealed class AlwaysSignInAdapter(string signInName) : ISignInAdapter
    {
        public string Name => "always";

        public Task<SignInResolution?> ResolveAsync(ISignInRequest request, CancellationToken ct = default) =>
            Task.FromResult<SignInResolution?>(new SignInResolution(signInName));
    }

    [Fact]
    public async Task A_session_that_has_ended_is_refused_and_never_handed_to_the_adapter_behind_it()
    {
        // An adapter that would sign everybody in as Alice, sitting where an
        // extension's would sit: after both built-ins.
        await using var world = await ApiWorld.NewAsync(
            server, options: new ApiHostOptions { ExtraSignInAdapters = [new AlwaysSignInAdapter("alice")] });
        using var client = world.Host.Client();

        // The control first, and it is the point of the case: with nothing on
        // the request, the adapter behind IS reached and IS believed. So this
        // host really would sign a request in as Alice if the session let it
        // through.
        using (var nothing = await client.GetAsync("/api/session"))
        {
            Assert.Equal(HttpStatusCode.OK, nothing.StatusCode);
            Assert.Contains("alice", await nothing.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }

        // A cookie that resolves to no session claims the request and proves
        // nobody, so it is refused here and never offered to the adapter behind.
        var dead = new HttpRequestMessage(HttpMethod.Get, "/api/session");
        dead.Headers.Add("Cookie", $"{Cookie}=not-a-session");
        using (var refused = await client.SendAsync(dead))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
            Assert.DoesNotContain("alice", await refused.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void The_two_built_in_adapters_are_named_for_the_settings_and_the_health_page()
    {
        Assert.Equal("trusted-header", new TrustedHeaderSignInAdapter(Header).Name);
        Assert.Equal("password", new SessionSignInAdapter(null!, Cookie).Name);

        // The trusted-header mode without a header to read is a configuration
        // mistake, not a mode that quietly reads nothing.
        Assert.Throws<ArgumentException>(() => new TrustedHeaderSignInAdapter(" "));
    }
}

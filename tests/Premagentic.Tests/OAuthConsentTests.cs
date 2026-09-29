using Premagentic.Core.Identity;

namespace Premagentic.Tests;

/// <summary>
/// The consent step: which errors are shown and which may go back to the
/// client; what the page says about where the answer goes; where the model
/// may be said to run; what re-approval does with the agent; and the bound.
/// Requires a running Docker daemon.
/// </summary>
public sealed class OAuthConsentTests(DatastoreTestDatabase server) : IClassFixture<DatastoreTestDatabase>
{
    private const string Port = "http://127.0.0.1:51004/callback";

    [Fact]
    public async Task An_unknown_or_ended_client_or_a_wrong_redirect_is_shown_and_never_sent_anywhere()
    {
        await using var w = await OAuthWorld.NewAsync(server);
        var (_, challenge) = OAuthWorld.Pkce();
        var client = await w.RegisterLoopbackAsync();

        async Task<AuthorizationCheck> Check(string id, string? redirect) => await w.Consent.CheckAsync(w.Params(id, redirect, challenge), default);

        var unknownMade = await Check("prem_cli_0123456789abcdef01234567", Port);
        Assert.Equal(OAuthConsent.RegistrationEnded, unknownMade.Message);
        var unknownUrl = await Check("https://app.example/client.json", Port);
        Assert.Equal(OAuthConsent.NotRegistered, unknownUrl.Message);
        var wrongRedirect = await Check(client, "http://127.0.0.1:51004/elsewhere");
        var otherHost = await Check(client, "http://localhost:51004/callback");

        foreach (var check in new[] { unknownMade, unknownUrl, wrongRedirect, otherHost })
        {
            Assert.Equal(AuthorizationCheckOutcome.ShowError, check.Outcome);
            Assert.Null(check.ReturnUrl);
        }

        await w.Clients.DisableAsync(w.Admin, client, default);
        Assert.Equal(AuthorizationCheckOutcome.ShowError, (await Check(client, Port)).Outcome);
        await w.Clients.RemoveAsync(w.Admin, client, default);
        Assert.Equal(OAuthConsent.RegistrationEnded, (await Check(client, Port)).Message);
    }

    [Fact]
    public async Task An_omitted_redirect_is_allowed_only_when_one_is_registered()
    {
        await using var w = await OAuthWorld.NewAsync(server);
        var (_, challenge) = OAuthWorld.Pkce();
        var one = await w.RegisterLoopbackAsync();
        var two = await w.Clients.AddAsync(w.Admin, null, "Two Doors", ["http://127.0.0.1/a", "http://127.0.0.1/b"], null, null, default);

        var single = await w.Consent.CheckAsync(w.Params(one, null, challenge), default);
        var ambiguous = await w.Consent.CheckAsync(w.Params(two, null, challenge), default);

        Assert.Equal(AuthorizationCheckOutcome.Valid, single.Outcome);
        Assert.Equal(OAuthWorld.Loopback, single.Request!.RedirectUri);
        Assert.Equal(AuthorizationCheckOutcome.ShowError, ambiguous.Outcome);
    }

    [Theory]
    [InlineData(null, "invalid_request")]
    [InlineData("plain", "invalid_request")]
    [InlineData("s256", "invalid_request")]
    public async Task Plain_and_missing_methods_are_refused(string? method, string error)
    {
        await using var w = await OAuthWorld.NewAsync(server);
        var (_, challenge) = OAuthWorld.Pkce();
        var client = await w.RegisterLoopbackAsync();
        var p = w.Params(client, Port, challenge);
        if (method is null) p.Remove("code_challenge_method"); else p["code_challenge_method"] = [method];

        var check = await w.Consent.CheckAsync(p, default);

        Assert.Equal(AuthorizationCheckOutcome.ReturnError, check.Outcome);
        Assert.Equal(error, check.Error);
        Assert.StartsWith(Port + "?", check.ReturnUrl);
        Assert.Equal("s1", OAuthWorld.Query(check.ReturnUrl!, "state"));
        Assert.Equal(OAuthWorld.PublicUrl, OAuthWorld.Query(check.ReturnUrl!, "iss"));
    }

    [Theory]
    [InlineData("https://prem.test:8443/mcp", true)]
    [InlineData("HTTPS://PREM.TEST:8443/mcp", true)]
    [InlineData("https://prem.test:8443", false)]
    [InlineData("https://other.test:8443/mcp", false)]
    [InlineData("https://prem.test:8443/MCP", false)]
    public async Task The_resource_must_name_this_server(string resource, bool valid)
    {
        await using var w = await OAuthWorld.NewAsync(server);
        var (_, challenge) = OAuthWorld.Pkce();
        var client = await w.RegisterLoopbackAsync();

        var check = await w.Consent.CheckAsync(w.Params(client, Port, challenge, resource), default);

        if (valid) Assert.Equal(AuthorizationCheckOutcome.Valid, check.Outcome);
        else Assert.Equal("invalid_target", check.Error);
    }

    [Fact]
    public async Task A_parameter_given_twice_is_refused()
    {
        await using var w = await OAuthWorld.NewAsync(server);
        var (_, challenge) = OAuthWorld.Pkce();
        var client = await w.RegisterLoopbackAsync();
        var twiceClient = w.Params(client, Port, challenge);
        twiceClient["client_id"] = [client, client];
        var twiceResource = w.Params(client, Port, challenge);
        twiceResource["resource"] = [w.Oauth.Resource, w.Oauth.Resource];

        Assert.Equal(AuthorizationCheckOutcome.ShowError, (await w.Consent.CheckAsync(twiceClient, default)).Outcome);
        Assert.Equal("invalid_request", (await w.Consent.CheckAsync(twiceResource, default)).Error);
    }

    [Fact]
    public async Task Authorize_refuses_a_dynamic_client_whose_uri_left_the_list()
    {
        await using var w = await OAuthWorld.NewAsync(server);
        var answer = await w.RegisterAsync($$"""{"client_name":"Hosted","redirect_uris":["{{OAuthWorld.Listed}}"]}""");
        var client = (string)answer.Body["client_id"]!;
        var (_, challenge) = OAuthWorld.Pkce();
        Assert.Equal(AuthorizationCheckOutcome.Valid, (await w.Consent.CheckAsync(w.Params(client, OAuthWorld.Listed, challenge), default)).Outcome);

        w.Oauth = new OAuthDeployment(OAuthWorld.PublicUrl, true, []);

        Assert.Equal(AuthorizationCheckOutcome.ShowError, (await w.Consent.CheckAsync(w.Params(client, OAuthWorld.Listed, challenge), default)).Outcome);
    }

    [Fact]
    public async Task Administrator_registration_still_accepts_an_https_redirect_and_never_a_private_scheme()
    {
        await using var w = await OAuthWorld.NewAsync(server);

        var id = await w.Clients.AddAsync(w.Admin, null, "Web Tool", ["https://tool.example/cb"], null, null, default);
        var refused = await Assert.ThrowsAsync<ArgumentException>(() =>
            w.Clients.AddAsync(w.Admin, null, "Cursor", ["cursor://anysphere.cursor-mcp/oauth/callback"], null, null, default));

        Assert.StartsWith(OAuthPrefixes.ClientId, id);
        Assert.Contains("private scheme", refused.Message);
        var (_, challenge) = OAuthWorld.Pkce();
        Assert.Equal(AuthorizationCheckOutcome.Valid, (await w.Consent.CheckAsync(w.Params(id, "https://tool.example/cb", challenge), default)).Outcome);
    }

    [Theory]
    [InlineData("http://127.0.0.1/callback", "http://127.0.0.1:51004/callback", true)]
    [InlineData("http://[::1]/callback", "http://[::1]:51004/callback", true)]
    [InlineData("http://localhost/callback", "http://localhost:51004/callback", true)]
    [InlineData("https://tool.example/cb", "https://tool.example/cb", false)]
    public async Task The_consent_view_says_when_the_answer_goes_to_a_program_on_this_computer(string registered, string requested, bool loopback)
    {
        await using var w = await OAuthWorld.NewAsync(server);
        var client = await w.Clients.AddAsync(w.Admin, null, "Tool", [registered], null, null, default);
        var (_, challenge) = OAuthWorld.Pkce();
        var request = (await w.Consent.CheckAsync(w.Params(client, requested, challenge), default)).Request!;

        var view = await w.Consent.ViewAsync(w.Alice.Id, request, default);

        Assert.Equal(loopback, view.LoopbackRedirect);
        Assert.Equal(loopback, view.LocalAllowed);
        Assert.Equal(RedirectUris.Origin(requested), view.RedirectOrigin);
        Assert.Equal(OAuthConsent.NewAgentName, view.AgentName);
    }

    [Fact]
    public async Task Local_is_refused_for_a_dynamic_client_with_an_https_redirect()
    {
        await using var w = await OAuthWorld.NewAsync(server);
        var client = (string)(await w.RegisterAsync($$"""{"client_name":"Hosted","redirect_uris":["{{OAuthWorld.Listed}}"]}""")).Body["client_id"]!;
        var (_, challenge) = OAuthWorld.Pkce();

        await Assert.ThrowsAsync<ArgumentException>(() => w.ApproveAsync(w.Alice, client, OAuthWorld.Listed, challenge, ModelLocation.Local));

        Assert.Equal(0, await w.RowsAsync("oauth.approve"));
    }

    [Fact]
    public async Task Local_is_refused_for_an_administrator_client_with_no_stated_location_and_a_non_loopback_redirect()
    {
        await using var w = await OAuthWorld.NewAsync(server);
        var client = await w.Clients.AddAsync(w.Admin, null, "Web Tool", ["https://tool.example/cb"], null, null, default);
        var (_, challenge) = OAuthWorld.Pkce();

        await Assert.ThrowsAsync<ArgumentException>(() => w.ApproveAsync(w.Alice, client, "https://tool.example/cb", challenge, ModelLocation.Local));
    }

    [Fact]
    public async Task A_client_stated_local_is_honored_and_a_re_approval_applies_it_again()
    {
        await using var w = await OAuthWorld.NewAsync(server);
        var client = await w.Clients.AddAsync(w.Admin, null, "Intranet Tool", ["https://intranet.example/cb"], ModelLocation.Local, null, default);

        var first = await w.ConnectAsync(w.Alice, client, "https://intranet.example/cb");
        var again = await w.ConnectAsync(w.Alice, client, "https://intranet.example/cb");

        Assert.Equal(ModelLocation.Local, (await w.Identity.FindAgentAsync(first.AgentId))!.ModelLocation);
        Assert.Equal(first.AgentId, again.AgentId);
    }

    [Fact]
    public async Task Re_approval_after_the_persons_own_revoke_gives_a_working_new_agent()
    {
        await using var w = await OAuthWorld.NewAsync(server);
        var client = await w.RegisterLoopbackAsync();
        var first = await w.ConnectAsync(w.Alice, client);
        await w.Grants.RevokeOwnAsync(w.Alice.Id, first.GrantId, default);

        var second = await w.ConnectAsync(w.Alice, client);

        Assert.NotEqual(first.AgentId, second.AgentId);
        Assert.Null(await w.CallAsync(first.Access));
        Assert.Equal(CallerStatus.Resolved, await w.CallAsync(second.Access));
    }

    [Fact]
    public async Task Re_approval_after_an_administrator_disables_the_agent_never_enables_it()
    {
        await using var w = await OAuthWorld.NewAsync(server);
        var client = await w.RegisterLoopbackAsync();
        var first = await w.ConnectAsync(w.Alice, client);
        await w.Identity.SetAgentDisabledAsync(first.AgentId, true);
        var added = await w.RowsAsync("agent.add");

        var second = await w.ConnectAsync(w.Alice, client);

        Assert.True((await w.Identity.FindAgentAsync(first.AgentId))!.Disabled);
        Assert.Equal(0, await w.RowsAsync("agent.enable"));
        Assert.Equal(added + 1, await w.RowsAsync("agent.add"));
        Assert.NotEqual(first.AgentId, second.AgentId);
    }

    [Fact]
    public async Task Re_approval_over_a_live_grant_keeps_the_agent_and_ends_the_old_tokens()
    {
        await using var w = await OAuthWorld.NewAsync(server);
        var client = await w.RegisterLoopbackAsync();
        var first = await w.ConnectAsync(w.Alice, client);

        var second = await w.ConnectAsync(w.Alice, client);

        Assert.Equal(first.AgentId, second.AgentId);
        Assert.Null(await w.CallAsync(first.Access));
        Assert.Equal(CallerStatus.Resolved, await w.CallAsync(second.Access));
        Assert.Equal(OAuthStore.ApprovedAgain, await w.ReasonAsync(first.GrantId));
    }

    [Fact]
    public async Task Re_approval_after_the_grant_expires_reuses_the_enabled_agent_and_the_count_does_not_grow()
    {
        await using var w = await OAuthWorld.NewAsync(server);
        var client = await w.RegisterLoopbackAsync();
        var first = await w.ConnectAsync(w.Alice, client);
        w.Clock.Now += TimeSpan.FromDays(OAuthSettings.GrantDaysDefault + 1);

        var second = await w.ConnectAsync(w.Alice, client);

        Assert.Equal(first.AgentId, second.AgentId);
        Assert.Equal(1, await new SelfServeAgents(w.Db, w.Tenant, w.Oauth, w.Clock).ConnectedCountAsync(w.Alice.Id, default));
    }

    [Fact]
    public async Task Hosted_to_local_on_a_kept_agent_gives_a_new_agent_and_leaves_the_old_location()
    {
        await using var w = await OAuthWorld.NewAsync(server);
        var client = await w.RegisterLoopbackAsync();
        var hosted = await w.ConnectAsync(w.Alice, client, location: ModelLocation.Hosted);

        var local = await w.ConnectAsync(w.Alice, client, location: ModelLocation.Local);

        Assert.NotEqual(hosted.AgentId, local.AgentId);
        var old = (await w.Identity.FindAgentAsync(hosted.AgentId))!;
        Assert.Equal(ModelLocation.Hosted, old.ModelLocation);
        Assert.True(old.Disabled);
        Assert.Equal(OAuthStore.ApprovedAgainElsewhere, await w.ReasonAsync(hosted.GrantId));
    }

    [Fact]
    public async Task Two_people_approving_one_client_get_two_distinct_server_made_agents()
    {
        await using var w = await OAuthWorld.NewAsync(server);
        var client = await w.RegisterLoopbackAsync("Evil Name That Is Not Used");

        var alice = await w.ConnectAsync(w.Alice, client);
        var bob = await w.ConnectAsync(w.Bob, client);

        var a = (await w.Identity.FindAgentAsync(alice.AgentId))!;
        var b = (await w.Identity.FindAgentAsync(bob.AgentId))!;
        Assert.NotEqual(a.Name, b.Name);
        Assert.Matches("^oauth-[0-9a-f]{24}$", a.Name);
        Assert.Equal(AgentOrigin.OAuth(client), a.Origin);
        Assert.Equal(AgentMode.ActsForUser, a.Mode);
    }

    [Fact]
    public async Task At_the_bound_re_approval_over_a_live_grant_keeps_the_agent_and_succeeds()
    {
        await using var w = await OAuthWorld.NewAsync(server, bound: 1);
        var client = await w.RegisterLoopbackAsync();
        var first = await w.ConnectAsync(w.Alice, client);
        var other = await w.RegisterLoopbackAsync("Other Tool");
        var (_, challenge) = OAuthWorld.Pkce();
        await Assert.ThrowsAsync<InvalidOperationException>(() => w.ApproveAsync(w.Alice, other, Port, challenge));

        var again = await w.ConnectAsync(w.Alice, client);

        Assert.Equal(first.AgentId, again.AgentId);
    }

    [Fact]
    public async Task A_person_at_the_bound_reconnects_after_a_re_enable()
    {
        await using var w = await OAuthWorld.NewAsync(server, bound: 2);
        var connect = new SelfServeAgents(w.Db, w.Tenant, w.Oauth, w.Clock);
        await connect.CreateAsync(w.Alice.Id, new SelfServeAgentRequest("alice-desk", "local", null, "coding tool"), default);
        var client = await w.RegisterLoopbackAsync();
        var before = await w.ConnectAsync(w.Alice, client);
        var third = await w.RegisterLoopbackAsync("Third Tool");
        await Assert.ThrowsAsync<InvalidOperationException>(() => w.ApproveAsync(w.Alice, third, Port, OAuthWorld.Pkce().Challenge));

        await w.Identity.SetUserDisabledAsync(w.Alice.Id, true);
        await w.Identity.SetUserDisabledAsync(w.Alice.Id, false);

        // Her connect-page token ended and her grant ended, so neither holds a
        // slot: the connect form takes a new assistant, and the client's kept
        // agent, still enabled since a person's disable disables no agent,
        // takes the other slot on its re-approval.
        Assert.Equal(0, await connect.ConnectedCountAsync(w.Alice.Id, default));
        await connect.CreateAsync(w.Alice.Id, new SelfServeAgentRequest("alice-laptop", "local", null, "coding tool"), default);
        var after = await w.ConnectAsync(w.Alice, client);
        Assert.Equal(before.AgentId, after.AgentId);
        Assert.Equal(CallerStatus.Resolved, await w.CallAsync(after.Access));
        Assert.Equal(2, await connect.ConnectedCountAsync(w.Alice.Id, default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => w.ApproveAsync(w.Alice, third, Port, OAuthWorld.Pkce().Challenge));
    }

    [Fact]
    public async Task A_pending_grant_holds_a_slot_only_while_its_code_can_be_exchanged()
    {
        await using var w = await OAuthWorld.NewAsync(server, bound: 1);
        var first = await w.RegisterLoopbackAsync("First");
        var second = await w.RegisterLoopbackAsync("Second");
        await w.ApproveAsync(w.Alice, first, Port, OAuthWorld.Pkce().Challenge);

        await Assert.ThrowsAsync<InvalidOperationException>(() => w.ApproveAsync(w.Alice, second, Port, OAuthWorld.Pkce().Challenge));
        var pending = Assert.Single(await w.Grants.ListOwnAsync(w.Alice.Id, default));
        Assert.Equal(OAuthGrantStatus.Pending, pending.Status);

        w.Clock.Now += TimeSpan.FromSeconds(OAuthSettings.CodeSecondsDefault + 1);
        await w.ApproveAsync(w.Alice, second, Port, OAuthWorld.Pkce().Challenge);
        Assert.Equal(OAuthGrantStatus.Expired, (await w.Grants.ListOwnAsync(w.Alice.Id, default)).Single(g => g.ClientId == first).Status);
    }

    [Fact]
    public async Task Self_service_off_refuses_an_approval()
    {
        await using var w = await OAuthWorld.NewAsync(server, bound: 0);
        var client = await w.RegisterLoopbackAsync();

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => w.ApproveAsync(w.Alice, client, Port, OAuthWorld.Pkce().Challenge));

        Assert.Equal(SelfServeAgents.SelfServiceOff, refused.Message);
    }

    [Fact]
    public async Task A_second_answer_to_the_same_request_is_refused_and_writes_nothing()
    {
        await using var w = await OAuthWorld.NewAsync(server);
        var client = await w.RegisterLoopbackAsync();
        var (_, challenge) = OAuthWorld.Pkce();
        await w.ApproveAsync(w.Alice, client, Port, challenge);
        var approvals = await w.RowsAsync("oauth.approve");

        var again = await Assert.ThrowsAsync<InvalidOperationException>(() => w.ApproveAsync(w.Alice, client, Port, challenge));

        Assert.Equal(OAuthConsent.AlreadyAnswered, again.Message);
        Assert.Equal(approvals, await w.RowsAsync("oauth.approve"));
    }

    [Fact]
    public async Task A_consent_posted_after_the_client_is_removed_is_refused()
    {
        await using var w = await OAuthWorld.NewAsync(server);
        var client = await w.RegisterLoopbackAsync();
        var (_, challenge) = OAuthWorld.Pkce();
        var shown = await w.Consent.CheckAsync(w.Params(client, Port, challenge), default);
        Assert.Equal(AuthorizationCheckOutcome.Valid, shown.Outcome);

        await w.Clients.RemoveAsync(w.Admin, client, default);

        await Assert.ThrowsAsync<InvalidOperationException>(() => w.ApproveAsync(w.Alice, client, Port, challenge));
        Assert.Equal(0, await w.RowsAsync("oauth.approve"));
    }

    [Fact]
    public async Task Deny_returns_access_denied_with_the_state_and_the_issuer_and_is_recorded()
    {
        await using var w = await OAuthWorld.NewAsync(server);
        var client = await w.RegisterLoopbackAsync();
        var (_, challenge) = OAuthWorld.Pkce();

        var url = await w.Consent.DenyAsync(w.Alice.Id, w.Params(client, Port, challenge), default);

        Assert.StartsWith(Port + "?", url);
        Assert.Equal("access_denied", OAuthWorld.Query(url, "error"));
        Assert.Equal("s1", OAuthWorld.Query(url, "state"));
        Assert.Equal(OAuthWorld.PublicUrl, OAuthWorld.Query(url, "iss"));
        Assert.Equal(1, await w.RowsAsync("oauth.deny"));
        Assert.Empty(await w.Grants.ListOwnAsync(w.Alice.Id, default));
    }

    [Fact]
    public async Task Every_authorization_response_carries_iss()
    {
        await using var w = await OAuthWorld.NewAsync(server);
        var client = await w.RegisterLoopbackAsync();
        var (_, challenge) = OAuthWorld.Pkce();

        var approved = await w.ApproveAsync(w.Alice, client, Port, challenge);

        Assert.Equal(OAuthWorld.PublicUrl, OAuthWorld.Query(approved, "iss"));
        Assert.Equal("s1", OAuthWorld.Query(approved, "state"));
        Assert.Matches("^[A-Za-z0-9_-]{43}$", OAuthWorld.Query(approved, "code"));
    }

    // Decision 10's default, until the owner picks: an administrator's revoke,
    // or disable of the assistant, ends what the person had, and the person may
    // connect the same assistant again with a new approval. Blocking that is a
    // later choice, not this build's.
    [Fact]
    public async Task A_person_may_connect_the_same_assistant_again_after_an_administrator_revokes_its_grant_or_disables_it()
    {
        await using var w = await OAuthWorld.NewAsync(server);
        var client = await w.RegisterLoopbackAsync();
        var first = await w.ConnectAsync(w.Alice, client);

        await w.Grants.RevokeAsync(w.Admin, first.GrantId, default);
        Assert.Null(await w.CallAsync(first.Access));
        var again = await w.ConnectAsync(w.Alice, client);
        Assert.NotEqual(first.GrantId, again.GrantId);
        Assert.Equal(CallerStatus.Resolved, await w.CallAsync(again.Access));

        await w.Identity.SetAgentDisabledAsync(again.AgentId, true);
        Assert.NotEqual(CallerStatus.Resolved, await w.CallAsync(again.Access));
        var third = await w.ConnectAsync(w.Alice, client);
        Assert.NotEqual(again.AgentId, third.AgentId);
        Assert.Equal(CallerStatus.Resolved, await w.CallAsync(third.Access));

        // The change record holds the administrator's revoke and every approval,
        // the first and both after it; the disable ended the second grant, and
        // that is recorded too.
        Assert.Equal(OAuthStore.RevokedByAdministrator, await w.ReasonAsync(first.GrantId));
        Assert.Equal(OAuthStore.AgentDisabled, await w.ReasonAsync(again.GrantId));
        Assert.Equal(2, await w.RowsAsync("oauth.grant.revoke"));
        Assert.Equal(3, await w.RowsAsync("oauth.approve"));
    }
}

using Premagentic.Core.Identity;
using Premagentic.Core.Security.Acl;

namespace Premagentic.Tests;

/// <summary>
/// How a grant ends and stays ended: a disable or a password change is final;
/// disabling or removing a client ends its grants; each grant shows where it
/// stands. Requires a running Docker daemon.
/// </summary>
public sealed class OAuthLifecycleTests(DatastoreTestDatabase server) : IClassFixture<DatastoreTestDatabase>
{
    private const string Port = "http://127.0.0.1:51004/callback";

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Disable_then_enable_before_any_call_still_refuses_at_mcp_and_at_refresh(bool user)
    {
        await using var w = await OAuthWorld.NewAsync(server);
        var client = await w.RegisterLoopbackAsync();
        var c = await w.ConnectAsync(w.Alice, client);

        if (user)
        {
            await w.Identity.SetUserDisabledAsync(w.Alice.Id, true);
            await w.Identity.SetUserDisabledAsync(w.Alice.Id, false);
        }
        else
        {
            await w.Identity.SetAgentDisabledAsync(c.AgentId, true);
            await w.Identity.SetAgentDisabledAsync(c.AgentId, false);
        }

        Assert.Null(await w.CallAsync(c.Access));
        Assert.Equal("""{"error":"invalid_grant"}""", System.Text.Json.JsonSerializer.Serialize((await w.RefreshAsync(client, c.Refresh)).Body));
        Assert.Equal(user ? OAuthStore.OwnerDisabledOrPasswordChanged : OAuthStore.AgentDisabled, await w.ReasonAsync(c.GrantId));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Disable_then_enable_before_the_code_exchange_still_refuses(bool user)
    {
        await using var w = await OAuthWorld.NewAsync(server);
        var client = await w.RegisterLoopbackAsync();
        var (verifier, challenge) = OAuthWorld.Pkce();
        var url = await w.ApproveAsync(w.Alice, client, Port, challenge);
        var agent = (await w.Grants.ListOwnAsync(w.Alice.Id, default)).Single().AgentId;

        if (user)
        {
            await w.Identity.SetUserDisabledAsync(w.Alice.Id, true);
            await w.Identity.SetUserDisabledAsync(w.Alice.Id, false);
        }
        else
        {
            await w.Identity.SetAgentDisabledAsync(agent, true);
            await w.Identity.SetAgentDisabledAsync(agent, false);
        }

        Assert.Equal(400, (await w.ExchangeAsync(client, OAuthWorld.Query(url, "code"), verifier)).StatusCode);
    }

    [Fact]
    public async Task A_grant_approved_after_the_re_enable_works()
    {
        await using var w = await OAuthWorld.NewAsync(server);
        var client = await w.RegisterLoopbackAsync();
        await w.ConnectAsync(w.Alice, client);
        await w.Identity.SetUserDisabledAsync(w.Alice.Id, true);
        await w.Identity.SetUserDisabledAsync(w.Alice.Id, false);

        var again = await w.ConnectAsync(w.Alice, client);

        Assert.Equal(CallerStatus.Resolved, await w.CallAsync(again.Access));
    }

    [Fact]
    public async Task Disabling_the_owner_stops_the_next_call_and_the_reason_says_so()
    {
        await using var w = await OAuthWorld.NewAsync(server);
        var client = await w.RegisterLoopbackAsync();
        var c = await w.ConnectAsync(w.Alice, client);

        await w.Identity.SetUserDisabledAsync(w.Alice.Id, true);

        Assert.Null(await w.CallAsync(c.Access));
        Assert.Equal(OAuthStore.OwnerDisabled, await w.ReasonAsync(c.GrantId));
        Assert.Equal(1, await w.RowsAsync("oauth.grant.revoke"));
    }

    [Fact]
    public async Task An_oauth_grant_ends_at_a_password_change_at_mcp_refresh_and_the_code_exchange()
    {
        await using var w = await OAuthWorld.NewAsync(server);
        var client = await w.RegisterLoopbackAsync();
        var c = await w.ConnectAsync(w.Alice, client);
        var other = await w.RegisterLoopbackAsync("Other");
        var (verifier, challenge) = OAuthWorld.Pkce();
        var pending = await w.ApproveAsync(w.Alice, other, Port, challenge);

        await w.Identity.SetPasswordHashAsync(w.Alice.Id, new PasswordHasher().Hash("alice picks a new long password"));

        Assert.Null(await w.CallAsync(c.Access));
        Assert.Equal(400, (await w.RefreshAsync(client, c.Refresh)).StatusCode);
        Assert.Equal(400, (await w.ExchangeAsync(other, OAuthWorld.Query(pending, "code"), verifier)).StatusCode);
        Assert.Equal(OAuthStore.OwnerDisabledOrPasswordChanged, await w.ReasonAsync(c.GrantId));
        Assert.Equal(OAuthGrantStatus.EndedByDisable, (await w.Grants.ListOwnAsync(w.Alice.Id, default)).Single(g => g.GrantId == c.GrantId).Status);
    }

    [Fact]
    public async Task Removing_a_client_stops_its_tokens_at_the_next_call()
    {
        await using var w = await OAuthWorld.NewAsync(server);
        var client = await w.RegisterLoopbackAsync();
        var c = await w.ConnectAsync(w.Alice, client);

        await w.Clients.RemoveAsync(w.Admin, client, default);

        Assert.Null(await w.CallAsync(c.Access));
        Assert.Equal(OAuthStore.ClientRemovedReason, await w.ReasonAsync(c.GrantId));
        Assert.True((await w.Identity.FindAgentAsync(c.AgentId))!.Disabled);
        Assert.Equal(1, await w.RowsAsync("oauth.client.remove"));
        Assert.Equal(OAuthGrantStatus.EndedByClient, (await w.Grants.ListOwnAsync(w.Alice.Id, default)).Single().Status);
    }

    [Fact]
    public async Task Disabling_a_client_stops_refresh_and_code_exchange()
    {
        await using var w = await OAuthWorld.NewAsync(server);
        var client = await w.RegisterLoopbackAsync();
        var c = await w.ConnectAsync(w.Alice, client);
        var (verifier, challenge) = OAuthWorld.Pkce();
        var pending = await w.ApproveAsync(w.Bob, client, Port, challenge);

        await w.Clients.DisableAsync(w.Admin, client, default);

        Assert.Equal("invalid_client", (await w.RefreshAsync(client, c.Refresh)).Body["error"]);
        Assert.Equal("invalid_client", (await w.ExchangeAsync(client, OAuthWorld.Query(pending, "code"), verifier)).Body["error"]);
        Assert.Equal(OAuthStore.ClientDisabledReason, await w.ReasonAsync(c.GrantId));
    }

    [Fact]
    public async Task The_liveness_rules_alone_refuse_a_grant_of_a_disabled_client()
    {
        await using var w = await OAuthWorld.NewAsync(server);
        var client = await w.RegisterLoopbackAsync();
        var c = await w.ConnectAsync(w.Alice, client);

        // Past the store, as an approval racing a disable would leave it: the
        // client is off and nothing revoked its grant.
        await using (var cmd = w.Db.DataSource.CreateCommand("UPDATE prem_config.oauth_client SET disabled = true WHERE id = @id"))
        {
            cmd.Parameters.AddWithValue("id", client);
            await cmd.ExecuteNonQueryAsync();
        }

        Assert.Null(await w.CallAsync(c.Access));
        Assert.Equal(OAuthStore.ClientDisabledReason, await w.ReasonAsync(c.GrantId));
    }

    [Fact]
    public async Task Reenabling_a_client_does_not_revive_its_grants()
    {
        await using var w = await OAuthWorld.NewAsync(server);
        var client = await w.RegisterLoopbackAsync();
        var c = await w.ConnectAsync(w.Alice, client);
        await w.Clients.DisableAsync(w.Admin, client, default);

        await w.Clients.EnableAsync(w.Admin, client, default);

        Assert.Null(await w.CallAsync(c.Access));
        Assert.Equal(400, (await w.RefreshAsync(client, c.Refresh)).StatusCode);
        Assert.Equal(1, await w.RowsAsync("oauth.client.enable"));
        // A fresh approval works again.
        Assert.Equal(CallerStatus.Resolved, await w.CallAsync((await w.ConnectAsync(w.Alice, client)).Access));
    }

    [Fact]
    public async Task Each_grant_shows_where_it_stands_and_the_live_filter_shows_only_live_ones()
    {
        await using var w = await OAuthWorld.NewAsync(server);
        var one = await w.RegisterLoopbackAsync("One");
        var two = await w.RegisterLoopbackAsync("Two");
        var three = await w.RegisterLoopbackAsync("Three");
        var live = await w.ConnectAsync(w.Alice, one);
        var revoked = await w.ConnectAsync(w.Bob, one);
        await w.Grants.RevokeAsync(w.Admin, revoked.GrantId, default);
        await w.ApproveAsync(w.Alice, two, Port, OAuthWorld.Pkce().Challenge);
        var ended = await w.ConnectAsync(w.Bob, three);
        await w.Clients.DisableAsync(w.Admin, three, default);

        var all = (await w.Grants.ListAllAsync(OAuthGrantFilter.All, default)).ToDictionary(g => g.GrantId, g => g.Status);
        var liveOnly = await w.Grants.ListAllAsync(OAuthGrantFilter.Live, default);

        Assert.Equal(OAuthGrantStatus.Live, all[live.GrantId]);
        Assert.Equal(OAuthGrantStatus.Revoked, all[revoked.GrantId]);
        Assert.Equal(OAuthGrantStatus.EndedByClient, all[ended.GrantId]);
        Assert.Contains(OAuthGrantStatus.Pending, all.Values);
        Assert.Equal(new[] { live.GrantId }, liveOnly.Select(g => g.GrantId));
    }

    [Fact]
    public async Task A_removed_group_membership_changes_what_the_next_call_holds()
    {
        await using var w = await OAuthWorld.NewAsync(server);
        var staff = await w.Identity.CreateGroupAsync("Staff");
        await w.Identity.AddMemberAsync(staff.Id, w.Alice.Id);
        var client = await w.RegisterLoopbackAsync();
        var c = await w.ConnectAsync(w.Alice, client);
        var group = Principal.Group(CallerResolver.IdText(staff.Id));

        async Task<bool> HoldsStaffAsync()
        {
            var (access, _) = await w.Tokens.CheckAccessAsync(c.Access, default);
            return (await CallerAccess.ResolveOAuthAsync(w.Identity, access!)).Resolved.Principals.Contains(group);
        }

        Assert.True(await HoldsStaffAsync());
        await w.Identity.RemoveMemberAsync(staff.Id, w.Alice.Id);
        Assert.False(await HoldsStaffAsync());
    }

    [Fact]
    public async Task The_configuration_export_leaves_the_flows_tables_out_even_with_secrets()
    {
        await using var w = await OAuthWorld.NewAsync(server);
        var client = await w.RegisterLoopbackAsync();
        await w.ConnectAsync(w.Alice, client);

        using var output = new MemoryStream();
        await Premagentic.Core.Admin.ConfigExport.WriteAsync(w.Db, w.Tenant, includeSecrets: true, w.Clock.Now, output);
        var plain = System.Text.Encoding.UTF8.GetString(output.ToArray());

        // The export is whole: the grant's agent is in it, by its origin.
        Assert.Contains($"oauth:{client}", plain);
        Assert.DoesNotContain("oauth_", plain);
    }

    [Fact]
    public async Task Revoke_all_ends_every_grant_and_disables_each_agent()
    {
        await using var w = await OAuthWorld.NewAsync(server);
        var client = await w.RegisterLoopbackAsync();
        var a = await w.ConnectAsync(w.Alice, client);
        var b = await w.ConnectAsync(w.Bob, client);

        Assert.Equal(2, await w.Grants.RevokeManyAsync(w.Admin, null, default));

        Assert.Null(await w.CallAsync(a.Access));
        Assert.Null(await w.CallAsync(b.Access));
        Assert.Equal(0, await w.Grants.RevokeManyAsync(w.Admin, null, default));
        Assert.True((await w.Identity.FindAgentAsync(a.AgentId))!.Disabled);
    }
}

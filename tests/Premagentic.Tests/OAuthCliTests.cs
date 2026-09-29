using System.Globalization;
using Premagentic.Cli.Admin;
using Premagentic.Core.Identity;

namespace Premagentic.Tests;

/// <summary>
/// <c>prem oauth</c> at the command line, with the flow off as the command line
/// finds it: an administrator's client is printed with its id and listed, the
/// grant list says how often each grant was refreshed and when last, and a
/// revoke says how many grants it ended. Requires a running Docker daemon.
/// </summary>
[Collection(ConsoleCollection.Name)]
public sealed class OAuthCliTests(DatastoreTestDatabase server) : IClassFixture<DatastoreTestDatabase>
{
    private static Task<(int Exit, string Out, string Err)> RunAsync(OAuthWorld w, params string[] args) =>
        ConsoleCapture.RunAsync(() => OAuthCommands.RunAsync(args, w.Db, w.Tenant));

    private static string[] Lines(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    [Fact]
    public async Task An_administrator_adds_a_client_and_the_list_shows_it_with_what_was_stated()
    {
        await using var w = await OAuthWorld.NewAsync(server);

        var add = await RunAsync(w, "oauth", "clients", "add", "Desk Tool", "--redirect", "http://127.0.0.1/callback",
            "--model", "hosted", "--vendor", "A Vendor");
        var list = await RunAsync(w, "oauth", "clients", "list");

        Assert.True(add.Exit == 0, add.Err);
        var id = Lines(add.Out).Last();
        Assert.Matches("^prem_cli_[0-9a-f]{24}$", id);
        var line = Assert.Single(Lines(list.Out), l => l.StartsWith(id, StringComparison.Ordinal));
        Assert.Contains("Desk Tool", line);
        Assert.Contains("enabled", line);
        Assert.Contains("never approved", line);
        Assert.Contains("registered by cli:", line);
        Assert.Contains("model: hosted (A Vendor)", line);
        Assert.Contains("redirects: http://127.0.0.1/callback", list.Out);
    }

    [Fact]
    public async Task A_hosted_client_with_no_vendor_is_refused_and_nothing_is_written()
    {
        await using var w = await OAuthWorld.NewAsync(server);

        var add = await RunAsync(w, "oauth", "clients", "add", "Desk Tool", "--redirect", "http://127.0.0.1/callback", "--model", "hosted");

        Assert.Equal(1, add.Exit);
        Assert.Contains("Name who runs the hosted model", add.Err);
        Assert.Equal(0, await w.RowsAsync("oauth.client.add"));
        Assert.Empty(await w.Clients.ListAsync(default));
    }

    [Fact]
    public async Task The_grant_list_says_how_often_each_grant_was_refreshed_and_when_last()
    {
        await using var w = await OAuthWorld.NewAsync(server);
        var client = await w.RegisterLoopbackAsync();
        var c = await w.ConnectAsync(w.Alice, client);
        w.Clock.Now += TimeSpan.FromMinutes(5);
        var first = await w.RefreshAsync(client, c.Refresh);
        w.Clock.Now += TimeSpan.FromMinutes(5);
        var second = await w.RefreshAsync(client, (string)first.Body["refresh_token"]!);
        Assert.Equal(200, second.StatusCode);

        var list = await RunAsync(w, "oauth", "grants", "list", "--all");

        var line = Assert.Single(Lines(list.Out), l => l.StartsWith(c.GrantId, StringComparison.Ordinal));
        Assert.Contains($"refreshed 2 time(s), last {w.Clock.Now.ToString("u", CultureInfo.InvariantCulture)}", line);
        Assert.Contains("Desk Tool", line);
        Assert.Contains("for alice", line);
    }

    [Fact]
    public async Task A_revoke_by_person_or_of_all_says_how_many_it_ended()
    {
        await using var w = await OAuthWorld.NewAsync(server);
        var one = await w.RegisterLoopbackAsync("One");
        var two = await w.RegisterLoopbackAsync("Two");
        var a1 = await w.ConnectAsync(w.Alice, one);
        await w.ConnectAsync(w.Alice, two);
        var b = await w.ConnectAsync(w.Bob, one);

        var alice = await RunAsync(w, "oauth", "grants", "revoke", "--user", "alice");
        Assert.Contains("2 grant(s) of alice revoked", alice.Out);
        Assert.Equal(CallerStatus.Resolved, await w.CallAsync(b.Access));

        var all = await RunAsync(w, "oauth", "grants", "revoke", "--all");
        Assert.Contains("1 grant(s) revoked", all.Out);
        Assert.Null(await w.CallAsync(b.Access));

        var again = await RunAsync(w, "oauth", "grants", "revoke", "--all");
        Assert.Contains("No grant was left to revoke; nothing changed.", again.Out);
        var twice = await RunAsync(w, "oauth", "grants", "revoke", a1.GrantId);
        Assert.Contains($"Grant {a1.GrantId} was revoked already ({OAuthStore.RevokedByAdministrator})", twice.Out);
        var neither = await RunAsync(w, "oauth", "grants", "revoke");
        Assert.Equal(1, neither.Exit);
        Assert.Contains("Give one grant id, --user <sign-in-name>, or --all.", neither.Err);
        Assert.Equal(3, await w.RowsAsync("oauth.grant.revoke"));
    }
}

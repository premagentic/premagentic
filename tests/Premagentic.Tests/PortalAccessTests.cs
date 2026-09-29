using System.Net;
using Premagentic.Portal.Html;

namespace Premagentic.Tests;

/// <summary>
/// Who may open what, over every portal route, through the real API host and
/// its caller middleware: signed out is refused, a member sees only search, an
/// auditor sees everything and changes nothing, a change needs its token and
/// the portal's own origin, and every change is in the change record with the
/// signed-in user. Requires a running Docker daemon.
/// </summary>
public sealed class PortalAccessTests(DatastoreTestDatabase server) : IClassFixture<DatastoreTestDatabase>
{
    private static readonly string[] EveryPage = ["/portal/search", .. PortalRoutes.ReaderPages];

    [Fact]
    public async Task Signed_out_every_page_and_every_change_is_refused_and_sign_in_is_open()
    {
        await using var p = await PortalWorld.NewAsync(server);

        // A browser that is signed out is sent to the sign-in page, which brings it back where it was going.
        foreach (var path in EveryPage.Concat(PortalRoutes.ReaderDownloads).Concat(PortalRoutes.AddOnAddresses).Concat(PortalRoutes.AdministratorPages))
        {
            using var response = await p.GetAsync(path);
            Assert.True(response.StatusCode == HttpStatusCode.SeeOther, $"GET {path} signed out gave {(int)response.StatusCode}");
            Assert.Equal("/portal/sign-in?return=" + Uri.EscapeDataString(path), response.Headers.Location!.OriginalString);
            Assert.DoesNotContain("<main>", await response.Content.ReadAsStringAsync());
        }
        foreach (var (path, fields) in PortalRoutes.Changes)
        {
            using var response = await p.PostAsync(path, session: null, fields);
            Assert.True(response.StatusCode == HttpStatusCode.Unauthorized, $"POST {path} signed out gave {(int)response.StatusCode}");
        }

        Assert.Equal(0, await p.ScalarAsync("SELECT count(*) FROM prem_config.admin_event"));

        // The control: the sign-in page and the assets need nobody.
        foreach (var path in new[] { "/portal/sign-in", "/portal/assets/portal.css", "/portal/assets/portal.js" })
        {
            using var open = await p.GetAsync(path);
            Assert.Equal(HttpStatusCode.OK, open.StatusCode);
        }
    }

    [Fact]
    public async Task An_agent_token_opens_no_portal_page_and_changes_nothing()
    {
        await using var p = await PortalWorld.NewAsync(server);

        foreach (var path in EveryPage.Concat(PortalRoutes.ReaderDownloads).Concat(PortalRoutes.AddOnAddresses).Concat(PortalRoutes.AdministratorPages))
        {
            using var response = await p.Client.SendAsync(Api.Request(HttpMethod.Get, path, bearer: p.World.AssistantToken));
            Assert.True(response.StatusCode == HttpStatusCode.Forbidden, $"GET {path} with an agent token gave {(int)response.StatusCode}");
        }
        foreach (var (path, fields) in PortalRoutes.Changes)
        {
            var request = Api.Request(HttpMethod.Post, path, bearer: p.World.BotToken);
            request.Content = new FormUrlEncodedContent(fields.Select(f => new KeyValuePair<string, string>(f.Item1, f.Item2)));
            request.Headers.Add("Origin", PortalWorld.Origin);
            using var response = await p.Client.SendAsync(request);
            Assert.True(response.StatusCode == HttpStatusCode.Forbidden, $"POST {path} with an agent token gave {(int)response.StatusCode}");
        }
        Assert.Equal(0, await p.ScalarAsync("SELECT count(*) FROM prem_config.admin_event"));
    }

    [Fact]
    public async Task A_member_sees_only_the_search_page_and_changes_nothing()
    {
        await using var p = await PortalWorld.NewAsync(server);

        var search = await p.TextAsync(await p.GetAsync("/portal/search", p.Member));
        Assert.Contains("<h1>Search</h1>", search);
        Assert.DoesNotContain("/portal/users", search);

        foreach (var path in PortalRoutes.ReaderPages.Concat(PortalRoutes.ReaderDownloads).Concat(PortalRoutes.AddOnAddresses).Concat(PortalRoutes.AdministratorPages))
        {
            using var response = await p.GetAsync(path, p.Member);
            Assert.True(response.StatusCode == HttpStatusCode.Forbidden, $"GET {path} as a member gave {(int)response.StatusCode}");
        }
        foreach (var (path, fields) in PortalRoutes.Changes)
        {
            using var response = await p.PostAsync(path, p.Member, fields);
            Assert.True(response.StatusCode == HttpStatusCode.Forbidden, $"POST {path} as a member gave {(int)response.StatusCode}");
        }
        Assert.Equal(0, await p.ScalarAsync("SELECT count(*) FROM prem_config.admin_event"));
    }

    [Fact]
    public async Task An_auditor_sees_every_page_and_changes_nothing()
    {
        await using var p = await PortalWorld.NewAsync(server);

        foreach (var path in EveryPage)
        {
            var html = await p.TextAsync(await p.GetAsync(path, p.Auditor));
            Assert.Contains("<main>", html);
            // An auditor is given no form that could change anything.
            Assert.DoesNotContain("name=\"prem_antiforgery\"", html);
        }
        foreach (var path in PortalRoutes.ReaderDownloads)
        {
            using var response = await p.GetAsync(path, p.Auditor);
            Assert.True(response.StatusCode == HttpStatusCode.OK, $"GET {path} as an auditor gave {(int)response.StatusCode}");
        }
        foreach (var path in PortalRoutes.AddOnAddresses)
        {
            using var response = await p.GetAsync(path, p.Auditor);
            Assert.True(response.StatusCode == HttpStatusCode.NotFound, $"GET {path} as an auditor gave {(int)response.StatusCode}");
        }
        foreach (var path in PortalRoutes.AdministratorPages)
        {
            using var response = await p.GetAsync(path, p.Auditor);
            Assert.True(response.StatusCode == HttpStatusCode.Forbidden, $"GET {path} as an auditor gave {(int)response.StatusCode}");
        }

        foreach (var (path, fields) in PortalRoutes.Changes)
        {
            using var response = await p.PostAsync(path, p.Auditor, fields);
            Assert.True(response.StatusCode == HttpStatusCode.Forbidden, $"POST {path} as an auditor gave {(int)response.StatusCode}");
        }
        Assert.Equal(0, await p.ScalarAsync("SELECT count(*) FROM prem_config.admin_event"));
        Assert.Equal(0, await p.ScalarAsync("SELECT count(*) FROM prem_config.app_group WHERE name = 'Visitors'"));
        Assert.Equal(0, await p.ScalarAsync("SELECT count(*) FROM prem_config.app_user WHERE sign_in_name = 'bob' AND disabled"));
    }

    /// <summary>
    /// View as shows the passages another caller would be served, so an
    /// auditor, who sees every document by path and never its text, is refused
    /// it with the page every administrator's page gives, and nothing is
    /// searched or recorded; an administrator is served.
    /// </summary>
    [Fact]
    public async Task View_as_is_an_administrators_and_an_auditor_is_refused_it()
    {
        await using var p = await PortalWorld.NewAsync(server);
        const string asBob = "/portal/permissions/view-as?who=user%3Abob&q=zeppelin";
        var searchedBefore = await p.ScalarAsync("SELECT count(*) FROM prem_config.retrieval_event");

        using (var auditor = await p.GetAsync(asBob, p.Auditor))
        {
            Assert.Equal(HttpStatusCode.Forbidden, auditor.StatusCode);
            var said = await auditor.Content.ReadAsStringAsync();
            Assert.Contains("This page is for administrators.", said);
            Assert.DoesNotContain(ApiWorld.Handbook, said);
        }
        Assert.Equal(searchedBefore, await p.ScalarAsync("SELECT count(*) FROM prem_config.retrieval_event"));
        Assert.DoesNotContain("/portal/permissions/view-as", await p.TextAsync(await p.GetAsync("/portal/permissions", p.Auditor)));

        // The control: the administrator is served, and offered the tool.
        using (var administrator = await p.GetAsync(asBob, p.Admin))
        {
            Assert.Equal(HttpStatusCode.OK, administrator.StatusCode);
            Assert.Contains(ApiWorld.Handbook, await administrator.Content.ReadAsStringAsync());
        }
        Assert.Contains("/portal/permissions/view-as", await p.TextAsync(await p.GetAsync("/portal/permissions", p.Admin)));
    }

    [Fact]
    public async Task A_change_without_its_token_or_from_another_origin_is_refused()
    {
        await using var p = await PortalWorld.NewAsync(server);
        (string, string)[] visitors = [("name", "Visitors")];

        Assert.True(await PortalRoutes.IsRefusedAsync(await p.PostAsync("/portal/groups", p.Admin, visitors, tokenField: false, headerToken: false)));
        Assert.True(await PortalRoutes.IsRefusedAsync(await p.PostAsync("/portal/groups", p.Admin, visitors, origin: "https://elsewhere.example")));
        Assert.True(await PortalRoutes.IsRefusedAsync(await p.PostAsync("/portal/groups", p.Admin, visitors, origin: null)));
        Assert.Equal(0, await p.ScalarAsync("SELECT count(*) FROM prem_config.app_group WHERE name = 'Visitors'"));

        // The control: the same change with its token, from the portal, is made.
        using var made = await p.PostAsync("/portal/groups", p.Admin, visitors);
        Assert.Equal(HttpStatusCode.Redirect, made.StatusCode);
        Assert.Equal(1, await p.ScalarAsync("SELECT count(*) FROM prem_config.app_group WHERE name = 'Visitors'"));
    }

    // Under the portal's Referrer-Policy: no-referrer a browser posts every form
    // with Origin: null and says where it came from in Sec-Fetch-Site.

    [Fact]
    public async Task A_browser_form_with_a_null_origin_from_this_site_is_made()
    {
        await using var p = await PortalWorld.NewAsync(server);

        using var made = await NullOriginPostAsync(p, "same-origin");

        Assert.Equal(HttpStatusCode.Redirect, made.StatusCode);
        Assert.Equal(1, await p.ScalarAsync("SELECT count(*) FROM prem_config.app_group WHERE name = 'Visitors'"));
    }

    [Fact]
    public async Task A_form_with_a_null_origin_from_another_site_is_refused()
    {
        await using var p = await PortalWorld.NewAsync(server);

        Assert.True(await PortalRoutes.IsRefusedAsync(await NullOriginPostAsync(p, "cross-site")));
        Assert.Equal(0, await p.ScalarAsync("SELECT count(*) FROM prem_config.app_group WHERE name = 'Visitors'"));
    }

    [Fact]
    public async Task A_form_with_a_null_origin_that_says_nothing_else_is_refused()
    {
        await using var p = await PortalWorld.NewAsync(server);

        Assert.True(await PortalRoutes.IsRefusedAsync(await NullOriginPostAsync(p, fetchSite: null)));
        Assert.Equal(0, await p.ScalarAsync("SELECT count(*) FROM prem_config.app_group WHERE name = 'Visitors'"));
    }

    /// <summary>The administrator adding the group Visitors with its token, as a browser posts it: Origin: null.</summary>
    private static Task<HttpResponseMessage> NullOriginPostAsync(PortalWorld p, string? fetchSite)
    {
        var request = Api.Request(HttpMethod.Post, "/portal/groups", session: p.Admin, antiForgery: false);
        request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["name"] = "Visitors",
            ["prem_antiforgery"] = p.Admin.AntiForgeryToken,
        });
        request.Headers.Add("Origin", "null");
        if (fetchSite is not null) request.Headers.Add("Sec-Fetch-Site", fetchSite);
        return p.Client.SendAsync(request);
    }

    [Fact]
    public async Task Every_change_lands_in_the_change_record_with_the_signed_in_user()
    {
        await using var p = await PortalWorld.NewAsync(server);
        var folder = SourcesTests.Folder(("a.md", "# A\n\n## Body\nText.\n"));
        var carol = p.World.Carol.Id;

        foreach (var (path, fields) in PortalRoutes.Changes)
        {
            var sent = path == "/portal/sources" ? fields.Select(f => f.Item1 == "folder" ? ("folder", folder) : f).ToArray() : fields;
            var before = await p.ScalarAsync("SELECT count(*) FROM prem_config.admin_event");
            using (var response = await p.PostAsync(path, p.Admin, sent))
            {
                var location = response.Headers.Location?.OriginalString ?? "";
                Assert.True(response.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.OK, $"POST {path} gave {(int)response.StatusCode}");
                Assert.False(location.Contains("error="), $"POST {path} was refused: {Uri.UnescapeDataString(location)}");
            }
            Assert.True(await p.ScalarAsync("SELECT count(*) FROM prem_config.admin_event") > before, $"POST {path} recorded nothing");
            Assert.Equal(0, await p.ScalarAsync(
                $"SELECT count(*) FROM prem_config.admin_event WHERE id > {before} AND (actor_surface <> 'portal' OR actor_user_id IS DISTINCT FROM '{carol}')"));
        }
    }

    [Fact]
    public async Task Pages_load_everything_from_their_own_origin_under_a_same_origin_policy()
    {
        await using var p = await PortalWorld.NewAsync(server);

        foreach (var path in EveryPage.Append("/portal/sign-in"))
        {
            using var response = await p.GetAsync(path, p.Admin);
            Assert.Equal(Layout.ContentSecurityPolicy, response.Headers.GetValues("Content-Security-Policy").Single());
            Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
            var html = await response.Content.ReadAsStringAsync();
            var references = PortalWorld.References(html).ToArray();
            Assert.Contains("/portal/assets/portal.css", references);
            Assert.All(references, r => Assert.True(PortalWorld.StaysHere(r) || PortalWorld.IsSourceLink(r), $"{path} points at {r}"));
            // No inline script or style for the policy to have to allow.
            Assert.DoesNotContain("<style", html);
            Assert.Single(System.Text.RegularExpressions.Regex.Matches(html, "<script"));
            Assert.DoesNotMatch(@"<[^>]*\son[a-z]+\s*=", html);
        }

        foreach (var asset in new[] { "/portal/assets/portal.css", "/portal/assets/portal.js" })
        {
            var text = await p.TextAsync(await p.GetAsync(asset));
            Assert.DoesNotContain("http://", text);
            Assert.DoesNotContain("https://", text);
            Assert.DoesNotContain("@import", text);
        }
    }
}

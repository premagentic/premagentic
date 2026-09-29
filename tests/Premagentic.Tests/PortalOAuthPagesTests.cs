using System.Net;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.RegularExpressions;
using Premagentic.Core.Admin;
using Premagentic.Core.Identity;
using Premagentic.Portal.Pages;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Premagentic.Tests;

/// <summary>
/// The authorization flow's portal pages, against stand-ins for the flow's
/// services: the consent page (the client's own text isolated, the two forms,
/// an answer that is a page with one link back and never a redirect, the errors
/// with and without a way back), the grants and clients pages, and the
/// navigation that shows them only while the flow is on. Every name and
/// address is invented. The page tests require a running Docker daemon.
/// </summary>
public sealed class PortalOAuthPagesTests(DatastoreTestDatabase server) : IClassFixture<DatastoreTestDatabase>
{
    private const string ClientId = "prem_cli_0a1b2c3d4e5f60718293a4b5";
    private const string Loopback = "http://127.0.0.1:53682";
    private const string ApproveTarget = Loopback + "/callback?code=invented-code&state=s1&iss=https%3A%2F%2Flocalhost";
    private const string DenyTarget = Loopback + "/callback?error=access_denied&state=s1&iss=https%3A%2F%2Flocalhost";

    private static readonly (string Name, string Value)[] Standard =
    [
        ("response_type", "code"), ("client_id", ClientId), ("redirect_uri", Loopback + "/callback"),
        ("code_challenge", "invented-challenge-0123456789abcdef0123456789ab"), ("code_challenge_method", "S256"),
        ("scope", "read"), ("state", "s1"), ("resource", "https://localhost/mcp"),
    ];

    // --- The flow off, and the navigation ---

    [Fact]
    public async Task While_the_flow_is_off_its_pages_answer_as_paths_never_mapped()
    {
        await using var p = await PortalWorld.NewAsync(server);

        var never = await Answer(p, "/portal/no-such-page");
        foreach (var path in new[] { "/portal/oauth/consent" + Query(Standard), "/portal/oauth/grants", "/portal/oauth/clients" })
            Assert.Equal(never, await Answer(p, path));
        Assert.DoesNotContain("/portal/oauth/", await p.TextAsync(await p.GetAsync("/portal/search", p.Admin)));
    }

    [Fact]
    public async Task While_the_flow_is_on_the_access_group_links_the_clients_and_grants_pages_for_readers_only()
    {
        await using var p = await NewAsync(new StandIns());

        var admin = await p.TextAsync(await p.GetAsync("/portal/search", p.Admin));
        Assert.Contains("href=\"/portal/oauth/clients\"", admin);
        Assert.Contains("href=\"/portal/oauth/grants\"", admin);
        Assert.Contains("href=\"/portal/oauth/grants\"", await p.TextAsync(await p.GetAsync("/portal/search", p.Auditor)));
        Assert.DoesNotContain("/portal/oauth/", await p.TextAsync(await p.GetAsync("/portal/search", p.Member)));
    }

    // --- The consent page ---

    [Fact]
    public async Task The_consent_page_shows_the_client_its_origin_and_the_loopback_warning_with_the_clients_text_isolated()
    {
        var stand = new StandIns();
        await using var p = await NewAsync(stand);

        using var response = await p.GetAsync("/portal/oauth/consent" + Query(Standard), p.Member);
        var page = await p.TextAsync(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("<bdi class=\"client\">Desk Helper</bdi>", page);
        Assert.Contains("It registered itself.", page);
        Assert.Contains($"<code>{ClientId}</code>", page);
        Assert.Contains($"<p class=\"origin\"><bdi>{Loopback}</bdi></p>", page);
        Assert.Contains($"The answer goes to whatever program is listening at <bdi>{Loopback}</bdi> on this computer. On a shared computer that can be another person's program. Approve only if you started this from <bdi>Desk Helper</bdi> just now.", page);
        Assert.Contains("Search and fetch documents you can read, read-only. It can never change anything.", page);
        Assert.Contains("<dt>Connected assistants</dt><dd>1 of 3</dd>", page);
        // The page asked about exactly the request it was given.
        Assert.Equal(Flat(Standard), Flat(Assert.Single(stand.Consent.Checked)));
    }

    [Fact]
    public async Task The_consent_page_has_two_forms_each_with_its_own_decision_and_every_parameter()
    {
        await using var p = await NewAsync(new StandIns());

        var page = await p.TextAsync(await p.GetAsync("/portal/oauth/consent" + Query(Standard), p.Member));

        var forms = Regex.Matches(page, "<form method=\"post\" action=\"/portal/oauth/consent\" class=\"inline\" data-consent>(.*?)</form>", RegexOptions.Singleline)
            .Select(m => m.Groups[1].Value).ToList();
        Assert.Equal(2, forms.Count);
        Assert.Equal(("approve", "deny"), (Hidden(forms[0])["decision"].Single(), Hidden(forms[1])["decision"].Single()));
        foreach (var form in forms)
        {
            var hidden = Hidden(form);
            Assert.Equal(p.Member.AntiForgeryToken, hidden["prem_antiforgery"].Single());
            Assert.Equal(Flat(Standard), Flat(hidden.Where(h => h.Key.StartsWith(OAuthPages.ParameterPrefix, StringComparison.Ordinal))
                .SelectMany(h => h.Value.Select(v => (h.Key[OAuthPages.ParameterPrefix.Length..], v)))));
        }
        // Hosted is chosen unless the person changes it, and only Approve asks.
        Assert.Contains("<option value=\"hosted\" selected>", forms[0]);
        Assert.Contains("<option value=\"local\">", forms[0]);
        Assert.DoesNotContain("name=\"model\"", forms[1]);
    }

    [Fact]
    public async Task A_client_name_that_would_turn_the_sentence_around_stays_inside_its_own_isolated_span()
    {
        var stand = new StandIns();
        const string hostile = "Desk‮txt.exe ​helper";
        stand.Consent.Client = stand.Consent.Client with { Name = hostile };
        await using var p = await NewAsync(stand);

        var page = await p.TextAsync(await p.GetAsync("/portal/oauth/consent" + Query(Standard), p.Member));

        var isolated = Regex.Matches(page, "<bdi[^>]*>(.*?)</bdi>", RegexOptions.Singleline).Select(m => WebUtility.HtmlDecode(m.Groups[1].Value)).ToList();
        Assert.Contains(hostile, isolated);
        // Outside every bdi, not one of its direction or width marks is left.
        // Compared by ordinal: a culture's comparison ignores such marks and
        // would find one at the start of any text.
        var outside = WebUtility.HtmlDecode(Regex.Replace(page, "<bdi[^>]*>.*?</bdi>", "", RegexOptions.Singleline));
        Assert.DoesNotContain("‮", outside, StringComparison.Ordinal);
        Assert.DoesNotContain("​", outside, StringComparison.Ordinal);
        Assert.Contains("‮", WebUtility.HtmlDecode(page), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_https_answer_address_is_named_as_a_website_and_a_disallowed_local_model_leaves_hosted_only()
    {
        var stand = new StandIns();
        stand.Consent.View = stand.Consent.View with { RedirectOrigin = "https://desk.example", LoopbackRedirect = false, LocalAllowed = false };
        await using var p = await NewAsync(stand);

        var page = await p.TextAsync(await p.GetAsync("/portal/oauth/consent" + Query(Standard), p.Member));

        Assert.Contains("The answer goes to <bdi>https://desk.example</bdi>, a website; that company's servers make the assistant's calls.", page);
        Assert.Contains("This assistant receives answers at <bdi>https://desk.example</bdi>, outside this computer, so what it is served leaves the network. An administrator can register it if it runs inside your network.", page);
        Assert.Contains("<input type=\"hidden\" name=\"model\" value=\"hosted\">", page);
        Assert.DoesNotContain("<select name=\"model\">", page);
        Assert.DoesNotContain("on this computer. On a shared computer", page);
    }

    [Fact]
    public async Task A_model_location_an_administrator_stated_is_shown_instead_of_the_question()
    {
        var stand = new StandIns();
        stand.Consent.Client = stand.Consent.Client with
        {
            RegisteredBy = OAuthClientRegistration.Administrator, StatedModelLocation = ModelLocation.Local,
        };
        await using var p = await NewAsync(stand);

        var page = await p.TextAsync(await p.GetAsync("/portal/oauth/consent" + Query(Standard), p.Member));

        Assert.Contains("It was registered by an administrator.", page);
        Assert.Contains("Where its model runs: a local model, inside your network, as an administrator registered it.", page);
        Assert.Contains("<input type=\"hidden\" name=\"model\" value=\"local\">", page);
        Assert.DoesNotContain("<select name=\"model\">", page);
    }

    [Fact]
    public async Task A_kept_assistant_is_named_and_a_different_answer_is_said_to_make_a_new_one()
    {
        var stand = new StandIns();
        stand.Consent.View = stand.Consent.View with
        {
            ReplacesGrant = true, AgentName = "oauth-0a1b2c3d4e5f60718293a4b5",
            KeptAgentModelLocation = ModelLocation.Hosted, KeptAgentModelVendor = "Invented Models",
        };
        await using var p = await NewAsync(stand);

        var page = await p.TextAsync(await p.GetAsync("/portal/oauth/consent" + Query(Standard), p.Member));

        Assert.Contains("Your assistant oauth-0a1b2c3d4e5f60718293a4b5 has a hosted model, outside your network, run by Invented Models. Approving keeps it. A different answer to where its model runs makes a new assistant and ends the old one.", page);
        Assert.Contains("name=\"vendor\" value=\"Invented Models\"", page);
    }

    [Fact]
    public async Task Approve_answers_with_a_page_holding_one_link_back_and_never_a_redirect()
    {
        var stand = new StandIns();
        await using var p = await NewAsync(stand);

        using var response = await p.PostAsync("/portal/oauth/consent", p.Member,
            [("decision", "approve"), ("model", "hosted"), ("vendor", "Invented Models"), .. Carried(Standard)]);
        var page = await p.TextAsync(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null(response.Headers.Location);
        Assert.Equal(ApproveTarget, Assert.Single(Outward(page)));
        Assert.Equal(ApproveTarget, WebUtility.HtmlDecode(Regex.Match(page, "data-continue=\"([^\"]*)\"").Groups[1].Value));
        Assert.Contains($"Continue to <bdi>{Loopback}</bdi>", page);
        var call = Assert.Single(stand.Consent.Approved);
        Assert.Equal(p.World.Alice.Id, call.User);
        Assert.Equal(Flat(Standard), Flat(call.Parameters));
        Assert.Equal((ModelLocation.Hosted, "Invented Models"), (call.Location, call.Vendor));
        Assert.Empty(stand.Consent.Denied);
    }

    [Fact]
    public async Task Deny_answers_with_a_page_holding_one_link_back()
    {
        var stand = new StandIns();
        await using var p = await NewAsync(stand);

        using var response = await p.PostAsync("/portal/oauth/consent", p.Member, [("decision", "deny"), .. Carried(Standard)]);
        var page = await p.TextAsync(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(DenyTarget, Assert.Single(Outward(page)));
        Assert.Equal(Flat(Standard), Flat(Assert.Single(stand.Consent.Denied).Parameters));
        Assert.Empty(stand.Consent.Approved);
    }

    [Fact]
    public async Task Every_parameter_goes_round_the_form_unchanged_repeats_included_and_nothing_of_the_forms_own()
    {
        var stand = new StandIns();
        await using var p = await NewAsync(stand);
        (string, string)[] sent = [.. Standard, ("extra", "one"), ("extra", "two & three")];

        var page = await p.TextAsync(await p.GetAsync("/portal/oauth/consent" + Query(sent), p.Member));
        var approve = Regex.Match(page, "<form[^>]*data-consent>(.*?)</form>", RegexOptions.Singleline).Groups[1].Value;
        var fields = Hidden(approve).Where(h => h.Key != "prem_antiforgery").SelectMany(h => h.Value.Select(v => (h.Key, v))).ToList();
        using (await p.PostAsync("/portal/oauth/consent", p.Member, [.. fields, ("model", "hosted"), ("vendor", "Invented Models")])) { }

        Assert.Equal(Flat(sent), Flat(Assert.Single(stand.Consent.Approved).Parameters));
    }

    [Fact]
    public async Task A_refused_choice_asks_again_with_the_reason()
    {
        var stand = new StandIns();
        stand.Consent.Refusal = new ArgumentException("A local model is not allowed for this assistant.");
        await using var p = await NewAsync(stand);

        using var response = await p.PostAsync("/portal/oauth/consent", p.Member,
            [("decision", "approve"), ("model", "local"), .. Carried(Standard)]);
        var page = await p.TextAsync(response);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("<p class=\"error\">A local model is not allowed for this assistant.</p>", page);
        Assert.Equal(2, Regex.Matches(page, "data-consent>").Count);
        Assert.Empty(Outward(page));
        Assert.DoesNotContain("data-continue", page);
    }

    [Fact]
    public async Task A_request_that_no_longer_stands_is_refused_with_nowhere_to_go()
    {
        var stand = new StandIns();
        stand.Consent.Refusal = new InvalidOperationException("This request was already answered.");
        await using var p = await NewAsync(stand);

        using var response = await p.PostAsync("/portal/oauth/consent", p.Member, [("decision", "deny"), .. Carried(Standard)]);
        var page = await p.TextAsync(response);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("<p class=\"error\">This request was already answered.</p>", page);
        Assert.Empty(Outward(page));
        Assert.DoesNotContain("data-consent", page);
        Assert.DoesNotContain("data-continue", page);
    }

    [Fact]
    public async Task The_link_back_is_exactly_what_the_flow_answered_and_never_the_requests_own_address()
    {
        // An answer at another address than the request named, so a link
        // assembled from the request's parameters could not pass for it.
        const string answered = "https://answered.example/after?code=c1&state=s1";
        var stand = new StandIns();
        stand.Consent.ApproveTarget = answered;
        await using var p = await NewAsync(stand);

        var page = await p.TextAsync(await p.PostAsync("/portal/oauth/consent", p.Member,
            [("decision", "approve"), ("model", "hosted"), ("vendor", "Invented Models"), .. Carried(Standard)]));

        Assert.Equal(answered, Assert.Single(Outward(page)));
        Assert.Equal(answered, WebUtility.HtmlDecode(Regex.Match(page, "data-continue=\"([^\"]*)\"").Groups[1].Value));
        Assert.Contains("Continue to <bdi>https://answered.example</bdi>", page);
        Assert.DoesNotContain("127.0.0.1", page);
    }

    [Theory]
    [InlineData("https://desk.example/callback?code=c1", true)]
    [InlineData("http://127.0.0.1:53682/callback?code=c1", true)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("data:text/html,hello", false)]
    [InlineData("//desk.example/callback", false)]
    [InlineData("/portal/search", false)]
    [InlineData("HTTPS://desk.example/callback", false)]
    [InlineData("https:desk.example", false)]
    [InlineData("", false)]
    public void Only_an_address_that_begins_with_https_or_http_is_ever_linked(string target, bool linked)
    {
        Assert.Equal(linked, OAuthPages.Openable(target));
    }

    [Fact]
    public async Task An_address_a_browser_does_not_open_as_a_page_is_never_linked()
    {
        var stand = new StandIns();
        stand.Consent.ApproveTarget = "javascript:alert(1)";
        await using var p = await NewAsync(stand);

        using var response = await p.PostAsync("/portal/oauth/consent", p.Member,
            [("decision", "approve"), ("model", "hosted"), ("vendor", "Invented Models"), .. Carried(Standard)]);
        var page = await p.TextAsync(response);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.DoesNotContain("javascript:", page);
        Assert.Empty(Outward(page));
    }

    [Fact]
    public async Task An_error_with_nowhere_safe_to_return_shows_the_error_and_no_link()
    {
        var stand = new StandIns();
        stand.Consent.Check = new AuthorizationCheck(AuthorizationCheckOutcome.ShowError, null, "invalid_client",
            "This assistant's registration on this server has expired or was removed. Remove this server from the assistant and add it again.", null);
        await using var p = await NewAsync(stand);

        using var response = await p.GetAsync("/portal/oauth/consent" + Query(Standard), p.Member);
        var page = await p.TextAsync(response);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("registration on this server has expired or was removed.", page);
        Assert.Empty(Outward(page));
        Assert.DoesNotContain("data-consent", page);
    }

    [Fact]
    public async Task An_error_the_client_can_be_told_offers_one_link_back_and_never_follows_it()
    {
        var stand = new StandIns();
        const string back = Loopback + "/callback?error=invalid_scope&state=s1&iss=https%3A%2F%2Flocalhost";
        stand.Consent.Check = new AuthorizationCheck(AuthorizationCheckOutcome.ReturnError, null, "invalid_scope",
            "The assistant asked for access this server does not give.", back);
        await using var p = await NewAsync(stand);

        using var response = await p.GetAsync("/portal/oauth/consent" + Query(Standard), p.Member);
        var page = await p.TextAsync(response);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(back, Assert.Single(Outward(page)));
        Assert.Contains($"Return to <bdi>{Loopback}</bdi>", page);
        Assert.DoesNotContain("data-continue", page);
    }

    [Fact]
    public async Task A_consent_from_another_origin_is_refused_and_reaches_nothing()
    {
        var stand = new StandIns();
        await using var p = await NewAsync(stand);

        using var response = await p.PostAsync("/portal/oauth/consent", p.Member,
            [("decision", "approve"), ("model", "hosted"), ("vendor", "Invented Models"), .. Carried(Standard)], origin: "https://elsewhere.example");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(stand.Consent.Approved);
    }

    [Fact]
    public async Task Signed_out_the_consent_page_sends_the_browser_to_sign_in_and_back_to_it()
    {
        var stand = new StandIns();
        await using var p = await NewAsync(stand);
        var path = "/portal/oauth/consent" + Query(Standard);

        using var response = await p.GetAsync(path);

        Assert.Equal(HttpStatusCode.SeeOther, response.StatusCode);
        Assert.Equal("/portal/sign-in?return=" + Uri.EscapeDataString(path), response.Headers.Location!.OriginalString);
        Assert.Empty(stand.Consent.Checked);
    }

    // --- Grants ---

    [Fact]
    public async Task The_grants_page_lists_live_grants_and_every_grant_on_asking_with_the_clients_text_isolated()
    {
        var stand = new StandIns();
        await using var p = await NewAsync(stand);

        var live = await p.TextAsync(await p.GetAsync("/portal/oauth/grants", p.Admin));
        var all = await p.TextAsync(await p.GetAsync("/portal/oauth/grants?show=all", p.Admin));

        Assert.Equal(new[] { OAuthGrantFilter.Live, OAuthGrantFilter.All }, stand.Grants.Filters);
        Assert.Contains("<bdi class=\"client\">Desk Helper</bdi>", live);
        Assert.Contains("registered itself", live);
        Assert.Contains("<code>grant-live</code>", live);
        Assert.Contains("3 times, last", live);
        Assert.Contains("value=\"grant-live\"", live);
        Assert.DoesNotContain("grant-ended", live);
        Assert.Contains("<code>grant-ended</code>", all);
        Assert.Contains("ended by a disable or a password change (owner disabled or password changed)", all);
        Assert.DoesNotContain("value=\"grant-ended\"", all);
    }

    [Fact]
    public async Task Revoking_a_grant_goes_to_the_flow_under_the_administrator()
    {
        var stand = new StandIns();
        await using var p = await NewAsync(stand);

        using var response = await p.PostAsync("/portal/oauth/grants/revoke", p.Admin, [("grant", "grant-live")]);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("done=", response.Headers.Location!.OriginalString);
        var (actor, id) = Assert.Single(stand.Grants.Revoked);
        Assert.Equal(("portal", p.World.Carol.Id, "grant-live"), (actor.Surface, actor.UserId!.Value, id));
    }

    [Fact]
    public async Task A_member_opens_neither_list_and_an_auditor_reads_them_with_nothing_to_press()
    {
        var stand = new StandIns();
        await using var p = await NewAsync(stand);

        foreach (var path in new[] { "/portal/oauth/grants", "/portal/oauth/clients" })
        {
            using (var member = await p.GetAsync(path, p.Member))
                Assert.Equal(HttpStatusCode.Forbidden, member.StatusCode);
            var auditor = await p.TextAsync(await p.GetAsync(path, p.Auditor));
            Assert.Contains("<bdi class=\"client\">Desk Helper</bdi>", auditor);
            Assert.DoesNotContain("<form method=\"post\"", auditor);
        }
        using (var revoke = await p.PostAsync("/portal/oauth/grants/revoke", p.Auditor, [("grant", "grant-live")]))
            Assert.Equal(HttpStatusCode.Forbidden, revoke.StatusCode);
        Assert.Empty(stand.Grants.Revoked);
    }

    // --- Clients ---

    [Fact]
    public async Task The_clients_page_lists_each_client_with_its_own_text_isolated_and_how_it_came()
    {
        var stand = new StandIns();
        await using var p = await NewAsync(stand);
        stand.Clients.Listed.Add(new OAuthClientListing(stand.Consent.Client with
        {
            Id = "https://desk.example/client", Name = "Office Desk", RedirectUris = ["https://desk.example/callback"],
            RegisteredBy = OAuthClientRegistration.Administrator, RegisteredByActor = "portal:" + p.World.Carol.Id.ToString("D"),
            StatedModelLocation = ModelLocation.Hosted, StatedModelVendor = "Invented Models", Disabled = true,
        }, null, null, 0));

        var page = await p.TextAsync(await p.GetAsync("/portal/oauth/clients", p.Admin));

        Assert.Contains("<bdi class=\"client\">Desk Helper</bdi>", page);
        Assert.Contains("registered itself, from 192.0.2.10", page);
        Assert.Contains($"<bdi>{Loopback}/callback</bdi>", page);
        Assert.Contains("each person says", page);
        Assert.Contains("<bdi class=\"client\">Office Desk</bdi>", page);
        Assert.Contains("registered by an administrator, in the portal, by carol", page);
        Assert.Contains("a hosted model, outside your network, run by Invented Models", page);
        Assert.Contains("Enable</button>", page);
        Assert.Contains("Disable</button>", page);
    }

    [Fact]
    public async Task A_client_stored_from_a_metadata_document_shows_the_documents_hash_and_when_it_was_stored_and_no_other_does()
    {
        const string Sha = "3f7a0c1e9b2d4c6a8e0f1b3d5c7a9e1f2b4d6c8a0e2f4b6d8c0a2e4f6b8d0c2a";
        var stand = new StandIns();
        await using var p = await NewAsync(stand);
        stand.Clients.Listed.Add(new OAuthClientListing(stand.Consent.Client with
        {
            Id = "https://desk.example/client", Name = "Office Desk", RedirectUris = ["https://desk.example/callback"],
            RegisteredBy = OAuthClientRegistration.Administrator, RegisteredByActor = "cli:operator",
        }, null, null, 0, new OAuthStoredDocument(Sha, DateTimeOffset.Parse("2026-09-24T16:30:00Z", System.Globalization.CultureInfo.InvariantCulture))));

        var page = await p.TextAsync(await p.GetAsync("/portal/oauth/clients", p.Admin));

        var rows = Regex.Matches(page, "<tr>.*?</tr>", RegexOptions.Singleline).Select(m => m.Value).ToList();
        var stored = Assert.Single(rows, row => row.Contains("https://desk.example/client", StringComparison.Ordinal));
        Assert.Contains($"<code>https://desk.example/client</code><br><span class=\"note\">from its metadata document, SHA-256 <code>{Sha}</code>, stored <span class=\"when\">2026-09-24 16:30 UTC</span></span>",
            stored, StringComparison.Ordinal);
        var other = Assert.Single(rows, row => row.Contains(ClientId, StringComparison.Ordinal));
        Assert.DoesNotContain("metadata document", other, StringComparison.Ordinal);
        Assert.Single(Regex.Matches(page, "from its metadata document"));
    }

    [Fact]
    public async Task Registering_a_client_hands_the_flow_what_the_administrator_typed()
    {
        var stand = new StandIns();
        await using var p = await NewAsync(stand);

        using var response = await p.PostAsync("/portal/oauth/clients", p.Admin,
            [("name", "Office Desk"), ("id", ""), ("redirectUris", "https://desk.example/callback\r\n\r\n  http://127.0.0.1/callback  \n"),
             ("model", "hosted"), ("vendor", "Invented Models")]);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("prem_cli_made0000000000000000", response.Headers.Location!.OriginalString);
        var added = Assert.Single(stand.Clients.Added);
        Assert.Equal(("portal", p.World.Carol.Id), (added.Actor.Surface, added.Actor.UserId!.Value));
        Assert.Null(added.Id);
        Assert.Equal("Office Desk", added.Name);
        Assert.Equal(new[] { "https://desk.example/callback", "http://127.0.0.1/callback" }, added.RedirectUris);
        Assert.Equal((ModelLocation.Hosted, "Invented Models"), (added.Location!.Value, added.Vendor));
    }

    [Fact]
    public async Task A_registration_the_flow_refuses_comes_back_as_the_reason()
    {
        var stand = new StandIns();
        stand.Clients.Refusal = new ArgumentException("A redirect address is https or loopback.");
        await using var p = await NewAsync(stand);

        using var response = await p.PostAsync("/portal/oauth/clients", p.Admin, [("name", "Office Desk"), ("redirectUris", "ftp://desk.example/")]);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("error=" + Uri.EscapeDataString("A redirect address is https or loopback."), response.Headers.Location!.OriginalString);
        Assert.Empty(stand.Clients.Added);
    }

    [Fact]
    public async Task Disable_enable_and_remove_go_to_the_flow_for_the_client_named()
    {
        var stand = new StandIns();
        await using var p = await NewAsync(stand);

        foreach (var verb in new[] { "disable", "enable", "remove" })
            using (await p.PostAsync("/portal/oauth/clients/" + verb, p.Admin, [("client", ClientId)])) { }

        Assert.Equal(new[] { ("disable", ClientId), ("enable", ClientId), ("remove", ClientId) }, stand.Clients.Changed);
    }

    // --- Clients from a metadata document ---

    private const string DeskId = "https://desk.example/client";

    /// <summary>An invented metadata document for <see cref="DeskId"/>: one https redirect kept, one private scheme dropped, one field left out.</summary>
    private static string DeskDocument(string name = "Office Desk", string id = DeskId, string newline = "\n") =>
        "{" + newline + $"  \"client_id\": \"{id}\"," + newline + $"  \"client_name\": \"{name}\"," + newline +
        "  \"redirect_uris\": [\"https://desk.example/callback\", \"vscode://desk/callback\"]," + newline +
        "  \"token_endpoint_auth_method\": \"none\"," + newline + "  \"logo_uri\": \"https://desk.example/logo.png\"" + newline + "}";

    private static OAuthClientListing StoredDesk() => new(new StandInConsent().Client with
    {
        Id = DeskId, Name = "Office Desk", RedirectUris = ["https://desk.example/callback"],
        RegisteredBy = OAuthClientRegistration.Administrator, RegisteredByActor = "cli:operator",
    }, null, null, 0, new OAuthStoredDocument(new string('a', 64), DateTimeOffset.Parse("2026-09-24T16:30:00Z", System.Globalization.CultureInfo.InvariantCulture)));

    /// <summary>What a change's redirect says, decoded: its <c>done</c> or its <c>error</c>.</summary>
    private static string? Said(HttpResponseMessage response, string which)
    {
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        // The portal writes each value with Uri.EscapeDataString; the location is relative.
        var location = response.Headers.Location!.OriginalString;
        var query = location.Contains('?', StringComparison.Ordinal) ? location[(location.IndexOf('?', StringComparison.Ordinal) + 1)..] : "";
        return query.Split('&').Select(pair => pair.Split('=', 2)).Where(pair => pair.Length == 2 && pair[0] == which)
            .Select(pair => Uri.UnescapeDataString(pair[1])).SingleOrDefault();
    }

    private static string RefusalOf(string document, string id)
    {
        var refusal = Assert.Throws<ArgumentException>(() => OAuthClientDocument.Parse(Encoding.UTF8.GetBytes(document), id));
        return refusal.Message;
    }

    [Fact]
    public async Task The_clients_page_offers_an_add_from_a_document_and_a_replace_only_on_a_client_stored_from_one()
    {
        var stand = new StandIns();
        await using var p = await NewAsync(stand);
        stand.Clients.Listed.Add(StoredDesk());

        var page = await p.TextAsync(await p.GetAsync("/portal/oauth/clients", p.Admin));

        var uploads = Regex.Matches(page, "<form method=\"post\" action=\"([^\"]*)\" enctype=\"multipart/form-data\">(.*?)</form>", RegexOptions.Singleline)
            .ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value);
        Assert.Equal(new[] { "/portal/oauth/clients/document", "/portal/oauth/clients/replace" }, uploads.Keys.Order(StringComparer.Ordinal));
        foreach (var form in uploads.Values)
        {
            Assert.Contains($"<input type=\"hidden\" name=\"prem_antiforgery\" value=\"{p.Admin.AntiForgeryToken}\">", form, StringComparison.Ordinal);
            Assert.Contains($"<textarea name=\"document\" rows=\"6\" maxlength=\"{OAuthClientDocument.MaxBytes}\"></textarea>", form, StringComparison.Ordinal);
            Assert.Contains("<input type=\"file\" name=\"documentFile\"", form, StringComparison.Ordinal);
            // Each field says the bound before a document is sent, in the number the flow checks.
            Assert.Contains($"The document, pasted, at most {OAuthClientDocument.MaxBytes / 1024} KB <textarea", form, StringComparison.Ordinal);
            Assert.Contains($"Or its file, at most {OAuthClientDocument.MaxBytes / 1024} KB <input type=\"file\"", form, StringComparison.Ordinal);
        }
        Assert.Contains("name=\"id\" value=\"\" required", uploads["/portal/oauth/clients/document"], StringComparison.Ordinal);
        Assert.Contains($"<input type=\"hidden\" name=\"client\" value=\"{DeskId}\">", uploads["/portal/oauth/clients/replace"], StringComparison.Ordinal);
        // The replace sits on the stored client's row, and on no other.
        var rows = Regex.Matches(page, "<tr>.*?</tr>", RegexOptions.Singleline).Select(m => m.Value).ToList();
        Assert.Contains("/portal/oauth/clients/replace", Assert.Single(rows, row => row.Contains($"<code>{DeskId}</code>", StringComparison.Ordinal)), StringComparison.Ordinal);
        Assert.DoesNotContain("/portal/oauth/clients/replace", Assert.Single(rows, row => row.Contains($"<code>{ClientId}</code>", StringComparison.Ordinal)), StringComparison.Ordinal);

        var auditor = await p.TextAsync(await p.GetAsync("/portal/oauth/clients", p.Auditor));
        Assert.DoesNotContain("multipart/form-data", auditor, StringComparison.Ordinal);
        Assert.DoesNotContain("/portal/oauth/clients/document", auditor, StringComparison.Ordinal);
        Assert.DoesNotContain("/portal/oauth/clients/replace", auditor, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Registering_from_a_pasted_document_hands_the_flow_the_text_as_sent_and_says_what_it_took()
    {
        var stand = new StandIns();
        await using var p = await NewAsync(stand);
        var document = DeskDocument();
        var parsed = OAuthClientDocument.Parse(Encoding.UTF8.GetBytes(document), DeskId);

        using var response = await p.PostMultipartAsync("/portal/oauth/clients/document", p.Admin,
            [("id", " " + DeskId + " "), ("document", document), ("model", "hosted"), ("vendor", "Invented Models")]);

        var done = Said(response, "done");
        Assert.NotNull(done);
        Assert.Contains($"Client 'Office Desk' registered from its metadata document, SHA-256 {parsed.Sha256}.", done, StringComparison.Ordinal);
        Assert.Contains("Its redirect addresses: https://desk.example/callback.", done, StringComparison.Ordinal);
        Assert.Contains($"Dropped, a private scheme: {Assert.Single(parsed.DroppedRedirects)}.", done, StringComparison.Ordinal);
        Assert.Contains("Left out, and never stored: logo_uri.", done, StringComparison.Ordinal);
        Assert.EndsWith($"Nothing was fetched from its address. The assistant is set up with this client id: {DeskId}", done, StringComparison.Ordinal);
        var added = Assert.Single(stand.Clients.AddedFromDocument);
        Assert.Equal(("portal", p.World.Carol.Id), (added.Actor.Surface, added.Actor.UserId!.Value));
        Assert.Equal(DeskId, added.Id);
        Assert.Equal(Encoding.UTF8.GetBytes(document), added.Document);
        Assert.Equal((ModelLocation.Hosted, "Invented Models"), (added.Location!.Value, added.Vendor));
    }

    [Fact]
    public async Task Registering_from_an_uploaded_file_hands_the_flow_the_files_bytes_exactly()
    {
        var stand = new StandIns();
        await using var p = await NewAsync(stand);
        var bytes = Encoding.UTF8.GetBytes(DeskDocument(newline: "\r\n"));

        using var response = await p.PostMultipartAsync("/portal/oauth/clients/document", p.Admin, [("id", DeskId), ("document", "")],
            [("documentFile", "desk-client.json", bytes)]);

        Assert.Contains($"SHA-256 {Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes))}.", Said(response, "done"), StringComparison.Ordinal);
        var added = Assert.Single(stand.Clients.AddedFromDocument);
        Assert.Equal(bytes, added.Document);
        Assert.Null(added.Location);
        Assert.Null(added.Vendor);
    }

    [Fact]
    public async Task A_document_both_pasted_and_uploaded_or_neither_is_refused_before_it_reaches_the_flow()
    {
        var stand = new StandIns();
        await using var p = await NewAsync(stand);

        using var both = await p.PostMultipartAsync("/portal/oauth/clients/document", p.Admin, [("id", DeskId), ("document", DeskDocument())],
            [("documentFile", "desk-client.json", Encoding.UTF8.GetBytes(DeskDocument()))]);
        using var neither = await p.PostMultipartAsync("/portal/oauth/clients/document", p.Admin, [("id", DeskId), ("document", "  \n ")],
            [("documentFile", "empty.json", [])]);
        using var replaceNeither = await p.PostMultipartAsync("/portal/oauth/clients/replace", p.Admin, [("client", DeskId)]);

        Assert.Equal("Paste the document or choose its file, not both.", Said(both, "error"));
        Assert.Equal("Paste the metadata document or choose its file.", Said(neither, "error"));
        Assert.Equal("Paste the metadata document or choose its file.", Said(replaceNeither, "error"));
        Assert.Empty(stand.Clients.AddedFromDocument);
        Assert.Empty(stand.Clients.Replaced);
    }

    [Fact]
    public async Task A_refusal_from_the_flow_is_shown_in_the_flows_own_words()
    {
        var stand = new StandIns();
        await using var p = await NewAsync(stand);

        // The flow's parse refuses a document that names another address.
        var other = DeskDocument(id: "https://desk.example/other");
        using var mismatch = await p.PostMultipartAsync("/portal/oauth/clients/document", p.Admin, [("id", DeskId), ("document", other)]);
        Assert.Equal(RefusalOf(other, DeskId), Said(mismatch, "error"));

        // A file over the flow's bound reaches it read to one byte past the bound,
        // no more, and the flow's own sentence comes back.
        var large = Encoding.UTF8.GetBytes(DeskDocument()).Concat(new byte[100 * 1024 - DeskDocument().Length].Select(_ => (byte)' ')).ToArray();
        using var tooLarge = await p.PostMultipartAsync("/portal/oauth/clients/document", p.Admin, [("id", DeskId)],
            [("documentFile", "desk-client.json", large)]);
        Assert.Equal("The metadata document is larger than 64 KB, which no client's document needs.", Said(tooLarge, "error"));
        Assert.Equal(OAuthClientDocument.MaxBytes + 1, stand.Clients.AddedFromDocument[^1].Document.Length);

        // Any other refusal the flow throws, as it is.
        stand.Clients.Refusal = new InvalidOperationException("An invented refusal from the flow, <b>with markup</b>.");
        using var refused = await p.PostMultipartAsync("/portal/oauth/clients/document", p.Admin, [("id", DeskId), ("document", DeskDocument())]);
        Assert.Equal("An invented refusal from the flow, <b>with markup</b>.", Said(refused, "error"));
    }

    [Fact]
    public async Task A_document_form_over_its_bounds_is_answered_on_the_clients_page_in_the_flows_sentence_and_reaches_nothing()
    {
        var stand = new StandIns();
        await using var p = await NewAsync(stand);

        // Past the bounds of the form itself (a file or a pasted value one byte
        // over twice the flow's bound), the host answers before the page runs:
        // the Clients page, with the sentence the flow gives a document over its
        // own bound.
        foreach (var (fields, files) in new[]
        {
            ((IEnumerable<(string, string)>)[("id", DeskId)], (IEnumerable<(string, string, byte[])>)[("documentFile", "big.json", new byte[2 * OAuthClientDocument.MaxBytes + 1])]),
            ([("id", DeskId), ("document", new string(' ', 2 * OAuthClientDocument.MaxBytes + 1))], []),
        })
        {
            using var response = await p.PostMultipartAsync("/portal/oauth/clients/document", p.Admin, fields, files);
            Assert.Equal(OAuthClientDocument.TooLarge, Said(response, "error"));
            Assert.StartsWith(OAuthPaths.Clients + "?", response.Headers.Location!.OriginalString, StringComparison.Ordinal);
        }
        Assert.Empty(stand.Clients.AddedFromDocument);
    }

    [Fact]
    public async Task A_document_form_over_its_bounds_lands_on_the_clients_page_that_shows_the_sentence_with_nothing_added()
    {
        var stand = new StandIns();
        stand.Clients.Listed.Add(StoredDesk());
        await using var p = await NewAsync(stand);

        // Both document forms, each with a file past the form's bounds, and the
        // redirect followed as a browser follows it: the page it lands on is the
        // Clients page, whole, with the sentence shown as text.
        foreach (var (path, field) in new[] { ("/portal/oauth/clients/document", ("id", DeskId)), ("/portal/oauth/clients/replace", ("client", DeskId)) })
        {
            using var response = await p.PostMultipartAsync(path, p.Admin, [field],
                [("documentFile", "big.json", new byte[2 * OAuthClientDocument.MaxBytes + 1])]);
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            using var landed = await p.GetAsync(response.Headers.Location!.OriginalString, p.Admin);
            Assert.Equal(HttpStatusCode.OK, landed.StatusCode);
            var page = await p.TextAsync(landed);
            Assert.Contains($"<p class=\"error\">{HtmlEncoder.Default.Encode(OAuthClientDocument.TooLarge)}</p>", page, StringComparison.Ordinal);
            Assert.Contains("<h2>Register from a metadata document</h2>", page, StringComparison.Ordinal);
        }
        Assert.Empty(stand.Clients.AddedFromDocument);
        Assert.Empty(stand.Clients.Replaced);
    }

    [Fact]
    public async Task Replacing_a_stored_document_hands_the_flow_the_client_and_the_new_bytes_and_says_what_changed()
    {
        var stand = new StandIns { Clients = { PreviousSha256 = new string('a', 64) } };
        await using var p = await NewAsync(stand);
        var bytes = Encoding.UTF8.GetBytes(DeskDocument("Office Desk Two"));
        var parsed = OAuthClientDocument.Parse(bytes, DeskId);

        using var response = await p.PostMultipartAsync("/portal/oauth/clients/replace", p.Admin, [("client", DeskId), ("document", "")],
            [("documentFile", "desk-client.json", bytes)]);

        var done = Said(response, "done");
        Assert.NotNull(done);
        Assert.StartsWith($"Client {DeskId}'s metadata document replaced: SHA-256 {new string('a', 64)} is now {parsed.Sha256}. Its name is 'Office Desk Two'.", done, StringComparison.Ordinal);
        Assert.EndsWith("Every grant stands. A code is exchanged only for a redirect address the new document lists. Nothing was fetched.", done, StringComparison.Ordinal);
        var replaced = Assert.Single(stand.Clients.Replaced);
        Assert.Equal(("portal", p.World.Carol.Id, DeskId), (replaced.Actor.Surface, replaced.Actor.UserId!.Value, replaced.Id));
        Assert.Equal(bytes, replaced.Document);
    }

    [Fact]
    public async Task A_replace_with_the_stored_document_says_nothing_changed_and_a_refusal_comes_back_as_the_flow_says_it()
    {
        var stand = new StandIns();
        await using var p = await NewAsync(stand);
        var document = DeskDocument();

        using var same = await p.PostMultipartAsync("/portal/oauth/clients/replace", p.Admin, [("client", DeskId), ("document", document)]);
        Assert.Equal($"The document is the one client {DeskId} is stored with already (SHA-256 {OAuthClientDocument.Parse(Encoding.UTF8.GetBytes(document), DeskId).Sha256}); nothing changed.",
            Said(same, "done"));

        stand.Clients.Refusal = new InvalidOperationException($"Client {DeskId} was typed in by hand; it has no stored document to replace.");
        using var refused = await p.PostMultipartAsync("/portal/oauth/clients/replace", p.Admin, [("client", DeskId), ("document", document)]);
        Assert.Equal($"Client {DeskId} was typed in by hand; it has no stored document to replace.", Said(refused, "error"));
    }

    [Fact]
    public async Task A_document_form_from_another_origin_without_its_token_or_from_a_non_administrator_reaches_nothing()
    {
        var stand = new StandIns();
        await using var p = await NewAsync(stand);

        foreach (var path in new[] { "/portal/oauth/clients/document", "/portal/oauth/clients/replace" })
        {
            (string, string)[] fields = [("id", DeskId), ("client", DeskId), ("document", DeskDocument())];
            using var foreign = await p.PostMultipartAsync(path, p.Admin, fields, origin: "https://elsewhere.example");
            using var noToken = await p.PostMultipartAsync(path, p.Admin, fields, tokenField: false);
            using var auditor = await p.PostMultipartAsync(path, p.Auditor, fields);
            using var member = await p.PostMultipartAsync(path, p.Member, fields);
            Assert.Equal(
                new[] { HttpStatusCode.Forbidden, HttpStatusCode.Forbidden, HttpStatusCode.Forbidden, HttpStatusCode.Forbidden },
                new[] { foreign.StatusCode, noToken.StatusCode, auditor.StatusCode, member.StatusCode });
        }
        Assert.Empty(stand.Clients.AddedFromDocument);
        Assert.Empty(stand.Clients.Replaced);
    }

    // --- Words the agent pages use ---

    [Fact]
    public void An_assistant_connected_through_the_flow_is_made_by_it_and_counts_as_made_for_themselves()
    {
        var origin = AgentOrigin.OAuth(ClientId);

        Assert.Equal($"through the assistant {ClientId}", AgentPages.MadeBy(origin, new Dictionary<Guid, UserAccount>()));
        Assert.True(AgentPages.MadeForThemselves(origin));
        Assert.True(AgentPages.MadeForThemselves(AgentOrigin.Self(Guid.NewGuid())));
        Assert.False(AgentPages.MadeForThemselves(AgentOrigin.Portal(Guid.NewGuid())));
        Assert.False(AgentPages.MadeForThemselves(null));
    }

    [Fact]
    public void A_token_ended_by_a_disable_or_a_password_change_says_so_and_revoked_says_revoked()
    {
        var now = DateTimeOffset.Parse("2026-09-25T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        var token = new AgentTokenRecord("t1", Guid.NewGuid(), [], now.AddDays(-1), now.AddDays(30), null, null);

        Assert.Equal("usable", AgentPages.TokenState(token, now));
        Assert.Equal("ended by a disable or a password change", AgentPages.TokenState(token with { Superseded = true }, now));
        Assert.Equal("revoked", AgentPages.TokenState(token with { Superseded = true, RevokedAt = now }, now));
        Assert.Equal("expired", AgentPages.TokenState(token, now.AddDays(31)));
    }

    // --- The world, and what the tests read ---

    private Task<PortalWorld> NewAsync(StandIns stand) => PortalWorld.NewAsync(server, options: stand.Options());

    private static async Task<(HttpStatusCode, string)> Answer(PortalWorld p, string path)
    {
        using var response = await p.GetAsync(path, p.Admin);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    private static string Query(IEnumerable<(string Name, string Value)> pairs) =>
        "?" + string.Join("&", pairs.Select(p => $"{Uri.EscapeDataString(p.Name)}={Uri.EscapeDataString(p.Value)}"));

    private static (string, string)[] Carried(IEnumerable<(string Name, string Value)> pairs) =>
        pairs.Select(p => (OAuthPages.ParameterPrefix + p.Name, p.Value)).ToArray();

    private static string Flat(IEnumerable<(string Name, string Value)> pairs) =>
        string.Join("\n", pairs.OrderBy(p => p.Name, StringComparer.Ordinal).ThenBy(p => p.Value, StringComparer.Ordinal).Select(p => p.Name + "=" + p.Value));

    private static string Flat(IReadOnlyDictionary<string, IReadOnlyList<string>> parameters) =>
        Flat(parameters.SelectMany(p => p.Value.Select(v => (p.Key, v))));

    /// <summary>A form's hidden fields, decoded, each name with every value it carries.</summary>
    private static Dictionary<string, List<string>> Hidden(string form)
    {
        var fields = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (Match m in Regex.Matches(form, "<input type=\"hidden\" name=\"([^\"]*)\" value=\"([^\"]*)\">"))
        {
            var name = WebUtility.HtmlDecode(m.Groups[1].Value) ?? "";
            if (!fields.TryGetValue(name, out var values)) fields[name] = values = [];
            values.Add(WebUtility.HtmlDecode(m.Groups[2].Value) ?? "");
        }
        return fields;
    }

    /// <summary>Every reference on a page that leaves this origin, decoded, but the link to the source every page carries.</summary>
    private static List<string> Outward(string page) =>
        PortalWorld.References(page).Where(r => !PortalWorld.StaysHere(r) && !PortalWorld.IsSourceLink(r))
            .Select(r => WebUtility.HtmlDecode(r) ?? "").ToList();

    // --- The stand-ins ---

    internal sealed class StandIns
    {
        public StandInConsent Consent { get; } = new();
        public StandInGrants Grants { get; } = new();
        public StandInClients Clients { get; } = new();

        public ApiHostOptions Options() => new()
        {
            Services = services =>
            {
                services.RemoveAll<OAuthDeployment>();
                services.AddSingleton(new OAuthDeployment("https://localhost", DynamicRegistration: true, DynamicRedirectUris: []));
                services.RemoveAll<IOAuthConsent>();
                services.AddSingleton<IOAuthConsent>(Consent);
                services.RemoveAll<IOAuthGrants>();
                services.AddSingleton<IOAuthGrants>(Grants);
                services.RemoveAll<IOAuthClients>();
                services.AddSingleton<IOAuthClients>(Clients);
            },
        };
    }

    internal sealed class StandInConsent : IOAuthConsent
    {
        public StandInConsent()
        {
            Client = new OAuthClient(ClientId, "Desk Helper", [Loopback + "/callback"], "native", OAuthClientRegistration.Dynamic,
                null, null, null, DateTimeOffset.Parse("2026-09-25T08:00:00Z", System.Globalization.CultureInfo.InvariantCulture), false);
            View = new ConsentView(Request, Loopback, LoopbackRedirect: true, LocalAllowed: true, ReplacesGrant: false,
                AgentName: "a new connected assistant", KeptAgentModelLocation: null, KeptAgentModelVendor: null,
                ConnectedAssistants: 1, Bound: 3);
        }

        public OAuthClient Client { get; set; }
        public OAuthAuthorizationRequest Request =>
            new(Client, Loopback + "/callback", "invented-challenge-0123456789abcdef0123456789ab", "read", "https://localhost/mcp", "s1");
        public AuthorizationCheck? Check { get; set; }
        public ConsentView View { get; set; }
        public string ApproveTarget { get; set; } = PortalOAuthPagesTests.ApproveTarget;
        public Exception? Refusal { get; set; }

        public List<IReadOnlyDictionary<string, IReadOnlyList<string>>> Checked { get; } = [];
        public List<(Guid User, IReadOnlyDictionary<string, IReadOnlyList<string>> Parameters, ModelLocation Location, string? Vendor)> Approved { get; } = [];
        public List<(Guid User, IReadOnlyDictionary<string, IReadOnlyList<string>> Parameters)> Denied { get; } = [];

        public Task<AuthorizationCheck> CheckAsync(IReadOnlyDictionary<string, IReadOnlyList<string>> parameters, CancellationToken ct)
        {
            Checked.Add(parameters);
            return Task.FromResult(Check ?? new AuthorizationCheck(AuthorizationCheckOutcome.Valid, Request, null, null, null));
        }

        public Task<ConsentView> ViewAsync(Guid userId, OAuthAuthorizationRequest request, CancellationToken ct) =>
            Task.FromResult(View with { Request = request });

        public Task<string> ApproveAsync(Guid userId, IReadOnlyDictionary<string, IReadOnlyList<string>> parameters,
            ModelLocation modelLocation, string? modelVendor, CancellationToken ct)
        {
            if (Refusal is { } refusal) throw refusal;
            Approved.Add((userId, parameters, modelLocation, modelVendor));
            return Task.FromResult(ApproveTarget);
        }

        public Task<string> DenyAsync(Guid userId, IReadOnlyDictionary<string, IReadOnlyList<string>> parameters, CancellationToken ct)
        {
            if (Refusal is { } refusal) throw refusal;
            Denied.Add((userId, parameters));
            return Task.FromResult(DenyTarget);
        }
    }

    internal sealed class StandInGrants : IOAuthGrants
    {
        private static readonly DateTimeOffset At = DateTimeOffset.Parse("2026-09-25T08:00:00Z", System.Globalization.CultureInfo.InvariantCulture);

        public List<OAuthGrantFilter> Filters { get; } = [];
        public List<(AdminActor Actor, string Id)> Revoked { get; } = [];

        private static OAuthGrantView Grant(string id, OAuthGrantStatus status, int refreshes, string? reason) => new(
            id, ClientId, "Desk Helper", OAuthClientRegistration.Dynamic, Guid.NewGuid(), "alice", Guid.NewGuid(),
            "oauth-0a1b2c3d4e5f60718293a4b5", status, At, At.AddDays(90), refreshes == 0 ? null : At.AddHours(2), refreshes,
            reason is null ? null : At.AddHours(3), reason);

        public Task<IReadOnlyList<OAuthGrantView>> ListOwnAsync(Guid userId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<OAuthGrantView>>([]);

        public Task RevokeOwnAsync(Guid userId, string grantId, CancellationToken ct) => Task.CompletedTask;

        public Task<IReadOnlyList<OAuthGrantView>> ListAllAsync(OAuthGrantFilter filter, CancellationToken ct)
        {
            Filters.Add(filter);
            var grants = new List<OAuthGrantView> { Grant("grant-live", OAuthGrantStatus.Live, 3, null) };
            if (filter == OAuthGrantFilter.All)
                grants.Add(Grant("grant-ended", OAuthGrantStatus.EndedByDisable, 0, "owner disabled or password changed"));
            return Task.FromResult<IReadOnlyList<OAuthGrantView>>(grants);
        }

        public Task RevokeAsync(AdminActor actor, string grantId, CancellationToken ct)
        {
            Revoked.Add((actor, grantId));
            return Task.CompletedTask;
        }
    }

    internal sealed class StandInClients : IOAuthClients
    {
        public StandInClients() =>
            Listed.Add(new OAuthClientListing(new StandInConsent().Client, "192.0.2.10",
                DateTimeOffset.Parse("2026-09-25T08:05:00Z", System.Globalization.CultureInfo.InvariantCulture), 1));

        public List<OAuthClientListing> Listed { get; } = [];
        public Exception? Refusal { get; set; }
        public List<(AdminActor Actor, string? Id, string Name, IReadOnlyList<string> RedirectUris, ModelLocation? Location, string? Vendor)> Added { get; } = [];
        public List<(string Verb, string Id)> Changed { get; } = [];

        public Task<IReadOnlyList<OAuthClientListing>> ListAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<OAuthClientListing>>(Listed.ToList());

        public Task<string> AddAsync(AdminActor actor, string? id, string name, IReadOnlyList<string> redirectUris,
            ModelLocation? modelLocation, string? modelVendor, CancellationToken ct)
        {
            if (Refusal is { } refusal) throw refusal;
            Added.Add((actor, id, name, redirectUris, modelLocation, modelVendor));
            return Task.FromResult("prem_cli_made0000000000000000");
        }

        public List<(AdminActor Actor, string Id, byte[] Document, ModelLocation? Location, string? Vendor)> AddedFromDocument { get; } = [];
        public List<(AdminActor Actor, string Id, byte[] Document)> Replaced { get; } = [];

        /// <summary>The hash a replace reports as the one it replaced; null reports the new document's own, which is no change.</summary>
        public string? PreviousSha256 { get; set; }

        // Every call is recorded before anything is judged, so a test sees what
        // reached the flow; the document is then checked by the flow's own parse,
        // whose refusals are the sentences a page must show as they are.
        public Task<OAuthClientDocument> AddFromDocumentAsync(AdminActor actor, string id, ReadOnlyMemory<byte> document,
            ModelLocation? modelLocation, string? modelVendor, CancellationToken ct)
        {
            AddedFromDocument.Add((actor, id, document.ToArray(), modelLocation, modelVendor));
            if (Refusal is { } refusal) throw refusal;
            return Task.FromResult(OAuthClientDocument.Parse(document.Span, id));
        }

        public Task<OAuthClientDocumentReplaced> ReplaceDocumentAsync(AdminActor actor, string id, ReadOnlyMemory<byte> document,
            CancellationToken ct)
        {
            Replaced.Add((actor, id, document.ToArray()));
            if (Refusal is { } refusal) throw refusal;
            var parsed = OAuthClientDocument.Parse(document.Span, id);
            return Task.FromResult(new OAuthClientDocumentReplaced(parsed, PreviousSha256 ?? parsed.Sha256));
        }

        public Task DisableAsync(AdminActor actor, string clientId, CancellationToken ct)
        {
            Changed.Add(("disable", clientId));
            return Task.CompletedTask;
        }

        public Task EnableAsync(AdminActor actor, string clientId, CancellationToken ct)
        {
            Changed.Add(("enable", clientId));
            return Task.CompletedTask;
        }

        public Task RemoveAsync(AdminActor actor, string clientId, CancellationToken ct)
        {
            Changed.Add(("remove", clientId));
            return Task.CompletedTask;
        }
    }
}

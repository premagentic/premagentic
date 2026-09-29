using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Premagentic.Core.Identity;
using Microsoft.AspNetCore.Http.Features;

namespace Premagentic.Tests;

/// <summary>
/// A form with bounds of its own, past them: the host reads such a form before
/// the page runs, and answers a form past its bounds with the page's own
/// redirect and sentence, the page never running. The two document forms of
/// the Clients page carry such bounds: twice the flow's bound in a value or a
/// file, five times in the whole body. The session cases of the document form
/// are in <see cref="PortalOAuthPagesTests"/>; here are the rest, the whole
/// body on Kestrel, and what the host keeps as it was. Every name, address and
/// document is invented. The tests require a running Docker daemon.
/// </summary>
public sealed class PortalFormBoundsTests(DatastoreTestDatabase server) : IClassFixture<DatastoreTestDatabase>
{
    private const string ClientId = "https://notes.example/client";
    private const string Header = "X-Test-Sign-In";
    private const int ValueBound = 2 * OAuthClientDocument.MaxBytes;
    private const int BodyBound = 5 * OAuthClientDocument.MaxBytes;

    private static readonly string Document =
        "{\n" + $"  \"client_id\": \"{ClientId}\",\n" + "  \"client_name\": \"Field Notes\",\n" +
        "  \"redirect_uris\": [\"https://notes.example/callback\"],\n" + "  \"token_endpoint_auth_method\": \"none\"\n}";

    [Fact]
    public async Task A_replace_form_over_its_bounds_is_answered_on_the_clients_page_in_the_flows_sentence()
    {
        var stand = new PortalOAuthPagesTests.StandIns();
        await using var p = await PortalWorld.NewAsync(server, options: stand.Options());

        using var response = await p.PostMultipartAsync("/portal/oauth/clients/replace", p.Admin,
            [("client", ClientId), ("document", new string(' ', ValueBound + 1))]);

        Assert.Equal(OAuthClientDocument.TooLarge, ErrorOf(response));
        Assert.Empty(stand.Clients.Replaced);
    }

    [Fact]
    public async Task At_its_bounds_a_document_form_still_reaches_the_page()
    {
        var stand = new PortalOAuthPagesTests.StandIns();
        await using var p = await PortalWorld.NewAsync(server, options: stand.Options());

        // A pasted value and a file, each exactly twice the flow's bound: the
        // host reads the form whole and the page hands the flow the document,
        // which the flow then refuses in its own words. (A pasted value of spaces
        // alone would be no document to the page, so the value is letters.)
        using var pasted = await p.PostMultipartAsync("/portal/oauth/clients/document", p.Admin,
            [("id", ClientId), ("document", new string('x', ValueBound))]);
        using var file = await p.PostMultipartAsync("/portal/oauth/clients/document", p.Admin, [("id", ClientId)],
            [("documentFile", "notes-client.json", new byte[ValueBound])]);

        Assert.Equal(2, stand.Clients.AddedFromDocument.Count);
        Assert.Equal(ValueBound, stand.Clients.AddedFromDocument[0].Document.Length);
        Assert.Equal(OAuthClientDocument.MaxBytes + 1, stand.Clients.AddedFromDocument[1].Document.Length);
    }

    [Fact]
    public async Task In_the_trusted_header_mode_a_form_over_its_bounds_is_answered_the_same_way()
    {
        var stand = new PortalOAuthPagesTests.StandIns();
        var options = stand.Options();
        options.SignInHeader = Header;
        await using var w = await ApiWorld.NewAsync(server, options);
        using var client = w.Host.Client(true, new PassThrough());

        // No session, so no anti-forgery field: the host reads the form for its
        // bounds all the same, and the page never meets them.
        var request = Api.Request(HttpMethod.Post, "/portal/oauth/clients/document", header: (Header, "carol"));
        request.Content = Multipart(("id", ClientId), ("document", new string(' ', ValueBound + 1)));
        request.Headers.Add("Origin", PortalWorld.Origin);
        using var response = await client.SendAsync(request);

        Assert.Equal(OAuthClientDocument.TooLarge, ErrorOf(response));
        Assert.Empty(stand.Clients.AddedFromDocument);
    }

    [Fact]
    public async Task Nothing_of_a_form_read_in_part_reaches_the_log_the_answer_or_the_change_record()
    {
        var logs = new List<string>();
        var stand = new PortalOAuthPagesTests.StandIns();
        var options = stand.Options();
        options.Logs = logs;
        await using var p = await PortalWorld.NewAsync(server, options: options);
        var changesBefore = await p.ScalarAsync("SELECT count(*) FROM prem_config.admin_event");

        // A form past its bounds with a wrong anti-forgery token: the host answers
        // before the token is looked at, so it is refused as too large, never as
        // forged, and nothing of what it carried goes anywhere.
        const string marker = "planted-form-marker-5d0c";
        const string wrongToken = "planted-wrong-token-81af";
        var request = Api.Request(HttpMethod.Post, "/portal/oauth/clients/document", session: p.Admin, antiForgery: false);
        request.Content = Multipart(("prem_antiforgery", wrongToken), ("id", ClientId),
            ("document", marker + new string(' ', ValueBound + 1 - marker.Length)));
        request.Headers.Add("Origin", PortalWorld.Origin);
        using var response = await p.Client.SendAsync(request);

        Assert.Equal(OAuthClientDocument.TooLarge, ErrorOf(response));
        var location = response.Headers.Location!.OriginalString;
        string[] lines;
        lock (logs) lines = [.. logs];
        foreach (var planted in new[] { marker, wrongToken, p.Admin.AntiForgeryToken })
        {
            Assert.DoesNotContain(planted, location, StringComparison.Ordinal);
            Assert.DoesNotContain(lines, line => line.Contains(planted, StringComparison.Ordinal));
        }
        Assert.Single(lines, line => line.Contains("the form passed its bound (a value or a file)", StringComparison.Ordinal));
        Assert.Equal(changesBefore, await p.ScalarAsync("SELECT count(*) FROM prem_config.admin_event"));
        Assert.Empty(stand.Clients.AddedFromDocument);

        // Within its bounds, the same wrong token is refused as it always was.
        var inBound = Api.Request(HttpMethod.Post, "/portal/oauth/clients/document", session: p.Admin, antiForgery: false);
        inBound.Content = Multipart(("prem_antiforgery", wrongToken), ("id", ClientId), ("document", Document));
        inBound.Headers.Add("Origin", PortalWorld.Origin);
        using var forged = await p.Client.SendAsync(inBound);
        Assert.Equal(HttpStatusCode.Forbidden, forged.StatusCode);
        Assert.Empty(stand.Clients.AddedFromDocument);
    }

    [Fact]
    public async Task A_form_past_its_bound_on_an_endpoint_without_bounds_of_its_own_is_refused_as_before()
    {
        var logs = new List<string>();
        var options = new PortalOAuthPagesTests.StandIns().Options();
        options.Logs = logs;
        await using var p = await PortalWorld.NewAsync(server, options: options);
        var changesBefore = await p.ScalarAsync("SELECT count(*) FROM prem_config.admin_event");

        // A group's name one character over the framework's own bound for a
        // value: the reader's refusal leaves the host unhandled, a 500, as it
        // always did, never as a page's redirect.
        HttpStatusCode? status = null;
        Uri? location = null;
        var thrown = await Record.ExceptionAsync(async () =>
        {
            using var response = await p.PostAsync("/portal/groups", p.Admin, [("name", new string('g', new FormOptions().ValueLengthLimit + 1))]);
            (status, location) = (response.StatusCode, response.Headers.Location);
        });

        Assert.Null(thrown);
        Assert.Equal(HttpStatusCode.InternalServerError, status);
        Assert.Null(location);
        string[] lines;
        lock (logs) lines = [.. logs];
        Assert.DoesNotContain(lines, line => line.Contains("the form passed its bound", StringComparison.Ordinal));
        Assert.Equal(changesBefore, await p.ScalarAsync("SELECT count(*) FROM prem_config.admin_event"));
    }

    [Fact]
    public async Task A_request_with_no_caller_has_no_form_read()
    {
        var logs = new List<string>();
        var stand = new PortalOAuthPagesTests.StandIns();
        var options = stand.Options();
        options.Logs = logs;
        await using var p = await PortalWorld.NewAsync(server, options: options);

        var request = Api.Request(HttpMethod.Post, "/portal/oauth/clients/document");
        request.Content = Multipart(("id", ClientId), ("document", new string(' ', ValueBound + 1)));
        request.Headers.Add("Origin", PortalWorld.Origin);
        using var response = await p.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        string[] lines;
        lock (logs) lines = [.. logs];
        Assert.DoesNotContain(lines, line => line.Contains("the form passed its bound", StringComparison.Ordinal));
        Assert.Empty(stand.Clients.AddedFromDocument);
    }

    [Fact]
    public async Task On_Kestrel_a_body_one_byte_over_its_bound_is_answered_on_the_clients_page_and_one_at_it_reaches_the_page()
    {
        var stand = new PortalOAuthPagesTests.StandIns();
        var options = stand.Options();
        options.Kestrel = true;
        options.AllowHttpSignIn = true;
        await using var p = await PortalWorld.NewAsync(server, options: options);
        var origin = p.Client.BaseAddress!.GetLeftPart(UriPartial.Authority);

        // The same form both times, every value under its own bound, the whole
        // body exactly at the bound and then one byte over it. The client asks for
        // 100 Continue, so the host's answer does not race the upload; a browser
        // does not ask, which is the stated limit this test does not cover.
        using var at = await p.Client.SendAsync(Sized(p.Admin, origin, BodyBound));
        using var over = await p.Client.SendAsync(Sized(p.Admin, origin, BodyBound + 1));

        Assert.NotNull(DoneOf(at));
        Assert.Single(stand.Clients.AddedFromDocument);
        Assert.Equal(OAuthClientDocument.TooLarge, ErrorOf(over));
        Assert.Single(stand.Clients.AddedFromDocument);
    }

    /// <summary>
    /// A document form whose whole body is exactly <paramref name="bytes"/> long:
    /// the token, the address, a small valid document, and padding the page never
    /// reads, each value under its own bound.
    /// </summary>
    private static HttpRequestMessage Sized(ApiSession session, string origin, int bytes)
    {
        MultipartFormDataContent Build(int last) => Multipart(("prem_antiforgery", session.AntiForgeryToken), ("id", ClientId),
            ("document", Document), ("pad1", new string('p', ValueBound)), ("pad2", new string('p', ValueBound)), ("pad3", new string('p', last)));
        long probe;
        using (var empty = Build(0)) probe = empty.Headers.ContentLength!.Value;
        var content = Build(checked((int)(bytes - probe)));
        Assert.Equal((long)bytes, content.Headers.ContentLength!.Value);
        var request = Api.Request(HttpMethod.Post, "/portal/oauth/clients/document", session: session, antiForgery: false);
        request.Content = content;
        request.Headers.Add("Origin", origin);
        request.Headers.ExpectContinue = true;
        return request;
    }

    /// <summary>A multipart form as a page's upload form sends one, with a fixed boundary so its length is known.</summary>
    private static MultipartFormDataContent Multipart(params (string Name, string Value)[] fields)
    {
        var content = new MultipartFormDataContent("prem-test-boundary");
        foreach (var (name, value) in fields)
        {
            var part = new ByteArrayContent(Encoding.UTF8.GetBytes(value));
            part.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
            content.Add(part, name);
        }
        return content;
    }

    private static string? ErrorOf(HttpResponseMessage response) => Said(response, "error");

    private static string? DoneOf(HttpResponseMessage response) => Said(response, "done");

    /// <summary>What a change's redirect says, decoded, the way the portal writes it.</summary>
    private static string? Said(HttpResponseMessage response, string which)
    {
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var location = response.Headers.Location!.OriginalString;
        Assert.StartsWith(OAuthPaths.Clients + "?", location, StringComparison.Ordinal);
        var query = location[(location.IndexOf('?', StringComparison.Ordinal) + 1)..];
        return query.Split('&').Select(pair => pair.Split('=', 2)).Where(pair => pair.Length == 2 && pair[0] == which)
            .Select(pair => Uri.UnescapeDataString(pair[1])).SingleOrDefault();
    }
}

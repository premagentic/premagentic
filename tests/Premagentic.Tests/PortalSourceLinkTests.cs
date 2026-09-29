using System.Net;
using Premagentic.Core;

namespace Premagentic.Tests;

/// <summary>
/// The link to this program's source, at the foot of every portal page, signed
/// in or not: on the sign-in page, which anybody who reaches the portal can
/// open, and on a page behind it. Requires a running Docker daemon.
/// </summary>
public sealed class PortalSourceLinkTests(DatastoreTestDatabase server) : IClassFixture<DatastoreTestDatabase>
{
    private const string Footer =
        "<footer class=\"source\"><a href=\"https://github.com/premagentic/premagentic\">Source</a></footer>";

    [Fact]
    public async Task The_source_is_linked_on_the_sign_in_page_and_on_a_page_behind_it()
    {
        await using var p = await PortalWorld.NewAsync(server, okfBundle: false);

        using var signIn = await p.GetAsync("/portal/sign-in");
        Assert.Equal(HttpStatusCode.OK, signIn.StatusCode);
        var signedOut = await signIn.Content.ReadAsStringAsync();

        using var search = await p.GetAsync("/portal/search", p.Member);
        Assert.Equal(HttpStatusCode.OK, search.StatusCode);
        var signedIn = await search.Content.ReadAsStringAsync();

        // Once on each: the sign-in page carries no navigation, so the link is
        // the page's own and not the sidebar's.
        Assert.Equal(1, signedOut.Split(Footer).Length - 1);
        Assert.Equal(1, signedIn.Split(Footer).Length - 1);
        Assert.Equal("https://github.com/premagentic/premagentic", BuildVersion.SourceUrl);
    }
}

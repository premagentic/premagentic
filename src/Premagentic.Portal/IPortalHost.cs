using Premagentic.Core.Identity;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Premagentic.Portal;

/// <summary>
/// A person signed in to the portal, as the host resolved them for this request.
/// </summary>
/// <param name="Caller">The person's rights as they are at this moment, for searching as themselves.</param>
/// <param name="AntiForgeryToken">
/// The token every form that changes state carries as a hidden field. Null when
/// the person came through a trusted header, which has no session to bind a
/// token to; the portal then relies on its same-origin check alone.
/// </param>
public sealed record PortalPerson(User User, Caller Caller, string? AntiForgeryToken);

/// <summary>
/// What the portal needs from the process that hosts it. The host owns sign-in,
/// sessions and the anti-forgery check; the portal owns its pages, and trusts
/// the host to have refused a request with no person before a page runs.
/// </summary>
public interface IPortalHost
{
    /// <summary>The person this request runs as, or null for an agent or for nobody.</summary>
    PortalPerson? SignedIn(HttpContext http);

    /// <summary>The tenant this process serves.</summary>
    Guid TenantId(HttpContext http);

    /// <summary>Whether ingest embeds with the "title > heading" prefix, so a run started here matches the CLI's.</summary>
    bool HeadingPrefix(HttpContext http);

    /// <summary>Marks endpoints anyone may reach, signed in or not: the sign-in page and the assets.</summary>
    void Open(IEndpointConventionBuilder endpoints);

    /// <summary>
    /// Marks endpoints only a signed-in person may reach: an agent token is
    /// refused, and a signed-out browser is sent to the sign-in page.
    /// </summary>
    void People(IEndpointConventionBuilder endpoints);
}

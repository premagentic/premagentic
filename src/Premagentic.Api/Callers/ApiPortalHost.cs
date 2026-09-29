// The portal's registration, app.MapPremagenticPortal(...), is one line in
// Program.cs; this brings its namespace in for the whole project.
global using Premagentic.Portal;

using Premagentic.Core.Identity;

namespace Premagentic.Api.Callers;

/// <summary>
/// Hosts the administration portal in this process, behind the same caller
/// middleware as the API: the portal gets the person the middleware resolved,
/// and the middleware keeps sign-in, sessions and the anti-forgery check.
/// </summary>
internal sealed class ApiPortalHost : IPortalHost
{
    public const string SignInPath = PortalEndpoints.Root + "/sign-in";

    public PortalPerson? SignedIn(HttpContext http)
    {
        var caller = http.Caller();
        return caller.Source is CallerSource.Session or CallerSource.TrustedHeader && caller.User is { } user
            ? new PortalPerson(user, caller.Caller, caller.SessionValue is { } session ? SessionStore.AntiForgeryToken(session) : null)
            : null;
    }

    public Guid TenantId(HttpContext http) => http.RequestServices.GetRequiredService<Deployment>().TenantId;

    public bool HeadingPrefix(HttpContext http) => http.RequestServices.GetRequiredService<ApiSettings>().HeadingPrefix;

    public void Open(IEndpointConventionBuilder endpoints) => endpoints.WithMetadata(CallerRequirement.Nobody);

    public void People(IEndpointConventionBuilder endpoints) => endpoints.WithMetadata(CallerRequirement.PeopleOnly(SignInPath));
}

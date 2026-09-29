using Premagentic.Core.Identity;
using Premagentic.Core.Identity.SignIn;

namespace Premagentic.Api.Callers;

/// <summary>
/// An <see cref="ISignInRequest"/> over one HTTP request. The adapters see the
/// headers and the cookies and nothing else, which is why the seam is shaped
/// that way: an adapter needs no web host to write or to test.
/// </summary>
internal sealed class HttpSignInRequest(HttpContext http) : ISignInRequest
{
    public string Header(string name) => http.Request.Headers[name].ToString();

    public string? Cookie(string name) => http.Request.Cookies[name];
}

/// <summary>
/// The ways a person may prove who they are on this deployment, in the order
/// they are asked. The trusted-header mode comes first when it is on, so a
/// proxy's word decides before a cookie does, exactly as it did before the seam
/// existed; the session cookie a password sign-in issued comes last.
/// <para>
/// The first adapter to claim the request decides it, whether it claims it for
/// somebody or for nobody. Agents are not here: an agent presents a token and
/// is not a person signing in.
/// </para>
/// </summary>
internal sealed class SignInAdapters
{
    /// <param name="extra">
    /// Adapters an extension brought, asked after both built-ins. Last on
    /// purpose: what ships decides before what was added, so installing an
    /// extension cannot take over a way of signing in that already worked.
    /// </param>
    public SignInAdapters(ApiSettings settings, SessionStore sessions, IEnumerable<ISignInAdapter>? extra = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Session = new SessionSignInAdapter(sessions, CallerMiddleware.SessionCookie);
        TrustedHeader = settings.SignInHeader is { } header ? new TrustedHeaderSignInAdapter(header) : null;

        var ordered = new List<ISignInAdapter>();
        if (TrustedHeader is not null) ordered.Add(TrustedHeader);
        ordered.Add(Session);
        ordered.AddRange(extra ?? []);
        Ordered = ordered;
    }

    /// <summary>
    /// The session adapter, held by name because the session is the one
    /// credential the request itself has to keep carrying: its value binds the
    /// anti-forgery check and rides on the caller.
    /// </summary>
    public SessionSignInAdapter Session { get; }

    /// <summary>The trusted-header adapter, or null when the mode is off.</summary>
    public TrustedHeaderSignInAdapter? TrustedHeader { get; }

    public IReadOnlyList<ISignInAdapter> Ordered { get; }

    /// <summary>
    /// How a caller resolved by <paramref name="adapter"/> is recorded.
    /// <para>
    /// The two built-ins are recognized by being the very adapters this built,
    /// not by their names. A name is something an extension chooses, and an
    /// extension that called itself <c>password</c> would otherwise have its
    /// callers recorded as password sign-ins, which is a lie in the one place
    /// an auditor goes to find out how somebody got in. Everything this did not
    /// build is an extension, whatever it calls itself.
    /// </para>
    /// </summary>
    public CallerSource SourceOf(ISignInAdapter adapter)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        if (ReferenceEquals(adapter, Session)) return CallerSource.Session;
        return ReferenceEquals(adapter, TrustedHeader) ? CallerSource.TrustedHeader : CallerSource.Extension;
    }
}

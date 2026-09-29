using Premagentic.Core.Identity;

namespace Premagentic.Api.Callers;

/// <summary>How a caller proved who it is on this request.</summary>
public enum CallerSource
{
    /// <summary>A person, by the session cookie a password sign-in issued.</summary>
    Session = 1,

    /// <summary>A person, by the sign-in name an authenticating proxy wrote into the trusted header.</summary>
    TrustedHeader = 2,

    /// <summary>An agent, by its bearer token.</summary>
    AgentToken = 3,

    /// <summary>
    /// A person, by a sign-in adapter an extension brought. It reaches only
    /// accounts an administrator made, like the two built in.
    /// </summary>
    Extension = 4,
}

/// <summary>The caller one request runs as, resolved by <see cref="CallerMiddleware"/> before any endpoint runs.</summary>
/// <param name="User">The person, for <see cref="CallerSource.Session"/> and <see cref="CallerSource.TrustedHeader"/>.</param>
/// <param name="SessionValue">The session id, for <see cref="CallerSource.Session"/> only. Never logged.</param>
internal sealed record HttpCaller(Caller Caller, CallerSource Source, User? User, string? SessionValue)
{
    /// <summary>Never includes the session id, so it is safe in a log line.</summary>
    public override string ToString() => $"{Source} {Caller.Scope.AuditLabel}";
}

/// <summary>
/// Where an endpoint finds its caller. There is no fallback: an endpoint that
/// runs without one throws, so a request can never quietly search as anybody.
/// </summary>
internal static class HttpCallerExtensions
{
    private static readonly object Key = new();

    public static HttpCaller Caller(this HttpContext http) =>
        http.Items[Key] as HttpCaller
        ?? throw new InvalidOperationException("No caller was resolved for this request, so it may not read anything.");

    public static void SetCaller(this HttpContext http, HttpCaller caller) => http.Items[Key] = caller;
}

/// <summary>
/// Endpoint metadata saying who may call an endpoint. An endpoint without it
/// needs a caller, a person or an agent, so a new endpoint is closed until
/// someone decides otherwise.
/// </summary>
/// <param name="RefusesAgents">A person only: an agent token is refused with a 403.</param>
/// <param name="SignInRedirect">
/// For a page a browser opens: where a signed-out GET or HEAD is sent, with a
/// 303, instead of the JSON 401. The page asked for rides along as
/// <c>return</c>.
/// </param>
internal sealed record CallerRequirement(bool Anonymous, bool AgentsOnly, bool RefusesAgents = false, string? SignInRedirect = null)
{
    /// <summary>Anyone, signed in or not: sign-in itself and the health check.</summary>
    public static CallerRequirement Nobody { get; } = new(Anonymous: true, AgentsOnly: false);

    /// <summary>A person or an agent. What an endpoint with no metadata gets.</summary>
    public static CallerRequirement PersonOrAgent { get; } = new(Anonymous: false, AgentsOnly: false);

    /// <summary>An agent presenting its token, and nothing else: the MCP endpoint.</summary>
    public static CallerRequirement AgentOnly { get; } = new(Anonymous: false, AgentsOnly: true);

    /// <summary>
    /// A person, by session or trusted header, and never an agent: pages for
    /// people. With <paramref name="signInRedirect"/>, a signed-out browser is
    /// sent to sign in rather than shown a 401.
    /// </summary>
    public static CallerRequirement PeopleOnly(string? signInRedirect = null)
    {
        if (signInRedirect is not null && !(signInRedirect.StartsWith('/') && !signInRedirect.StartsWith("//", StringComparison.Ordinal)))
            throw new ArgumentException("A sign-in redirect is a path on this site, such as /portal/sign-in.", nameof(signInRedirect));
        return new(Anonymous: false, AgentsOnly: false, RefusesAgents: true, SignInRedirect: signInRedirect);
    }
}

/// <summary>The tenant this process serves, known once the database is ready at startup.</summary>
public sealed class Deployment
{
    private Guid? _tenantId;

    public Guid TenantId => _tenantId ?? throw new InvalidOperationException("The deployment's tenant is not known until startup has finished.");

    internal void Start(Guid tenantId) => _tenantId = tenantId;
}

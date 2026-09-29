namespace Premagentic.Core.Identity.SignIn;

/// <summary>
/// What one request carries that a sign-in adapter may read: its headers and
/// its cookies. Nothing else decides who is calling, so nothing else is here.
/// <para>
/// The abstraction exists so an adapter can be written, tested and shipped
/// without the web host: the API supplies one of these over its own request,
/// and a test supplies a handful of strings.
/// </para>
/// </summary>
public interface ISignInRequest
{
    /// <summary>
    /// The header's value, or an empty string when the request does not carry
    /// it. Header names compare case-insensitively, as they do on the wire.
    /// </summary>
    string Header(string name);

    /// <summary>The cookie's value, or null when the request does not carry it. Cookie names compare exactly.</summary>
    string? Cookie(string name);
}

/// <summary>
/// Who an adapter says is calling: a Premagentic sign-in name, and the groups
/// the system it spoke to reported, in that system's own terms.
/// </summary>
/// <param name="SignInName">
/// The name a Premagentic account is found by, or null when the request was
/// this adapter's and proved nobody: a session that has ended, a token that did
/// not verify. Null is not the same as returning no resolution at all. A
/// credential that failed has to refuse the request, not fall through to the
/// next adapter, or an expired cookie would become an invitation to be
/// somebody else. The account has to exist already; an adapter never creates
/// one, so a name nothing matches reaches nothing either.
/// </param>
/// <param name="ExternalGroups">
/// Group names or ids exactly as the external system gave them, before any
/// mapping. Empty when the adapter saw none. They never carry a role, and one
/// the deployment's principal mapper does not map, which is every one when no
/// mapper is loaded, is ignored rather than guessed at.
/// </param>
public sealed record SignInResolution(string? SignInName, IReadOnlyList<string> ExternalGroups)
{
    /// <summary>An adapter that sees no groups, which is the usual case.</summary>
    public SignInResolution(string signInName) : this(signInName, [])
    {
    }

    /// <summary>
    /// The request was this adapter's and proved nobody. The request is refused
    /// there and then; no later adapter is asked.
    /// </summary>
    public static SignInResolution Nobody { get; } = new(null, []);
}

/// <summary>
/// One way a person proves who they are. The password sign-in and the
/// trusted-header mode are the two built in; anything else, such as a company
/// directory, arrives as an extension through this same seam.
/// <para>
/// An adapter answers one question about one request: which Premagentic
/// account is this. It resolves a person to an account that already exists. It
/// never creates an account, never grants a role, and never widens what anyone
/// may read: the groups it reports become Premagentic groups only through the
/// deployment's principal mapper, and what it does not map, or everything when
/// there is none, is ignored. An adapter that
/// lied about every name would still reach only accounts an administrator made
/// and only the rules those accounts already sit under.
/// </para>
/// </summary>
public interface ISignInAdapter
{
    /// <summary>
    /// How this adapter is named in settings, on the health page and in the
    /// log: lower case, shaped like a chunker name.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Who this request is, or null when the request is not this adapter's.
    /// Returning null is not a refusal: the next adapter is asked, and a request
    /// no adapter claims has no caller, which is how a request with nothing on
    /// it is treated today. To claim a request and refuse it, return
    /// <see cref="SignInResolution.Nobody"/>.
    /// </summary>
    Task<SignInResolution?> ResolveAsync(ISignInRequest request, CancellationToken ct = default);
}

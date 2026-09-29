namespace Premagentic.Core.Identity.SignIn;

/// <summary>
/// The trusted-header mode: an authenticating proxy in front of Premagentic
/// writes the person's Premagentic sign-in name into a header, and Premagentic
/// takes the proxy's word for who they are.
/// <para>
/// It takes the proxy's word and nothing else. The name is looked up among
/// accounts an administrator made, so a header naming somebody unknown reaches
/// nothing, and no role and no group comes from the header. A deployment that
/// turns this on is saying that nothing but the proxy can reach the port.
/// </para>
/// </summary>
public sealed class TrustedHeaderSignInAdapter(string headerName) : ISignInAdapter
{
    public const string AdapterName = "trusted-header";

    private readonly string _headerName = string.IsNullOrWhiteSpace(headerName)
        ? throw new ArgumentException("The trusted-header mode needs the name of the header the proxy writes.", nameof(headerName))
        : headerName.Trim();

    public string Name => AdapterName;

    /// <summary>The header names somebody, or the request is not this adapter's.</summary>
    public Task<SignInResolution?> ResolveAsync(ISignInRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var name = request.Header(_headerName).Trim();
        return Task.FromResult<SignInResolution?>(name.Length == 0 ? null : new SignInResolution(name));
    }
}

/// <summary>
/// The password sign-in, on the requests that follow it: the session cookie it
/// issued says which account signed in, and this reads it back.
/// <para>
/// Signing in is the other half and is not here. A password is checked once, by
/// <see cref="SignInService"/>, which issues the session; every request after
/// that carries the cookie and no password. So what an adapter can answer about
/// a request is what this answers: which account this session belongs to.
/// </para>
/// <para>
/// A cookie that resolves to no session claims the request all the same, and
/// refuses it. Falling through would mean an expired cookie quietly turned into
/// an anonymous request, or into whatever the next adapter made of it.
/// </para>
/// </summary>
public sealed class SessionSignInAdapter(SessionStore sessions, string cookieName) : ISignInAdapter
{
    public const string AdapterName = "password";

    public string Name => AdapterName;

    public async Task<SignInResolution?> ResolveAsync(ISignInRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Cookie(cookieName) is not { Length: > 0 } value) return null;

        var user = await sessions.FindUserAsync(value, ct);
        return user is null ? SignInResolution.Nobody : new SignInResolution(user.Name);
    }
}

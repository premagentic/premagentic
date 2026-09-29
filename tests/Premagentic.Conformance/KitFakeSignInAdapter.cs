using System.Security.Cryptography;
using System.Text;
using Premagentic.Core.Identity.SignIn;

namespace Premagentic.Conformance;

/// <summary>
/// A sign-in adapter the kit carries to prove its own fixture: correct as
/// made, and wrong in one chosen way when asked, so each rule of
/// <see cref="SignInAdapterConformance"/> can be seen to fail. It reads one
/// header holding a name and a signature over it with a key made for each
/// instance, the shape of a real adapter that verifies what it reads. Not for
/// a deployment.
/// </summary>
public sealed class KitFakeSignInAdapter(KitFakeSignInAdapter.Flaw flaw = KitFakeSignInAdapter.Flaw.None) : ISignInAdapter
{
    /// <summary>The one thing it does wrong, or nothing.</summary>
    public enum Flaw
    {
        None,

        /// <summary>A name no setting could hold.</summary>
        UnusableName,

        /// <summary>A request with nothing on it is a fixed person.</summary>
        SignsInAnEmptyRequest,

        /// <summary>The name in the header is believed without its signature.</summary>
        BelievesAnyCredential,
    }

    public const string HeaderName = "X-Kit-Credential";

    private readonly byte[] _key = RandomNumberGenerator.GetBytes(32);

    public string Name => flaw == Flaw.UnusableName ? "Kit Adapter!" : "kit-fake";

    /// <summary>A credential this adapter will verify, for <paramref name="signInName"/>.</summary>
    public string Issue(string signInName) => $"{signInName}.{Sign(signInName)}";

    public Task<SignInResolution?> ResolveAsync(ISignInRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var value = request.Header(HeaderName).Trim();
        if (value.Length == 0)
            return Task.FromResult(flaw == Flaw.SignsInAnEmptyRequest ? new SignInResolution("kit-person") : null);

        var dot = value.LastIndexOf('.');
        var name = dot > 0 ? value[..dot] : value;
        var verified = dot > 0 && CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(value[(dot + 1)..]), Encoding.ASCII.GetBytes(Sign(name)));
        return Task.FromResult<SignInResolution?>(verified || flaw == Flaw.BelievesAnyCredential
            ? new SignInResolution(name, ["kit-directory-group"])
            : SignInResolution.Nobody);
    }

    private string Sign(string name) => Convert.ToHexStringLower(HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes(name)));
}

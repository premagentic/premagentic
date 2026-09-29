namespace Premagentic.Api;

/// <summary>
/// What the API reads from configuration at startup. Environment variables are
/// part of configuration, so each setting is set by the variable of its name.
/// </summary>
/// <param name="SignInHeader">
/// The trusted-header mode, off when null: the header an authenticating proxy
/// writes a Premagentic sign-in name into (<c>PREM_SIGN_IN_HEADER</c>).
/// </param>
/// <param name="AllowHttpSignIn">
/// Password sign-in over plain HTTP, for development only
/// (<c>PREM_ALLOW_HTTP_SIGN_IN=1</c>). Off unless set exactly.
/// </param>
public sealed record ApiSettings(
    string TenantKey,
    string TenantName,
    bool HeadingPrefix,
    string? SignInHeader,
    bool AllowHttpSignIn)
{
    public const string SignInHeaderKey = "PREM_SIGN_IN_HEADER";
    public const string AllowHttpSignInKey = "PREM_ALLOW_HTTP_SIGN_IN";

    // An earlier build's header carried principals. Its meaning changed, so a deployment
    // that still sets it is stopped rather than read the new way unannounced.
    private const string RetiredPrincipalHeaderKey = "PREM_PRINCIPAL_HEADER";

    /// <exception cref="InvalidOperationException">A setting is retired or does not have one of its allowed values.</exception>
    public static ApiSettings From(IConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);

        if (!string.IsNullOrWhiteSpace(config[RetiredPrincipalHeaderKey]))
            throw new InvalidOperationException(
                $"{RetiredPrincipalHeaderKey} is no longer read: Premagentic no longer takes principals from a header. " +
                $"If your proxy signs people in, set {SignInHeaderKey} to the header it writes the person's Premagentic " +
                $"sign-in name into, and unset {RetiredPrincipalHeaderKey}.");

        var allowHttp = config[AllowHttpSignInKey];
        if (!string.IsNullOrEmpty(allowHttp) && allowHttp is not ("0" or "1"))
            throw new InvalidOperationException($"{AllowHttpSignInKey} is 1 to allow password sign-in over plain HTTP, or 0 or unset to refuse it.");

        var header = config[SignInHeaderKey];
        return new ApiSettings(
            TenantKey: NonEmpty(config["PREM_TENANT_KEY"]) ?? "default",
            TenantName: NonEmpty(config["PREM_TENANT_NAME"]) ?? "Premagentic deployment",
            HeadingPrefix: config["PREM_HEADING_PREFIX"] != "0",
            SignInHeader: string.IsNullOrWhiteSpace(header) ? null : header.Trim(),
            AllowHttpSignIn: allowHttp == "1");
    }

    private static string? NonEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;
}

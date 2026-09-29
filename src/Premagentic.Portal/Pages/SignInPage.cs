using Premagentic.Portal.Html;
using Microsoft.AspNetCore.Http;

namespace Premagentic.Portal.Pages;

/// <summary>
/// The sign-in page, open to anyone. The form is sent by the portal's script to
/// the API's session endpoint, which checks the password, throttles, locks out
/// and sets the cookie; the page itself knows nothing about passwords.
/// </summary>
internal static class SignInPage
{
    public static IResult Show(HttpContext http)
    {
        var target = SafeReturn(http.Request.Query["return"].FirstOrDefault());
        var body = M.H($"""
            <div class="sign-in-card">
            <video class="logo motion" src="/portal/assets/logo-loop.mp4" poster="/portal/assets/logo.png" width="256" height="256" autoplay loop muted playsinline aria-hidden="true"></video>
            <img class="logo still" src="/portal/assets/logo.png" alt="" width="239" height="256">
            <h1>PremAgentic</h1>
            <form data-sign-in data-return="{target}">
            <label>Sign-in name <input type="text" name="signInName" autocomplete="username" required></label>
            <label>Password <input type="password" name="password" autocomplete="current-password" required></label>
            <button type="submit">Sign in</button>
            <p class="error-text" data-sign-in-message role="alert"></p>
            </form>
            <noscript><p class="error">Signing in needs JavaScript, which this page loads from this server only.</p></noscript>
            </div>
            """);
        // The card carries the page's heading, so the shell draws none.
        return Layout.Html(Layout.Document("Sign in", Markup.Empty, Markup.Empty, body, antiForgeryToken: null,
            bodyClass: "sign-in", heading: false));
    }

    /// <summary>
    /// Where to go after signing in: a portal path only, never another site, so
    /// the sign-in page cannot be used to send someone elsewhere.
    /// </summary>
    internal static string SafeReturn(string? requested) =>
        requested is { Length: > 0 } path
        && path.StartsWith(PortalEndpoints.Root, StringComparison.Ordinal)
        && !path.StartsWith("//", StringComparison.Ordinal)
        && !path.Contains('\\')
        && !path.Contains("://", StringComparison.Ordinal)
        && (path.Length == PortalEndpoints.Root.Length || path[PortalEndpoints.Root.Length] is '/' or '?')
            ? path
            : PortalEndpoints.Root;
}

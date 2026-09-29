using Premagentic.Core.Extensions;
using Premagentic.Core.Identity;
using Premagentic.Portal.Html;
using Premagentic.Portal.Pages;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Premagentic.Portal;

/// <summary>
/// The administration portal: server-rendered pages under <c>/portal</c>, with
/// one stylesheet and one script served from the portal itself.
/// <list type="bullet">
/// <item>Every page needs a signed-in person. A member sees only the search
/// page and the connect page, where they make and revoke their own agents; an
/// auditor sees every page but View as, which shows passages of documents'
/// text, and changes nothing but their own agents; an administrator changes
/// things. An endpoint that declares no <see cref="PortalNeed"/> is for
/// administrators.</item>
/// <item>Every change is written to the change record with the signed-in user,
/// in the same transaction as the change.</item>
/// <item>Every request that changes state must come from the portal's own
/// origin, and under a session carries the anti-forgery token as a hidden
/// field, which the host checks.</item>
/// </list>
/// </summary>
public static class PortalEndpoints
{
    public const string Root = "/portal";

    public static IEndpointRouteBuilder MapPremagenticPortal(this IEndpointRouteBuilder app, IPortalHost host)
    {
        ArgumentNullException.ThrowIfNull(host);

        var open = app.MapGroup(Root);
        host.Open(open);
        open.AddEndpointFilter(async (context, next) =>
        {
            Layout.SecurityHeaders(context.HttpContext.Response);
            return await next(context);
        });
        open.MapGet("/sign-in", (Delegate)SignInPage.Show);
        open.MapGet("/assets/{name}", (Delegate)Assets.Serve);

        var people = app.MapGroup(Root);
        host.People(people);
        people.AddEndpointFilter(new PortalAccessFilter(host));

        people.MapGet("", () => Results.Redirect("/portal/search")).Needs(PortalNeed.Person);
        people.MapGet("/search", (Delegate)SearchPages.Search).Needs(PortalNeed.Person);

        // Every person, for their own agents: the page decides whose they are.
        people.MapGet("/connect", (Delegate)ConnectPages.Show).Needs(PortalNeed.Person);
        people.MapPost("/connect", (Delegate)ConnectPages.Create).Needs(PortalNeed.Person);
        people.MapPost("/connect/{id:guid}/revoke", (Delegate)ConnectPages.Revoke).Needs(PortalNeed.Person);
        people.MapPost("/connect/{id:guid}/reissue", (Delegate)ConnectPages.Reissue).Needs(PortalNeed.Person);
        people.MapPost("/connect/{id:guid}/remove", (Delegate)ConnectPages.Remove).Needs(PortalNeed.Person);

        people.MapGet("/users", (Delegate)UserPages.List).Needs(PortalNeed.Reader);
        people.MapGet("/users/{name}", (Delegate)UserPages.Show).Needs(PortalNeed.Reader);
        people.MapPost("/users", (Delegate)UserPages.Create);
        people.MapPost("/users/{name}/enabled", (Delegate)UserPages.SetEnabled);
        people.MapPost("/users/{name}/password", (Delegate)UserPages.SetPassword);
        people.MapPost("/users/{name}/role", (Delegate)UserPages.SetRole);

        people.MapGet("/groups", (Delegate)GroupPages.List).Needs(PortalNeed.Reader);
        people.MapGet("/groups/{name}", (Delegate)GroupPages.Show).Needs(PortalNeed.Reader);
        people.MapPost("/groups", (Delegate)GroupPages.Create);
        people.MapPost("/groups/{name}/members", (Delegate)GroupPages.Members);
        people.MapPost("/groups/{name}/rename", (Delegate)GroupPages.Rename);
        people.MapPost("/groups/{name}/remove", (Delegate)GroupPages.Remove);

        people.MapGet("/agents", (Delegate)AgentPages.List).Needs(PortalNeed.Reader);
        people.MapGet("/agents/{name}", (Delegate)AgentPages.Show).Needs(PortalNeed.Reader);
        people.MapPost("/agents", (Delegate)AgentPages.Create);
        people.MapPost("/agents/{name}/enabled", (Delegate)AgentPages.SetEnabled);
        people.MapPost("/agents/{name}/limits", (Delegate)AgentPages.SetLimits);
        people.MapPost("/agents/{name}/model", (Delegate)AgentPages.SetModel);
        people.MapPost("/agents/{name}/grants", (Delegate)AgentPages.Grants);
        people.MapPost("/agents/{name}/tokens", (Delegate)AgentPages.IssueToken);
        people.MapPost("/agents/{name}/remove", (Delegate)AgentPages.Remove);
        people.MapPost("/tokens/{id}/revoke", (Delegate)AgentPages.RevokeToken);
        people.MapPost("/tokens/{id}/reissue", (Delegate)AgentPages.ReissueToken);

        people.MapGet("/sources", (Delegate)SourcePages.List).Needs(PortalNeed.Reader);
        people.MapGet("/sources/{name}", (Delegate)SourcePages.Show).Needs(PortalNeed.Reader);
        people.MapGet("/runs/{id:guid}", (Delegate)SourcePages.Run).Needs(PortalNeed.Reader);
        people.MapPost("/sources", (Delegate)SourcePages.Add);
        people.MapPost("/sources/{name}/set", (Delegate)SourcePages.Set);
        people.MapPost("/sources/{name}/hosted", (Delegate)SourcePages.Hosted);
        people.MapPost("/sources/{name}/remove", (Delegate)SourcePages.Remove);
        people.MapPost("/sources/{name}/run", (Delegate)SourcePages.RunNow);

        people.MapGet("/documents", (Delegate)DocumentPages.List).Needs(PortalNeed.Reader);
        people.MapGet("/documents/view", (Delegate)DocumentPages.Show).Needs(PortalNeed.Reader);
        people.MapGet("/review", (Delegate)ReviewPages.Queue).Needs(PortalNeed.Reader);

        people.MapGet("/permissions", (Delegate)PermissionPages.Rules).Needs(PortalNeed.Reader);
        // View as shows the passages another caller would be served, so it is
        // an administrator's: an auditor sees documents by path, never their text.
        people.MapGet("/permissions/view-as", (Delegate)PermissionPages.ViewAs).Needs(PortalNeed.Administrator);
        people.MapGet("/permissions/why", (Delegate)PermissionPages.Why).Needs(PortalNeed.Reader);
        people.MapPost("/permissions/rules", (Delegate)PermissionPages.SetRule);
        people.MapPost("/permissions/rules/remove", (Delegate)PermissionPages.RemoveRule);

        people.MapGet("/settings", (Delegate)SettingsPages.Show).Needs(PortalNeed.Reader);
        people.MapPost("/settings", (Delegate)SettingsPages.Set);
        people.MapGet("/tuning", (Delegate)TuningPages.Show).Needs(PortalNeed.Reader);
        people.MapPost("/tuning", (Delegate)TuningPages.Set);
        people.MapPost("/tuning/unset", (Delegate)TuningPages.Unset);
        people.MapPost("/tuning/run", (Delegate)TuningPages.Run);

        people.MapGet("/usage", (Delegate)UsagePages.Show).Needs(PortalNeed.Reader);
        people.MapGet("/audit", (Delegate)AuditPages.Questions).Needs(PortalNeed.Reader);
        people.MapGet("/changes", (Delegate)AuditPages.Changes).Needs(PortalNeed.Reader);

        people.MapGet("/health", (Delegate)HealthPages.Show).Needs(PortalNeed.Reader);
        people.MapGet("/health/support-bundle.json", (Delegate)HealthPages.SupportBundle).Needs(PortalNeed.Reader);

        people.MapGet("/export", (Delegate)ExportPages.Show).Needs(PortalNeed.Reader);
        people.MapGet("/export/config.json", (Delegate)ExportPages.Config).Needs(PortalNeed.Reader);
        people.MapPost("/export/config.json", (Delegate)ExportPages.Config);
        people.MapGet("/export/changes.jsonl", (Delegate)ExportPages.ChangeRecord).Needs(PortalNeed.Reader);
        // The audit trail's export is the business add-on's. Its address, with
        // a window or without, answers with the one sentence that names it.
        people.MapGet("/export/audit.jsonl", (Delegate)AddOnPage).Needs(PortalNeed.Reader);

        // The authorization flow's pages exist only while the flow is on: while
        // it is off, the host registers no deployment, and each of these paths
        // answers exactly as a path that was never mapped.
        if (app.ServiceProvider.GetService<OAuthDeployment>() is not null)
        {
            people.MapGet(Local(OAuthPaths.Consent), (Delegate)OAuthPages.Consent).Needs(PortalNeed.Person);
            people.MapPost(Local(OAuthPaths.Consent), (Delegate)OAuthPages.Answer).Needs(PortalNeed.Person);
            people.MapGet(Local(OAuthPaths.Grants), (Delegate)OAuthPages.Grants).Needs(PortalNeed.Reader);
            people.MapPost(Local(OAuthPaths.Grants) + "/revoke", (Delegate)OAuthPages.RevokeGrant);
            people.MapGet(Local(OAuthPaths.Clients), (Delegate)OAuthPages.Clients).Needs(PortalNeed.Reader);
            people.MapPost(Local(OAuthPaths.Clients), (Delegate)OAuthPages.AddClient);
            people.MapPost(Local(OAuthPaths.Clients) + "/disable", (Delegate)OAuthPages.DisableClient);
            people.MapPost(Local(OAuthPaths.Clients) + "/enable", (Delegate)OAuthPages.EnableClient);
            people.MapPost(Local(OAuthPaths.Clients) + "/remove", (Delegate)OAuthPages.RemoveClient);
            people.MapPost(Local(OAuthPaths.Clients) + "/document", (Delegate)OAuthPages.AddClientFromDocument).WithDocumentLimits();
            people.MapPost(Local(OAuthPaths.Clients) + "/replace", (Delegate)OAuthPages.ReplaceClientDocument).WithDocumentLimits();
            people.MapPost(Local(ConnectPages.GrantRevokePath), (Delegate)ConnectPages.RevokeGrant).Needs(PortalNeed.Person);
        }

        return app;
    }

    /// <summary>
    /// The bounds of a form that carries a client's metadata document, pasted or
    /// as a file. The form is read before the page runs (the host reads it for
    /// the anti-forgery field), so these hold that read to twice the largest
    /// document the flow takes in each value and each file, and five times in
    /// the whole body (the paste, the file and the fields), instead of the
    /// framework's megabytes. A document over the flow's bound and under these
    /// still reaches the flow, which refuses it with its own sentence; a form
    /// past these is answered by the host with the same sentence, on the
    /// Clients page, and the page never runs.
    /// </summary>
    private static RouteHandlerBuilder WithDocumentLimits(this RouteHandlerBuilder endpoint) => endpoint
        .WithFormOptions(valueLengthLimit: 2 * OAuthClientDocument.MaxBytes, multipartBodyLengthLimit: 2 * OAuthClientDocument.MaxBytes)
        .WithMetadata(new DocumentFormSizeLimit())
        .WithMetadata(new PortalFormBounds(OAuthPaths.Clients, OAuthClientDocument.TooLarge));

    private sealed class DocumentFormSizeLimit : Microsoft.AspNetCore.Http.Metadata.IRequestSizeLimitMetadata
    {
        public long? MaxRequestBodySize => 5 * OAuthClientDocument.MaxBytes;
    }

    /// <summary>A page the business add-on brings, answered with the sentence that names the add-on.</summary>
    private static IResult AddOnPage(HttpContext http)
    {
        var r = PortalRequest.Of(http);
        var path = http.Request.Path.Value ?? "";
        return Layout.Refused(r, StatusCodes.Status404NotFound,
            BusinessAddOn.PageRefusal(path, r.Extensions ?? ExtensionHost.BuiltIn) ?? $"There is no page {path}.");
    }

    /// <summary>A portal path as the portal's route group maps it, without the root the group already carries.</summary>
    private static string Local(string path) =>
        path.StartsWith(Root + "/", StringComparison.Ordinal)
            ? path[Root.Length..]
            : throw new ArgumentException($"'{path}' is not a portal path.", nameof(path));
}

/// <summary>
/// Runs before every page: the security headers, a signed-in person, the role
/// the endpoint needs, and for a change, a request from the portal's own
/// origin. Refusals are pages, never a partial run of the endpoint.
/// </summary>
internal sealed class PortalAccessFilter(IPortalHost host) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        Layout.SecurityHeaders(http.Response);

        // The host refuses a request with no caller before this runs; an agent
        // arrives here with no person, and the portal is for people.
        if (host.SignedIn(http) is not { } person)
            return Layout.Refused(null, StatusCodes.Status403Forbidden, "The portal is for people signed in with a password or through the sign-in proxy.");

        var request = PortalRequest.Attach(http, person, host.TenantId(http), host);

        var need = http.GetEndpoint()?.Metadata.GetMetadata<PortalNeedMetadata>()?.Need ?? PortalNeed.Administrator;
        if (!PortalRequest.Allows(person.User.Role, need))
            return Layout.Refused(request, StatusCodes.Status403Forbidden,
                need != PortalNeed.Administrator ? "This page is for auditors and administrators."
                : ChangesState(http.Request.Method) ? "Only an administrator can change this."
                : "This page is for administrators.");

        if (ChangesState(http.Request.Method) && !FromThisOrigin(http.Request))
            return Layout.Refused(request, StatusCodes.Status403Forbidden, "A change must be made from a portal page.");

        return await next(context);
    }

    private static bool ChangesState(string method) =>
        !(HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method));

    /// <summary>
    /// A second line against a cross-site form, beside the host's anti-forgery
    /// token, and the only line in the trusted-header mode, which has no
    /// session to bind a token to: the browser must say the request came from
    /// this origin. A request that says nothing is refused.
    /// <para>
    /// An origin of <c>null</c> says nothing. The portal sends
    /// <c>Referrer-Policy: no-referrer</c>, and under that policy a browser
    /// writes the origin of every form it posts as <c>null</c>, so reading it as
    /// a foreign origin refused every portal form a browser submitted. It falls
    /// to <c>Sec-Fetch-Site</c>, which the browser sets and no page can.
    /// </para>
    /// </summary>
    internal static bool FromThisOrigin(HttpRequest request)
    {
        var own = $"{request.Scheme}://{request.Host}";
        if (request.Headers.Origin.ToString() is { Length: > 0 } origin && origin != "null")
            return string.Equals(origin, own, StringComparison.OrdinalIgnoreCase);
        return string.Equals(request.Headers["Sec-Fetch-Site"].ToString(), "same-origin", StringComparison.Ordinal);
    }
}

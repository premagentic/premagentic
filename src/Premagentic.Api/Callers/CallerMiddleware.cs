using System.Globalization;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.Metadata;
using Premagentic.Core.Identity;
using Premagentic.Core.Security.Acl;

namespace Premagentic.Api.Callers;

/// <summary>
/// Resolves who is calling, on every request, before any endpoint runs, and
/// refuses the request when nobody is.
/// <list type="number">
/// <item>An <c>Authorization</c> header is an agent token and nothing else:
/// <c>Bearer prem_agt_...</c>. A request that sends one is judged by it alone,
/// so a bad token is refused even when a session cookie rides along. Each
/// agent is held to its <c>requests_per_minute</c>.</item>
/// <item>Otherwise the sign-in adapters, in the order
/// <see cref="SignInAdapters"/> puts them: the trusted-header mode when it is
/// on, then the session cookie a password sign-in issued, then anything an
/// extension brought. The first to claim the request decides it, and each one
/// answers with a Premagentic sign-in name, never with a role or a group: the
/// person is that user, with the groups Premagentic holds for them and the
/// groups the deployment's principal mapper says the adapter's groups mean,
/// and an unknown or disabled name holds nothing.</item>
/// <item>A request that changes state under the session cookie must carry the
/// session's anti-forgery token in <see cref="AntiForgeryHeader"/>, or, for a
/// form a page posts, in the form field <see cref="AntiForgeryField"/>.</item>
/// <item>A form an endpoint gives bounds of its own (<see cref="PortalFormBounds"/>)
/// is read before that check, for a signed-in person in any mode; a form past
/// its bounds is answered with the endpoint's page and sentence, and the
/// endpoint never runs. Over HTTP/1.1, a whole body past its bound and within
/// <see cref="PortalFormDrain"/> is read and discarded first, for a request
/// from the portal's own origin, so the browser sees the answer.</item>
/// </list>
/// An endpoint for people only refuses an agent token with a 403, and when it
/// is a page, sends a signed-out browser to sign in with a 303.
/// No caller is a 401, never a search as "everyone". Why a caller was refused
/// is logged for the operator and never told to the caller.
/// <para>
/// While the MCP authorization flow is on, <c>/mcp</c>, and nothing else, also
/// takes the flow's access token (<c>prem_oat_</c>): checked against its grant,
/// then resolved as its agent, so every rule after that is the agent token's.
/// Its 401 then points a client at the resource's metadata. While the flow is
/// off, such a token is only a malformed agent token, as it always was.
/// <paramref name="oauth"/> is the flow as start read it: it resolves to null
/// while the flow is off, and the default then applies.
/// </para>
/// </summary>
internal sealed class CallerMiddleware(
    RequestDelegate next, AgentRateLimiter rateLimiter, UnmappedPrincipalLog unmappedLog,
    ILogger<CallerMiddleware> log, OAuthDeployment? oauth = null, PortalFormDrain? drain = null)
{
    private readonly PortalFormDrain _drain = drain ?? PortalFormDrain.Default;

    public const string SessionCookie = "__Host-prem-session";
    public const string AntiForgeryHeader = "X-Prem-Antiforgery";
    public const string AntiForgeryField = "prem_antiforgery";

    private const string BearerPrefix = "Bearer ";

    public async Task InvokeAsync(
        HttpContext http, IdentityStore identity, SignInAdapters adapters, PrincipalMapping mappings)
    {
        var requirement = http.GetEndpoint()?.Metadata.GetMetadata<CallerRequirement>() ?? CallerRequirement.PersonOrAgent;
        if (requirement.Anonymous)
        {
            await next(http);
            return;
        }

        var ct = http.RequestAborted;

        if (http.Request.Headers.Authorization.Count > 0)
        {
            var authorization = http.Request.Headers.Authorization.ToString();
            if (!authorization.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase))
            {
                await RefuseAsync(http, "an Authorization header that is not a bearer token");
                return;
            }

            var presented = authorization[BearerPrefix.Length..].Trim();
            Caller agent;
            if (TakesOAuthAccess(http, requirement) && presented.StartsWith(OAuthPrefixes.AccessToken, StringComparison.Ordinal))
            {
                var (access, refusal) = await http.RequestServices.GetRequiredService<OAuthTokenService>().CheckAccessAsync(presented, ct);
                if (access is null)
                {
                    await RefuseAsync(http, $"an access token of the authorization flow ({refusal})", tokenPresented: true);
                    return;
                }
                agent = await CallerAccess.ResolveOAuthAsync(identity, access, ct);
            }
            else
            {
                agent = await CallerAccess.ResolveAgentTokenAsync(identity, presented, ct);
            }
            if (!agent.IsResolved)
            {
                await RefuseAsync(http, agent.Scope.AuditLabel, tokenPresented: true);
                return;
            }
            if (requirement.RefusesAgents)
            {
                log.LogInformation("Refused {Method} {Path}: an agent token on an endpoint for people.", http.Request.Method, http.Request.Path);
                http.Response.StatusCode = StatusCodes.Status403Forbidden;
                await http.Response.WriteAsJsonAsync(new { error = "This is for people. An agent reads through the API or MCP." }, ct);
                return;
            }
            if (!rateLimiter.TryAcquire(agent.Agent!.Id, agent.Agent.RequestsPerMinute, out var retryAfter))
            {
                http.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                http.Response.Headers.RetryAfter = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
                await http.Response.WriteAsJsonAsync(new { error = "This agent has used its requests for this minute." }, ct);
                return;
            }

            http.SetCaller(new HttpCaller(agent, CallerSource.AgentToken, User: null, SessionValue: null));
            await next(http);
            return;
        }

        if (requirement.AgentsOnly)
        {
            await RefuseAsync(http, "no agent token on an endpoint for agents");
            return;
        }

        // The adapters in their fixed order. The first to claim the request
        // decides it, for somebody or for nobody; a claim that proves nobody is
        // refused here rather than offered to the next adapter, so a cookie
        // that has ended cannot become an anonymous request or somebody else's.
        var signInRequest = new HttpSignInRequest(http);
        foreach (var adapter in adapters.Ordered)
        {
            var resolution = await adapter.ResolveAsync(signInRequest, ct);
            if (resolution is null) continue;

            if (resolution.SignInName is not { } name)
            {
                await RefuseAsync(http, $"a {adapter.Name} credential that proves nobody");
                return;
            }

            // What the adapter's directory said this person is in, read through
            // the deployment's principal mapper. A group it does not map, and
            // every group when there is no mapper, means nothing here and is
            // ignored, said once per process so a missing mapping is visible
            // without a line on every request.
            var fromDirectory = Array.Empty<Principal>();
            if (resolution.ExternalGroups.Count > 0)
            {
                var resolved = await mappings.ResolveAsync(resolution.ExternalGroups, ct);
                if (resolved.Unmapped.Count > 0) unmappedLog.Note(adapter.Name, mappings.Mapper?.Name, resolved.Unmapped);
                fromDirectory = [.. resolved.Mapped.Values];
            }

            var user = await identity.FindUserByNameAsync(name, ct);
            var person = user is null ? null : await CallerAccess.ResolveUserAsync(identity, user.Id, fromDirectory, ct);
            if (person is not { IsResolved: true })
            {
                await RefuseAsync(http, person?.Scope.AuditLabel ?? $"a {adapter.Name} name that is no user");
                return;
            }

            // A form with bounds of its own is read here, once, in every sign-in
            // mode, so a form past them is answered with the page's own sentence
            // and nothing after this runs: no anti-forgery check, no page, no
            // change. The redirect it answers with changes nothing either.
            if (http.GetEndpoint()?.Metadata.GetMetadata<PortalFormBounds>() is { } bounds
                && ChangesState(http.Request.Method) && http.Request.HasFormContentType
                && (await DrainedPastBoundAsync(http, ct) ?? await BoundPassedAsync(http, ct)) is { } passed)
            {
                log.LogInformation("Refused {Method} {Path}: the form passed its bound ({Passed}).", http.Request.Method, http.Request.Path, passed);
                await bounds.Answer().ExecuteAsync(http);
                return;
            }

            // The session is the one credential the request keeps carrying: its
            // value binds the anti-forgery check and rides on the caller.
            var source = adapters.SourceOf(adapter);
            var sessionValue = source == CallerSource.Session ? http.Request.Cookies[SessionCookie] : null;
            if (sessionValue is not null
                && ChangesState(http.Request.Method)
                && !SessionStore.AntiForgeryMatches(sessionValue, await PresentedAntiForgeryAsync(http, ct)))
            {
                log.LogInformation("Refused {Method} {Path}: no valid anti-forgery token with the session.", http.Request.Method, http.Request.Path);
                http.Response.StatusCode = StatusCodes.Status403Forbidden;
                await http.Response.WriteAsJsonAsync(
                    new { error = $"A request that changes state under a session needs the session's anti-forgery token in {AntiForgeryHeader}." }, ct);
                return;
            }

            http.SetCaller(new HttpCaller(person, source, user, sessionValue));
            await next(http);
            return;
        }

        await RefuseAsync(http, "no credentials");
    }

    /// <summary>
    /// The anti-forgery token the request carries: the header, or for a form a
    /// page posts, the form field. The form is read once and stays cached for
    /// the endpoint.
    /// </summary>
    private static async Task<string?> PresentedAntiForgeryAsync(HttpContext http, CancellationToken ct)
    {
        var header = http.Request.Headers[AntiForgeryHeader].ToString();
        if (header.Length > 0 || !http.Request.HasFormContentType) return header;
        var form = await http.Request.ReadFormAsync(ct);
        return form[AntiForgeryField].ToString();
    }

    /// <summary>
    /// Over HTTP/1.1, a body the request declares past the endpoint's bound and
    /// within <see cref="PortalFormDrain.CapBytes"/> is read and discarded before
    /// the answer, so a browser still sending gets the answer rather than a reset
    /// connection. Decided before any byte is read, since the endpoint's limit
    /// can be raised only then. Only for a request from the portal's own origin,
    /// by the portal's own test, so a cross-site request is refused without a
    /// byte read. The body is never parsed or kept. At
    /// <see cref="PortalFormDrain.Time"/>, or if the sender stops or goes, the
    /// drain stops and the answer closes the connection, as it would have.
    /// </summary>
    /// <returns>The bound passed when the body was drained; null when this request is not one to drain.</returns>
    private async Task<string?> DrainedPastBoundAsync(HttpContext http, CancellationToken ct)
    {
        var limit = http.Features.Get<IHttpMaxRequestBodySizeFeature>();
        var bound = http.GetEndpoint()?.Metadata.GetMetadata<IRequestSizeLimitMetadata>()?.MaxRequestBodySize;
        if (limit is not { IsReadOnly: false }
            || !PortalFormDrain.Drains(http.Request.Protocol, http.Request.ContentLength, bound, _drain.CapBytes,
                PortalFormBounds.FromThisOrigin(http.Request), ExpectsContinue(http.Request)))
            return null;

        limit.MaxRequestBodySize = http.Request.ContentLength;
        using var within = CancellationTokenSource.CreateLinkedTokenSource(ct);
        within.CancelAfter(_drain.Time);
        var buffer = new byte[16 * 1024];
        try
        {
            while (await http.Request.Body.ReadAsync(buffer, within.Token) > 0)
            {
            }
        }
        catch (Exception ex) when ((ex is OperationCanceledException or IOException) && !ct.IsCancellationRequested)
        {
            // What is left is never read; the answer ends the connection.
            http.Response.Headers.Connection = "close";
        }
        return "the whole body";
    }

    /// <summary>Whether the request asks for <c>100 Continue</c> before it sends its body.</summary>
    private static bool ExpectsContinue(HttpRequest request) =>
        request.Headers.Expect.Any(value => value is not null && value.Contains("100-continue", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Reads the form within the bounds routing gave the endpoint, and names the
    /// bound it passed, or null when it was read whole; the endpoint then reads
    /// it from the cache. Only a bound is caught, and nothing of a form read in
    /// part is kept: the reader stops at the first byte over, and a declared
    /// length over the body's bound is refused before a byte is read.
    /// </summary>
    private static async Task<string?> BoundPassedAsync(HttpContext http, CancellationToken ct)
    {
        try
        {
            await http.Request.ReadFormAsync(ct);
            return null;
        }
        catch (InvalidDataException)
        {
            return "a value or a file";
        }
        catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            return "the whole body";
        }
    }

    private static bool ChangesState(string method) =>
        !(HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method) || HttpMethods.IsTrace(method));

    /// <summary>Whether this request may present the flow's access token: while the flow is on, at <c>/mcp</c> only, the resource every such token is bound to.</summary>
    private bool TakesOAuthAccess(HttpContext http, CallerRequirement requirement) =>
        oauth is not null && requirement.AgentsOnly && http.Request.Path.StartsWithSegments(CallerServices.McpPath);

    private async Task RefuseAsync(HttpContext http, string why, bool tokenPresented = false)
    {
        log.LogInformation("Refused {Method} {Path}: {Why}.", http.Request.Method, http.Request.Path, why);
        var requirement = http.GetEndpoint()?.Metadata.GetMetadata<CallerRequirement>();
        if (requirement?.SignInRedirect is { } signIn && (HttpMethods.IsGet(http.Request.Method) || HttpMethods.IsHead(http.Request.Method)))
        {
            var back = http.Request.Path.Add(http.Request.QueryString).ToString();
            http.Response.StatusCode = StatusCodes.Status303SeeOther;
            http.Response.Headers.Location = signIn + "?return=" + Uri.EscapeDataString(back);
            return;
        }
        http.Response.StatusCode = StatusCodes.Status401Unauthorized;
        http.Response.Headers.WWWAuthenticate = Challenge(http, requirement ?? CallerRequirement.PersonOrAgent, tokenPresented);
        await http.Response.WriteAsJsonAsync(new { error = "Sign in, or present an agent token." }, http.RequestAborted);
    }

    /// <summary>
    /// The challenge of a 401. At <c>/mcp</c> while the flow is on, it names
    /// where the resource's metadata is (RFC 9728) and the scope, and says
    /// <c>invalid_token</c> when a bearer token was presented. Everywhere else,
    /// and always while the flow is off, it is <c>Bearer</c>.
    /// </summary>
    private string Challenge(HttpContext http, CallerRequirement requirement, bool tokenPresented)
    {
        if (!TakesOAuthAccess(http, requirement)) return "Bearer";
        var error = tokenPresented ? "error=\"invalid_token\", " : "";
        return $"Bearer {error}resource_metadata=\"{oauth!.ResourceMetadataUrl}\", scope=\"{OAuthScopes.Read}\"";
    }
}

using System.Globalization;
using Premagentic.Core;
using Premagentic.Core.Identity;
using Premagentic.Core.Retrieval;

namespace Premagentic.Api.Callers;

/// <summary>
/// The HTTP surface. Every route needs a caller except sign-in and the health
/// check, which say so in their metadata.
/// <list type="bullet">
/// <item><c>POST /api/session</c>: password sign-in. HTTPS only, unless the
/// development switch allows plain HTTP.</item>
/// <item><c>GET /api/session</c>: who am I, for a person or an agent.</item>
/// <item><c>DELETE /api/session</c>: sign-out.</item>
/// <item><c>POST /api/search</c> and <c>POST /api/section</c>: read, as the caller,
/// a body of at most <see cref="ReadRequestLimit.Bytes"/> bytes.</item>
/// <item><c>GET /health</c>.</item>
/// </list>
/// </summary>
internal static class ApiEndpoints
{
    // One body for every failed sign-in, whatever the reason.
    private static readonly object SignInFailed = new { error = "Sign-in failed." };

    public static void MapPremagenticApi(this WebApplication app)
    {
        app.MapPost("/api/session", SignInAsync).WithMetadata(CallerRequirement.Nobody);
        app.MapGet("/api/session", WhoAmI);
        app.MapDelete("/api/session", SignOutAsync);
        app.MapPost("/api/search", SearchAsync).WithMetadata(ReadRequestLimit.Instance);
        app.MapPost("/api/section", SectionAsync).WithMetadata(ReadRequestLimit.Instance);
        app.MapGet("/health", (ApiSettings settings, IServiceProvider services) => Results.Ok(new
            {
                status = "ok",
                signInHeaderConfigured = settings.SignInHeader is not null,
                rowLevelSecurity = services.GetService<Premagentic.Core.Security.SearchRole>() is not null,
                // The model's name and revision, not its folder: this page answers
                // anyone who can reach the port, and a path on the server is not
                // theirs to know. The portal's health page shows the folder.
                model = Premagentic.Core.Admin.HealthReport.Model(services.GetRequiredService<Premagentic.Core.Embeddings.IEmbeddingProvider>()) is var m
                    ? new { name = m.Name, revision = m.Revision }
                    : null,
            }))
            .WithMetadata(CallerRequirement.Nobody);
    }

    internal static CookieOptions SessionCookieOptions() => new()
    {
        HttpOnly = true,
        Secure = true,
        SameSite = SameSiteMode.Strict,
        Path = "/",
        IsEssential = true,
    };

    private static string RoleKey(Role role) => role.ToString().ToLowerInvariant();

    private static async Task<IResult> SignInAsync(
        SignInRequest request, HttpContext http, ApiSettings settings, SignInThrottle throttle, SignInService signIn,
        IdentityStore identity, SessionStore sessions, CancellationToken ct)
    {
        if (!http.Request.IsHttps && !settings.AllowHttpSignIn)
            return Results.Json(
                new { error = "Password sign-in is refused over plain HTTP, where the password would cross the network unencrypted. Connect with HTTPS." },
                statusCode: StatusCodes.Status403Forbidden);

        var address = http.Connection.RemoteIpAddress;
        if (throttle.IsBlocked(address, out var wait))
        {
            http.Response.Headers.RetryAfter = Math.Max(1, (int)Math.Ceiling(wait.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
            return Results.Json(new { error = "Too many failed sign-ins from this address. Wait and try again." },
                statusCode: StatusCodes.Status429TooManyRequests);
        }

        var result = await signIn.SignInAsync(identity, sessions, request.SignInName, request.Password, ct);
        if (!result.IsSuccess)
        {
            throttle.RecordFailure(address);
            return Results.Json(SignInFailed, statusCode: StatusCodes.Status401Unauthorized);
        }

        var session = result.Session!;
        http.Response.Cookies.Append(CallerMiddleware.SessionCookie, session.Value, SessionCookieOptions());
        return Results.Ok(new
        {
            kind = "person",
            signInName = result.User!.Name,
            role = RoleKey(result.User.Role),
            antiForgeryToken = SessionStore.AntiForgeryToken(session.Value),
            expiresAt = session.ExpiresAt,
        });
    }

    private static IResult WhoAmI(HttpContext http)
    {
        var caller = http.Caller();
        if (caller.Source == CallerSource.AgentToken)
        {
            var agent = caller.Caller.Agent!;
            return Results.Ok(new
            {
                kind = "agent",
                agent = agent.Name,
                mode = agent.Mode == AgentMode.ActsForUser ? "acts-for-user" : "service",
                requestsPerMinute = agent.RequestsPerMinute,
                // So an assistant's own settings page can show the person
                // whether it is registered as using a hosted model.
                modelLocation = ModelLocations.Text(agent.ModelLocation),
                modelVendor = agent.ModelVendor,
            });
        }

        return Results.Ok(new
        {
            kind = "person",
            signInName = caller.User!.Name,
            role = RoleKey(caller.User.Role),
            via = caller.Source == CallerSource.Session ? "session" : "trusted-header",
            antiForgeryToken = caller.SessionValue is { } value ? SessionStore.AntiForgeryToken(value) : null,
        });
    }

    private static async Task<IResult> SignOutAsync(HttpContext http, SessionStore sessions, CancellationToken ct)
    {
        var caller = http.Caller();
        if (caller.Source != CallerSource.Session)
            return Results.Json(
                new { error = "Only a session signs out. An agent token is revoked with 'prem tokens revoke'; a trusted-header sign-in ends at the proxy." },
                statusCode: StatusCodes.Status400BadRequest);

        await sessions.EndAsync(caller.SessionValue, ct);
        http.Response.Cookies.Delete(CallerMiddleware.SessionCookie, SessionCookieOptions());
        return Results.NoContent();
    }

    private static async Task<IResult> SearchAsync(SearchRequest request, HttpContext http, CallerQueries queries, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Query))
            return Results.BadRequest(new { error = "query is required" });

        var caller = http.Caller().Caller;
        SearchResult result;
        try
        {
            result = await queries.SearchAsync(caller, request.Query, request.TopK ?? 5, request.IncludeHistorical ?? false, ct);
        }
        catch (QueryTooLongException tooLong)
        {
            return Results.BadRequest(new { error = tooLong.Message });
        }
        return Results.Ok(new
        {
            query = result.Query,
            access = caller.Scope.AuditLabel,
            elapsedMs = result.ElapsedMs,
            hits = result.Hits.Select(ResultFields.Hit),
        });
    }

    private static async Task<IResult> SectionAsync(SectionRequest request, HttpContext http, CallerQueries queries, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Path))
            return Results.BadRequest(new { error = "path is required" });

        DocumentSectionResult? section;
        try
        {
            section = await queries.SectionAsync(http.Caller().Caller, request.Path, request.Heading, request.IncludeHistorical ?? false, ct);
        }
        catch (QueryTooLongException tooLong)
        {
            // Said of any path that long, readable or not, so it tells nothing.
            return Results.BadRequest(new { error = tooLong.Message });
        }

        // Absent and unreadable look the same, so a path cannot be probed.
        return section is null
            ? Results.NotFound(new { error = "No such document is available to this caller." })
            : Results.Ok(ResultFields.Section(section));
    }
}

internal sealed record SignInRequest(string? SignInName, string? Password)
{
    /// <summary>Never includes the password, so it is safe in a log line.</summary>
    public override string ToString() => $"sign-in as {SignInName}";
}

internal sealed record SearchRequest(string? Query, int? TopK, bool? IncludeHistorical);

internal sealed record SectionRequest(string? Path, string? Heading, bool? IncludeHistorical);

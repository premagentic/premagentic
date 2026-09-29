using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Net.Http.Headers;
using Premagentic.Core.Identity;
using Premagentic.Core.Storage;

namespace Premagentic.Api.Callers;

/// <summary>
/// The MCP authorization flow over HTTP: the two discovery documents,
/// self-registration, the authorization forwarder, and the token and
/// revocation endpoints. The consent, grants and clients pages are the
/// portal's.
/// <para>
/// Whether the flow runs is read once, at start (<see cref="OAuthStart"/>).
/// While it is off nothing here is mapped and every flow service resolves to
/// null, so each of these paths answers what any unmapped path answers, and no
/// request reads the flow's tables.
/// </para>
/// <para>
/// Nothing here makes a request of its own: a client is known only by what it
/// registered, or what an administrator registered for it.
/// </para>
/// </summary>
internal static class OAuthEndpoints
{
    /// <summary>The largest form body the token and revocation endpoints read.</summary>
    public const int FormBodyLimit = 8 * 1024;

    /// <summary>The largest JSON body a self-registration may send.</summary>
    public const int RegistrationBodyLimit = 16 * 1024;

    /// <summary>The longest forward to the consent page, once escaped again as the sign-in page's return address.</summary>
    public const int ForwardLimit = 8_000;

    /// <summary>What an HTTP Basic challenge from the token endpoint names.</summary>
    public const string Realm = "PremAgentic";

    public const string LogCategory = "Premagentic.Api.OAuth";

    private const string FormMediaType = "application/x-www-form-urlencoded";
    private const string JsonMediaType = "application/json";

    // The form limits of the token and revocation endpoints.
    private const int FormValueCountLimit = 16;
    private const int FormKeyLengthLimit = 64;
    private const int FormValueLengthLimit = 4 * 1024;

    private const string TooManyFailures = "Too many failed requests from this address. Wait and try again.";

    /// <summary>
    /// The flow's services. Each resolves to null while the flow is off, so a
    /// page or a host asks with <c>GetService</c> and takes null for "off":
    /// <see cref="OAuthDeployment"/>, the stores behind the contract's
    /// interfaces, the token service and the throttles. Only
    /// <see cref="OAuthStart"/>, which read the flag, is there either way.
    /// </summary>
    public static IServiceCollection AddPremagenticOAuth(this IServiceCollection services)
    {
        services.AddSingleton<OAuthStart>();
        services.AddSingleton<OAuthDeployment>(sp => sp.GetRequiredService<OAuthStart>().Loaded!);
        services.AddSingleton<OAuthThrottles>(sp =>
            sp.GetService<OAuthDeployment>() is null ? null! : new OAuthThrottles(sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton<IOAuthHealth>(sp => sp.GetService<OAuthThrottles>()!);
        services.AddScoped<OAuthTokenService>(sp => WhileOn(sp, (db, tenant, oauth, time) => new OAuthTokenService(db, tenant, oauth, time)));
        services.AddScoped<OAuthClients>(sp => WhileOn(sp, (db, tenant, oauth, time) => new OAuthClients(db, tenant, oauth, time)));
        services.AddScoped<IOAuthClients>(sp => sp.GetService<OAuthClients>()!);
        services.AddScoped<IOAuthConsent>(sp => WhileOn<IOAuthConsent>(sp, (db, tenant, oauth, time) => new OAuthConsent(db, tenant, oauth, time)));
        services.AddScoped<IOAuthGrants>(sp => WhileOn<IOAuthGrants>(sp, (db, tenant, oauth, time) => new OAuthGrants(db, tenant, oauth, time)));
        return services;
    }

    private static T WhileOn<T>(IServiceProvider sp, Func<PremagenticDatabase, Guid, OAuthDeployment, TimeProvider, T> make) where T : class =>
        sp.GetService<OAuthDeployment>() is { } oauth
            ? make(sp.GetRequiredService<PremagenticDatabase>(), sp.GetRequiredService<Deployment>().TenantId, oauth,
                sp.GetRequiredService<TimeProvider>())
            : null!;

    /// <summary>
    /// Maps the flow's endpoints. Called only while the flow is on. Each is
    /// open to anyone, since a client has no credential before it has a token,
    /// and each answers with <c>nosniff</c> and <c>no-store</c>.
    /// </summary>
    public static void MapPremagenticOAuth(this WebApplication app, OAuthDeployment oauth)
    {
        ArgumentNullException.ThrowIfNull(oauth);
        // One byte array for both addresses of the resource's document, so the
        // two are the same byte for byte.
        var resourceDocument = JsonSerializer.SerializeToUtf8Bytes(ProtectedResourceMetadata(oauth));
        var serverDocument = JsonSerializer.SerializeToUtf8Bytes(AuthorizationServerMetadata(oauth));

        var flow = app.MapGroup("").WithMetadata(CallerRequirement.Nobody).AddEndpointFilter(NotSniffedNotStored);
        flow.MapGet(OAuthPaths.ProtectedResourceMetadata, () => Results.Bytes(resourceDocument, JsonMediaType));
        flow.MapGet(OAuthPaths.ProtectedResourceMetadataRoot, () => Results.Bytes(resourceDocument, JsonMediaType));
        flow.MapGet(OAuthPaths.AuthorizationServerMetadata, () => Results.Bytes(serverDocument, JsonMediaType));
        if (oauth.DynamicRegistration) flow.MapPost(OAuthPaths.Register, RegisterAsync);
        flow.MapGet(OAuthPaths.Authorize, Authorize);
        flow.MapPost(OAuthPaths.Token, TokenAsync);
        flow.MapPost(OAuthPaths.Revoke, RevokeAsync);
    }

    /// <summary>Protected resource metadata (RFC 9728) for <c>/mcp</c>.</summary>
    public static Dictionary<string, object> ProtectedResourceMetadata(OAuthDeployment oauth) => new()
    {
        ["resource"] = oauth.Resource,
        ["authorization_servers"] = new[] { oauth.Issuer },
        ["scopes_supported"] = new[] { OAuthScopes.Read },
        ["bearer_methods_supported"] = new[] { "header" },
        ["resource_name"] = Realm,
    };

    /// <summary>
    /// Authorization server metadata (RFC 8414). It says no client metadata
    /// document is fetched, since this server makes no request of its own, and
    /// lists the registration endpoint only while self-registration is on.
    /// </summary>
    public static Dictionary<string, object> AuthorizationServerMetadata(OAuthDeployment oauth)
    {
        var document = new Dictionary<string, object>
        {
            ["issuer"] = oauth.Issuer,
            ["authorization_endpoint"] = oauth.PublicUrl + OAuthPaths.Authorize,
            ["token_endpoint"] = oauth.PublicUrl + OAuthPaths.Token,
        };
        if (oauth.DynamicRegistration) document["registration_endpoint"] = oauth.PublicUrl + OAuthPaths.Register;
        document["revocation_endpoint"] = oauth.PublicUrl + OAuthPaths.Revoke;
        document["response_types_supported"] = new[] { "code" };
        document["response_modes_supported"] = new[] { "query" };
        document["grant_types_supported"] = new[] { "authorization_code", "refresh_token" };
        document["token_endpoint_auth_methods_supported"] = new[] { "none" };
        document["revocation_endpoint_auth_methods_supported"] = new[] { "none" };
        document["code_challenge_methods_supported"] = new[] { "S256" };
        document["scopes_supported"] = new[] { OAuthScopes.Read };
        document["authorization_response_iss_parameter_supported"] = true;
        document["client_id_metadata_document_supported"] = false;
        return document;
    }

    private static ValueTask<object?> NotSniffedNotStored(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var headers = context.HttpContext.Response.Headers;
        headers.XContentTypeOptions = "nosniff";
        headers.CacheControl = "no-store";
        return next(context);
    }

    /// <summary>
    /// Self-registration (RFC 7591): JSON only, at most 16 KB, and at most
    /// <c>mcp.oauth.registrations_per_hour</c> from one address. What is kept
    /// and what is refused is <see cref="OAuthClients.RegisterAsync"/>'s.
    /// </summary>
    private static async Task<IResult> RegisterAsync(
        HttpContext http, OAuthClients clients, OAuthThrottles throttles, PremagenticDatabase db, Deployment deployment,
        ILoggerFactory loggers, CancellationToken ct)
    {
        var log = loggers.CreateLogger(LogCategory);
        if (!IsMediaType(http.Request.ContentType, JsonMediaType))
            return Refused(http, log, OAuthEndpointAnswer.Error(StatusCodes.Status415UnsupportedMediaType, "invalid_client_metadata",
                "A registration is sent as application/json.", "register_media_type"), clientId: null);
        if (await ReadBoundedAsync(http, RegistrationBodyLimit, ct) is not { } body)
            return Refused(http, log, OAuthEndpointAnswer.Error(StatusCodes.Status413PayloadTooLarge, "invalid_client_metadata",
                $"A registration is at most {RegistrationBodyLimit / 1024} KB.", "register_too_large"), clientId: null);

        var source = SignInThrottle.Key(http.Connection.RemoteIpAddress);
        var stored = await new SettingsStore(db, deployment.TenantId).GetManyAsync([OAuthSettings.RegistrationsPerHour], ct);
        var perHour = OAuthSettingRules.NumberOr(
            stored.TryGetValue(OAuthSettings.RegistrationsPerHour, out var value) ? value : null,
            OAuthSettingRules.Range(OAuthSettings.RegistrationsPerHour, 1, OAuthThrottles.RegistrationsPerHourMax), OAuthSettings.RegistrationsPerHourDefault);
        if (throttles.RegistrationsOver(source, perHour, out var wait, out var made))
            return Refused(http, log, OAuthEndpointAnswer.Error(StatusCodes.Status429TooManyRequests, "temporarily_unavailable",
                OAuthClients.PendingLimit, "register_rate", retryAfter: wait,
                warning: $"A self-registration from {source} was refused: {OAuthSettings.RegistrationsPerHour} is {perHour}, " +
                         $"and that address registered {made} assistants in the last hour."), clientId: null, throttles);

        JsonElement json;
        try
        {
            using var document = JsonDocument.Parse(body);
            json = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return Refused(http, log, OAuthEndpointAnswer.Error(StatusCodes.Status400BadRequest, "invalid_client_metadata",
                "The registration is not valid JSON.", "register_not_json"), clientId: null);
        }

        var answer = await clients.RegisterAsync(json, source, ct);
        if (answer.Succeeded)
        {
            throttles.RecordRegistration(source);
            return Answered(http, answer);
        }
        return Refused(http, log, answer, clientId: null, throttles);
    }

    /// <summary>
    /// The authorization endpoint serves no page. It answers 303 to the consent
    /// page with the query rebuilt from every parameter it parsed, each name
    /// and value escaped again and repeats kept, so the consent page still
    /// refuses a repeat and no raw text reaches the sign-in page's return
    /// address. A request too long to survive that trip gets a plain-text 400.
    /// </summary>
    private static IResult Authorize(HttpContext http, ILoggerFactory loggers)
    {
        var query = new StringBuilder();
        foreach (var (name, values) in http.Request.Query)
        {
            foreach (var value in values)
                query.Append(query.Length == 0 ? '?' : '&')
                    .Append(Uri.EscapeDataString(name)).Append('=').Append(Uri.EscapeDataString(value ?? ""));
        }
        var forward = OAuthPaths.Consent + query;
        if (Uri.EscapeDataString(forward).Length > ForwardLimit)
        {
            loggers.CreateLogger(LogCategory).LogInformation("Refused {Method} {Path}: {Reason}.", http.Request.Method, http.Request.Path, "authorize_too_long");
            return Results.Text("This authorization request is too long.", "text/plain", Encoding.UTF8, StatusCodes.Status400BadRequest);
        }
        http.Response.Headers.Location = forward;
        return Results.StatusCode(StatusCodes.Status303SeeOther);
    }

    private static async Task<IResult> TokenAsync(
        HttpContext http, OAuthTokenService tokens, OAuthThrottles throttles, ILoggerFactory loggers, CancellationToken ct)
    {
        var log = loggers.CreateLogger(LogCategory);
        var (form, refusal) = await ReadClientFormAsync(http, log, "token", ct);
        if (refusal is not null) return refusal;
        return AfterThrottle(http, log, throttles, await tokens.TokenAsync(form!, ct), form!);
    }

    private static async Task<IResult> RevokeAsync(
        HttpContext http, OAuthTokenService tokens, OAuthThrottles throttles, ILoggerFactory loggers, CancellationToken ct)
    {
        var log = loggers.CreateLogger(LogCategory);
        var (form, refusal) = await ReadClientFormAsync(http, log, "revoke", ct);
        if (refusal is not null) return refusal;
        return AfterThrottle(http, log, throttles, await tokens.RevokeAsync(form!, ct), form!);
    }

    /// <summary>
    /// The throttle of the token and revocation endpoints, applied after the
    /// request was judged, so a request that succeeds is always answered and a
    /// value that matched a row still has its effect (a reused refresh token
    /// still ends its grant). Only a failure whose value matched nothing
    /// counts. Over 30 of those in ten minutes, an address's failures are
    /// answered 429 and count no further, and that is logged once a minute.
    /// </summary>
    private static IResult AfterThrottle(
        HttpContext http, ILogger log, OAuthThrottles throttles, OAuthEndpointAnswer answer, IReadOnlyDictionary<string, string> form)
    {
        if (answer.Succeeded) return Answered(http, answer);
        var address = http.Connection.RemoteIpAddress;
        if (throttles.FailuresBlocked(address, out var wait))
        {
            if (throttles.LogThrottleNow(address))
                log.LogInformation("Refused {Method} {Path}: too many failed requests from {Address}; each is answered 429 for {Seconds} seconds more.",
                    http.Request.Method, http.Request.Path, SignInThrottle.Key(address), Seconds(wait));
            http.Response.Headers.RetryAfter = Seconds(wait);
            return Results.Json(new Dictionary<string, object?> { ["error"] = "temporarily_unavailable", ["error_description"] = TooManyFailures },
                statusCode: StatusCodes.Status429TooManyRequests);
        }
        if (answer.CountsAsFailure) throttles.RecordFailure(address);
        return Refused(http, log, answer, form.GetValueOrDefault("client_id"));
    }

    /// <summary>
    /// A token or revocation request's form, read the one way both take it.
    /// An Authorization header is refused first: a client that authenticates
    /// that way is told so in its own scheme (401), and any other scheme is a
    /// 400. Then application/x-www-form-urlencoded only (400 before the body is
    /// read), at most 8 KB (413), at most 16 values, 64-character names and
    /// 4 KB values, and each name at most once. An empty value is taken as
    /// absent (RFC 6749 section 3.1).
    /// </summary>
    private static async Task<(Dictionary<string, string>? Form, IResult? Refusal)> ReadClientFormAsync(
        HttpContext http, ILogger log, string endpoint, CancellationToken ct)
    {
        if (http.Request.Headers.Authorization.Count > 0)
        {
            var scheme = http.Request.Headers.Authorization.ToString().Split(' ', 2)[0];
            if (scheme.Equals("Basic", StringComparison.OrdinalIgnoreCase) || scheme.Equals("Bearer", StringComparison.OrdinalIgnoreCase))
            {
                http.Response.Headers.WWWAuthenticate = scheme.Equals("Basic", StringComparison.OrdinalIgnoreCase)
                    ? $"Basic realm=\"{Realm}\""
                    : "Bearer";
                return (null, Refused(http, log, OAuthEndpointAnswer.Error(StatusCodes.Status401Unauthorized, "invalid_client",
                    "This server issues no client credentials. Send client_id in the form, and no Authorization header.",
                    $"{endpoint}_authorization_header"), clientId: null));
            }
            return (null, Refused(http, log, OAuthEndpointAnswer.Error(StatusCodes.Status400BadRequest, "invalid_request",
                "Send no Authorization header here.", $"{endpoint}_authorization_header"), clientId: null));
        }
        if (!IsMediaType(http.Request.ContentType, FormMediaType))
            return (null, Refused(http, log, OAuthEndpointAnswer.Error(StatusCodes.Status400BadRequest, "invalid_request",
                "The request is sent as application/x-www-form-urlencoded.", $"{endpoint}_media_type"), clientId: null));
        if (await ReadBoundedAsync(http, FormBodyLimit, ct) is not { } body)
            return (null, Refused(http, log, OAuthEndpointAnswer.Error(StatusCodes.Status413PayloadTooLarge, "invalid_request",
                $"The request is at most {FormBodyLimit / 1024} KB.", $"{endpoint}_too_large"), clientId: null));

        Dictionary<string, Microsoft.Extensions.Primitives.StringValues> parsed;
        try
        {
            using var reader = new FormReader(new MemoryStream(body))
            {
                ValueCountLimit = FormValueCountLimit,
                KeyLengthLimit = FormKeyLengthLimit,
                ValueLengthLimit = FormValueLengthLimit,
            };
            parsed = await reader.ReadFormAsync(ct);
        }
        catch (InvalidDataException)
        {
            return (null, Refused(http, log, OAuthEndpointAnswer.Error(StatusCodes.Status400BadRequest, "invalid_request",
                $"The request holds at most {FormValueCountLimit} values, with names of at most {FormKeyLengthLimit} characters " +
                $"and values of at most {FormValueLengthLimit / 1024} KB.", $"{endpoint}_form_limits"), clientId: null));
        }

        var form = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, values) in parsed)
        {
            var given = values.Where(v => !string.IsNullOrEmpty(v)).ToArray();
            if (given.Length > 1)
                return (null, Refused(http, log, OAuthEndpointAnswer.Error(StatusCodes.Status400BadRequest, "invalid_request",
                    "A parameter is given more than once.", $"{endpoint}_repeated_parameter"), clientId: null));
            if (given.Length == 1) form[name] = given[0]!;
        }
        return (form, null);
    }

    /// <summary>The body, read up to <paramref name="limit"/> bytes; null when it is longer, before the rest is read.</summary>
    private static async Task<byte[]?> ReadBoundedAsync(HttpContext http, int limit, CancellationToken ct)
    {
        if (http.Request.ContentLength > limit) return null;
        var buffer = new byte[limit + 1];
        var total = 0;
        int read;
        while (total < buffer.Length && (read = await http.Request.Body.ReadAsync(buffer.AsMemory(total), ct)) > 0) total += read;
        return total > limit ? null : buffer[..total];
    }

    private static bool IsMediaType(string? contentType, string expected) =>
        MediaTypeHeaderValue.TryParse(contentType, out var type)
        && type.MediaType.Equals(expected, StringComparison.OrdinalIgnoreCase);

    private static IResult Answered(HttpContext http, OAuthEndpointAnswer answer)
    {
        if (answer.RetryAfter is { } retry) http.Response.Headers.RetryAfter = Seconds(retry);
        return Results.Json(answer.Body, statusCode: answer.StatusCode);
    }

    /// <summary>
    /// A refusal, answered, with one Information line for the operator: the
    /// fixed reason, the client id and the grant, each cut short. A token, a
    /// code, a verifier, a state or a challenge is never logged. A refusal at
    /// one of the operator's limits also warns, at most once a minute.
    /// </summary>
    private static IResult Refused(HttpContext http, ILogger log, OAuthEndpointAnswer answer, string? clientId, OAuthThrottles? throttles = null)
    {
        log.LogInformation("Refused {Method} {Path}: {Reason}, client {Client}, grant {Grant}.",
            http.Request.Method, http.Request.Path, answer.LogReason, Cut(clientId), Cut(answer.LogGrant));
        if (answer.Warning is { } warning && throttles is not null && throttles.RegistrationRefused())
            log.LogWarning("{OAuth}", warning);
        return Answered(http, answer);
    }

    /// <summary>At most 64 characters of printable ASCII, so a value a client chose cannot forge a log line.</summary>
    private static string Cut(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "-";
        var kept = new string(value.Take(64).Select(c => c is >= ' ' and <= '~' ? c : '?').ToArray());
        return value.Length > 64 ? kept + "..." : kept;
    }

    private static string Seconds(TimeSpan span) =>
        Math.Max(1, (int)Math.Ceiling(span.TotalSeconds)).ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// When this process serves its own certificate with no proxy in front, a
    /// warning if that certificate does not name the public address's host: an
    /// assistant checks the name, and would refuse to connect.
    /// </summary>
    /// <param name="publicCertificate">The certificate <c>prem setup</c> wrote in PEM beside its settings, or null when there is none.</param>
    public static string? CertificateWarning(string? publicCertificate, OAuthDeployment oauth)
    {
        ArgumentNullException.ThrowIfNull(oauth);
        if (publicCertificate is null || !File.Exists(publicCertificate)) return null;
        var authority = oauth.PublicUrl["https://".Length..];
        var host = authority.Split(':')[0];
        try
        {
            using var certificate = X509Certificate2.CreateFromPem(File.ReadAllText(publicCertificate));
            if (certificate.MatchesHostname(host)) return null;
            return $"{OAuthSettings.PublicUrl} names the host '{host}', but the certificate this server presents " +
                   $"({certificate.Subject}) does not. An assistant checks that name and will refuse to connect. " +
                   $"Set {OAuthSettings.PublicUrl} to an address the certificate names, or give the server a certificate for '{host}'.";
        }
        catch (CryptographicException)
        {
            return $"The certificate at {publicCertificate} could not be read, so whether it names '{host}' was not checked.";
        }
    }
}

/// <summary>
/// Reads, once, whether this process runs the authorization flow: forced at
/// start, after the tenant is known, so no request waits for it and every later
/// reader sees the same answer. The one piece of the flow present while it is
/// off; it holds the null.
/// </summary>
internal sealed class OAuthStart(PremagenticDatabase db, Deployment deployment, ILogger<OAuthStart> log)
{
    private readonly Lazy<OAuthDeployment?> _deployment = new(() => OAuthDeploymentLoader.LoadAsync(
        new SettingsStore(db, deployment.TenantId), line => log.LogWarning("{OAuth}", line)).GetAwaiter().GetResult());

    public OAuthDeployment? Loaded => _deployment.Value;
}

/// <summary>
/// The flow's limits held in this process's memory, each counting an IPv6
/// client by its /64 as sign-in does: failed token and revocation requests,
/// and self-registrations. Two API processes in front of one database each
/// hold their own; a restart forgets both. The limits the database holds (the
/// pending clients per address and in all) do not depend on either.
/// </summary>
internal sealed class OAuthThrottles(TimeProvider time) : IOAuthHealth
{
    /// <summary>Thirty failed requests in ten minutes from one address, then that address's failures wait.</summary>
    public static readonly SignInThrottleOptions FailureLimit = new(30, TimeSpan.FromMinutes(10), 10_000);

    /// <summary>The largest value <c>mcp.oauth.registrations_per_hour</c> takes, so no address's list grows past it.</summary>
    public const int RegistrationsPerHourMax = 1_000;

    private const int MaxAddresses = 10_000;
    private static readonly TimeSpan Hour = TimeSpan.FromHours(1);
    private static readonly TimeSpan Minute = TimeSpan.FromMinutes(1);

    private readonly SignInThrottle _failures = new(time, FailureLimit);
    private readonly Dictionary<string, Queue<DateTimeOffset>> _registrations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _throttleLogged = new(StringComparer.Ordinal);
    private DateTimeOffset? _registrationWarned;
    private readonly Lock _lock = new();

    public bool FailuresBlocked(IPAddress? address, out TimeSpan retryAfter) => _failures.IsBlocked(address, out retryAfter);

    public void RecordFailure(IPAddress? address) => _failures.RecordFailure(address);

    /// <summary>True at most once a minute for an address: whether its 429 from the token or revocation endpoint is logged.</summary>
    public bool LogThrottleNow(IPAddress? address)
    {
        var now = time.GetUtcNow();
        var key = SignInThrottle.Key(address);
        lock (_lock)
        {
            if (_throttleLogged.TryGetValue(key, out var last) && now - last < Minute) return false;
            if (_throttleLogged.Count >= MaxAddresses)
            {
                foreach (var (k, at) in _throttleLogged.ToArray())
                    if (now - at >= Minute) _throttleLogged.Remove(k);
                if (_throttleLogged.Count >= MaxAddresses) _throttleLogged.Remove(_throttleLogged.MinBy(kv => kv.Value).Key);
            }
            _throttleLogged[key] = now;
            return true;
        }
    }

    /// <summary>Whether an address has made <paramref name="perHour"/> self-registrations within the last hour.</summary>
    public bool RegistrationsOver(string key, int perHour, out TimeSpan retryAfter, out int made)
    {
        var now = time.GetUtcNow();
        lock (_lock)
        {
            retryAfter = TimeSpan.Zero;
            made = 0;
            if (!_registrations.TryGetValue(key, out var recent)) return false;
            while (recent.Count > 0 && recent.Peek() <= now - Hour) recent.Dequeue();
            made = recent.Count;
            if (made < perHour) return false;
            retryAfter = recent.ElementAt(made - perHour) + Hour - now;
            return true;
        }
    }

    public void RecordRegistration(string key)
    {
        var now = time.GetUtcNow();
        lock (_lock)
        {
            if (!_registrations.TryGetValue(key, out var recent))
            {
                if (_registrations.Count >= MaxAddresses)
                {
                    foreach (var (k, q) in _registrations.ToArray())
                    {
                        while (q.Count > 0 && q.Peek() <= now - Hour) q.Dequeue();
                        if (q.Count == 0) _registrations.Remove(k);
                    }
                    if (_registrations.Count >= MaxAddresses) _registrations.Remove(_registrations.MinBy(kv => kv.Value.Last()).Key);
                }
                _registrations[key] = recent = new Queue<DateTimeOffset>();
            }
            recent.Enqueue(now);
            while (recent.Count > RegistrationsPerHourMax) recent.Dequeue();
        }
    }

    /// <summary>
    /// Notes a self-registration refused at one of the operator's limits.
    /// True at most once a minute, when that refusal is to be warned about.
    /// </summary>
    public bool RegistrationRefused()
    {
        var now = time.GetUtcNow();
        lock (_lock)
        {
            LastRegistrationRefusal = now;
            if (_registrationWarned is { } last && now - last < Minute) return false;
            _registrationWarned = now;
            return true;
        }
    }

    /// <summary>When a self-registration was last refused at one of the operator's limits in this process, or null.</summary>
    public DateTimeOffset? LastRegistrationRefusal { get; private set; }
}

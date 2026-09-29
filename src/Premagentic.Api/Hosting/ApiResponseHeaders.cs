using Premagentic.Core.Storage;

namespace Premagentic.Api.Hosting;

/// <summary>Whether this process terminates TLS itself, which is when it may tell a browser to use nothing else.</summary>
/// <param name="Hsts">True when <c>Strict-Transport-Security</c> is sent on HTTPS responses.</param>
public sealed record ApiResponseHeaderOptions(bool Hsts)
{
    /// <summary>
    /// HSTS only when the service holds the certificate <c>prem setup</c> made
    /// (its Kestrel settings file beside the credentials file) and no proxy is
    /// trusted. Behind a proxy, the proxy owns the TLS and says what browsers
    /// should do; a header from here would speak for a host this process does
    /// not answer for.
    /// </summary>
    public static ApiResponseHeaderOptions From(IConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var ownCertificate = InstallFiles.KestrelSettingsBeside(config["PREM_CREDENTIALS_FILE"]) is not null;
        var behindProxy = !string.IsNullOrWhiteSpace(config[TrustedProxies.Key]);
        return new ApiResponseHeaderOptions(ownCertificate && !behindProxy);
    }
}

/// <summary>
/// Headers on every response of the API's own surfaces, <c>/api</c>,
/// <c>/mcp</c> and <c>/health</c>: no sniffing a JSON body into something a
/// browser would run, and no storing it, since a search result is a passage
/// of somebody's document. The portal sets its own, stricter set. HSTS, when
/// <see cref="ApiResponseHeaderOptions.Hsts"/> allows it, goes on every HTTPS
/// response, since it speaks for the whole host.
/// </summary>
public sealed class ApiResponseHeaders(RequestDelegate next, ApiResponseHeaderOptions options)
{
    /// <summary>A year, the value browsers' preload lists expect.</summary>
    public const string HstsValue = "max-age=31536000";

    private static readonly PathString[] Surfaces = ["/api", "/mcp", "/health"];

    public Task InvokeAsync(HttpContext context)
    {
        var request = context.Request;
        var surface = Surfaces.Any(p => request.Path.StartsWithSegments(p, StringComparison.OrdinalIgnoreCase));
        var hsts = options.Hsts && request.IsHttps;
        if (surface || hsts)
        {
            context.Response.OnStarting(() =>
            {
                var headers = context.Response.Headers;
                if (surface)
                {
                    headers.XContentTypeOptions = "nosniff";
                    headers.CacheControl = "no-store";
                }
                if (hsts) headers.StrictTransportSecurity = HstsValue;
                return Task.CompletedTask;
            });
        }
        return next(context);
    }
}

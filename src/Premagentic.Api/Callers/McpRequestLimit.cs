using System.Text.Json;
using Microsoft.AspNetCore.Http.Metadata;
using Premagentic.Core.Identity;
using Premagentic.Core.Storage;

namespace Premagentic.Api.Callers;

/// <summary>
/// The bound on a request to <c>/mcp</c>, of the deployment's own choosing
/// (<see cref="McpSettings.MaxRequestBytes"/>) rather than the web server's
/// default. The endpoint carries it, so the web server applies it through its
/// per-endpoint body size feature; <see cref="McpRequestLimitMiddleware"/>
/// answers a body over it with the HTTP status for a body too large and a
/// JSON-RPC error, on every host, including one without that feature.
/// </summary>
internal sealed class McpRequestSizeLimit(int bytes) : IRequestSizeLimitMetadata
{
    public int Bytes { get; } = bytes;

    public long? MaxRequestBodySize => Bytes;

    /// <summary>
    /// Read once, at start, as the other MCP settings are: a bound that moved
    /// under a running server would refuse a request the same assistant sent a
    /// minute ago. A stored value that cannot be used leaves the default, with
    /// one warning.
    /// </summary>
    public static async Task<McpRequestSizeLimit> ReadAsync(IServiceProvider services, ILogger logger, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(logger);

        var store = new SettingsStore(services.GetRequiredService<PremagenticDatabase>(), services.GetRequiredService<Deployment>().TenantId);
        var bytes = McpSettings.MaxRequestBytesDefault;
        if (await store.GetAsync(McpSettings.MaxRequestBytes, ct) is { } stored)
        {
            if (McpSettings.MaxRequestBytesProblem(stored) is { } problem)
                logger.LogWarning("{Problem} The stored value cannot be used, so the default of {Default} bytes applies.",
                    problem, McpSettings.MaxRequestBytesDefault);
            else
                bytes = stored.GetInt32();
        }
        logger.LogInformation("A request to /mcp may carry at most {Bytes} bytes ({Key}).", bytes, McpSettings.MaxRequestBytes);
        return new McpRequestSizeLimit(bytes);
    }

    /// <summary>The one comparison both paths make, so a body at the bound is taken and one byte over it is not.</summary>
    public static bool Over(long bytes, int bound) => bytes > bound;

    /// <summary>What a client reads when its request is refused, in the JSON-RPC error the SDK answers a malformed body with.</summary>
    public static string Message(int bound) => $"Content Too Large: the request body is over this server's limit of {bound} bytes.";
}

/// <summary>
/// Refuses a request to an endpoint that carries <see cref="McpRequestSizeLimit"/>
/// when its body is over the bound. A declared length over it is refused
/// before a byte is read. A POST of unknown length is read here as it
/// arrives, never more than one byte past the bound, so memory follows what
/// was sent and not the bound, and handed on from memory.
/// </summary>
internal sealed class McpRequestLimitMiddleware(RequestDelegate next, ILogger<McpRequestLimitMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext http)
    {
        if (http.GetEndpoint()?.Metadata.GetMetadata<McpRequestSizeLimit>() is not { } limit)
        {
            await next(http);
            return;
        }

        if (http.Request.ContentLength is { } declared)
        {
            if (McpRequestSizeLimit.Over(declared, limit.Bytes))
            {
                await RefuseAsync(http, limit.Bytes);
                return;
            }
        }
        else if (HttpMethods.IsPost(http.Request.Method))
        {
            var received = new MemoryStream();
            http.Response.RegisterForDispose(received);
            var chunk = new byte[16 * 1024];
            try
            {
                int read;
                while ((read = await http.Request.Body.ReadAsync(
                           chunk.AsMemory(0, (int)Math.Min(chunk.Length, limit.Bytes + 1L - received.Length)), http.RequestAborted)) > 0)
                {
                    received.Write(chunk, 0, read);
                    if (McpRequestSizeLimit.Over(received.Length, limit.Bytes))
                    {
                        await RefuseAsync(http, limit.Bytes);
                        return;
                    }
                }
            }
            catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge)
            {
                // The web server's own count, at the same bound, got there first.
                await RefuseAsync(http, limit.Bytes);
                return;
            }
            received.Position = 0;
            http.Request.Body = received;
        }
        await next(http);
    }

    private async Task RefuseAsync(HttpContext http, int bound)
    {
        logger.LogInformation("Refused {Method} {Path}: the body is over {Key}, {Bound} bytes.",
            http.Request.Method, http.Request.Path, McpSettings.MaxRequestBytes, bound);
        http.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
        http.Response.ContentType = "application/json";
        await http.Response.WriteAsync(JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = (string?)null,
            error = new { code = -32600, message = McpRequestSizeLimit.Message(bound) },
        }), http.RequestAborted);
    }
}

using Microsoft.AspNetCore.Http.Metadata;
using Premagentic.Core.Retrieval;

namespace Premagentic.Api.Callers;

/// <summary>
/// The bound on a request to <c>/api/search</c> and <c>/api/section</c>:
/// 64 KiB rather than the web server's 30 MB. The largest body their fields
/// take is a path and a heading of <see cref="QueryLimits.MaxLength"/>
/// characters each with every character written as a six-byte JSON escape,
/// 48,000 bytes and the field names, so the bound leaves room and no more.
/// The endpoints carry it, so the web server applies it through its
/// per-endpoint body size feature, and <see cref="ReadRequestLimitMiddleware"/>
/// answers a body over it with 413 and one sentence on every host, including
/// one without that feature.
/// </summary>
internal sealed class ReadRequestLimit : IRequestSizeLimitMetadata
{
    public const int Bytes = 64 * 1024;

    public static readonly ReadRequestLimit Instance = new();

    public long? MaxRequestBodySize => Bytes;

    /// <summary>What a caller reads when its request is refused.</summary>
    public static string Refusal { get; } = $"The request body is over this endpoint's limit of {Bytes} bytes.";

    /// <summary>The one comparison both paths make, so a body at the bound is taken and one byte over it is not.</summary>
    public static bool Over(long bytes) => bytes > Bytes;
}

/// <summary>
/// Refuses a request to an endpoint that carries <see cref="ReadRequestLimit"/>
/// when its body is over the bound. A declared length over it is refused
/// before a byte is read. A body of unknown length is read here as it arrives,
/// never more than one byte past the bound, and handed on from memory.
/// </summary>
internal sealed class ReadRequestLimitMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext http)
    {
        if (http.GetEndpoint()?.Metadata.GetMetadata<ReadRequestLimit>() is null)
        {
            await next(http);
            return;
        }

        if (http.Request.ContentLength is { } declared)
        {
            if (ReadRequestLimit.Over(declared))
            {
                await RefuseAsync(http);
                return;
            }
        }
        else if (!await HeldWithinBoundAsync(http))
        {
            await RefuseAsync(http);
            return;
        }
        await next(http);
    }

    /// <returns>False when the body went past the bound; otherwise true, with the body read into memory and handed on from there.</returns>
    private static async Task<bool> HeldWithinBoundAsync(HttpContext http)
    {
        var received = new MemoryStream();
        http.Response.RegisterForDispose(received);
        var chunk = new byte[16 * 1024];
        try
        {
            int read;
            while ((read = await http.Request.Body.ReadAsync(
                       chunk.AsMemory(0, (int)Math.Min(chunk.Length, ReadRequestLimit.Bytes + 1L - received.Length)), http.RequestAborted)) > 0)
            {
                received.Write(chunk, 0, read);
                if (ReadRequestLimit.Over(received.Length)) return false;
            }
        }
        catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            // The web server's own count, at the same bound, got there first.
            return false;
        }
        received.Position = 0;
        http.Request.Body = received;
        return true;
    }

    private static Task RefuseAsync(HttpContext http)
    {
        http.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
        return http.Response.WriteAsJsonAsync(new { error = ReadRequestLimit.Refusal }, http.RequestAborted);
    }
}

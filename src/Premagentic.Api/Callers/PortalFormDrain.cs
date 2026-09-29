using Microsoft.AspNetCore.Http;

namespace Premagentic.Api.Callers;

/// <summary>
/// How much of a form body past its bounds the host reads and discards before
/// it answers, over HTTP/1.1, so a browser still sending sees the portal's
/// answer instead of a reset connection. Registered by the host with
/// <see cref="Default"/>; not an operator setting.
/// </summary>
/// <param name="CapBytes">
/// The largest declared body drained: Kestrel's own default request body limit,
/// which any endpoint without a bound of its own already takes from a signed-in
/// person, so a drain makes the host read nothing it could not already be made
/// to read. A body declared larger is answered as before, without a drain.
/// </param>
/// <param name="Time">
/// The most one drain takes: Kestrel's default for a request's headers. At the
/// limit the drain stops, and the answer closes the connection as it did before.
/// </param>
internal sealed record PortalFormDrain(long CapBytes, TimeSpan Time)
{
    public static PortalFormDrain Default { get; } = new(30_000_000, TimeSpan.FromSeconds(30));

    /// <summary>
    /// Whether a request's body is drained before the answer: over HTTP/1.1
    /// only, since over HTTP/2 the stream ends and the answer arrives already;
    /// from the portal's own origin only, so a request the portal would refuse
    /// is refused without a byte read; and only for a declared length past the
    /// endpoint's bound and within <paramref name="cap"/>. A body with no
    /// declared length is not drained.
    /// <para>
    /// Nor is the body of a request that asks for <c>100 Continue</c>: that
    /// sender holds its body back until the host says go, so it is not a
    /// browser still sending, and reading would only make the host say go and
    /// then throw away what arrives. It is answered at once, as before.
    /// </para>
    /// </summary>
    public static bool Drains(string protocol, long? declared, long? bound, long cap, bool fromThisOrigin, bool expectsContinue) =>
        HttpProtocol.IsHttp11(protocol)
        && fromThisOrigin
        && !expectsContinue
        && declared is { } length && bound is { } limit
        && length > limit
        && length <= cap;
}

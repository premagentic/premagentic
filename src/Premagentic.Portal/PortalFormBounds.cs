using Premagentic.Portal.Html;
using Microsoft.AspNetCore.Http;

namespace Premagentic.Portal;

/// <summary>
/// Endpoint metadata for a form with bounds of its own, such as a form that
/// carries a client's metadata document. The host reads such a form once,
/// before the page runs, within the bounds routing gives the endpoint. When
/// the form is past them, the page never runs, and the host answers with
/// <see cref="Answer"/>: the redirect every portal form gives, back to
/// <see cref="Back"/>, with <see cref="Refusal"/> to show.
/// <para>
/// The sentence is shown for every form the host refuses this way: a value or
/// a file past its bound, or a whole body past its bound. It must be true of
/// each of them. Nothing of a form read in part reaches it.
/// </para>
/// </summary>
/// <param name="back">The portal page the person lands on.</param>
/// <param name="refusal">The sentence that page shows.</param>
public sealed class PortalFormBounds(string back, string refusal)
{
    public string Back { get; } = back;

    public string Refusal { get; } = refusal;

    /// <summary>The answer to a form past its bounds.</summary>
    public IResult Answer() => Layout.After(Back, error: Refusal);

    /// <summary>
    /// Whether a request came from the portal's own origin, by the very test the
    /// portal's access filter applies to every change: headers only, nothing of
    /// the body read. A host that would read a body before that filter runs asks
    /// this first, so a request the filter would refuse is never read.
    /// </summary>
    public static bool FromThisOrigin(HttpRequest request) => PortalAccessFilter.FromThisOrigin(request);
}

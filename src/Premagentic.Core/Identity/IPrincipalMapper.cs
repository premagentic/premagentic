using Premagentic.Core.Storage;

namespace Premagentic.Core.Identity;

/// <summary>
/// What a principal from somebody else's system means here: a PremAgentic
/// group, or nothing. Two things hand PremAgentic such principals. A sign-in
/// adapter reports the groups the system it spoke to saw, and a connector can
/// name the principals a file's permissions carry. A principal mapper, which
/// an extension brings, is the only thing that turns either into a group.
/// <para>
/// With no mapper loaded, which is the built-in answer, every such principal
/// means nothing: a reported group is ignored and a file readable only by
/// outside principals is readable by nobody. Nothing reads an outside name as
/// a PremAgentic name.
/// </para>
/// <para>
/// A mapper answers with group ids and nothing else, and the host keeps an
/// answer only when it is for a principal it asked about and names a live
/// group an administrator made in this deployment. A mapper can therefore not
/// hand anybody <c>everyone</c>, a user, a role, or a group PremAgentic
/// maintains itself, whatever it returns. One deployment has at most one
/// mapper.
/// </para>
/// </summary>
public interface IPrincipalMapper
{
    /// <summary>
    /// How this mapper is named on the health page and in the log: lower case,
    /// shaped like a chunker name.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// For each principal in <paramref name="request"/> that means a group
    /// here, that group's id. A principal with no entry means nothing. A
    /// principal is compared exactly, as the outside system gave it: never case
    /// folded and never trimmed into shape, because a mapping that repairs a
    /// principal matches somebody else's.
    /// <para>
    /// An exception is not caught. The request that asked fails, or the ingest
    /// run is recorded as failed, so a mapper that cannot answer never quietly
    /// widens or narrows what anybody reaches.
    /// </para>
    /// </summary>
    Task<IReadOnlyDictionary<string, Guid>> MapAsync(PrincipalMapRequest request, CancellationToken ct = default);
}

/// <summary>What a principal mapper is asked: which principals, in which deployment.</summary>
/// <param name="Database">The deployment's database, as the host process connects to it.</param>
/// <param name="TenantId">The tenant the principals are asked about.</param>
/// <param name="Principals">Each principal once, none blank, in the order they were met.</param>
public sealed record PrincipalMapRequest(PremagenticDatabase Database, Guid TenantId, IReadOnlyList<string> Principals);

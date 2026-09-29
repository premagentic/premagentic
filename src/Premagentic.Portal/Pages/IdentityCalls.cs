using Premagentic.Core.Identity;
using Premagentic.Core.Security;

namespace Premagentic.Portal.Pages;

/// <summary>
/// The identity operations whose store method answers "nothing to change" with
/// false. The portal turns that into a refusal with a reason, so a change the
/// page asked for is either made and recorded, or refused with nothing written.
/// </summary>
internal static class RoleChange
{
    public static async Task SetAsync(IdentityStore identity, Guid userId, Role role, CancellationToken ct)
    {
        if (!await identity.SetUserRoleAsync(userId, role, ct))
            throw new InvalidOperationException("That user no longer exists.");
    }
}

internal static class AgentLimits
{
    public static async Task SetAsync(IdentityStore identity, Guid agentId, int requestsPerMinute, string? minimumTrustTier, CancellationToken ct)
    {
        if (!await identity.SetAgentLimitsAsync(agentId, requestsPerMinute, minimumTrustTier, ct))
            throw new InvalidOperationException("That agent no longer exists.");
    }
}

internal static class ViewAsScope
{
    /// <summary>The target's scope, labeled in the audit trail as the administrator viewing as the target.</summary>
    public static AccessScope For(AccessScope target, Guid administratorUserId) => target.ViewedAsBy(administratorUserId);
}

internal static class AgentCaller
{
    /// <summary>A registered agent as it would be resolved now, with no token, for view as and why only.</summary>
    public static Task<Caller> ResolveAsync(IdentityStore identity, Guid agentId, CancellationToken ct) =>
        CallerAccess.ResolveAgentAsync(identity, agentId, ct);
}

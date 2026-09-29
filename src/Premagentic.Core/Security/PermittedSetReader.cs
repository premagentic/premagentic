using Premagentic.Core.Security.Acl;
using Premagentic.Core.Storage;

namespace Premagentic.Core.Security;

/// <summary>
/// Decides, for one read, which access lists a scope may read: every list the
/// tenant has stored, evaluated in C# against the scope, first match in each
/// list's own order. The access gate then compares integers.
/// <para>
/// For an agent in the hosted-model agents group, the lists held documents
/// store are then taken out (<see cref="HostedHolds"/>), which fails closed.
/// </para>
/// <para>
/// Nothing is cached. Lists are few (documents under one folder share one), so
/// reading them all per search is cheap, and it means a revoked group, a
/// disabled account or a changed rule applies to the very next query with
/// nothing to invalidate.
/// </para>
/// </summary>
public static class PermittedSetReader
{
    /// <summary>The same scope, carrying the ids of the lists it may read in <paramref name="tenantId"/>.</summary>
    public static async Task<AccessScope> ResolveAsync(
        PremagenticDatabase db, Guid tenantId, AccessScope scope, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(scope);

        // The gate lets an unrestricted scope through on its own flag, and a
        // scope that holds nothing can match no entry, so neither needs the lists.
        if (scope.Unrestricted || scope.Holds.Count == 0)
            return scope with { PermittedSetIds = [] };

        var sets = await new AclStore(db, tenantId).LoadSetsAsync(ct);
        var permitted = PermittedSets.ResolveWith(sets, scope.CanRead);

        // An agent PremAgentic keeps in the hosted-model agents group reads no
        // list that any held document stores, whatever the list says. Held
        // documents store their list with that group denied first, so this
        // takes away nothing they did not already deny; it is what holds if a
        // writer ever failed to stamp one. Taken out here, before the read is
        // bound, so the SQL gate and row-level security both carry it.
        if (scope.SystemGroups.Count > 0)
        {
            var held = await HostedHolds.HeldSetIdsAsync(db, tenantId, scope.SystemGroups, permitted, ct);
            if (held.Count > 0) permitted = [.. permitted.Where(id => !held.Contains(id))];
        }
        return scope with { PermittedSetIds = permitted };
    }
}

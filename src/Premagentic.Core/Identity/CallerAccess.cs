using Premagentic.Core.Okf;
using Premagentic.Core.Security;
using Premagentic.Core.Security.Acl;
using Premagentic.Core.Storage;

namespace Premagentic.Core.Identity;

/// <summary>
/// One caller, resolved for one request: the scope its searches run under, and
/// whether it is a person or an agent, which decides its trust policy.
/// </summary>
public sealed record Caller(ResolvedCaller Resolved, AccessScope Scope)
{
    public bool IsResolved => Resolved.IsResolved;

    /// <summary>An agent whenever a token was presented, whichever mode the agent runs in.</summary>
    public CallerKind Kind => Resolved.AgentId is null ? CallerKind.Person : CallerKind.Agent;

    /// <summary>The agent's record, for a resolved agent. Null for a person.</summary>
    public Agent? Agent => Resolved.Agent;
}

/// <summary>
/// Turns a user or a presented agent token into the scope a search runs under,
/// resolved against this tenant's users, groups and agents as they are now.
/// </summary>
public static class CallerAccess
{
    /// <summary>A user whose identity the host has already established, such as an operator at the console.</summary>
    public static async Task<AccessScope> ForUserAsync(IdentityStore store, Guid userId, CancellationToken ct = default) =>
        (await ResolveUserAsync(store, userId, ct)).Scope;

    /// <summary>An agent presenting a token. See <see cref="ResolveAgentTokenAsync"/>.</summary>
    public static async Task<AccessScope> ForAgentTokenAsync(IdentityStore store, string presentedToken, CancellationToken ct = default) =>
        (await ResolveAgentTokenAsync(store, presentedToken, ct)).Scope;

    /// <summary>A user whose identity the host has already established: by a session, a trusted header, or at the console.</summary>
    public static Task<Caller> ResolveUserAsync(IdentityStore store, Guid userId, CancellationToken ct = default) =>
        ResolveUserAsync(store, userId, [], ct);

    /// <summary>
    /// The same, plus the groups a sign-in adapter's directory said this person
    /// is in, already read through the deployment's principal mapper.
    /// </summary>
    /// <param name="fromDirectory">
    /// Premagentic groups, never the outside names: what an outside group means
    /// here is decided by the principal mapper, before this is called, and is
    /// nothing when there is none. They are added to what the person holds,
    /// which is what the administrator asked for by allowing the mapper, and
    /// they are added only to a caller that resolved: a disabled or unknown
    /// account still holds nothing at all.
    /// </param>
    public static async Task<Caller> ResolveUserAsync(
        IdentityStore store, Guid userId, IReadOnlyList<Principal> fromDirectory, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(fromDirectory);
        var caller = await new CallerResolver(store, store.Time).ResolveAsync(CallerIdentity.SignedInUser(userId), ct);
        if (caller.IsResolved && fromDirectory.Count > 0)
            caller = caller with { Principals = PrincipalSet.From(caller.Principals.Concat(fromDirectory)) };
        return new Caller(caller, AccessScope.ForCaller(caller));
    }

    /// <summary>
    /// A registered agent by its id, with no token, as it would read if it
    /// called now. For an administrator viewing as the agent only: an agent's
    /// own call presents its token. Nothing is recorded as a use.
    /// </summary>
    public static async Task<Caller> ResolveAgentAsync(IdentityStore store, Guid agentId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        var caller = await new CallerResolver(store, store.Time).ResolveAgentAsync(agentId, ct);
        return new Caller(caller, AccessScope.ForCaller(caller));
    }

    /// <summary>
    /// An agent calling with an access token of the authorization flow, which
    /// <see cref="OAuthTokenService.CheckAccessAsync"/> has already passed. The
    /// agent and its owner are judged as for any agent; the audit label names
    /// the grant and the token. No agent token use is recorded.
    /// </summary>
    public static async Task<Caller> ResolveOAuthAsync(IdentityStore store, OAuthAccess access, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(access);
        var caller = await new CallerResolver(store, store.Time).ResolveOAuthAsync(access.AgentId, access.CredentialId, ct);
        return new Caller(caller, AccessScope.ForCaller(caller));
    }

    /// <summary>
    /// An agent presenting a token. A token that resolves has its use recorded
    /// here, after resolution, because the resolver itself never writes; a
    /// refused token is not touched.
    /// </summary>
    public static async Task<Caller> ResolveAgentTokenAsync(IdentityStore store, string presentedToken, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        var caller = await new CallerResolver(store, store.Time).ResolveAsync(CallerIdentity.AgentToken(presentedToken), ct);
        if (caller.IsResolved && caller.TokenId is { } tokenId)
            await store.RecordTokenUseAsync(tokenId, ct);
        return new Caller(caller, AccessScope.ForCaller(caller));
    }
}

/// <summary>
/// The one place a caller's trust policy is decided. Every host that searches
/// for a caller asks here, so the deployment's settings reach them all at once.
/// </summary>
public static class CallerPolicy
{
    /// <summary>
    /// The trust policy for one caller in one tenant, from the tenant's trust
    /// settings as they are at this moment (<see cref="TrustSettingsStore"/>), so
    /// a change applies to the very next search. A tenant that has changed
    /// nothing runs on the defaults: agents see machine-written content only once
    /// a person has reviewed it, people see it flagged, and stale content is
    /// hidden from agents.
    /// </summary>
    public static Task<TrustPolicy> TrustAsync(
        PremagenticDatabase db, Guid tenantId, CallerKind kind, string? agentMinimumTier, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        return new TrustSettingsStore(db, tenantId).ResolveAsync(kind, agentMinimumTier, ct);
    }

    /// <inheritdoc cref="TrustAsync(PremagenticDatabase, Guid, CallerKind, string?, CancellationToken)"/>
    public static Task<TrustPolicy> TrustAsync(PremagenticDatabase db, Guid tenantId, Caller caller, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        return TrustAsync(db, tenantId, caller.Kind, caller.Agent?.MinimumTrustTier, ct);
    }
}

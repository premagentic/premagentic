using Premagentic.Core.Security.Acl;

namespace Premagentic.Core.Identity;

/// <summary>
/// Who is calling, as proven by whatever authenticated them, before it becomes principals.
/// </summary>
public sealed class CallerIdentity
{
    private readonly string? _presentedToken;

    private CallerIdentity(Guid? userId, string? presentedToken)
    {
        UserId = userId;
        _presentedToken = presentedToken;
    }

    /// <summary>A user whose sign-in the host has already verified.</summary>
    public static CallerIdentity SignedInUser(Guid userId) => new(userId, null);

    /// <summary>An agent presenting a token, not yet checked.</summary>
    public static CallerIdentity AgentToken(string presentedToken) => new(null, presentedToken ?? "");

    public Guid? UserId { get; }

    public bool IsAgentToken => _presentedToken is not null;

    internal string? PresentedToken => _presentedToken;

    /// <summary>Never includes a presented token, so it is safe in a log line.</summary>
    public override string ToString() => IsAgentToken ? "agent token (redacted)" : $"user {UserId}";
}

/// <summary>Why a caller did or did not resolve. For the audit trail and the operator, never for the caller.</summary>
public enum CallerStatus
{
    Resolved = 1,
    UnknownUser,
    UserDisabled,
    MalformedToken,
    UnknownToken,
    TokenMismatch,
    TokenRevoked,
    TokenExpired,
    UnknownAgent,
    AgentDisabled,
    OwnerUnknown,
    OwnerDisabled,

    /// <summary>The directory returned a record that does not belong to what was asked for.</summary>
    InconsistentDirectory,

    /// <summary>
    /// A token of an agent its person made (<c>created_by</c> <c>self:</c>)
    /// issued before its owner or the agent was disabled, or before its owner's
    /// password changed. Final: re-enabling does not bring it back. Checked
    /// after the token's expiry and after the agent and owner checks, so a
    /// disabled owner or agent still reads as <see cref="OwnerDisabled"/> or
    /// <see cref="AgentDisabled"/>.
    /// </summary>
    TokenSuperseded,
}

/// <summary>
/// The principals a caller holds on this call, and for the audit trail, which
/// user and agent they came from. Anything other than
/// <see cref="CallerStatus.Resolved"/> carries <see cref="PrincipalSet.Empty"/>.
/// </summary>
/// <param name="NarrowedBy">
/// For an agent acting for a user, the agent's own principal: a list must also
/// permit the user's principals plus this one, so an entry naming the agent can
/// only take access away. Null otherwise.
/// </param>
/// <param name="Agent">
/// The agent's record as it was read for this call, for its rate limit and its
/// minimum trust tier. Set only on a resolved agent.
/// </param>
/// <param name="SystemGroups">
/// The groups Premagentic itself puts the agent in, such as the hosted-model
/// group (<see cref="Identity.SystemGroups.HostedModelAgents"/>). They are
/// weighed beside <see cref="NarrowedBy"/> and never among
/// <see cref="Principals"/>, which is what makes them one-way: a rule that
/// denies such a group holds the agent back, and a rule that allows one gives
/// nothing to anybody, so marking an agent's model hosted can never let it
/// reach further than it already could.
/// </param>
public sealed record ResolvedCaller(
    CallerStatus Status,
    PrincipalSet Principals,
    Guid? UserId,
    Guid? AgentId,
    string? TokenId,
    Principal? NarrowedBy = null,
    Agent? Agent = null,
    IReadOnlyList<Principal>? SystemGroups = null)
{
    public bool IsResolved => Status == CallerStatus.Resolved;

    internal static ResolvedCaller Refused(CallerStatus status, Guid? userId = null, Guid? agentId = null, string? tokenId = null) =>
        new(status, PrincipalSet.Empty, userId, agentId, tokenId);
}

/// <summary>
/// Read access to identity records, supplied by the store. Lookups only: nothing
/// that resolves a caller writes anything.
/// </summary>
public interface IIdentityDirectory
{
    Task<User?> FindUserAsync(Guid userId, CancellationToken ct = default);

    /// <summary>Every group the user belongs to. If groups nest, the store returns the whole closure.</summary>
    Task<IReadOnlyList<Group>> GroupsOfUserAsync(Guid userId, CancellationToken ct = default);

    Task<Agent?> FindAgentAsync(Guid agentId, CancellationToken ct = default);

    /// <summary>The groups an administrator granted to a service agent.</summary>
    Task<IReadOnlyList<Group>> GroupsGrantedToAgentAsync(Guid agentId, CancellationToken ct = default);

    /// <summary>
    /// The groups Premagentic maintains that this agent is in, whatever its
    /// mode. Kept apart from the granted groups because these narrow and never
    /// widen; see <see cref="ResolvedCaller.SystemGroups"/>.
    /// </summary>
    Task<IReadOnlyList<Group>> SystemGroupsOfAgentAsync(Guid agentId, CancellationToken ct = default);

    Task<AgentTokenRecord?> FindTokenAsync(string tokenId, CancellationToken ct = default);
}

/// <summary>
/// The sign-in side twin of a document source: given an authenticated identity,
/// the principals it holds right now. Resolution happens on every call, so a
/// membership change applies to the next query rather than the next crawl.
/// </summary>
public interface ICallerResolver
{
    Task<ResolvedCaller> ResolveAsync(CallerIdentity identity, CancellationToken ct = default);
}

/// <summary>
/// Resolves callers from the identity records.
/// <list type="bullet">
/// <item>A user holds <c>user:&lt;id&gt;</c>, one <c>group:&lt;id&gt;</c> per
/// membership, and <c>everyone</c>. A role adds nothing.</item>
/// <item>An agent that acts for a user holds exactly its owner's principals as
/// they are at the moment of the call, and is narrowed by its own
/// <c>agent:&lt;id&gt;</c> (see <see cref="ResolvedCaller.NarrowedBy"/>), so it can
/// never reach more than the owner can, and an entry naming it can hold it
/// back.</item>
/// <item>A service agent holds <c>agent:&lt;id&gt;</c>, the groups an
/// administrator granted it, and <c>everyone</c>.</item>
/// <item>An agent of either mode also carries the groups Premagentic maintains
/// for it, such as the hosted-model group, on the narrowing side only: they can
/// take access away and never give any, whatever a rule says.</item>
/// <item>A disabled user or agent, a token that is malformed, unknown, wrong,
/// revoked or expired, and an agent whose owner is disabled or gone all hold
/// nothing at all, not even <c>everyone</c>.</item>
/// </list>
/// </summary>
public sealed class CallerResolver(IIdentityDirectory directory, TimeProvider? time = null) : ICallerResolver
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public async Task<ResolvedCaller> ResolveAsync(CallerIdentity identity, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        return identity.IsAgentToken
            ? await ResolveAgentTokenAsync(identity.PresentedToken!, ct)
            : await ResolveUserAsync(identity.UserId!.Value, ct);
    }

    private async Task<ResolvedCaller> ResolveUserAsync(Guid userId, CancellationToken ct)
    {
        var user = await directory.FindUserAsync(userId, ct);
        if (user is null) return ResolvedCaller.Refused(CallerStatus.UnknownUser, userId);
        if (user.Id != userId) return ResolvedCaller.Refused(CallerStatus.InconsistentDirectory, userId);
        if (user.Disabled) return ResolvedCaller.Refused(CallerStatus.UserDisabled, userId);

        var groups = await directory.GroupsOfUserAsync(userId, ct);
        return new ResolvedCaller(CallerStatus.Resolved, UserPrincipals(user, groups), userId, AgentId: null, TokenId: null);
    }

    private async Task<ResolvedCaller> ResolveAgentTokenAsync(string presented, CancellationToken ct)
    {
        if (!AgentTokens.TryParse(presented, out var token)) return ResolvedCaller.Refused(CallerStatus.MalformedToken);

        var tokenId = token.TokenId;
        var record = await directory.FindTokenAsync(tokenId, ct);
        if (record is null) return ResolvedCaller.Refused(CallerStatus.UnknownToken, tokenId: tokenId);
        if (record.Id != tokenId) return ResolvedCaller.Refused(CallerStatus.InconsistentDirectory, tokenId: tokenId);

        // The secret is checked before anything about the token's state is
        // reported, so only a holder of the real secret learns it was revoked.
        if (!AgentTokens.Matches(token, record.SecretHash)) return ResolvedCaller.Refused(CallerStatus.TokenMismatch, tokenId: tokenId);

        var agentId = record.AgentId;
        if (record.RevokedAt is not null) return ResolvedCaller.Refused(CallerStatus.TokenRevoked, agentId: agentId, tokenId: tokenId);
        if (_time.GetUtcNow() >= record.ExpiresAt) return ResolvedCaller.Refused(CallerStatus.TokenExpired, agentId: agentId, tokenId: tokenId);

        // The agent and its owner are judged before the token's stamps, so a
        // disabled owner or agent reads as that, and only a token that is
        // dead by its stamps alone reads as superseded.
        var resolved = await ResolveAgentAsync(agentId, tokenId, ct);
        if (resolved.IsResolved && record.Superseded)
            return ResolvedCaller.Refused(CallerStatus.TokenSuperseded, resolved.UserId, agentId, tokenId);
        return resolved;
    }

    /// <summary>
    /// A registered agent by its id, with no token: what it would hold if it
    /// called now. For an administrator viewing as the agent, never for an
    /// agent's own call, which must present its token.
    /// </summary>
    public Task<ResolvedCaller> ResolveAgentAsync(Guid agentId, CancellationToken ct = default) =>
        ResolveAgentAsync(agentId, tokenId: null, ct);

    /// <summary>
    /// An agent's own call through the authorization flow, after its access
    /// token passed the flow's checks: the agent and its owner are judged as
    /// for any agent, and <paramref name="credentialId"/>
    /// (<c>oauth:&lt;grant&gt;:&lt;token&gt;</c>) names the credential in the audit
    /// label, so a call joins to its grant. It is never a token use.
    /// </summary>
    internal Task<ResolvedCaller> ResolveOAuthAsync(Guid agentId, string credentialId, CancellationToken ct = default) =>
        ResolveAgentAsync(agentId, credentialId, ct);

    private async Task<ResolvedCaller> ResolveAgentAsync(Guid agentId, string? tokenId, CancellationToken ct)
    {
        var agent = await directory.FindAgentAsync(agentId, ct);
        if (agent is null) return ResolvedCaller.Refused(CallerStatus.UnknownAgent, agentId: agentId, tokenId: tokenId);
        if (agent.Id != agentId) return ResolvedCaller.Refused(CallerStatus.InconsistentDirectory, agentId: agentId, tokenId: tokenId);
        if (agent.Disabled) return ResolvedCaller.Refused(CallerStatus.AgentDisabled, agentId: agentId, tokenId: tokenId);

        // Both modes answer to the owner. An agent whose owner is disabled or
        // gone stops, whichever mode it runs in.
        var ownerId = agent.OwnerUserId;
        var owner = await directory.FindUserAsync(ownerId, ct);
        if (owner is null) return ResolvedCaller.Refused(CallerStatus.OwnerUnknown, ownerId, agentId, tokenId);
        if (owner.Id != ownerId) return ResolvedCaller.Refused(CallerStatus.InconsistentDirectory, ownerId, agentId, tokenId);
        if (owner.Disabled) return ResolvedCaller.Refused(CallerStatus.OwnerDisabled, ownerId, agentId, tokenId);

        // Read for both modes, and read from the membership rows rather than
        // from the agent's own attributes, so that the gate weighs the same
        // thing an administrator sees listed under the group.
        var systemGroups = (await directory.SystemGroupsOfAgentAsync(agentId, ct))
            .Select(g => Principal.Group(IdText(g.Id)))
            .ToArray();

        switch (agent.Mode)
        {
            case AgentMode.ActsForUser:
                var ownerGroups = await directory.GroupsOfUserAsync(ownerId, ct);
                return new ResolvedCaller(CallerStatus.Resolved, UserPrincipals(owner, ownerGroups), ownerId, agentId, tokenId,
                    NarrowedBy: Principal.Agent(IdText(agentId)), Agent: agent, SystemGroups: systemGroups);

            case AgentMode.Service:
                var granted = await directory.GroupsGrantedToAgentAsync(agentId, ct);
                return new ResolvedCaller(CallerStatus.Resolved, ServiceAgentPrincipals(agent, granted), UserId: null, agentId, tokenId,
                    Agent: agent, SystemGroups: systemGroups);

            default:
                return ResolvedCaller.Refused(CallerStatus.InconsistentDirectory, agentId: agentId, tokenId: tokenId);
        }
    }

    private static PrincipalSet UserPrincipals(User user, IEnumerable<Group> groups) =>
        PrincipalSet.From(
            new[] { Principal.User(IdText(user.Id)), Principal.Everyone }
                .Concat(groups.Select(g => Principal.Group(IdText(g.Id)))));

    private static PrincipalSet ServiceAgentPrincipals(Agent agent, IEnumerable<Group> granted) =>
        PrincipalSet.From(
            new[] { Principal.Agent(IdText(agent.Id)), Principal.Everyone }
                .Concat(granted.Select(g => Principal.Group(IdText(g.Id)))));

    /// <summary>The one text form of an id inside a principal: lower-case, hyphenated.</summary>
    public static string IdText(Guid id) => id.ToString("D");
}

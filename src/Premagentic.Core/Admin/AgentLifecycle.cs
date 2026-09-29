using Premagentic.Core.Identity;

namespace Premagentic.Core.Admin;

/// <summary>
/// Reissuing an assistant's key and removing an assistant: the one place the
/// command line, the administrator's pages and a person's own page go through,
/// each inside an administrator change, so the change and its one row in the
/// change record land together or not at all. Who may is the caller's to
/// decide: an administrator any agent, a person their own, through
/// <see cref="SelfServeAgents"/>.
/// <para>
/// A reissue rotates a live key and is never a way back: a disabled agent, a
/// disabled person, a revoked, expired or superseded token and an agent whose
/// credentials belong to a grant are all refused, so nothing the decision that
/// a disable is final ended can return through here.
/// </para>
/// </summary>
public static class AgentLifecycle
{
    /// <summary>The kinds of the change record rows a reissue and a removal leave.</summary>
    public const string ReissueKind = "token.reissue";

    public const string RemoveKind = "agent.remove";

    public const string NoSuchToken = "There is no token with this id.";

    public const string NoSuchAgent = "There is no agent with this id.";

    public const string GrantHeld = "This assistant's credentials belong to its grant. Revoke the grant to end them.";

    private const string NeverBack = "A reissue replaces a live key and never brings one back.";

    public static string AgentDisabled(string name) => $"Assistant '{name}' is disabled. {NeverBack}";

    public static string OwnerDisabled(string name) => $"Assistant '{name}' acts for a person who is disabled. {NeverBack}";

    public static string TokenRevoked(string tokenId) => $"Token {tokenId} was revoked. {NeverBack}";

    public static string TokenExpired(string tokenId) => $"Token {tokenId} has expired. Issue a new one.";

    public static string TokenSuperseded(string tokenId) =>
        $"Token {tokenId} ended when its assistant or its person was disabled or the password changed. {NeverBack}";

    /// <summary>
    /// Replaces a live token with a new one for the same agent: the new one is
    /// issued as <see cref="IdentityStore.IssueTokenAsync"/> issues any, and the
    /// old one revoked, both or neither. Refused, with nothing written, unless
    /// the agent is live, enabled, not made through the authorization flow, its
    /// person live, and the token neither revoked, expired nor superseded.
    /// </summary>
    /// <param name="lifetime">How long the new token lasts; null gives it the replaced token's own lifetime, counted from now.</param>
    /// <exception cref="InvalidOperationException">The reissue is refused; the message says why.</exception>
    public static async Task<ReissuedAgentToken> ReissueAsync(
        AdminChange change, string tokenId, TimeSpan? lifetime = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(change);
        ArgumentNullException.ThrowIfNull(tokenId);
        var store = change.Identity;
        if (await store.LockTokenAsync(tokenId, ct) is not { Agent: { } agent } standing)
            throw new InvalidOperationException(NoSuchToken);
        var token = standing.Token;

        if (agent.Origin?.Kind == AgentOrigin.OAuthKind) throw new InvalidOperationException(GrantHeld);
        if (agent.Disabled) throw new InvalidOperationException(AgentDisabled(agent.Name));
        if (!standing.OwnerLive) throw new InvalidOperationException(OwnerDisabled(agent.Name));
        if (token.RevokedAt is not null) throw new InvalidOperationException(TokenRevoked(tokenId));
        if (store.Time.GetUtcNow() >= token.ExpiresAt) throw new InvalidOperationException(TokenExpired(tokenId));
        if (token.Superseded) throw new InvalidOperationException(TokenSuperseded(tokenId));

        var issued = await store.IssueTokenAsync(agent.Id, lifetime ?? token.ExpiresAt - token.CreatedAt, ct);
        // The lock makes this revoke find the token as it was judged. Should it
        // find it revoked all the same, the exception leaves the change
        // uncommitted, and the new token with it.
        if (!await store.RevokeTokenAsync(tokenId, ct)) throw new InvalidOperationException(TokenRevoked(tokenId));
        change.Record(ReissueKind, agent.Name,
            new { token_id = tokenId }, new { token_id = issued.Record.Id, expires_at = issued.Record.ExpiresAt });
        return new ReissuedAgentToken(issued, tokenId, agent.Name);
    }

    /// <summary>
    /// Removes an agent in any state: its tokens revoked, its grant and any
    /// stamped token ended by its credential generation, and it marked removed
    /// by the change's actor, gone from every list and from resolution. Nothing
    /// is deleted, so the change record, the audit and the usage still name it,
    /// and its name is free for a new agent.
    /// </summary>
    /// <exception cref="InvalidOperationException">There is no live agent with this id.</exception>
    public static async Task<AgentRemoval> RemoveAsync(AdminChange change, Guid agentId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(change);
        var removal = await change.Identity.RemoveAgentAsync(agentId, change.Actor.Describe(), ct)
            ?? throw new InvalidOperationException(NoSuchAgent);
        change.Record(RemoveKind, removal.Name,
            new { disabled = removal.WasDisabled }, new { removed = true, token_ids_revoked = removal.RevokedTokenIds });
        return removal;
    }
}

/// <summary>
/// A reissue's result: the new token, shown once, the id of the one it
/// replaced, and the agent's name. Printing it names the token without its
/// secret.
/// </summary>
public sealed record ReissuedAgentToken(IssuedAgentToken New, string ReplacedTokenId, string AgentName);

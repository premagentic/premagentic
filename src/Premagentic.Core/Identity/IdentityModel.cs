namespace Premagentic.Core.Identity;

/// <summary>
/// What a person may do in the administration portal. A role never widens what
/// search returns: an administrator reads only what the access rules give them,
/// like anyone else.
/// </summary>
public enum Role
{
    /// <summary>Manages users, groups, agents and rules.</summary>
    Administrator = 1,

    /// <summary>Sees the portal and the audit trail, and changes nothing.</summary>
    Auditor = 2,

    /// <summary>Searches, and nothing else.</summary>
    Member = 3,
}

/// <summary>
/// A local account. <see cref="Id"/> is immutable and never reused after a
/// delete; access rules name the id, and a reused id would inherit them.
/// </summary>
public sealed record User(Guid Id, string Name, Role Role, bool Disabled);

/// <summary>
/// A group. Access rules name <see cref="Id"/>, never <see cref="Name"/>: renaming
/// a group must not disarm a deny entry that names it, and a new group that
/// happens to take an old name must not inherit the old group's rules.
/// </summary>
/// <param name="SystemKey">
/// Set on a group the product maintains itself, such as
/// <see cref="SystemGroups.HostedModelAgents"/>. Null on a group an
/// administrator made. A system group cannot be renamed, deleted or added to by
/// hand; it is looked up by this key, since its id is per tenant.
/// </param>
public sealed record Group(Guid Id, string Name, string? SystemKey = null)
{
    /// <summary>True for a group the product maintains, which no command may change.</summary>
    public bool IsSystem => SystemKey is not null;
}

/// <summary>The groups the product maintains itself, by their stable keys.</summary>
public static class SystemGroups
{
    /// <summary>
    /// Every agent whose model runs outside the network, kept in step with
    /// <see cref="Agent.ModelLocation"/> by the agent commands. A folder rule
    /// that denies this group is the sentence "this folder never leaves the
    /// network"; see <see cref="Agent.ModelLocation"/> for why denying is all it
    /// can do.
    /// </summary>
    public const string HostedModelAgents = "hosted_model_agents";

    /// <summary>The name people see, fixed when the group is created.</summary>
    public const string HostedModelAgentsName = "hosted-model agents";
}

/// <summary>How a registered agent gets its permissions.</summary>
public enum AgentMode
{
    /// <summary>
    /// Acts for its owner: on every call it holds exactly the owner's principals
    /// as they are at that moment, and nothing else, so it can never reach more
    /// than the owner can.
    /// </summary>
    ActsForUser = 1,

    /// <summary>Holds only its own agent principal and the groups an administrator granted it.</summary>
    Service = 2,
}

/// <summary>Where the model an agent speaks for runs.</summary>
public enum ModelLocation
{
    /// <summary>
    /// Inside the company's network, on hardware it runs. Nothing served to this
    /// agent leaves.
    /// </summary>
    Local = 1,

    /// <summary>
    /// On someone else's machines. Everything served to this agent leaves the
    /// network, which is why every such agent is in
    /// <see cref="SystemGroups.HostedModelAgents"/> and why the location is
    /// written on every audit row.
    /// </summary>
    Hosted = 2,
}

/// <summary>
/// The one text form of a <see cref="ModelLocation"/>: what the database
/// stores, what a command accepts, and what an audit row carries.
/// </summary>
public static class ModelLocations
{
    public const string Local = "local";

    public const string Hosted = "hosted";

    public static string Text(ModelLocation location) => location switch
    {
        ModelLocation.Local => Local,
        ModelLocation.Hosted => Hosted,
        _ => throw new ArgumentOutOfRangeException(nameof(location), location, "Unknown model location."),
    };

    /// <summary>
    /// Parses one of the two words, exactly. Nothing is guessed at and nothing
    /// falls back to a default: a typed word that is not understood must be
    /// refused, not read as the safer of the two, because the person typing it
    /// meant something and would not be told otherwise.
    /// </summary>
    public static bool TryParse(string? text, out ModelLocation location)
    {
        switch (text)
        {
            case Local: location = ModelLocation.Local; return true;
            case Hosted: location = ModelLocation.Hosted; return true;
            default: location = default; return false;
        }
    }
}

/// <summary>
/// A registered agent. Agents read and never write; there is no scope to grant
/// beyond that. <see cref="MinimumTrustTier"/> is carried as an opaque value
/// until the trust gate defines its tiers; null follows the deployment's policy.
/// </summary>
/// <param name="ModelLocation">
/// Where the model this agent speaks for runs. A hosted agent is a member of
/// <see cref="SystemGroups.HostedModelAgents"/> for as long as it is marked
/// hosted, and that membership can only take access away from it: it is
/// weighed beside the agent's own principal, on the side of the decision that
/// narrows, never among the principals it holds. So a folder rule that denies
/// the group keeps a hosted agent out, and one that allows the group gives
/// nothing to anybody.
/// </param>
/// <param name="ModelVendor">Who runs a hosted model, for the record to name. Null for a local one.</param>
/// <param name="Origin">Who made the agent; null only on a value built by hand rather than read from the store.</param>
public sealed record Agent(
    Guid Id,
    string Name,
    Guid OwnerUserId,
    AgentMode Mode,
    bool Disabled,
    int RequestsPerMinute,
    string? MinimumTrustTier,
    ModelLocation ModelLocation,
    string? ModelVendor,
    AgentOrigin? Origin = null);

/// <summary>
/// What is stored about one agent token. Only the SHA-256 of the secret is
/// here; the plain token is never stored and cannot be recovered.
/// </summary>
/// <param name="Superseded">
/// True for a token of an agent its person made whose owner or agent was
/// disabled, or whose owner's password changed, after it was issued: it
/// reaches nothing, and a re-enable does not bring it back. Read from the
/// token's stamped generations against the current ones, by both the lookup
/// and the list, so every view can say why. Always false for a token of any
/// other agent. <see cref="IsUsableAt"/> is false for it.
/// </param>
public sealed record AgentTokenRecord(
    string Id,
    Guid AgentId,
    byte[] SecretHash,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    DateTimeOffset? RevokedAt,
    DateTimeOffset? LastUsedAt,
    bool Superseded = false)
{
    /// <summary>
    /// Usable only while not revoked, not superseded, and strictly before its
    /// expiry. At the exact instant of <see cref="ExpiresAt"/> it is already
    /// expired.
    /// </summary>
    public bool IsUsableAt(DateTimeOffset now) => RevokedAt is null && !Superseded && now < ExpiresAt;
}

/// <summary>
/// A token with its agent and its person, as a reissue judges them, read with
/// the token's and the agent's rows locked until the change ends.
/// </summary>
/// <param name="Agent">Null when the agent was removed.</param>
/// <param name="OwnerLive">False when the agent's person is disabled or gone.</param>
public sealed record TokenStanding(AgentTokenRecord Token, Agent? Agent, bool OwnerLive);

/// <summary>What one removal did: the agent's name, whether it was disabled, and the tokens it revoked.</summary>
public sealed record AgentRemoval(string Name, bool WasDisabled, IReadOnlyList<string> RevokedTokenIds);

/// <summary>
/// An agent that was removed: gone from every list and from resolution, its
/// row and history kept. <paramref name="RemovedBy"/> is the actor as the
/// change record describes it.
/// </summary>
public sealed record RemovedAgent(
    Guid Id, string Name, Guid OwnerUserId, string OwnerSignInName, DateTimeOffset RemovedAt, string RemovedBy);

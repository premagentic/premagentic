using Premagentic.Core.Identity;
using Premagentic.Core.Security.Acl;

namespace Premagentic.Core.Security;

/// <summary>
/// Who may retrieve a document, as a connector states it. At ingest every
/// value becomes one ordered access list, in one place,
/// <see cref="AccessDecision"/>, and the document stores that list's id.
/// <para>
/// There is no permissive default on purpose. <see cref="SourceDocument"/>
/// takes an access value positionally with no default, so a connector author
/// cannot forget to state one and silently publish a document to the whole
/// organization.
/// </para>
/// </summary>
public sealed record DocumentAccess
{
    private DocumentAccess(
        bool isPublic, IReadOnlyList<string> allowedPrincipals, AclSet? sourceAcl, bool usesFolderRules,
        IReadOnlyList<string>? externalPrincipals = null)
    {
        Public = isPublic;
        AllowedPrincipals = allowedPrincipals;
        SourceAcl = sourceAcl;
        UsesFolderRules = usesFolderRules;
        ExternalPrincipals = externalPrincipals ?? [];
    }

    /// <summary>True for <see cref="Everyone"/>.</summary>
    public bool Public { get; }

    /// <summary>The principals given to <see cref="For"/>, as given.</summary>
    public IReadOnlyList<string> AllowedPrincipals { get; }

    /// <summary>The source system's own ordered list, when the connector read one; see <see cref="FromSource"/>.</summary>
    public AclSet? SourceAcl { get; }

    /// <summary>True for <see cref="FolderRules"/>: the operator's folder rules decide.</summary>
    public bool UsesFolderRules { get; }

    /// <summary>
    /// The principals given to <see cref="External"/>, in the source system's
    /// own terms, before the principal mapper is asked. Empty for every other value.
    /// </summary>
    public IReadOnlyList<string> ExternalPrincipals { get; }

    /// <summary>Readable by anyone the deployment authenticates. A deliberate choice, not a fallback.</summary>
    public static DocumentAccess Everyone { get; } = new(true, [], null, false);

    /// <summary>
    /// Readable only by callers holding at least one of these principals, such
    /// as <c>group:&lt;id&gt;</c> or <c>user:&lt;id&gt;</c>. Blank entries are
    /// dropped. If any remaining entry is not a well-formed principal, the whole
    /// value reaches nobody: a guessed principal is one that matches the wrong
    /// caller.
    /// </summary>
    public static DocumentAccess For(params string[] principals) =>
        new(false, principals.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.Ordinal).ToArray(), null, false);

    /// <summary>
    /// Readable by nobody. What a connector returns when the source system has
    /// permissions it could not read. The document still indexes so the gap is
    /// visible and countable, but it can never reach an answer. Fail closed.
    /// </summary>
    public static DocumentAccess NoOne { get; } = new(false, [], null, false);

    /// <summary>
    /// The source system's own permission list for this document, reduced to the
    /// entries that cover reading and handed over in the order the source
    /// evaluates them. For a connector that reads real permissions.
    /// </summary>
    public static DocumentAccess FromSource(AclSet acl)
    {
        ArgumentNullException.ThrowIfNull(acl);
        return new(false, [], acl, false);
    }

    /// <summary>
    /// The connector read no permissions, and the operator's folder rules for its
    /// source decide: the rule with the longest prefix covering the path, or, when
    /// no rule covers it, nobody. A rule change moves these documents with it.
    /// </summary>
    public static DocumentAccess FolderRules { get; } = new(false, [], null, true);

    /// <summary>
    /// Readable by whoever holds the groups these outside principals mean here,
    /// as the deployment's principal mapper says. For a connector that reads a
    /// source system's own principals, such as a file share's security
    /// identifiers, and cannot know what they mean in this deployment.
    /// <para>
    /// The principals are carried as the source gave them and resolved at
    /// ingest, through the mapper the run was given. A principal it does not
    /// map contributes nothing, and with no mapper none is mapped, so a
    /// document whose principals are all unmapped reaches nobody. That is the
    /// fail-closed reading, and it is the only safe one: the alternative is
    /// serving a file to people the share never let near it, on the strength
    /// of a mapping nobody has made. Unmapped principals are counted on the
    /// run, so a missing mapping does not read as an empty folder.
    /// </para>
    /// </summary>
    public static DocumentAccess External(params string[] principals)
    {
        ArgumentNullException.ThrowIfNull(principals);
        return new(false, [], null, false,
            principals.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.Ordinal).ToArray());
    }

    /// <summary>
    /// True when this value denies every caller, which the ingest summary counts.
    /// A <see cref="FolderRules"/> value is decided by the rules instead, so it
    /// is never counted here.
    /// </summary>
    public bool DeniesEveryone => !UsesFolderRules && ConnectorAcl().DeniesEveryone;

    /// <summary>
    /// The same value with its outside principals read as the groups they mean
    /// here. Only the groups that were mapped are carried over, so a value whose
    /// principals all meant nothing becomes one that reaches nobody.
    /// </summary>
    internal DocumentAccess WithResolvedExternals(IEnumerable<Principal> groups)
    {
        ArgumentNullException.ThrowIfNull(groups);
        return new(false, groups.Select(g => g.ToString()).Distinct(StringComparer.Ordinal).ToArray(), null, false);
    }

    /// <summary>The ordered list a connector's own value stands for. Not meaningful for <see cref="FolderRules"/>.</summary>
    internal AclSet ConnectorAcl()
    {
        if (SourceAcl is not null) return SourceAcl;
        if (Public) return AclSet.Of(AclEntry.Allow(Principal.Everyone));
        return AclSet.TryParse(AllowedPrincipals.Select(p => "allow " + p), out var set) ? set : AclSet.Empty;
    }
}

/// <summary>
/// The caller's authorization on one retrieval call.
/// <para>
/// A host either resolves callers itself and hands over their principals
/// (<see cref="ForPrincipals"/>), or lets Premagentic resolve its own users and
/// agents (<see cref="ForCaller"/>). Either way the principals are parsed
/// strictly: if one is malformed the caller holds nothing at all, because under
/// ordered evaluation dropping a principal can widen what a caller reaches.
/// </para>
/// <para>
/// Every scope that holds anything also holds <c>everyone</c>, the principal a
/// connector's <see cref="DocumentAccess.Everyone"/> value allows.
/// </para>
/// </summary>
public sealed record AccessScope
{
    private AccessScope(
        IReadOnlyList<string> principals, PrincipalSet holds, bool unrestricted, string auditLabel,
        Principal? narrowedBy = null, Guid? userId = null, Guid? agentId = null,
        IReadOnlyList<Principal>? systemGroups = null, ModelLocation? modelLocation = null)
    {
        ModelLocation = modelLocation;
        Principals = principals;
        Holds = holds;
        Unrestricted = unrestricted;
        AuditLabel = auditLabel;
        NarrowedBy = narrowedBy;
        UserId = userId;
        AgentId = agentId;
        SystemGroups = systemGroups ?? [];
    }

    /// <summary>The principals as the host supplied them, blanks and duplicates removed.</summary>
    public IReadOnlyList<string> Principals { get; }

    /// <summary>What the evaluator checks: the parsed principals and <c>everyone</c>, or nothing if any failed to parse.</summary>
    public PrincipalSet Holds { get; }

    /// <summary>
    /// For an agent acting for a user: the agent's own principal. A list must
    /// permit the user, and permit the user plus this principal, so an entry
    /// naming the agent can only take access away.
    /// </summary>
    public Principal? NarrowedBy { get; }

    /// <summary>
    /// The groups Premagentic maintains for an agent, such as the group of
    /// agents whose model runs outside the network. They are weighed with
    /// <see cref="NarrowedBy"/> and never among <see cref="Holds"/>, so a rule
    /// that denies one holds the agent back and a rule that allows one gives
    /// nothing to anybody. Empty for a person.
    /// </summary>
    public IReadOnlyList<Principal> SystemGroups { get; }

    /// <summary>
    /// Everything weighed on the narrowing side: the agent's own principal and
    /// its system groups. Empty means one evaluation decides.
    /// </summary>
    public IReadOnlyList<Principal> Narrowing =>
        NarrowedBy is null ? SystemGroups : [NarrowedBy, .. SystemGroups];

    /// <summary>Bypasses the document gate entirely. Always recorded on the retrieval event.</summary>
    public bool Unrestricted { get; }

    /// <summary>Written to <c>retrieval_event.access_label</c> so an audit can tell how an answer was reached.</summary>
    public string AuditLabel { get; }

    /// <summary>The Premagentic user this call reads as, when Premagentic resolved it. Written to the audit trail.</summary>
    public Guid? UserId { get; }

    /// <summary>The agent making this call, when Premagentic resolved it. Written to the audit trail.</summary>
    public Guid? AgentId { get; }

    /// <summary>
    /// Where the model that receives this answer runs, written on the audit row
    /// so the trail says what went to hosted-model agents. The agent's own location when
    /// Premagentic resolved the caller, <see cref="Identity.ModelLocation.Local"/>
    /// for a person reading in the portal, since nothing leaves then, and null
    /// when a host resolved the caller itself and Premagentic cannot know.
    /// </summary>
    public ModelLocation? ModelLocation { get; }

    /// <summary>
    /// The access-list ids this scope may read, computed for one read from the
    /// tenant's current lists by <see cref="PermittedSetReader"/>. Null until
    /// then, and the gate refuses to run on a scope nobody resolved.
    /// </summary>
    internal IReadOnlyList<long>? PermittedSetIds { get; init; }

    /// <summary>Whether this scope may read under <paramref name="set"/>.</summary>
    public bool CanRead(AclSet set)
    {
        ArgumentNullException.ThrowIfNull(set);
        if (Unrestricted) return true;
        if (!AclEvaluator.CanRead(set, Holds)) return false;
        var narrowing = Narrowing;
        return narrowing.Count == 0 || AclEvaluator.CanRead(set, PrincipalSet.From(Holds.Concat(narrowing)));
    }

    /// <summary>
    /// The normal case for a host that resolves callers itself: the principals it
    /// resolved for this caller. <paramref name="auditLabel"/> should identify the
    /// caller without being sensitive (a user id or an opaque session id, never a
    /// name or an email).
    /// </summary>
    public static AccessScope ForPrincipals(string auditLabel, params string[] principals)
    {
        var given = principals
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var holds = PrincipalSet.TryParse(given, out var parsed)
            ? PrincipalSet.From(parsed.Append(Principal.Everyone))
            : PrincipalSet.Empty;
        return new AccessScope(given, holds, unrestricted: false, auditLabel);
    }

    /// <summary>Only documents readable by <c>everyone</c>, such as a connector's <see cref="DocumentAccess.Everyone"/>.</summary>
    public static AccessScope PublicOnly { get; } =
        new([], PrincipalSet.Of(Principal.Everyone), unrestricted: false, "public-only");

    /// <summary>
    /// Sees every indexed document regardless of its access value. For the
    /// evaluation harness, the drift report and operator tooling, never for a
    /// user-facing query path. The name is deliberately hard to reach for and
    /// the reason is stored on every event it produces.
    /// </summary>
    public static AccessScope UnrestrictedAudited(string reason) =>
        new([], PrincipalSet.Empty, unrestricted: true, "unrestricted:" + reason);

    /// <summary>
    /// The same scope, read on behalf of an administrator looking at what it
    /// can see: it holds exactly what it held, and its audit label says who
    /// looked, so the one event row records both.
    /// </summary>
    public AccessScope ViewedAsBy(Guid administratorUserId) =>
        new(Principals, Holds, Unrestricted, $"view-as:user:{CallerResolver.IdText(administratorUserId)} {AuditLabel}",
            NarrowedBy, UserId, AgentId, SystemGroups, ModelLocation)
        {
            PermittedSetIds = PermittedSetIds,
        };

    /// <summary>
    /// A caller Premagentic resolved from its own users, agents and tokens. A
    /// caller that did not resolve holds nothing, and its label says why.
    /// </summary>
    public static AccessScope ForCaller(ResolvedCaller caller)
    {
        ArgumentNullException.ThrowIfNull(caller);
        if (!caller.IsResolved)
            return new AccessScope([], PrincipalSet.Empty, unrestricted: false,
                "refused:" + caller.Status, userId: caller.UserId, agentId: caller.AgentId,
                modelLocation: Identity.ModelLocation.Local);

        var label = caller.AgentId is { } agent
            ? $"agent:{CallerResolver.IdText(agent)}" + (caller.TokenId is { } token ? $" token:{token}" : "")
            : $"user:{CallerResolver.IdText(caller.UserId!.Value)}";
        return new AccessScope(
            caller.Principals.Select(p => p.ToString()).Order(StringComparer.Ordinal).ToArray(),
            caller.Principals, unrestricted: false, label,
            caller.NarrowedBy, caller.UserId, caller.AgentId, caller.SystemGroups,
            caller.Agent?.ModelLocation ?? Identity.ModelLocation.Local);
    }
}

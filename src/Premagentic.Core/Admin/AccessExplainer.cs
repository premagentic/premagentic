using Premagentic.Core.Okf;
using Premagentic.Core.Security;
using Premagentic.Core.Security.Acl;

namespace Premagentic.Core.Admin;

/// <summary>One gate's outcome for one document and one caller, with the reason in words.</summary>
public sealed record GateOutcome(bool Passes, string Reason);

/// <summary>
/// Why one caller can or cannot read one document: the rule that set its
/// access list, the entry that decided, and the trust, freshness and lifecycle
/// outcomes, each as the gates judge it.
/// </summary>
/// <param name="Rule">The folder rule that covers the document's path now, or null when none does.</param>
/// <param name="DecidingIndex">The position (from 1) of the entry that decided, or null when none named the caller.</param>
/// <param name="NarrowingIndex">
/// For an agent that acts for a user, the entry that decided the second check,
/// with the agent's own principal added, or null when none did or there is no
/// second check.
/// </param>
public sealed record AccessExplanation(
    FolderRule? Rule,
    AclSet? Acl,
    int? DecidingIndex,
    int? NarrowingIndex,
    GateOutcome Access,
    GateOutcome Trust,
    GateOutcome Freshness,
    GateOutcome Lifecycle)
{
    /// <summary>Whether a search by this caller can return the document: every gate passes.</summary>
    public bool Readable => Access.Passes && Trust.Passes && Freshness.Passes && Lifecycle.Passes;
}

/// <summary>
/// Explains the four gates for one document and one caller. It evaluates the
/// same rules the gates apply in SQL: first match over the ordered list with a
/// default deny, the agent narrowing, machine-written content below the
/// policy's tier, content past its stale date, and non-current lifecycle. A test
/// holds it to the gates' own answers.
/// </summary>
public static class AccessExplainer
{
    public static AccessExplanation Explain(
        CatalogDocument document, FolderRule? rule, AccessScope scope, TrustPolicy policy, DateTimeOffset now, bool includeHistorical)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(policy);

        var acl = document.AclText is { } text && AclSet.TryParseCanonical(text, out var parsed) ? parsed : null;
        var (access, deciding, narrowing) = Access(acl, scope);

        return new AccessExplanation(
            rule, acl, deciding, narrowing, access,
            Trust(document, policy),
            Freshness(document, policy, now),
            Lifecycle(document, includeHistorical));
    }

    private static (GateOutcome Outcome, int? Deciding, int? Narrowing) Access(AclSet? acl, AccessScope scope)
    {
        if (scope.Unrestricted)
            return (new GateOutcome(true, "The caller reads past the access gate, on a recorded reason."), null, null);
        if (acl is null)
            return (new GateOutcome(false, "The document has no readable access list, so nobody may read it."), null, null);

        var deciding = FirstMatch(acl, scope.Holds);
        if (deciding is null)
            return (new GateOutcome(false, "No entry names this caller, and no match is a deny."), null, null);

        var entry = acl.Entries[deciding.Value - 1];
        if (entry.Effect == AclEffect.Deny)
            return (new GateOutcome(false, $"Entry {deciding} of {acl.Entries.Count}, a deny, is the first that names this caller."), deciding, null);

        var narrowing = scope.Narrowing;
        if (narrowing.Count == 0)
            return (new GateOutcome(true, $"Entry {deciding} of {acl.Entries.Count}, an allow, is the first that names this caller."), deciding, null);

        // An agent reads only what the entry above allows AND what the list
        // allows once the agent's own principals are added: its own principal,
        // and any group Premagentic maintains for it, such as the group of
        // agents whose model runs outside the network. Adding them can only
        // take access away.
        var narrowed = FirstMatch(acl, PrincipalSet.From(scope.Holds.Concat(narrowing)));
        var narrowedEntry = narrowed is { } n ? acl.Entries[n - 1] : null;
        if (narrowedEntry is { Effect: AclEffect.Allow })
            return (new GateOutcome(true, $"Entry {deciding} allows the caller it reads as, and with the agent's own principals added, entry {narrowed} allows too."), deciding, narrowed);
        if (narrowedEntry is null)
            return (new GateOutcome(false, $"Entry {deciding} allows the caller it reads as, but with the agent's own principals added no entry allows, and no match is a deny."), deciding, null);

        // Name the reason when it is one of ours, so an administrator reading
        // this is not left hunting for a group nobody put the agent in by hand.
        var reason = scope.SystemGroups.Contains(narrowedEntry.Principal)
            ? $"Entry {deciding} allows the caller it reads as, but entry {narrowed} denies {narrowedEntry.Principal}, a group Premagentic keeps this agent in for as long as its model runs outside the network."
            : $"Entry {deciding} allows the caller it reads as, but with the agent's own principals added, entry {narrowed} denies it.";
        return (new GateOutcome(false, reason), deciding, narrowed);
    }

    private static int? FirstMatch(AclSet acl, PrincipalSet holds)
    {
        for (var i = 0; i < acl.Entries.Count; i++)
            if (holds.Contains(acl.Entries[i].Principal)) return i + 1;
        return null;
    }

    private static GateOutcome Trust(CatalogDocument document, TrustPolicy policy)
    {
        if (document.Authorship != OkfAuthorship.Machine)
            return new GateOutcome(true, $"Authorship is {Name(document.Authorship)}, and only machine-written content is held to a trust tier.");
        return document.TrustTier >= policy.MinimumMachineTier
            ? new GateOutcome(true, $"Machine-written at {TrustPolicy.TierKey(document.TrustTier)}, which meets this caller's minimum, {TierName(policy.MinimumMachineTier)}.")
            : new GateOutcome(false, $"Machine-written at {TrustPolicy.TierKey(document.TrustTier)}, below this caller's minimum, {TierName(policy.MinimumMachineTier)}.");
    }

    private static GateOutcome Freshness(CatalogDocument document, TrustPolicy policy, DateTimeOffset now)
    {
        if (document.StaleAfter is not { } staleAfter || staleAfter > now)
            return new GateOutcome(true, document.StaleAfter is null ? "It has no stale date." : "It is not yet past its stale date.");
        return policy.IncludeStale
            ? new GateOutcome(true, "It is past its stale date, and this caller is shown stale content, flagged.")
            : new GateOutcome(false, "It is past its stale date, and this caller is not shown stale content.");
    }

    private static GateOutcome Lifecycle(CatalogDocument document, bool includeHistorical)
    {
        if (document.LifecycleStatus == DocumentLifecycle.Active)
            return new GateOutcome(true, "It is current.");
        return includeHistorical
            ? new GateOutcome(true, $"It is {document.LifecycleStatus}, and historical access was asked for.")
            : new GateOutcome(false, $"It is {document.LifecycleStatus}, and a search leaves that out unless historical access is asked for.");
    }

    private static string Name(OkfAuthorship authorship) => authorship.ToString().ToLowerInvariant();

    private static string TierName(OkfTrustTier tier) => Enum.IsDefined(tier) ? TrustPolicy.TierKey(tier) : "human-reviewed";
}

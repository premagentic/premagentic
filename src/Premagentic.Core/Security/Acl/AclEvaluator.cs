namespace Premagentic.Core.Security.Acl;

/// <summary>
/// Decides whether a caller may read under one <see cref="AclSet"/>: the first
/// entry whose principal the caller holds decides, and no match is a deny.
/// <para>
/// This is first match, not "deny wins", and the evaluator never reorders the
/// entries. The source system already put them in the order it evaluates them,
/// and that order carries meaning. On Windows, every explicit entry on an
/// object comes before every inherited one, and deny comes before allow only
/// within each of those two groups, so an explicit allow on a file beats a deny
/// inherited from its folder. POSIX access lists are ordered too: the first
/// matching class decides, so the owner entry binds the owner even when a group
/// entry would grant more. A connector hands entries over in the source's own
/// evaluation order, reduced to the ones that cover reading; this type honors
/// that order exactly. Sorting denies to the front would hide files the source
/// system itself lets people read.
/// </para>
/// <para>
/// Consequences, each pinned by the conformance cases: a caller in both an
/// allowed and a denied group gets whichever entry comes first; the empty set
/// denies everyone; a caller holding no principals is denied by every set,
/// including one that allows <c>everyone</c>.
/// </para>
/// </summary>
public static class AclEvaluator
{
    public static bool CanRead(AclSet set, PrincipalSet caller)
    {
        ArgumentNullException.ThrowIfNull(set);
        ArgumentNullException.ThrowIfNull(caller);

        foreach (var entry in set.Entries)
        {
            if (caller.Contains(entry.Principal))
                return entry.Effect == AclEffect.Allow;
        }

        // No entry names this caller. Default deny.
        return false;
    }
}

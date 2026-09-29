namespace Premagentic.Core.Security.Acl;

/// <summary>An access list as stored, with the integer id documents refer to it by.</summary>
public sealed record StoredAclSet
{
    public StoredAclSet(long id, AclSet set)
    {
        ArgumentNullException.ThrowIfNull(set);
        Id = id;
        Set = set;
    }

    public long Id { get; }

    public AclSet Set { get; }
}

/// <summary>
/// Turns the stored access lists and one caller into the list of set ids that
/// caller may read. The search filter consumes it as
/// <c>acl_set_id = ANY(@permitted)</c>, so the allow and deny logic lives here,
/// in one engine-neutral place, and the database only compares integers.
/// </summary>
public static class PermittedSets
{
    /// <summary>
    /// The ids of the sets <paramref name="caller"/> may read, ascending, each
    /// once. Each distinct set is evaluated once per call, however many documents
    /// share it.
    /// <para>
    /// If one id arrives more than once, it is permitted only when every set
    /// given under it permits. Conflicting input never widens access.
    /// </para>
    /// </summary>
    public static long[] Resolve(IEnumerable<StoredAclSet> sets, PrincipalSet caller)
    {
        ArgumentNullException.ThrowIfNull(caller);
        return ResolveWith(sets, set => AclEvaluator.CanRead(set, caller));
    }

    /// <summary>
    /// The same, with the decision made by <paramref name="canRead"/>: for a
    /// caller whose rule is more than one evaluation, such as an agent acting
    /// for a user, which must be permitted both as the user and as the user
    /// plus itself.
    /// </summary>
    public static long[] ResolveWith(IEnumerable<StoredAclSet> sets, Func<AclSet, bool> canRead)
    {
        ArgumentNullException.ThrowIfNull(sets);
        ArgumentNullException.ThrowIfNull(canRead);

        var decisions = new Dictionary<long, bool>();
        foreach (var stored in sets)
        {
            ArgumentNullException.ThrowIfNull(stored, nameof(sets));
            var allowed = canRead(stored.Set);
            decisions[stored.Id] = decisions.TryGetValue(stored.Id, out var earlier) ? earlier && allowed : allowed;
        }

        return decisions.Where(d => d.Value).Select(d => d.Key).Order().ToArray();
    }
}

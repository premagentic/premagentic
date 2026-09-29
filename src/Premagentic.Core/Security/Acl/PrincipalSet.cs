using System.Collections;
using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;

namespace Premagentic.Core.Security.Acl;

/// <summary>
/// Every principal one caller holds on one call. Immutable, and compared only by
/// <see cref="Principal"/>'s own ordinal equality, so no caller can hand the
/// evaluator a looser comparer.
/// <para>
/// An empty set matches nothing, not even an entry for <c>everyone</c>. It is
/// what a disabled, expired or unknown caller resolves to.
/// </para>
/// </summary>
public sealed class PrincipalSet : IReadOnlyCollection<Principal>
{
    private readonly FrozenSet<Principal> _principals;

    private PrincipalSet(FrozenSet<Principal> principals) => _principals = principals;

    /// <summary>Holds nothing, so every access list denies it.</summary>
    public static PrincipalSet Empty { get; } = new(FrozenSet<Principal>.Empty);

    public static PrincipalSet Of(params Principal[] principals) => From(principals);

    public static PrincipalSet From(IEnumerable<Principal> principals)
    {
        ArgumentNullException.ThrowIfNull(principals);
        var list = principals.ToList();
        if (list.Any(p => p is null))
            throw new ArgumentException("A principal set cannot hold a null principal.", nameof(principals));
        return list.Count == 0 ? Empty : new PrincipalSet(list.ToFrozenSet());
    }

    /// <summary>
    /// Parses every text as a principal, all or nothing. Never throws.
    /// <para>
    /// There is no partial result on purpose. Under ordered evaluation, dropping
    /// one principal a caller holds can WIDEN what they reach: a deny entry that
    /// would have matched it no longer does, and a later allow decides instead.
    /// A caller whose principals do not all parse must be treated as holding none.
    /// </para>
    /// </summary>
    public static bool TryParse(IEnumerable<string?>? texts, [NotNullWhen(true)] out PrincipalSet? set)
    {
        set = null;
        if (texts is null) return false;

        var parsed = new List<Principal>();
        foreach (var text in texts)
        {
            if (!Principal.TryParse(text, out var principal)) return false;
            parsed.Add(principal);
        }

        set = From(parsed);
        return true;
    }

    public int Count => _principals.Count;

    public bool Contains(Principal principal) => principal is not null && _principals.Contains(principal);

    public IEnumerator<Principal> GetEnumerator() => ((IEnumerable<Principal>)_principals).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

using System.Diagnostics.CodeAnalysis;

namespace Premagentic.Core.Security.Acl;

/// <summary>Whether an entry grants or refuses reading. There is no zero value, so an unset effect is never mistaken for either.</summary>
public enum AclEffect
{
    Allow = 1,
    Deny = 2,
}

/// <summary>
/// One read-relevant access entry: an effect and the principal it applies to.
/// <para>
/// Only entries whose rights cover reading ever reach this type. A connector
/// leaves out entries about writing or deleting, because a deny on write must
/// never become a deny on read, and folds in anything that caps an entry before
/// handing it over.
/// </para>
/// </summary>
public sealed record AclEntry
{
    public AclEntry(AclEffect effect, Principal principal)
    {
        if (effect is not (AclEffect.Allow or AclEffect.Deny))
            throw new ArgumentOutOfRangeException(nameof(effect), effect, "An entry is either allow or deny.");
        ArgumentNullException.ThrowIfNull(principal);
        Effect = effect;
        Principal = principal;
    }

    public AclEffect Effect { get; }

    public Principal Principal { get; }

    public static AclEntry Allow(Principal principal) => new(AclEffect.Allow, principal);

    public static AclEntry Deny(Principal principal) => new(AclEffect.Deny, principal);

    /// <summary>
    /// Parses the canonical line form: <c>allow</c> or <c>deny</c> in lower case,
    /// one space, one principal. Never throws.
    /// </summary>
    public static bool TryParse(string? text, [NotNullWhen(true)] out AclEntry? entry)
    {
        entry = null;
        if (text is null) return false;

        var space = text.IndexOf(' ');
        if (space <= 0) return false;

        AclEffect? effect = text.AsSpan(0, space) switch
        {
            "allow" => AclEffect.Allow,
            "deny" => AclEffect.Deny,
            _ => null,
        };
        if (effect is null) return false;
        if (!Principal.TryParse(text[(space + 1)..], out var principal)) return false;

        entry = new AclEntry(effect.Value, principal);
        return true;
    }

    /// <summary>The canonical line form, without a line ending.</summary>
    public override string ToString() => (Effect == AclEffect.Allow ? "allow " : "deny ") + Principal;
}

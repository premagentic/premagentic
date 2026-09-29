using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace Premagentic.Core.Security.Acl;

/// <summary>What a <see cref="Principal"/> names. Its text prefix is the lower-case name.</summary>
public enum PrincipalKind
{
    /// <summary>A user account, by id.</summary>
    User = 1,

    /// <summary>A group, by id.</summary>
    Group,

    /// <summary>A registered agent, by id.</summary>
    Agent,

    /// <summary>A Windows security identifier. Reserved for file-share connectors; nothing resolves to it yet.</summary>
    Sid,

    /// <summary>A POSIX user id. Reserved for file-share connectors; nothing resolves to it yet.</summary>
    Uid,

    /// <summary>A POSIX group id. Reserved for file-share connectors; nothing resolves to it yet.</summary>
    Gid,

    /// <summary>The one well-known principal: any signed-in caller. Written as the bare word <c>everyone</c>.</summary>
    Everyone,
}

/// <summary>
/// One party an access entry can name: a namespaced string such as
/// <c>user:&lt;id&gt;</c> or <c>group:&lt;id&gt;</c>, or the well-known
/// <c>everyone</c>.
/// <para>
/// Parsing is strict because a principal that is guessed at is a principal that
/// matches the wrong caller. The kind must be one of the known lower-case names,
/// then a colon, then a non-empty value with no control characters, no unpaired
/// surrogates and no leading or trailing white space. Anything else is rejected,
/// never repaired.
/// </para>
/// <para>
/// Comparison is ordinal over kind and value, so <c>group:HR</c> and
/// <c>group:hr</c> are different principals, and so are <c>user:1000</c> and
/// <c>uid:1000</c>. Values should be stable ids, never display names: a renamed
/// group named in a deny entry would silently stop being denied.
/// </para>
/// <para>
/// This is a class rather than a struct so that no default instance can exist.
/// A zeroed principal would compare equal to every other zeroed principal.
/// </para>
/// </summary>
public sealed class Principal : IEquatable<Principal>
{
    private const string EveryoneText = "everyone";

    private Principal(PrincipalKind kind, string value)
    {
        Kind = kind;
        Value = value;
    }

    public PrincipalKind Kind { get; }

    /// <summary>The part after the colon. Empty only for <see cref="Everyone"/>.</summary>
    public string Value { get; }

    /// <summary>Any signed-in caller. A caller that resolved to no principals does not hold it.</summary>
    public static Principal Everyone { get; } = new(PrincipalKind.Everyone, "");

    public static Principal User(string id) => Of(PrincipalKind.User, id);

    public static Principal Group(string id) => Of(PrincipalKind.Group, id);

    public static Principal Agent(string id) => Of(PrincipalKind.Agent, id);

    /// <summary>Builds a namespaced principal. Throws on an invalid value instead of guessing at it.</summary>
    public static Principal Of(PrincipalKind kind, string value)
    {
        if (kind == PrincipalKind.Everyone)
            throw new ArgumentException("The well-known principal carries no value; use Principal.Everyone.", nameof(kind));
        if (PrefixOf(kind) is null)
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown principal kind.");
        if (!IsValidValue(value))
            throw new ArgumentException(
                "A principal value must be non-empty, with no control characters, no unpaired surrogates and no leading or trailing white space.",
                nameof(value));
        return new Principal(kind, value);
    }

    /// <summary>Parses <c>kind:value</c> or <c>everyone</c>. Never throws; anything malformed returns false.</summary>
    public static bool TryParse(string? text, [NotNullWhen(true)] out Principal? principal)
    {
        principal = null;
        if (text is null) return false;

        if (text == EveryoneText)
        {
            principal = Everyone;
            return true;
        }

        var colon = text.IndexOf(':');
        if (colon <= 0) return false;

        var kind = KindOf(text.AsSpan(0, colon));
        if (kind is null) return false;

        var value = text[(colon + 1)..];
        if (!IsValidValue(value)) return false;

        principal = new Principal(kind.Value, value);
        return true;
    }

    public static Principal Parse(string text) =>
        TryParse(text, out var principal) ? principal : throw new FormatException("Not a valid principal.");

    public bool Equals(Principal? other) =>
        other is not null && Kind == other.Kind && string.Equals(Value, other.Value, StringComparison.Ordinal);

    public override bool Equals(object? obj) => Equals(obj as Principal);

    public override int GetHashCode() => HashCode.Combine(Kind, StringComparer.Ordinal.GetHashCode(Value));

    public static bool operator ==(Principal? left, Principal? right) => left is null ? right is null : left.Equals(right);

    public static bool operator !=(Principal? left, Principal? right) => !(left == right);

    /// <summary>The canonical text form, the same text <see cref="TryParse"/> accepts.</summary>
    public override string ToString() => Kind == PrincipalKind.Everyone ? EveryoneText : PrefixOf(Kind) + ":" + Value;

    private static string? PrefixOf(PrincipalKind kind) => kind switch
    {
        PrincipalKind.User => "user",
        PrincipalKind.Group => "group",
        PrincipalKind.Agent => "agent",
        PrincipalKind.Sid => "sid",
        PrincipalKind.Uid => "uid",
        PrincipalKind.Gid => "gid",
        _ => null,
    };

    private static PrincipalKind? KindOf(ReadOnlySpan<char> prefix) => prefix switch
    {
        "user" => PrincipalKind.User,
        "group" => PrincipalKind.Group,
        "agent" => PrincipalKind.Agent,
        "sid" => PrincipalKind.Sid,
        "uid" => PrincipalKind.Uid,
        "gid" => PrincipalKind.Gid,
        _ => null,
    };

    private static bool IsValidValue(string? value)
    {
        if (string.IsNullOrEmpty(value)) return false;
        if (char.IsWhiteSpace(value[0]) || char.IsWhiteSpace(value[^1])) return false;

        // Control characters are refused so a line break can never be forged
        // inside the canonical text of an access list. Unpaired surrogates are
        // refused because they all encode to the same replacement character,
        // which would give two different lists one hash.
        var rest = value.AsSpan();
        while (!rest.IsEmpty)
        {
            if (Rune.DecodeFromUtf16(rest, out var rune, out var used) != OperationStatus.Done) return false;
            if (Rune.IsControl(rune)) return false;
            rest = rest[used..];
        }
        return true;
    }
}

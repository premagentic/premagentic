using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;

namespace Premagentic.Core.Security.Acl;

/// <summary>
/// An ordered access list, in the order the source system evaluates it, with a
/// canonical text form and a stable hash of that text.
/// <para>
/// Identical lists have identical text and so one hash, which is how many
/// documents come to share one stored set: documents under one folder usually
/// carry the same list. Order is part of the identity. The same entries in a
/// different order are a different set, because they can decide differently.
/// Entries are kept exactly as given, duplicates included; nothing is sorted,
/// merged or dropped.
/// </para>
/// <para>
/// The canonical text is one line per entry, each ended by a line feed: the
/// effect in lower case, one space, the principal. The empty set's text is the
/// empty string, and the empty set denies everyone.
/// </para>
/// </summary>
public sealed class AclSet : IEquatable<AclSet>
{
    private AclSet(AclEntry[] entries)
    {
        Entries = new ReadOnlyCollection<AclEntry>(entries);

        var text = new StringBuilder();
        foreach (var entry in entries) text.Append(entry).Append('\n');
        CanonicalText = text.ToString();
        Hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(CanonicalText)));

        // Walks to the first entry that can decide for every caller: an allow
        // means someone may read; a deny for everyone means nobody after it can.
        DeniesEveryone = true;
        foreach (var entry in entries)
        {
            if (entry.Effect == AclEffect.Allow)
            {
                DeniesEveryone = false;
                break;
            }
            if (entry.Principal == Principal.Everyone) break;
        }
    }

    /// <summary>No entries, so no caller matches and every caller is denied.</summary>
    public static AclSet Empty { get; } = new([]);

    public static AclSet Of(params AclEntry[] entries) => Create(entries);

    public static AclSet Create(IEnumerable<AclEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var copy = entries.ToArray();
        if (copy.Any(e => e is null))
            throw new ArgumentException("An access list cannot hold a null entry.", nameof(entries));
        return copy.Length == 0 ? Empty : new AclSet(copy);
    }

    /// <summary>
    /// Parses one entry per line, all or nothing. Never throws.
    /// <para>
    /// A list with one bad line is rejected whole, never parsed around. The bad
    /// line might have been a deny, and skipping a deny widens access. A caller
    /// that gets false here must treat the list as <see cref="Empty"/>.
    /// </para>
    /// </summary>
    public static bool TryParse(IEnumerable<string?>? lines, [NotNullWhen(true)] out AclSet? set)
    {
        set = null;
        if (lines is null) return false;

        var entries = new List<AclEntry>();
        foreach (var line in lines)
        {
            if (!AclEntry.TryParse(line, out var entry)) return false;
            entries.Add(entry);
        }

        set = Create(entries);
        return true;
    }

    /// <summary>
    /// Parses text exactly as <see cref="CanonicalText"/> writes it, and nothing
    /// looser: no carriage returns, no blank lines, no missing final line feed.
    /// Never throws.
    /// </summary>
    public static bool TryParseCanonical(string? text, [NotNullWhen(true)] out AclSet? set)
    {
        set = null;
        if (text is null) return false;
        if (text.Length == 0)
        {
            set = Empty;
            return true;
        }
        if (text[^1] != '\n') return false;

        if (!TryParse(text[..^1].Split('\n'), out var parsed)) return false;
        if (!string.Equals(parsed.CanonicalText, text, StringComparison.Ordinal)) return false;

        set = parsed;
        return true;
    }

    /// <summary>The entries in the source's own evaluation order.</summary>
    public IReadOnlyList<AclEntry> Entries { get; }

    public bool IsEmpty => Entries.Count == 0;

    /// <summary>
    /// True when no caller can be allowed: no allow entry comes before the first
    /// deny for <c>everyone</c>. Every caller that holds anything holds
    /// <c>everyone</c>, so such a list reaches nobody. The empty set is one.
    /// </summary>
    public bool DeniesEveryone { get; }

    public string CanonicalText { get; }

    /// <summary>Lower-case hex SHA-256 of the UTF-8 canonical text. Equal lists, equal hash.</summary>
    public string Hash { get; }

    public bool Equals(AclSet? other) =>
        other is not null && string.Equals(CanonicalText, other.CanonicalText, StringComparison.Ordinal);

    public override bool Equals(object? obj) => Equals(obj as AclSet);

    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(CanonicalText);

    public override string ToString() => $"AclSet({Entries.Count} entries, {Hash[..12]})";
}

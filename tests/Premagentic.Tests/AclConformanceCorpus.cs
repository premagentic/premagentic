using Premagentic.Core.Security.Acl;

namespace Premagentic.Tests;

/// <summary>
/// One case of the access conformance corpus: an access list in the source's
/// own evaluation order, the principals a caller holds, and whether that caller
/// may read. Written as text on purpose, so the corpus reads the same in any
/// language and the parsing is exercised along with the evaluation. Principal
/// values here are readable placeholders; in a deployment they are ids.
/// </summary>
public sealed record AclConformanceCase(string Name, string[] Entries, string[] Caller, bool MayRead);

/// <summary>
/// The conformance corpus, runnable against any evaluator function. The real
/// evaluator must get every case right, and every hand-written mutant of it
/// must get at least one wrong; see <see cref="AclConformanceTests"/>.
/// </summary>
public static class AclConformanceCorpus
{
    private static readonly string[] Alice = ["user:alice", "group:contractors", "everyone"];
    private static readonly string[] InBoth = ["user:bob", "group:staff", "group:contractors", "everyone"];

    // A caller in 5,000 groups, g0000 to g4999.
    private static readonly string[] ManyGroups =
        ["user:frank", "everyone", .. Enumerable.Range(0, 5000).Select(i => $"group:g{i:D4}")];

    // A thousand entries that name nobody the caller holds.
    private static readonly string[] LongDenyPrefix =
        Enumerable.Range(0, 1000).Select(i => $"deny group:x{i:D4}").ToArray();

    public static IReadOnlyList<AclConformanceCase> Cases { get; } =
    [
        // Order decides, not the effect.
        new("explicit allow ahead of inherited deny",
            ["allow user:alice", "deny group:contractors"], Alice, true),
        new("inherited deny ahead of allow, the same two entries reversed",
            ["deny group:contractors", "allow user:alice"], Alice, false),
        new("caller in an allowed and a denied group, deny first",
            ["deny group:contractors", "allow group:staff"], InBoth, false),
        new("caller in an allowed and a denied group, allow first",
            ["allow group:staff", "deny group:contractors"], InBoth, true),
        new("a deny for a group the caller is not in is passed over",
            ["deny group:contractors", "allow group:staff"], ["user:carol", "group:staff", "everyone"], true),

        // Default deny.
        new("empty set denies",
            [], ["user:alice", "group:staff", "everyone"], false),
        new("no entry names the caller",
            ["allow group:hr"], ["user:erin", "group:staff", "everyone"], false),

        // The well-known principal is just a principal.
        new("everyone allow admits a signed-in caller",
            ["allow everyone"], ["user:dave", "everyone"], true),
        new("everyone deny ahead of a user allow",
            ["deny everyone", "allow user:alice"], ["user:alice", "everyone"], false),
        new("user allow ahead of everyone deny",
            ["allow user:alice", "deny everyone"], ["user:alice", "everyone"], true),

        // A caller that resolved to nothing.
        new("caller with no principals, everyone allow",
            ["allow everyone"], [], false),
        new("caller with no principals, several allows",
            ["allow group:staff", "allow user:alice", "allow everyone"], [], false),

        // Kinds and exact comparison.
        new("unknown kind in the access list rejects the whole list",
            ["allow role:staff", "allow group:staff"], ["user:bob", "group:staff", "everyone"], false),
        new("unknown kind in the caller rejects the whole caller",
            ["allow group:staff"], ["role:admin", "group:staff"], false),
        new("the same value under another kind never matches",
            ["allow uid:1000"], ["user:1000", "gid:1000", "everyone"], false),
        new("values compare case-sensitively",
            ["allow group:Staff"], ["group:staff", "everyone"], false),

        // Duplicates.
        new("duplicate allow entries",
            ["allow group:staff", "allow group:staff"], ["group:staff"], true),
        new("a later allow for an already denied principal is never reached",
            ["deny group:staff", "allow group:staff"], ["group:staff"], false),
        new("a later deny for an already allowed principal is never reached",
            ["allow group:staff", "deny group:staff"], ["group:staff"], true),

        // Deny-only lists.
        new("deny-only set, caller it names",
            ["deny group:contractors", "deny user:mallory"], ["user:mallory", "group:contractors", "everyone"], false),
        new("deny-only set, caller it does not name",
            ["deny group:contractors", "deny user:mallory"], ["user:alice", "group:staff", "everyone"], false),

        // Size.
        new("large group list, deny for the last group first",
            ["deny group:g4999", "allow group:g0000"], ManyGroups, false),
        new("large group list, allow for the first group first",
            ["allow group:g0000", "deny group:g4999"], ManyGroups, true),
        new("large group list, a group not held",
            ["allow group:g5000"], ManyGroups, false),
        new("large group list, a group in the middle",
            ["deny group:outsiders", "allow group:g2500"], ManyGroups, true),
        new("long access list, the only match is last",
            [.. LongDenyPrefix, "allow user:alice"], ["user:alice", "everyone"], true),
    ];

    /// <summary>
    /// Runs every case against <paramref name="evaluate"/> and returns the names
    /// of the cases it got wrong.
    /// <para>
    /// Text that does not parse becomes the fail-closed value any real caller
    /// must use: an access list that does not parse is the empty set, and a
    /// caller whose principals do not parse holds none. Parsing is all or
    /// nothing because dropping one bad entry or principal can widen access.
    /// </para>
    /// </summary>
    public static IReadOnlyList<string> Failures(Func<AclSet, PrincipalSet, bool> evaluate) =>
        Failures(evaluate, StrictEntries, StrictCaller);

    /// <summary>The same run with the parsing swapped out, to prove the malformed-input cases can fail.</summary>
    internal static IReadOnlyList<string> Failures(
        Func<AclSet, PrincipalSet, bool> evaluate,
        Func<string[], AclSet> parseEntries,
        Func<string[], PrincipalSet> parseCaller) =>
        Cases
            .Where(c => evaluate(parseEntries(c.Entries), parseCaller(c.Caller)) != c.MayRead)
            .Select(c => c.Name)
            .ToList();

    private static AclSet StrictEntries(string[] lines) =>
        AclSet.TryParse(lines, out var set) ? set : AclSet.Empty;

    private static PrincipalSet StrictCaller(string[] texts) =>
        PrincipalSet.TryParse(texts, out var set) ? set : PrincipalSet.Empty;
}

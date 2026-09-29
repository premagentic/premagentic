namespace Premagentic.Core.Security.Acl;

/// <summary>
/// An operator's permission rule: every document from <see cref="Source"/>
/// whose path lies under <see cref="PathPrefix"/> gets <see cref="Acl"/>.
/// <para>
/// Paths are relative, use forward slashes and compare case-sensitively, the
/// form connectors already produce. A prefix is whole path segments: the empty
/// prefix covers the whole source, and <c>hr</c> covers <c>hr</c> and
/// <c>hr/pay.md</c> but not <c>hr-archive/pay.md</c>. A prefix with a leading or
/// trailing slash, an empty segment, a backslash, a <c>.</c> or <c>..</c>
/// segment or a control character is rejected, never normalized.
/// </para>
/// </summary>
public sealed record FolderRule
{
    public FolderRule(string source, string pathPrefix, AclSet acl)
    {
        if (!FolderPaths.IsValidSourceName(source))
            throw new ArgumentException("A source name must be non-empty, with no control characters and no leading or trailing white space.", nameof(source));
        if (pathPrefix is null || !(pathPrefix.Length == 0 || FolderPaths.IsValidPath(pathPrefix)))
            throw new ArgumentException("A path prefix is empty or relative forward-slash segments, with no empty, '.' or '..' segment.", nameof(pathPrefix));
        ArgumentNullException.ThrowIfNull(acl);

        Source = source;
        PathPrefix = pathPrefix;
        Acl = acl;
    }

    public string Source { get; }

    public string PathPrefix { get; }

    public AclSet Acl { get; }

    /// <summary>True when <paramref name="path"/> is the prefix itself or lies beneath it, at a segment boundary.</summary>
    public bool Covers(string path) =>
        path is not null
        && (PathPrefix.Length == 0
            || string.Equals(path, PathPrefix, StringComparison.Ordinal)
            || (path.Length > PathPrefix.Length
                && path.StartsWith(PathPrefix, StringComparison.Ordinal)
                && path[PathPrefix.Length] == '/'));
}

/// <summary>
/// Picks the rule for a document: among the rules for its source, the one with
/// the longest prefix that covers its path. No rule means the empty access
/// list, which denies everyone. A path that is not in clean relative form
/// matches no rule, so it too is denied.
/// </summary>
public sealed class FolderRuleMatcher
{
    // Per source, longest prefix first, so the first covering rule is the answer.
    private readonly Dictionary<string, FolderRule[]> _bySource;

    public FolderRuleMatcher(IEnumerable<FolderRule> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        var list = rules.ToList();
        if (list.Any(r => r is null))
            throw new ArgumentException("A rule list cannot hold a null rule.", nameof(rules));

        // Two rules for one folder would leave the answer to list order. Refuse
        // rather than pick one.
        var duplicate = list.GroupBy(r => (r.Source, r.PathPrefix)).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
            throw new ArgumentException("Two rules name the same source and path prefix.", nameof(rules));

        _bySource = list
            .GroupBy(r => r.Source, StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                g => g.OrderByDescending(r => r.PathPrefix.Length).ToArray(),
                StringComparer.Ordinal);
    }

    public FolderRule? Match(string source, string path)
    {
        if (source is null || path is null || !FolderPaths.IsValidPath(path)) return null;
        if (!_bySource.TryGetValue(source, out var candidates)) return null;
        return candidates.FirstOrDefault(r => r.Covers(path));
    }

    /// <summary>The access list for a document, or <see cref="AclSet.Empty"/> when no rule covers it.</summary>
    public AclSet AclFor(string source, string path) => Match(source, path)?.Acl ?? AclSet.Empty;
}

internal static class FolderPaths
{
    public static bool IsValidSourceName(string? source) =>
        !string.IsNullOrEmpty(source)
        && !char.IsWhiteSpace(source[0])
        && !char.IsWhiteSpace(source[^1])
        && !source.Any(char.IsControl);

    /// <summary>Non-empty relative forward-slash segments, none of them empty, <c>.</c> or <c>..</c>.</summary>
    public static bool IsValidPath(string? path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        if (path.Contains('\\') || path.Any(char.IsControl)) return false;
        foreach (var segment in path.Split('/'))
        {
            if (segment.Length == 0 || segment == "." || segment == "..") return false;
        }
        return true;
    }
}

using Premagentic.Core.Security.Acl;

namespace Premagentic.Core.Security;

/// <summary>A document's decided access list, and whether the folder rules decided it.</summary>
/// <param name="FromFolderRule">
/// True when the operator's folder rules decided the list, so a later rule change
/// moves the document with it. False when its connector decided, and for a path
/// the rules cannot judge.
/// </param>
public sealed record DocumentAcl(AclSet Set, bool FromFolderRule);

/// <summary>
/// The one place that decides which access list a document gets.
/// <list type="number">
/// <item>A connector that stated the document's access decides it: the
/// source system's own ordered list (<see cref="DocumentAccess.FromSource"/>),
/// or <see cref="DocumentAccess.Everyone"/>, <see cref="DocumentAccess.For"/> or
/// <see cref="DocumentAccess.NoOne"/>.</item>
/// <item>Otherwise (<see cref="DocumentAccess.FolderRules"/>) the operator's
/// folder rules for its source decide, longest prefix first.</item>
/// <item>No rule covering the path, or a path not in clean relative form,
/// means the empty list, which reaches nobody.</item>
/// </list>
/// </summary>
public static class AccessDecision
{
    public static DocumentAcl For(DocumentAccess access, string source, string path, FolderRuleMatcher rules)
    {
        ArgumentNullException.ThrowIfNull(access);
        ArgumentNullException.ThrowIfNull(rules);

        if (!access.UsesFolderRules) return new DocumentAcl(access.ConnectorAcl(), FromFolderRule: false);

        // A path the matcher refuses stays denied for good: marking it as
        // rule-decided would let a later rule change reach it by prefix.
        if (source is null || path is null || !FolderPaths.IsValidPath(path))
            return new DocumentAcl(AclSet.Empty, FromFolderRule: false);

        return new DocumentAcl(rules.AclFor(source, path), FromFolderRule: true);
    }
}

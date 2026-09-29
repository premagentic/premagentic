using System.Globalization;

namespace Premagentic.Core.Retrieval;

/// <summary>
/// The longest query a search takes, and the longest path and heading a
/// section fetch takes, on every surface: the API, MCP, the portal and the
/// command line. A question is a sentence or a paragraph. The embedding model
/// reads only the start of a longer one (the local model its first 384
/// tokens), the text match does more work for every word in it, and every
/// query is kept whole in the audit trail, so a query of megabytes would cost
/// the server and the trail and make the answer no better. Checked before
/// anything is read or recorded.
/// </summary>
public static class QueryLimits
{
    /// <summary>In characters (UTF-16 code units), for a query, a path and a heading alike.</summary>
    public const int MaxLength = 4_000;

    /// <summary>Null when <paramref name="query"/> is within <see cref="MaxLength"/>; otherwise the one sentence that refuses it.</summary>
    public static string? SearchRefusal(string query) =>
        query.Length > MaxLength
            ? string.Create(CultureInfo.InvariantCulture,
                $"A query is at most {MaxLength:N0} characters and this one has {query.Length:N0}: the embedding model reads only the start of a longer one, and every query is kept whole in the audit trail.")
            : null;

    /// <summary>Null when <paramref name="path"/> and <paramref name="heading"/> are each within <see cref="MaxLength"/>; otherwise the one sentence that refuses them.</summary>
    public static string? SectionRefusal(string path, string? heading) =>
        Math.Max(path.Length, heading?.Length ?? 0) is var longest && longest > MaxLength
            ? string.Create(CultureInfo.InvariantCulture,
                $"A path or a heading is at most {MaxLength:N0} characters and this one has {longest:N0}: every fetch is kept whole in the audit trail.")
            : null;
}

/// <summary>
/// A search or a section fetch refused for its length by <see cref="QueryLimits"/>,
/// before anything was read or recorded. The message is the one sentence a
/// surface shows the caller.
/// </summary>
public sealed class QueryTooLongException(string message) : ArgumentException(message);

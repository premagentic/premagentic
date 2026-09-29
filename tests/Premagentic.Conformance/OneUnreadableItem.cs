using System.Runtime.CompilerServices;
using Premagentic.Core;
using Premagentic.Core.Sources;

namespace Premagentic.Conformance;

/// <summary>
/// A connector as it would behave with one of its items locked: every path it
/// finds passes through untouched except the first one it hands to a reader,
/// whose content cannot be opened. It lets
/// <see cref="DocumentSourceConformance"/> prove the rule about unreadable
/// items on a machine that cannot make a real one, such as one whose account
/// may read every file it owns.
/// <para>
/// It proves what happens to the item once the connector has found it: the
/// run goes on, the item is reported unreadable, and the connector is never
/// asked to finish a document from content that was not read. It cannot prove
/// what the connector itself does when the system it reads refuses it, so a
/// connector that can be given a real unreadable item should be.
/// </para>
/// </summary>
public sealed class OneUnreadableItem(IDocumentSource inner) : IDocumentSource
{
    /// <summary>What opening the held item throws.</summary>
    public const string Reason = "held unreadable by the conformance kit";

    public string Name => inner.Name;

    public string PathPrefix => inner.PathPrefix;

    /// <summary>The path that was held unreadable, once one has been; null until then.</summary>
    public string? Held { get; private set; }

    public async IAsyncEnumerable<SourceRead> EnumerateAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var read in inner.EnumerateAsync(ct))
        {
            if (Held is null && read.Content is { } content)
            {
                Held = content.Path;
                yield return SourceRead.Unread(content with { Open = _ => throw new IOException(Reason) });
                continue;
            }
            yield return read;
        }
    }
}

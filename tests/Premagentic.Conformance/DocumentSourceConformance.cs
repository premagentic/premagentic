using Premagentic.Core.Extensions;
using Premagentic.Core;
using Premagentic.Core.Ingestion.Readers;
using Premagentic.Core.Security;
using Premagentic.Core.Sources;

namespace Premagentic.Conformance;

/// <summary>
/// Inherit this and give it your connector to prove it keeps the connector
/// contract. Two rules carry the weight.
/// <para>
/// One item that cannot be read must not throw. A protected or partly readable
/// source is the normal case here, and throwing costs the customer every other
/// document in the run.
/// </para>
/// <para>
/// A document whose permissions the connector could not read is readable by
/// nobody. Returning <see cref="DocumentAccess.Everyone"/> because the real
/// permissions were inconvenient to fetch is how a knowledge base becomes a
/// leak with a search box on it.
/// </para>
/// </summary>
public abstract class DocumentSourceConformance
{
    /// <summary>
    /// This kit proves the version of this seam that the PremAgentic your
    /// extension builds against offers. A kit from another release fails here,
    /// naming the version to match, rather than passing in silence.
    /// </summary>
    [Fact]
    public void The_kit_proves_the_seams_this_extension_builds_against() =>
        KitSeams.AssertProves(SeamVersions.SourceName, nameof(SeamVersions.Source));

    /// <summary>A source holding at least one item this connector can read.</summary>
    protected abstract Task<IDocumentSource> SourceAsync();

    /// <summary>
    /// A source holding several items, at least one of which cannot be read:
    /// locked, encrypted, or denied to the account the test runs as.
    /// <para>
    /// Left alone, it is <see cref="SourceAsync"/> with the kit's
    /// <see cref="OneUnreadableItem"/> holding its first item unreadable, so
    /// the rule is proved on every machine. Override it with a source that
    /// holds a real unreadable item where this machine can make one: that also
    /// proves what the connector does when the system it reads refuses it,
    /// which the kit's fake cannot. Returning null fails the test; the rule is
    /// never passed over in silence.
    /// </para>
    /// </summary>
    protected virtual async Task<IDocumentSource?> WithAnUnreadableItemAsync() =>
        new OneUnreadableItem(await SourceAsync());

    /// <summary>
    /// A source whose items' permissions cannot be read, or null when this
    /// connector can always read them. Returning null says so out loud rather
    /// than leaving the rule untested by accident.
    /// </summary>
    protected abstract Task<IDocumentSource?> WithPermissionsItCannotReadAsync();

    [Fact]
    public async Task It_names_itself_and_owns_a_path_prefix()
    {
        var source = await SourceAsync();

        Assert.False(string.IsNullOrWhiteSpace(source.Name), "A connector's name is recorded on every ingest run.");
        Assert.NotNull(source.PathPrefix);
    }

    [Fact]
    public async Task Every_path_it_yields_starts_with_its_path_prefix()
    {
        var source = await SourceAsync();
        if (source.PathPrefix.Length == 0) return;

        var paths = 0;
        await foreach (var read in source.ReadThroughAsync(ReaderRegistry.BuiltIn, CancellationToken.None))
        {
            var path = read.Document?.Path ?? read.Failure?.Path ?? read.Skip?.Path;
            Assert.NotNull(path);
            Assert.StartsWith(source.PathPrefix, path);
            paths++;
        }
        Assert.True(paths > 0, "The source given for this test yielded nothing, so nothing was checked.");
    }

    [Fact]
    public async Task One_item_it_cannot_read_does_not_throw_and_does_not_become_a_document()
    {
        var source = await WithAnUnreadableItemAsync();
        Assert.True(source is not null,
            "No source with an unreadable item was given, so the rule that one unreadable item must not throw would " +
            "go untested. Return one, or leave WithAnUnreadableItemAsync alone to use the kit's OneUnreadableItem.");

        var reads = new List<SourceRead>();
        await foreach (var read in source.ReadThroughAsync(ReaderRegistry.BuiltIn, CancellationToken.None))
            reads.Add(read);

        if (source is OneUnreadableItem { Held: null })
            Assert.Fail(
                "The kit's OneUnreadableItem found no item this connector hands to a reader, so it had nothing to " +
                "hold unreadable: this connector reads its items itself. Override WithAnUnreadableItemAsync and give " +
                "it a source that holds a real unreadable item.");

        Assert.NotEmpty(reads);
        Assert.Contains(reads, r => r.Failure is not null);
        foreach (var read in reads)
        {
            Assert.Null(read.Content);
            Assert.Equal(1, new object?[] { read.Document, read.Failure, read.Skip }.Count(x => x is not null));
        }
    }

    [Fact]
    public async Task A_document_whose_permissions_it_cannot_read_is_readable_by_nobody()
    {
        if (await WithPermissionsItCannotReadAsync() is not { } source) return;

        var documents = 0;
        await foreach (var read in source.ReadThroughAsync(ReaderRegistry.BuiltIn, CancellationToken.None))
        {
            if (read.Document is not { } document) continue;
            documents++;
            Assert.Equal(DocumentAccess.NoOne, document.Access);
        }
        Assert.True(documents > 0,
            "The source given for this test yielded no document, so nothing was checked. Give one that yields " +
            "at least one document whose permissions cannot be read.");
    }
}

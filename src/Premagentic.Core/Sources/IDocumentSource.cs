namespace Premagentic.Core.Sources;

/// <summary>
/// A connector: something that can enumerate documents out of a system the
/// customer already runs.
/// <para>
/// The posture every connector must take is that the source system stays
/// authoritative. Premagentic's store is derived and rebuildable, ingest is one
/// directional, and nothing here ever writes back. A customer keeps authoring
/// wherever they author today.
/// </para>
/// <para>
/// A connector is also the only place document permissions can be read, so it
/// is responsible for them. Returning <see cref="Security.DocumentAccess.Everyone"/>
/// because the real permissions were inconvenient to fetch is how a knowledge
/// base becomes a leak with a search box on it.
/// </para>
/// </summary>
public interface IDocumentSource
{
    /// <summary>Stable connector name, recorded on each ingest run.</summary>
    string Name { get; }

    /// <summary>
    /// The path prefix this source owns, or empty for the whole index.
    /// Orphan reconciliation is scoped to it, so two sources can populate one
    /// index without either deleting the other's documents. Every
    /// <see cref="SourceDocument.Path"/> this source yields must start with it.
    /// </summary>
    string PathPrefix { get; }

    /// <summary>
    /// Yields one <see cref="SourceRead"/> per path the connector finds: a
    /// document, a failure for a path that exists and could not be read, or a
    /// skip for a file in a format the connector does not read. A file skipped
    /// without a word is how a folder of PDFs reads as an empty success.
    /// <para>
    /// A connector must NOT throw because one item failed. Throwing costs the
    /// caller every other document in the run, and a protected or partially
    /// readable source is the normal case here, not an exceptional one. Reserve
    /// exceptions for the source itself being unusable, such as a root that does
    /// not exist, where continuing would be wrong.
    /// </para>
    /// </summary>
    IAsyncEnumerable<SourceRead> EnumerateAsync(CancellationToken ct = default);
}

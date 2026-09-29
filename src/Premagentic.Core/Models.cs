using Premagentic.Core.Okf;
using Premagentic.Core.Security;

namespace Premagentic.Core;

/// <summary>
/// Lifecycle buckets used for default-retrieval filtering. Superseded and
/// archived material stays indexed but is reachable only behind an explicit
/// historical flag, so a stale answer cannot be served as a current one.
/// </summary>
public static class DocumentLifecycle
{
    public const string Active = "active";
    public const string Draft = "draft";
    public const string Superseded = "superseded";
    public const string Archived = "archived";
    public const string Expired = "expired";

    /// <summary>
    /// Maps a free-text status from a source system onto a lifecycle bucket.
    /// Anything unrecognized is treated as current, which matches how most
    /// document stores behave: a file with no status is a live file.
    /// OKF's three words are known by name: <c>deprecated</c> is kept for links
    /// and history and is no longer current, so it must not fall through to
    /// active.
    /// </summary>
    public static string FromSourceStatus(string? status) => status?.Trim().ToLowerInvariant() switch
    {
        "draft" or "wip" or "in progress" => Draft,
        "stable" => Active,
        "superseded" or "replaced" or "deprecated" => Superseded,
        "archived" or "archive" or "retired" => Archived,
        "expired" or "obsolete" => Expired,
        _ => Active,
    };
}

public sealed record DocumentChunk(int Seq, string HeadingPath, string Content);

/// <summary>
/// One document as a connector presents it. The connector owns extraction and
/// permissions; the pipeline owns chunking, embedding and storage.
/// <para>
/// <paramref name="Text"/> is Markdown or close to it. A connector for a
/// format with its own structure (PDF, Word, a wiki) converts to Markdown
/// first so the structural chunker can see headings, which is how extraction
/// libraries emit text anyway.
/// </para>
/// </summary>
/// <param name="Path">
/// Stable identifier within the source, used as the citation and the upsert
/// key. It must survive a re-scan unchanged or every run will orphan and
/// re-embed the document.
/// </param>
/// <param name="Access">
/// Required with no default. See <see cref="DocumentAccess"/>: a connector
/// that cannot read the source's permissions returns
/// <see cref="DocumentAccess.NoOne"/> rather than guessing.
/// </param>
/// <param name="DocClass">
/// The source's own notion of what kind of document this is, if it has one.
/// Feeds <see cref="AuthorityWeights"/> only. Null is normal.
/// </param>
/// <param name="Okf">
/// What the document's frontmatter says in Open Knowledge Format terms: trust
/// tier, authorship, staleness and provenance. Null for a document with no
/// frontmatter, which is normal, unless its source is read as an OKF bundle.
/// Read as text only; nothing in it is executed.
/// </param>
public sealed record SourceDocument(
    string Path,
    string? Title,
    string Text,
    string ContentHash,
    DocumentAccess Access,
    string LifecycleStatus = DocumentLifecycle.Active,
    string? DocClass = null,
    DateTimeOffset? SourceModifiedAt = null,
    OkfMetadata? Okf = null);

/// <summary>
/// A path the connector found but could not turn into a document: an encrypted
/// file it holds no key for, a file the service account may not read, a format
/// with no extractor, a corrupt file.
/// <para>
/// This is deliberately NOT an error that stops a run. A protected folder is the
/// normal case for this product, and one unreadable file must not cost the
/// customer every other document beside it. It is also deliberately not silence:
/// the path stays in the index if it was already there, the count is reported,
/// and an operator can see what the system cannot see.
/// </para>
/// </summary>
public sealed record SourceFailure(string Path, string Reason);

/// <summary>
/// A file the connector found and did not read, because this version does not
/// read its format: a PDF, a Word file, a spreadsheet. Not a failure, and not
/// indexed. Reported so that a folder of files nobody can search does not look
/// like an empty one.
/// </summary>
/// <param name="Extension">
/// The grouping key: the lower-case extension with its dot, such as <c>.pdf</c>,
/// or <c>(none)</c>. A file a reader recognized and would not index carries the
/// reader's reason after it, as in <c>.pdf (no text layer)</c>.
/// </param>
/// <param name="Reason">
/// The reason a reader gave for not indexing a file it read, or null when no
/// reader here reads the format. The two are kept apart because they mean
/// opposite things for a document indexed earlier at the path: a reader that
/// looked at the file and found nothing to index says the old text is gone; no
/// reader says nothing about the file at all.
/// </param>
public sealed record SourceSkip(string Path, string Extension, string? Reason = null)
{
    public const string NoExtension = "(none)";

    /// <summary>The grouping key for a file name: its lower-case extension, or <see cref="NoExtension"/>.</summary>
    public static string ExtensionOf(string fileName) =>
        System.IO.Path.GetExtension(fileName) is { Length: > 1 } extension ? extension.ToLowerInvariant() : NoExtension;
}

/// <summary>
/// A path a connector found and did not read: it hands over a way to open the
/// content and a way to finish the document once a reader has read it. Which
/// formats can be read is a property of the process, not of the connector, so
/// the reader is picked centrally from a
/// <see cref="Ingestion.Readers.ReaderRegistry"/> and a connector gains every
/// format an installation adds.
/// </summary>
/// <param name="Open">
/// Opens the content, positioned at the start. Called once per read, and the
/// caller disposes what it returns. An <see cref="IOException"/>,
/// <see cref="UnauthorizedAccessException"/> or
/// <see cref="System.Security.SecurityException"/> from here or from reading
/// the stream is the path being unreadable, and is reported as a
/// <see cref="SourceFailure"/> rather than ending the run.
/// </param>
/// <param name="Complete">
/// Turns what the reader read, and the SHA-256 of the bytes it read from, into
/// the document. The connector owns everything the content does not say:
/// permissions, the fallback title, lifecycle, provenance and the modified
/// time.
/// </param>
public sealed record SourceContent(
    string Path,
    Func<CancellationToken, Task<Stream>> Open,
    Func<Ingestion.Readers.ReadDocument, string, SourceDocument> Complete);

/// <summary>
/// One result from enumerating a connector: a document, a path that exists and
/// could not be read, a file skipped for its format, or a path found and not
/// yet read.
/// <para>
/// The distinction is load-bearing for reconciliation. A path that is ABSENT
/// from the source has been deleted and its index entry should go. A path that
/// is PRESENT but unreadable has not been deleted, and removing its index entry
/// would silently shrink the corpus every time a volume was locked or a
/// permission was tightened.
/// </para>
/// </summary>
public sealed record SourceRead(SourceDocument? Document, SourceFailure? Failure, SourceSkip? Skip = null)
{
    /// <summary>
    /// A path found and not read yet. The reading step resolves it into one of
    /// the other three: a document, a failure, or a skip for a format this
    /// process has no reader for.
    /// </summary>
    public SourceContent? Content { get; init; }

    public static SourceRead Ok(SourceDocument document) => new(document, null);

    public static SourceRead Failed(string path, string reason) =>
        new(null, new SourceFailure(path, reason));

    public static SourceRead Skipped(string path, string extension, string? reason = null) =>
        new(null, null, new SourceSkip(path, extension, reason));

    /// <summary>A path found, for a reader to read.</summary>
    public static SourceRead Unread(SourceContent content) => new(null, null) { Content = content };
}

/// <param name="Trust">
/// What this search may return of machine-written content and of stale
/// content. Null means <see cref="TrustPolicy.Strict"/>, so a host that sets
/// nothing fails safe. <see cref="TrustPolicy.Resolve"/> gives the policy for
/// one caller.
/// </param>
/// <param name="AsOf">
/// The instant staleness is judged at. Null means the clock when the search
/// starts. Either way it is fixed once per search, so every read of one search
/// and the stale flag on its hits agree.
/// </param>
public sealed record SearchOptions(
    AccessScope Scope,
    int TopK = 5,
    bool IncludeHistorical = false,
    int CandidatePoolSize = 20,
    TrustPolicy? Trust = null,
    DateTimeOffset? AsOf = null)
{
    /// <summary>The policy this search runs under: <see cref="Trust"/>, or the strict one when none was given.</summary>
    public TrustPolicy EffectiveTrust => Trust ?? TrustPolicy.Strict;
}

/// <param name="ContentHash">
/// The content hash of the document version this passage was read from, so a
/// reader can check the passage against the file it cites.
/// </param>
/// <param name="ConceptId">The OKF concept id, when the document came from a source read as a bundle.</param>
/// <param name="TrustTier">How far the content has been confirmed. Carried whatever the policy.</param>
/// <param name="Authorship">Who wrote the content: a person, a machine, or unknown for an ordinary file.</param>
/// <param name="Stale">
/// The passage is past its <c>stale_after</c> at the instant this search ran.
/// Carried whatever the policy, so a stale passage a person was allowed to see
/// still says so.
/// </param>
public sealed record SearchHit(
    Guid ChunkId,
    string Path,
    string? Title,
    string HeadingPath,
    string Content,
    string LifecycleStatus,
    int? LexicalRank,
    bool LexicalFallback,   // matched only under degraded OR semantics, not the primary AND pass
    int? VectorRank,
    double? CosineDistance,
    double FusedScore,
    string ContentHash,
    string? ConceptId = null,
    OkfTrustTier TrustTier = OkfTrustTier.Unverified,
    OkfAuthorship Authorship = OkfAuthorship.Unknown,
    bool Stale = false)
{
    /// <summary>Human and agent facing citation, for example "handbook/travel.md § Per diem".</summary>
    public string Citation => string.IsNullOrEmpty(HeadingPath) ? Path : $"{Path} § {HeadingPath}";
}

/// <param name="Settings">
/// The retrieval tuning this search ranked under, read once for it, so whoever
/// judges the hits (the no-answer floor, for one) judges them by the same
/// reading that ranked them, even if the stored settings change meanwhile.
/// </param>
public sealed record SearchResult(
    string Query,
    bool IncludeHistorical,
    IReadOnlyList<SearchHit> Hits,
    long ElapsedMs,
    Retrieval.RetrievalSettingsReading Settings);

/// <summary>
/// Rank weighting by document class, applied to the fused score.
/// <para>
/// Flat by default, and that default is the important part. Weighting one kind
/// of document above another is a claim about a specific organization's filing
/// habits. A weighting table lifted from another deployment is worse than none,
/// because it silently demotes real answers. Populate this only from a measured
/// golden set for the corpus in front of you.
/// </para>
/// </summary>
public sealed class AuthorityWeights(IReadOnlyDictionary<string, double> byDocClass, double defaultWeight = 1.0)
{
    public static AuthorityWeights Flat { get; } =
        new(new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase));

    public double DefaultWeight => defaultWeight;

    public IReadOnlyDictionary<string, double> ByClass => byDocClass;

    public double For(string? docClass) =>
        docClass is not null && byDocClass.TryGetValue(docClass, out var w) ? w : defaultWeight;
}

/// <summary>
/// Per-deployment retrieval tuning. Every number here is corpus dependent.
/// </summary>
/// <param name="NoAnswerDistanceFloor">
/// Cosine distance above which a vector-only hit does not count as evidence of
/// an answer. The 0.55 default is the value measured on the corpus the
/// retrieval core was developed against (about 1,300 Markdown documents), using
/// MiniLM with heading context, where true no-answer queries bottomed
/// out at 0.587 and genuine matches ran 0.36 to 0.51. It is a starting point,
/// not a constant: it moves with the embedding model AND with the corpus, so a
/// deployment must recalibrate it against its own golden set before anyone
/// relies on "no results" meaning there is no answer.
/// </param>
/// <param name="RrfK">Reciprocal Rank Fusion smoothing constant.</param>
/// <param name="FallbackRrfWeight">
/// Weight for lexical hits that matched only under degraded OR semantics. At
/// full strength, a corpus where many documents share one generic query term
/// lets them collectively outrank a decisively close vector hit.
/// </param>
public sealed record RetrievalTuning(
    double NoAnswerDistanceFloor = 0.55,
    int RrfK = 60,
    double FallbackRrfWeight = 0.5)
{
    public static RetrievalTuning Default { get; } = new();
}

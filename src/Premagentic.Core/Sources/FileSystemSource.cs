using System.Runtime.CompilerServices;
using System.Text;
using Premagentic.Core.Ingestion.Readers;
using Premagentic.Core.Okf;
using Premagentic.Core.Security;

namespace Premagentic.Core.Sources;

/// <summary>
/// Finds documents in a folder tree. The reference connector: a file share is
/// what most organizations actually hand over first, and it is the one source
/// that needs no credentials to demonstrate.
/// <para>
/// Access is a constructor argument with no default. A folder tree carries
/// NTFS or POSIX permissions that this connector does not read, so the caller
/// has to say what the documents under this root are. Point it at a folder
/// whose audience you can state in one sentence.
/// </para>
/// <para>
/// Markdown frontmatter is read for Open Knowledge Format metadata in every
/// mode. Setting <see cref="OkfBundle"/> additionally reads the folder as an
/// OKF bundle; see there.
/// </para>
/// <para>
/// This connector does not decide which formats can be read. It yields every
/// file it finds for a reader to read, and a file no reader claims is counted
/// as not read, so adding a reader to an installation widens what this
/// connector indexes without a change here. Hidden files and folders, those
/// whose name starts with a dot, are left out entirely and not counted: they
/// are tool state such as <c>.git</c>, not documents.
/// </para>
/// </summary>
public sealed class FileSystemSource(
    string root,
    Func<string, DocumentAccess> accessForRelativePath,
    string pathPrefix = "") : IDocumentSource
{
    private static readonly string[] MarkdownExtensions = [".md", ".markdown"];
    private static readonly string[] ReservedBundleNames = ["index.md", "log.md"];

    private static readonly IReadOnlyDictionary<string, string> NoFields =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Every document under the root gets the same access value.</summary>
    public FileSystemSource(string root, DocumentAccess access, string pathPrefix = "")
        : this(root, _ => access, pathPrefix) { }

    public string Name => "filesystem";
    public string PathPrefix { get; } = pathPrefix.Replace('\\', '/').Trim('/');

    /// <summary>The folder read, as given.</summary>
    public string Root { get; } = root;

    /// <summary>
    /// Read the root as an OKF bundle. Off by default and chosen per source,
    /// because an ordinary folder may hold a real document called
    /// <c>index.md</c>. When on:
    /// <list type="bullet">
    /// <item><c>index.md</c> and <c>log.md</c> at any level are directory
    /// listings and update history (spec section 3.1), not documents, and are
    /// not yielded. The names match case-insensitively.</item>
    /// <item>Each <c>.md</c> file is a concept and carries its concept id.</item>
    /// <item><see cref="BundleReport"/> is filled with the declared
    /// <c>okf_version</c> and the files that do not conform. Those files are
    /// still yielded.</item>
    /// </list>
    /// </summary>
    public bool OkfBundle { get; init; }

    /// <summary>
    /// In bundle mode, store a concept that does not say who wrote it, with no
    /// frontmatter or with frontmatter that has no <c>generated</c>, as
    /// machine-written and unverified, so agents do not see it until a person
    /// signs it off. Off by default, which serves such a concept as an ordinary
    /// file. Ignored outside bundle mode: an ordinary folder makes no claim
    /// about authorship to hold anyone to.
    /// </summary>
    public bool UndeclaredAuthorshipIsMachine { get; init; }

    /// <summary>
    /// The conformance report from the most recent enumeration that ran to the
    /// end with <see cref="OkfBundle"/> on. Null before then, and always null
    /// when the source is not a bundle.
    /// </summary>
    public OkfBundleReport? BundleReport { get; private set; }

    public async IAsyncEnumerable<SourceRead> EnumerateAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        // A missing root still throws. That is the source being unusable rather
        // than one item failing, and continuing would let reconciliation read it
        // as "every document was deleted".
        if (!Directory.Exists(Root))
            throw new DirectoryNotFoundException($"Source folder not found: {Root}");

        // IgnoreInaccessible keeps a locked subdirectory from ending the walk.
        // Such a directory's files are simply not seen, which is why the pipeline
        // refuses to reconcile a run that saw nothing at all.
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
        };

        // The root as the system resolves it, once, for the check each open
        // makes: a link or a junction swapped in after the listing is followed
        // by the open, so the opened file is checked against the listed name.
        var finalRoot = ContainedFile.FinalRoot(Root);

        var issues = OkfBundle ? new List<OkfConformanceIssue>() : null;
        string? okfVersion = null;

        foreach (var file in Directory.EnumerateFiles(Root, "*", options))
        {
            ct.ThrowIfCancellationRequested();
            var native = Path.GetRelativePath(Root, file);
            var relative = native.Replace('\\', '/');
            if (relative.Split('/').Any(p => p.StartsWith('.'))) continue;
            var path = PathPrefix.Length == 0 ? relative : $"{PathPrefix}/{relative}";

            if (OkfBundle && ReservedBundleNames.Contains(Path.GetFileName(relative), StringComparer.OrdinalIgnoreCase))
            {
                if (relative.Equals("index.md", StringComparison.OrdinalIgnoreCase))
                    okfVersion = await ReadOkfVersionAsync(file, finalRoot, native, MaxIndexBytes, ct);
                continue;
            }

            // Read before the content is, and kept out of the reading step: the
            // file's own modified time is the connector's to report, and a
            // reader is given bytes and nothing to open.
            DateTimeOffset? modified = null;
            try { modified = File.GetLastWriteTimeUtc(file); } catch (IOException) { }

            yield return SourceRead.Unread(new SourceContent(
                path,
                _ => Task.FromResult(ContainedFile.Open(file, finalRoot, native)),
                (read, hash) => Complete(file, relative, path, read, hash, modified, issues)));
        }

        if (issues is not null) BundleReport = new OkfBundleReport(okfVersion, issues);
    }

    /// <summary>
    /// Finishes a document from what a reader read: everything the content does
    /// not say is the connector's, and the OKF reading of the frontmatter is
    /// done here because only the connector knows whether its root is a bundle.
    /// </summary>
    private SourceDocument Complete(
        string file, string relative, string path, ReadDocument read, string contentHash,
        DateTimeOffset? modified, List<OkfConformanceIssue>? issues)
    {
        // A reader that parses no frontmatter leaves a document with none: its
        // flat fields if it returned any, no nested values, and a state of
        // Absent, which is what an ordinary file with no block has.
        var frontmatter = read.ParsedFrontmatter
            ?? new FrontmatterResult(read.Frontmatter ?? NoFields, read.Text);
        var fields = frontmatter.Fields;
        var extension = Path.GetExtension(file);

        // Every concept in a bundle carries metadata, so its concept id rides
        // along even when its frontmatter is missing or unreadable. Outside a
        // bundle only Markdown with readable frontmatter has any to carry.
        var isConcept = OkfBundle && extension.Equals(".md", StringComparison.OrdinalIgnoreCase);
        OkfMetadata? okf = null;
        if (isConcept)
        {
            okf = OkfMetadata.FromFrontmatter(frontmatter) with
            {
                ConceptId = OkfMetadata.ConceptIdFor(relative),
                BundleConcept = true,
                UndeclaredIsMachine = UndeclaredAuthorshipIsMachine,
            };
            AddConformanceIssues(issues!, path, okf);
        }
        else if (frontmatter.State == FrontmatterState.Parsed
                 && MarkdownExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
        {
            okf = OkfMetadata.FromFrontmatter(frontmatter);
        }

        return new SourceDocument(
            Path: path,
            Title: read.Title ?? Path.GetFileNameWithoutExtension(file),
            Text: read.Text,
            ContentHash: contentHash,
            Access: accessForRelativePath(relative),
            LifecycleStatus: DocumentLifecycle.FromSourceStatus(fields.GetValueOrDefault("status")),
            DocClass: fields.GetValueOrDefault("type"),
            SourceModifiedAt: modified,
            Okf: okf);
    }

    private static void AddConformanceIssues(List<OkfConformanceIssue> issues, string path, OkfMetadata okf)
    {
        switch (okf.FrontmatterState)
        {
            case FrontmatterState.Absent:
                issues.Add(new OkfConformanceIssue(path, OkfConformanceProblem.NoFrontmatter));
                return;
            case FrontmatterState.Unparseable:
                issues.Add(new OkfConformanceIssue(path, OkfConformanceProblem.UnparseableFrontmatter));
                return;
        }

        if (okf.Type is null)
            issues.Add(new OkfConformanceIssue(path, OkfConformanceProblem.MissingType));
        if (okf.MalformedFields.Count > 0)
            issues.Add(new OkfConformanceIssue(path, OkfConformanceProblem.MalformedField, string.Join(", ", okf.MalformedFields)));
    }

    /// <summary>
    /// The most of the bundle-root <c>index.md</c> that is read: the pipeline's
    /// own limit for any file. Settable so a test can prove the limit without
    /// a file that large.
    /// </summary>
    internal long MaxIndexBytes { get; init; } = DocumentSourceReading.MaxFileBytes;

    /// <summary>
    /// Reads <c>okf_version</c> from the bundle-root <c>index.md</c>, the only
    /// frontmatter an index file may carry (spec section 8). It is opened and
    /// checked as every other file is, and read to the same size limit. An
    /// index that cannot be read, that is not a regular file at its listed
    /// path, or that is larger than the limit, is treated as one that declares
    /// no version.
    /// </summary>
    private static async Task<string?> ReadOkfVersionAsync(
        string file, string? finalRoot, string relative, long limit, CancellationToken ct)
    {
        try
        {
            byte[] bytes;
            await using (var stream = ContainedFile.Open(file, finalRoot, relative))
                bytes = await DocumentSourceReading.ReadBoundedAsync(stream, limit, ct);
            using var text = new StreamReader(new MemoryStream(bytes, writable: false), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var raw = await text.ReadToEndAsync(ct);
            return Frontmatter.Parse(raw).Fields.GetValueOrDefault("okf_version")?.Trim() is { Length: > 0 } version
                ? version
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return null;
        }
    }

}

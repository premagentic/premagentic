namespace Premagentic.Core.Okf;

public enum OkfConformanceProblem
{
    /// <summary>A concept file with no frontmatter block.</summary>
    NoFrontmatter,

    /// <summary>A frontmatter block that could not be read.</summary>
    UnparseableFrontmatter,

    /// <summary>Frontmatter with no non-empty <c>type</c>, the one required key.</summary>
    MissingType,

    /// <summary>An OKF field present but not in the shape the spec gives it. The detail names the fields.</summary>
    MalformedField,
}

/// <param name="Path">The document path, as the source yields it.</param>
public sealed record OkfConformanceIssue(string Path, OkfConformanceProblem Problem, string? Detail = null);

/// <summary>
/// What an operator needs to know about a folder read as an OKF bundle. A
/// report, never a rejection: every file listed here was still ingested
/// (spec section 11 forbids rejecting a bundle for these).
/// </summary>
/// <param name="OkfVersion">
/// <c>okf_version</c> from the bundle-root <c>index.md</c>, as written, or null
/// when it does not declare one.
/// </param>
public sealed record OkfBundleReport(string? OkfVersion, IReadOnlyList<OkfConformanceIssue> Issues)
{
    private static readonly string[] KnownVersions = ["0.1", "0.2"];

    /// <summary>
    /// Whether the declared version is one this reader was written against. An
    /// unknown version is still read, on a best-effort basis, as the spec asks
    /// (section 12).
    /// </summary>
    public bool VersionKnown => OkfVersion is not null && KnownVersions.Contains(OkfVersion);
}

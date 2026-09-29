using System.Text.RegularExpressions;

namespace Premagentic.Tests;

/// <summary>
/// Package ids this repository must never reference. Each one is a lookalike of
/// a package it does use: published under a name a person or a tool could
/// reach for by mistake, with no license and no project behind it. A package is
/// referenced by its exact id and pinned to an exact version, and this test is
/// what makes a wrong id fail the build's tests instead of restoring quietly.
/// </summary>
public sealed partial class PackageIdentityTests
{
    /// <summary>The lookalike, and the package it imitates.</summary>
    private static readonly (string Refused, string Instead)[] Lookalikes =
    [
        ("UglyToad.PdfPig", "PdfPig"),
    ];

    [GeneratedRegex("""<(?:PackageReference|PackageVersion|GlobalPackageReference)\b[^>]*\b(?:Include|Update)\s*=\s*"([^"]+)"[^>]*>""",
        RegexOptions.IgnoreCase)]
    private static partial Regex PackageItem();

    /// <summary>Every package id a project file names, as written.</summary>
    internal static IEnumerable<string> PackageIds(string projectText) =>
        PackageItem().Matches(projectText).Select(m => m.Groups[1].Value.Trim());

    private static IEnumerable<string> ProjectFiles(string root)
    {
        var pending = new Stack<string>([root]);
        while (pending.TryPop(out var folder))
        {
            foreach (var child in Directory.GetDirectories(folder))
                if (Path.GetFileName(child) is not (".git" or "bin" or "obj" or "node_modules")) pending.Push(child);
            foreach (var file in Directory.GetFiles(folder))
                if (Path.GetExtension(file).ToLowerInvariant() is ".csproj" or ".props" or ".targets")
                    yield return file;
        }
    }

    [Fact]
    public void The_check_finds_a_lookalike_written_any_way_msbuild_accepts()
    {
        // Controls: the pattern this test relies on sees the id in each shape a
        // project file can carry it, and does not see it in the package it imitates.
        Assert.Contains("UglyToad.PdfPig", PackageIds("""<PackageReference Include="UglyToad.PdfPig" Version="1.7.0-custom-5" />"""));
        Assert.Contains("uglytoad.pdfpig", PackageIds("""<packagereference Version="1" include="uglytoad.pdfpig"/>"""));
        Assert.Contains("UglyToad.PdfPig", PackageIds("""<PackageVersion Include="UglyToad.PdfPig" Version="0.1.9-alpha001-patch1" />"""));
        Assert.Equal(["PdfPig"], PackageIds("""<PackageReference Include="PdfPig" Version="0.1.16" />"""));
    }

    [Fact]
    public void No_project_in_the_repository_references_a_lookalike_package()
    {
        var root = RepositoryRoot();
        var files = ProjectFiles(root).ToList();
        // A scan that found no project would pass by checking nothing.
        Assert.Contains(files, f => Path.GetFileName(f) == "Premagentic.Core.csproj");

        var found = (
            from file in files
            from id in PackageIds(File.ReadAllText(file))
            from lookalike in Lookalikes
            where id.Equals(lookalike.Refused, StringComparison.OrdinalIgnoreCase)
            select $"{Path.GetRelativePath(root, file)} references {id}; the package is {lookalike.Instead}").ToList();

        Assert.True(found.Count == 0, string.Join("\n", found));
    }

    private static string RepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Premagentic.slnx"))) return dir.FullName;
        throw new InvalidOperationException("Premagentic.slnx was not found above the test's folder.");
    }
}

using System.Reflection;
using System.Security.Cryptography;
using Premagentic.Core;
using Premagentic.Core.Extensions;
using Premagentic.Core.Ingestion;
using Premagentic.Core.Security;

namespace Premagentic.Tests;

/// <summary>
/// An extension that ships a library of its own, the paragraph sample: the
/// library loads only when the manifest lists it and its bytes hash to the
/// listed value, and the hash an administrator allows covers it, so neither a
/// file dropped beside the extension nor a library replaced together with its
/// manifest line gets loaded on the strength of an earlier decision.
/// </summary>
public sealed class ExtensionDependencyTests : IDisposable
{
    private const string Name = "paragraph-chunker";
    private const string AssemblyFile = "ParagraphChunker.dll";
    private const string Library = "ParagraphRules.dll";

    private static readonly SourceDocument Letter = new(
        "letter.md", null, "The greenhouse opens at eight.\n\nThe shop opens at nine.\n",
        "sample-letter", DocumentAccess.NoOne);

    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("prem-extension-dependency-");

    public void Dispose()
    {
        try
        {
            _root.Delete(recursive: true);
        }
        catch (IOException)
        {
            // A folder a test could not remove is the temp folder's to clean.
        }
        catch (UnauthorizedAccessException)
        {
            // An assembly a load context still holds open; the temp folder's to clean.
        }
    }

    [Fact]
    public void A_listed_library_loads_and_the_extension_runs_on_it()
    {
        var folder = Install(listLibrary: true);
        var measured = ExtensionAllowList.Measure(folder);

        var host = ExtensionHost.Load(_root.FullName, [(measured.Name, measured.Sha256)]);

        Assert.Empty(host.Refused);
        var loaded = Assert.Single(host.Loaded);
        Assert.Equal(measured.Sha256, loaded.Sha256);
        // The allowed hash is not the assembly's alone: it covers the library too.
        Assert.NotEqual(measured.AssemblySha256, measured.Sha256);
        Assert.Equal([(Library, Hash(File.ReadAllBytes(Path.Combine(folder, Library))))],
            measured.Files.Select(f => (f.File, f.Sha256)));

        // The chunker cuts with the library's code, which is the proof it was loaded.
        var chunks = host.Chunkers.Resolve("paragraph").Chunk(Letter);
        Assert.Equal(["The greenhouse opens at eight.", "The shop opens at nine."], chunks.Select(c => c.Content));
    }

    [Fact]
    public void An_unlisted_library_beside_the_extension_is_not_loaded_and_the_extension_is_refused()
    {
        var folder = Install(listLibrary: false);
        var measured = ExtensionAllowList.Measure(folder);
        Assert.Empty(measured.Files);

        var host = ExtensionHost.Load(_root.FullName, [(measured.Name, measured.Sha256)]);

        Assert.Empty(host.Loaded);
        var refused = Assert.Single(host.Refused);
        Assert.Equal(ExtensionRefusal.NotAllowed, refused.Reason);
        Assert.Contains($"\"{Library}\"", refused.Detail);
        Assert.Contains("not in its manifest", refused.Detail);
        Assert.DoesNotContain("paragraph", host.Chunkers.Names);
    }

    [Fact]
    public void A_library_changed_after_the_manifest_was_written_is_refused_by_name()
    {
        var folder = Install(listLibrary: true);
        var measured = ExtensionAllowList.Measure(folder);
        Tamper(Path.Combine(folder, Library));

        var host = ExtensionHost.Load(_root.FullName, [(measured.Name, measured.Sha256)]);

        Assert.Empty(host.Loaded);
        var refused = Assert.Single(host.Refused);
        Assert.Equal(ExtensionRefusal.HashMismatch, refused.Reason);
        Assert.StartsWith($"\"{Library}\" hashes to", refused.Detail);
        // Allowing it now is refused too, naming the file.
        var measuring = Assert.Throws<ArgumentException>(() => ExtensionAllowList.Measure(folder));
        Assert.StartsWith($"\"{Library}\" hashes to", measuring.Message);
    }

    [Fact]
    public void A_library_replaced_together_with_its_manifest_line_is_not_what_was_allowed()
    {
        var folder = Install(listLibrary: true);
        var allowed = ExtensionAllowList.Measure(folder);

        // Someone with write access to the folder swaps the library and makes
        // the manifest agree with it. The manifest is consistent again; the
        // administrator's entry is about the old bytes.
        Tamper(Path.Combine(folder, Library));
        WriteManifest(folder, listLibrary: true);

        var host = ExtensionHost.Load(_root.FullName, [(allowed.Name, allowed.Sha256)]);

        Assert.Empty(host.Loaded);
        var refused = Assert.Single(host.Refused);
        Assert.Equal(ExtensionRefusal.NotAllowed, refused.Reason);
        Assert.Contains(ExtensionAllowList.Measure(folder).Sha256, refused.Detail);
    }

    [Fact]
    public void A_manifest_with_no_files_list_keeps_the_assembly_hash_every_earlier_entry_holds()
    {
        var sentence = SampleExtension.InstallInto(_root.FullName);
        var measured = ExtensionAllowList.Measure(Path.Combine(_root.FullName, SampleExtension.Name));

        Assert.Equal(sentence.Sha256, measured.Sha256);
        Assert.Equal(measured.AssemblySha256, measured.Sha256);
        Assert.Empty(measured.Files);
    }

    /// <summary>The paragraph sample in a folder of its own, with or without its library in the manifest.</summary>
    private string Install(bool listLibrary)
    {
        var built = BuiltPath();
        var folder = Directory.CreateDirectory(Path.Combine(_root.FullName, Name)).FullName;
        File.Copy(built, Path.Combine(folder, AssemblyFile), overwrite: true);
        File.Copy(Path.Combine(Path.GetDirectoryName(built)!, Library), Path.Combine(folder, Library), overwrite: true);
        WriteManifest(folder, listLibrary);
        return folder;
    }

    private static void WriteManifest(string folder, bool listLibrary)
    {
        var assembly = Hash(File.ReadAllBytes(Path.Combine(folder, AssemblyFile)));
        var library = Hash(File.ReadAllBytes(Path.Combine(folder, Library)));
        var files = listLibrary ? $$"""[ { "file": "{{Library}}", "sha256": "{{library}}" } ]""" : "[]";
        File.WriteAllText(Path.Combine(folder, ExtensionManifest.FileName), $$"""
            {
              "name": "{{Name}}",
              "version": "0.1.0",
              "assemblyFile": "{{AssemblyFile}}",
              "sha256": "{{assembly}}",
              "files": {{files}},
              "seams": { "chunker": 1 }
            }
            """);
    }

    /// <summary>One byte changed, which is what a file exchanged behind an administrator's back looks like.</summary>
    private static void Tamper(string path)
    {
        var bytes = File.ReadAllBytes(path);
        bytes[bytes.Length / 2]++;
        File.WriteAllBytes(path, bytes);
    }

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static string BuiltPath()
    {
        var path = typeof(ExtensionDependencyTests).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(attribute => attribute.Key == "Premagentic.Sample.ParagraphChunker.Path")
            .Value;
        Assert.True(File.Exists(path), "the paragraph sample was not built where the test project recorded it: " + path);
        return path!;
    }
}

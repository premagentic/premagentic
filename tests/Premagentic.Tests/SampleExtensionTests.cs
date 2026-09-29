using Premagentic.Core;
using Premagentic.Core.Extensions;
using Premagentic.Core.Ingestion;
using Premagentic.Core.Ingestion.Readers;
using Premagentic.Core.Security;

namespace Premagentic.Tests;

/// <summary>
/// The extension host against a real extension: the sample in
/// <c>samples/extensions/sentence-chunker</c>, built by this solution, copied
/// into a temp folder and loaded from there by hash. Nothing here references
/// the sample's types; the host finds them.
/// <para>
/// These are the tests that let the refusals mean something. Every refusal
/// case below uses the same folder as
/// <see cref="An_allow_listed_extension_loads_and_its_chunker_is_usable_by_name"/>,
/// so "the chunker is not registered" is the guard working and not an
/// extension that could never have loaded either way.
/// </para>
/// </summary>
public sealed class SampleExtensionTests : IDisposable
{
    private static readonly SourceDocument Doors = new(
        "doors.md", null, "The side door opens at eight. The front door opens at nine.",
        "sample-doors", DocumentAccess.NoOne);

    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("prem-sample-extension-");

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
    }

    [Fact]
    public void An_allow_listed_extension_loads_and_its_chunker_is_usable_by_name()
    {
        var allowed = SampleExtension.InstallInto(_root.FullName);

        var host = ExtensionHost.Load(_root.FullName, [allowed]);

        Assert.Empty(host.Refused);
        var loaded = Assert.Single(host.Loaded);
        Assert.Equal(SampleExtension.Name, loaded.Name);
        Assert.Equal(allowed.Sha256, loaded.Sha256);
        Assert.Equal(Path.Combine(_root.FullName, SampleExtension.Name), loaded.Folder);

        Assert.Contains(SampleExtension.ChunkerName, host.Chunkers.Names);
        Assert.Equal(
            ["The side door opens at eight.", "The front door opens at nine."],
            host.Chunkers.Resolve(SampleExtension.ChunkerName).Chunk(Doors).Select(c => c.Content));

        // And its reader, for a format no built-in reader claims.
        Assert.Contains(SampleExtension.ReaderName, host.Readers.Names);
        Assert.Contains(SampleExtension.ReaderExtension, host.Readers.Extensions);
        Assert.NotNull(host.Readers.ForPath("prices" + SampleExtension.ReaderExtension));

        // And its way of signing in, which the host holds for the API to ask
        // after every built-in way.
        Assert.Equal(SampleExtension.SignInAdapterName, Assert.Single(host.SignInAdapters).Name);

        // The built-ins are still the built-ins.
        Assert.Contains(ChunkerRegistry.DefaultName, host.Chunkers.Names);
        foreach (var builtIn in ReaderRegistry.BuiltIn.Names) Assert.Contains(builtIn, host.Readers.Names);
        Assert.Empty(host.EmbeddingProviders);
    }

    [Fact]
    public void An_extension_nobody_allowed_registers_nothing()
    {
        SampleExtension.InstallInto(_root.FullName);

        var host = ExtensionHost.Load(_root.FullName, []);

        Assert.Equal(ExtensionRefusal.NotAllowed, Assert.Single(host.Refused).Reason);
        Assert.Empty(host.Loaded);
        Assert.DoesNotContain(SampleExtension.ChunkerName, host.Chunkers.Names);
        Assert.Equal(ChunkerRegistry.BuiltIn.Names, host.Chunkers.Names);
        // Nothing it brought, across all three seams: the reader is gone, so a
        // file only it could read goes back to being one nothing reads, and no
        // way of signing in came with it either.
        Assert.Equal(ReaderRegistry.BuiltIn.Names, host.Readers.Names);
        Assert.Null(host.Readers.ForPath("prices" + SampleExtension.ReaderExtension));
        Assert.Empty(host.SignInAdapters);
    }

    [Fact]
    public void An_assembly_changed_after_its_manifest_was_written_registers_nothing()
    {
        var allowed = SampleExtension.InstallInto(_root.FullName, tamper: true);

        var host = ExtensionHost.Load(_root.FullName, [allowed]);

        Assert.Equal(ExtensionRefusal.HashMismatch, Assert.Single(host.Refused).Reason);
        Assert.Empty(host.Loaded);
        Assert.DoesNotContain(SampleExtension.ChunkerName, host.Chunkers.Names);
    }

    [Fact]
    public void An_extension_built_for_a_newer_seam_registers_nothing()
    {
        var allowed = SampleExtension.InstallInto(_root.FullName, chunkerSeam: SeamVersions.Chunker + 1);

        var host = ExtensionHost.Load(_root.FullName, [allowed]);

        Assert.Equal(ExtensionRefusal.SeamTooNew, Assert.Single(host.Refused).Reason);
        Assert.Empty(host.Loaded);
        Assert.DoesNotContain(SampleExtension.ChunkerName, host.Chunkers.Names);
    }

    [Fact]
    public void The_same_extension_installed_twice_loads_once()
    {
        var allowed = SampleExtension.InstallInto(_root.FullName, "a-copy");
        SampleExtension.InstallInto(_root.FullName, "b-copy");

        var host = ExtensionHost.Load(_root.FullName, [allowed]);

        Assert.Equal(Path.Combine(_root.FullName, "a-copy"), Assert.Single(host.Loaded).Folder);
        Assert.Equal(ExtensionRefusal.NameTaken, Assert.Single(host.Refused).Reason);
        Assert.Equal(1, host.Chunkers.Names.Count(n => n == SampleExtension.ChunkerName));
    }

    [Fact]
    public void The_manifest_the_sample_ships_matches_the_assembly_beside_it()
    {
        var built = Path.GetDirectoryName(SampleExtension.BuiltPath)!;

        Assert.True(ExtensionManifest.TryRead(built, out var manifest, out var problem), problem);
        Assert.Equal(SampleExtension.Name, manifest!.Name);
        Assert.Equal(SampleExtension.AssemblyFile, manifest.AssemblyFile);
        Assert.Equal(SampleExtension.Hash(File.ReadAllBytes(SampleExtension.BuiltPath)), manifest.Sha256);
        Assert.Equal(SeamVersions.Chunker, manifest.Seams["chunker"]);
        Assert.Equal(1, manifest.Seams["reader"]);
        Assert.Equal(SeamVersions.SignIn, manifest.Seams["signin"]);
        Assert.Null(manifest.SeamTooNew(SeamVersions.Of));
    }

    [Fact]
    public void The_sample_is_built_for_reader_version_1_and_its_shipped_manifest_loads_on_this_host()
    {
        // The sample's own build output, its manifest as shipped and not one a
        // test wrote: the standing proof that an extension built for an older
        // reader seam still loads after the seam moved on.
        var built = Path.GetDirectoryName(SampleExtension.BuiltPath)!;
        Assert.True(ExtensionManifest.TryRead(built, out var manifest, out var problem), problem);
        Assert.Equal(1, manifest!.Seams["reader"]);
        Assert.True(SeamVersions.Reader > 1, "The reader seam has not moved past version 1, so this proves nothing.");

        var folder = Directory.CreateDirectory(Path.Combine(_root.FullName, SampleExtension.Name));
        File.Copy(SampleExtension.BuiltPath, Path.Combine(folder.FullName, SampleExtension.AssemblyFile));
        File.Copy(Path.Combine(built, ExtensionManifest.FileName), Path.Combine(folder.FullName, ExtensionManifest.FileName));

        var host = ExtensionHost.Load(_root.FullName, [(SampleExtension.Name, manifest.Sha256)]);

        Assert.Empty(host.Refused);
        Assert.Equal(SampleExtension.Name, Assert.Single(host.Loaded).Name);
        Assert.NotNull(host.Readers.ForPath("prices" + SampleExtension.ReaderExtension));
    }
}

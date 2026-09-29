using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Premagentic.Core.Extensions;
using Premagentic.Core.Ingestion;

namespace Premagentic.Tests;

/// <summary>
/// The extension host on its own: what it refuses, in what order it decides,
/// and that a deployment whose extensions folder is full of rubbish still
/// starts with exactly the built-in behavior. No database and no Docker.
/// <para>
/// Every folder here is written by the test and every manifest is invented.
/// The assembly in most of them is a few bytes of text, which is deliberate:
/// each guard runs before anything is loaded, so a refusal that these tests
/// see is a refusal the loader never got past. The control for that is
/// <see cref="An_extension_nobody_allowed_is_refused_and_allowing_it_carries_it_to_the_loader"/>,
/// where allowing the same folder moves the refusal on to the loader itself.
/// That an allowed extension then registers what it brought is proven against
/// a real assembly by the sample extension's tests.
/// </para>
/// </summary>
public sealed class ExtensionHostTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("prem-extensions-");

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
    public void No_folder_means_no_extensions_and_no_error()
    {
        foreach (var folder in new[] { null, "", "   ", Path.Combine(_root.FullName, "not-there") })
        {
            var host = ExtensionHost.Load(folder, []);

            Assert.Empty(host.Loaded);
            Assert.Empty(host.Refused);
            Assert.Empty(host.EmbeddingProviders);
            Assert.Equal(ChunkerRegistry.BuiltIn.Names, host.Chunkers.Names);
        }

        Assert.Null(ExtensionHost.Load(null, []).Folder);
        Assert.Empty(ExtensionHost.BuiltIn.Loaded);
    }

    [Fact]
    public void An_empty_extensions_folder_gives_exactly_the_built_in_behavior()
    {
        var host = ExtensionHost.Load(_root.FullName, []);

        Assert.Empty(host.Loaded);
        Assert.Empty(host.Refused);
        Assert.Equal(ChunkerRegistry.BuiltIn.Names, host.Chunkers.Names);
        Assert.Equal(_root.FullName, host.Folder);
    }

    [Fact]
    public void A_manifest_that_cannot_be_used_is_refused_and_the_host_still_starts()
    {
        var bytes = "not an assembly"u8.ToArray();
        var hash = Hash(bytes);

        // The folder name, the manifest text, and what should be said about it.
        (string Folder, string? Manifest)[] cases =
        [
            ("no-manifest", null),
            ("not-json", "this is not JSON"),
            ("empty-json", ""),
            ("json-null", "null"),
            ("no-name", Manifest(name: null, sha256: hash)),
            ("blank-name", Manifest(name: "", sha256: hash)),
            ("spaced-name", Manifest(name: "two words", sha256: hash)),
            ("long-name", Manifest(name: new string('x', 65), sha256: hash)),
            ("no-version", Manifest(version: null, sha256: hash)),
            ("blank-version", Manifest(version: "  ", sha256: hash)),
            ("no-assembly-file", Manifest(assemblyFile: null, sha256: hash)),
            ("assembly-in-subfolder", Manifest(assemblyFile: "inner/thing.dll", sha256: hash)),
            ("assembly-up-a-level", Manifest(assemblyFile: @"..\elsewhere.dll", sha256: hash)),
            ("assembly-rooted", Manifest(assemblyFile: @"C:\windows\system32\thing.dll", sha256: hash)),
            ("assembly-not-there", Manifest(assemblyFile: "missing.dll", sha256: hash)),
            ("no-hash", Manifest(sha256: null)),
            ("short-hash", Manifest(sha256: hash[..63])),
            ("not-hex-hash", Manifest(sha256: new string('z', 64))),
            ("seam-version-zero", Manifest(sha256: hash, seams: """{"chunker": 0}""")),
            ("seam-version-text", Manifest(sha256: hash, seams: """{"chunker": "one"}""")),
            ("seam-twice", Manifest(sha256: hash, seams: """{"chunker": 1, "Chunker": 1}""")),
            ("over-the-cap", Manifest(sha256: hash) + new string(' ', ExtensionManifest.MaxBytes)),
        ];

        foreach (var (folder, manifest) in cases) Write(folder, manifest, bytes);

        // Allowed by every name a case uses, so nothing here is refused for
        // want of permission. The manifest is what is being read.
        var host = ExtensionHost.Load(_root.FullName, [..cases.Select(c => (c.Folder, hash)), ("thing", hash)]);

        Assert.Empty(host.Loaded);
        Assert.Equal(cases.Length, host.Refused.Count);
        foreach (var refusal in host.Refused)
        {
            Assert.Equal(ExtensionRefusal.BadManifest, refusal.Reason);
            Assert.NotEqual("", refusal.Detail);
        }
        Assert.Equal(ChunkerRegistry.BuiltIn.Names, host.Chunkers.Names);
    }

    [Fact]
    public void A_manifest_one_byte_over_64_KB_is_refused_with_the_file_and_the_cap_named_and_one_at_the_cap_is_read()
    {
        Assert.Equal(64 * 1024, ExtensionManifest.MaxBytes);
        var usable = Encoding.UTF8.GetBytes(Manifest(sha256: Hash("not an assembly"u8.ToArray())));

        // Written as bytes, so the size is exact: the usable manifest, then
        // spaces, which JSON reads past.
        string Folder(string name, int size)
        {
            var directory = Directory.CreateDirectory(Path.Combine(_root.FullName, name)).FullName;
            File.WriteAllBytes(Path.Combine(directory, ExtensionManifest.FileName),
                [.. usable, .. Enumerable.Repeat((byte)' ', size - usable.Length)]);
            return directory;
        }

        Assert.True(ExtensionManifest.TryRead(Folder("at-the-cap", ExtensionManifest.MaxBytes), out var read, out var problem), problem);
        Assert.Equal("thing", read!.Name);

        Assert.False(ExtensionManifest.TryRead(Folder("one-over", ExtensionManifest.MaxBytes + 1), out var over, out problem));
        Assert.Null(over);
        Assert.Equal("extension.json is larger than 64 KB, the most a manifest may be.", problem);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_managed_library_built_for_one_platform_refuses_the_extension_with_the_file_and_the_limit_named(bool listed)
    {
        var bytes = "not an assembly"u8.ToArray();
        var hash = Hash(bytes);
        var helper = "not a helper"u8.ToArray();
        const string platformFile = "runtimes/linux-x64/lib/net10.0/Helper.dll";
        var files = listed ? $", \"files\": [ {{ \"file\": \"{platformFile}\", \"sha256\": \"{Hash(helper)}\" }} ]" : "";
        Write("thing", Manifest(sha256: hash)[..^1] + files + "}", bytes);
        var folder = Directory.CreateDirectory(Path.Combine(_root.FullName, "thing", "runtimes", "linux-x64", "lib", "net10.0"));
        File.WriteAllBytes(Path.Combine(folder.FullName, "Helper.dll"), helper);
        var extensionHash = ExtensionManifest.ExtensionSha256("thing.dll", hash, listed ? [new ExtensionFile(platformFile, Hash(helper))] : []);

        var host = ExtensionHost.Load(_root.FullName, [("thing", extensionHash)]);

        var refusal = Assert.Single(host.Refused);
        Assert.Equal(ExtensionRefusal.BadManifest, refusal.Reason);
        Assert.Equal(
            $"\"{platformFile}\" is a managed library built for one platform, and this version loads managed libraries " +
            "from beside the extension's assembly only. Publish the extension for its platform (dotnet publish -r <rid>), " +
            "which puts that library beside the assembly.",
            refusal.Detail);
    }

    [Fact]
    public void A_file_under_runtimes_lib_that_is_not_a_library_is_held_to_the_listing_rule_as_before()
    {
        var bytes = "not an assembly"u8.ToArray();
        Write("thing", Manifest(sha256: Hash(bytes)), bytes);
        var folder = Directory.CreateDirectory(Path.Combine(_root.FullName, "thing", "runtimes", "linux-x64", "lib", "net10.0"));
        File.WriteAllText(Path.Combine(folder.FullName, "Helper.xml"), "<doc/>");

        var host = ExtensionHost.Load(_root.FullName, [("thing", Hash(bytes))]);

        var refusal = Assert.Single(host.Refused);
        Assert.Equal(ExtensionRefusal.NotAllowed, refusal.Reason);
        Assert.Contains("not in its manifest's \"files\" list", refusal.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void A_manifest_with_a_byte_order_mark_is_read_as_before()
    {
        var hash = Hash("not an assembly"u8.ToArray());
        var directory = Directory.CreateDirectory(Path.Combine(_root.FullName, "marked")).FullName;
        File.WriteAllText(Path.Combine(directory, ExtensionManifest.FileName), Manifest(sha256: hash), new UTF8Encoding(true));
        Assert.Equal(0xEF, File.ReadAllBytes(Path.Combine(directory, ExtensionManifest.FileName))[0]);

        Assert.True(ExtensionManifest.TryRead(directory, out var read, out var problem), problem);
        Assert.Equal(hash, read!.Sha256);
    }

    [Fact]
    public void An_assembly_outside_the_extension_folder_is_refused_although_it_is_there_and_is_allowed()
    {
        // The file the manifests below point at is real, hashes to what they
        // claim, and is allow-listed. Only the rule that an assembly lives in
        // its own extension folder stops it, so this is what tells that rule
        // apart from the folder simply being empty.
        var bytes = "not an assembly"u8.ToArray();
        var hash = Hash(bytes);
        var outside = Path.Combine(_root.FullName, "outside.dll");
        File.WriteAllBytes(outside, bytes);

        var inner = Directory.CreateDirectory(Path.Combine(_root.FullName, "thing", "inner"));
        File.WriteAllBytes(Path.Combine(inner.FullName, "thing.dll"), bytes);

        foreach (var elsewhere in new[] { "../outside.dll", outside, "inner/thing.dll" })
        {
            Write("thing", Manifest(assemblyFile: elsewhere, sha256: hash), bytes);

            var refusal = Assert.Single(ExtensionHost.Load(_root.FullName, [("thing", hash)]).Refused);
            Assert.Equal(ExtensionRefusal.BadManifest, refusal.Reason);
        }

        // The control: the same bytes, the same hash, the same allow list, named
        // as a file in the extension's own folder, get past the manifest and as
        // far as the loader.
        Write("thing", Manifest(sha256: hash), bytes);
        Assert.Equal(ExtensionRefusal.DidNotLoad,
            Assert.Single(ExtensionHost.Load(_root.FullName, [("thing", hash)]).Refused).Reason);
    }

    [Fact]
    public void An_assembly_that_does_not_hash_to_its_manifest_is_refused_even_when_that_hash_is_allowed()
    {
        var written = "the bytes the manifest was written for"u8.ToArray();
        var claimed = Hash(written);

        var tampered = written.ToArray();
        tampered[0]++;
        Assert.NotEqual(claimed, Hash(tampered));

        Write("thing", Manifest(sha256: claimed), tampered);

        // The allow list holds the hash the manifest claims, so an allow list
        // matched against the claim rather than against the file would let this
        // through.
        var host = ExtensionHost.Load(_root.FullName, [("thing", claimed)]);

        var refusal = Assert.Single(host.Refused);
        Assert.Equal(ExtensionRefusal.HashMismatch, refusal.Reason);
        Assert.Contains(Hash(tampered), refusal.Detail);
        Assert.Empty(host.Loaded);

        // The same folder, untampered, gets past the hash check.
        Write("thing", Manifest(sha256: claimed), written);
        Assert.Equal(ExtensionRefusal.DidNotLoad,
            Assert.Single(ExtensionHost.Load(_root.FullName, [("thing", claimed)]).Refused).Reason);
    }

    [Fact]
    public void An_extension_nobody_allowed_is_refused_and_allowing_it_carries_it_to_the_loader()
    {
        var bytes = "not an assembly"u8.ToArray();
        var hash = Hash(bytes);
        Write("thing", Manifest(sha256: hash), bytes);

        foreach (var allowed in new IReadOnlyList<(string, string)>[]
                 {
                     [],
                     [("thing", Hash("other bytes"u8.ToArray()))],   // the name, another hash
                     [("other", hash)],                              // the hash, another name
                 })
        {
            var refusal = Assert.Single(ExtensionHost.Load(_root.FullName, allowed).Refused);
            Assert.Equal(ExtensionRefusal.NotAllowed, refusal.Reason);
            Assert.Contains(ExtensionSettings.Allowed, refusal.Detail);
        }

        // The control. Nothing about the folder changed: the allow list did,
        // and the refusal moves on to the loader, which is the next thing that
        // can refuse it. Every check above therefore ran and stopped it.
        var loaded = ExtensionHost.Load(_root.FullName, [("THING", hash.ToUpperInvariant())]);
        Assert.Equal(ExtensionRefusal.DidNotLoad, Assert.Single(loaded.Refused).Reason);
    }

    [Fact]
    public void A_seam_this_version_does_not_offer_is_refused_and_one_it_does_is_carried_past()
    {
        var bytes = "not an assembly"u8.ToArray();
        var hash = Hash(bytes);
        IReadOnlyList<(string, string)> allowed = [("thing", hash)];

        foreach (var seams in new[] { """{"chunker": 2}""", """{"reader": 1, "chunker": 9}""", """{"quantum": 1}""" })
        {
            Write("thing", Manifest(sha256: hash, seams: seams), bytes);
            var refusal = Assert.Single(ExtensionHost.Load(_root.FullName, allowed).Refused);
            Assert.Equal(ExtensionRefusal.SeamTooNew, refusal.Reason);
        }

        foreach (var seams in new[] { null, "{}", """{"chunker": 1}""", """{"CHUNKER": 1, "reader": 1}""" })
        {
            Write("thing", Manifest(sha256: hash, seams: seams), bytes);
            Assert.Equal(ExtensionRefusal.DidNotLoad,
                Assert.Single(ExtensionHost.Load(_root.FullName, allowed).Refused).Reason);
        }
    }

    [Fact]
    public void Every_folder_is_reported_with_its_own_reason_and_none_of_them_stops_the_host()
    {
        var bytes = "not an assembly"u8.ToArray();
        var hash = Hash(bytes);

        Write("a-bad-manifest", "not JSON", bytes);
        Write("b-wrong-hash", Manifest(name: "b-wrong-hash", sha256: Hash("elsewhere"u8.ToArray())), bytes);
        Write("c-not-allowed", Manifest(name: "c-not-allowed", sha256: hash), bytes);
        Write("d-seam-too-new", Manifest(name: "d-seam-too-new", sha256: hash, seams: """{"reader": 4}"""), bytes);

        var host = ExtensionHost.Load(_root.FullName, [("d-seam-too-new", hash)]);

        Assert.Equal(
            [
                ("a-bad-manifest", ExtensionRefusal.BadManifest),
                ("b-wrong-hash", ExtensionRefusal.HashMismatch),
                ("c-not-allowed", ExtensionRefusal.NotAllowed),
                ("d-seam-too-new", ExtensionRefusal.SeamTooNew),
            ],
            host.Refused.Select(r => (r.Name, r.Reason)));
        foreach (var refusal in host.Refused)
            Assert.Equal(Path.Combine(_root.FullName, refusal.Name), refusal.Folder);

        Assert.Empty(host.Loaded);
        Assert.Empty(host.EmbeddingProviders);
        Assert.Equal(ChunkerRegistry.BuiltIn.Names, host.Chunkers.Names);
    }

    /// <summary>A manifest with every part usable, and whichever parts a case replaces.</summary>
    private static string Manifest(
        string? name = "thing",
        string? version = "1.0.0",
        string? assemblyFile = "thing.dll",
        string? sha256 = null,
        string? seams = """{"chunker": 1}""")
    {
        var parts = new List<string>();
        if (name is not null) parts.Add($"\"name\": {JsonSerializer.Serialize(name)}");
        if (version is not null) parts.Add($"\"version\": {JsonSerializer.Serialize(version)}");
        if (assemblyFile is not null) parts.Add($"\"assemblyFile\": {JsonSerializer.Serialize(assemblyFile)}");
        if (sha256 is not null) parts.Add($"\"sha256\": {JsonSerializer.Serialize(sha256)}");
        if (seams is not null) parts.Add($"\"seams\": {seams}");
        return $"{{{string.Join(", ", parts)}}}";
    }

    /// <summary>
    /// One extension folder: the manifest when there is one, and the assembly
    /// the usable manifests name.
    /// </summary>
    private void Write(string folder, string? manifest, byte[] assembly)
    {
        var directory = Directory.CreateDirectory(Path.Combine(_root.FullName, folder));
        if (manifest is not null)
            File.WriteAllText(Path.Combine(directory.FullName, ExtensionManifest.FileName), manifest, Encoding.UTF8);
        File.WriteAllBytes(Path.Combine(directory.FullName, "thing.dll"), assembly);
    }

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}

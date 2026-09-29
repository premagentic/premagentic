using System.Reflection;
using System.Security.Cryptography;
using Premagentic.Core;
using Premagentic.Core.Extensions;
using Premagentic.Core.Security;

namespace Premagentic.Tests;

/// <summary>
/// An extension that brings a native library, the native-lines sample, on its
/// real bytes: the library for this platform is found under
/// <c>runtimes/&lt;rid&gt;/native/</c> and called when it is listed and matches,
/// a native file the manifest does not list refuses the extension, a listed one
/// with a changed byte refuses it, and one changed after startup is not loaded.
/// The sample carries the Windows and the Linux library, so this runs on both.
/// </summary>
public sealed class NativeExtensionTests : IDisposable
{
    private const string Name = "native-lines";
    private const string AssemblyFile = "NativeLines.dll";
    private const string Windows = "runtimes/win-x64/native/prem_sample_lines.dll";
    private const string Linux = "runtimes/linux-x64/native/libprem_sample_lines.so";

    private static readonly SourceDocument Letter = new(
        "letter.md", null, "The greenhouse opens at eight.\nThe shop opens at nine.\n\nThe gate closes at six.",
        "sample-letter", DocumentAccess.NoOne);

    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("prem-native-extension-");

    public void Dispose()
    {
        try
        {
            _root.Delete(recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A native library a process still has loaded; the temp folder's to clean.
        }
    }

    /// <summary>The library this platform loads, or null where the sample carries none.</summary>
    private static string? ThisPlatform =>
        OperatingSystem.IsWindows() && Environment.Is64BitProcess ? Windows
        : OperatingSystem.IsLinux() && System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture
            == System.Runtime.InteropServices.Architecture.X64 ? Linux
        : null;

    [Fact]
    public void A_listed_native_library_for_this_platform_is_loaded_and_called()
    {
        Assert.NotNull(ThisPlatform);
        var folder = Install(list: [Windows, Linux]);
        var host = ExtensionHost.Load(_root.FullName, [Allow(folder)]);

        Assert.Empty(host.Refused);
        Assert.Single(host.Loaded);
        // The chunker finds every line end through the native function, so
        // these chunks are the proof it was loaded and ran.
        var chunks = host.Chunkers.Resolve("native-line").Chunk(Letter);
        Assert.Equal(["The greenhouse opens at eight.", "The shop opens at nine.", "The gate closes at six."],
            chunks.Select(c => c.Content));
    }

    [Fact]
    public void A_native_file_under_runtimes_that_the_manifest_does_not_list_refuses_the_extension()
    {
        var folder = Install(list: [Windows]);
        var host = ExtensionHost.Load(_root.FullName, [Allow(folder)]);

        Assert.Empty(host.Loaded);
        var refused = Assert.Single(host.Refused);
        Assert.Equal(ExtensionRefusal.NotAllowed, refused.Reason);
        Assert.Contains($"\"{Linux}\"", refused.Detail);
    }

    [Fact]
    public void A_listed_native_file_with_a_changed_byte_refuses_the_extension_by_name()
    {
        Assert.NotNull(ThisPlatform);
        var folder = Install(list: [Windows, Linux]);
        var allowed = Allow(folder);
        Tamper(folder, ThisPlatform!);

        var host = ExtensionHost.Load(_root.FullName, [allowed]);

        Assert.Empty(host.Loaded);
        var refused = Assert.Single(host.Refused);
        Assert.Equal(ExtensionRefusal.HashMismatch, refused.Reason);
        Assert.StartsWith($"\"{ThisPlatform}\" hashes to", refused.Detail);
    }

    /// <summary>
    /// Asked of the load context itself, the way the runtime asks it at the
    /// first call. Going through a call instead would depend on test order: a
    /// process that has already loaded a library of the same name can be handed
    /// that one by the operating system's own lookup.
    /// </summary>
    [Fact]
    public void A_native_library_changed_after_startup_is_measured_again_and_not_loaded()
    {
        Assert.NotNull(ThisPlatform);
        var folder = Install(list: [Windows, Linux]);
        Assert.True(ExtensionManifest.TryRead(folder, out var manifest, out _));
        var verified = manifest!.Files.Select(f =>
        {
            var path = Path.Combine([folder, .. f.File.Split('/')]);
            return new VerifiedFile(f.File, path, f.Sha256, File.ReadAllBytes(path));
        }).ToList();

        // Unchanged: the library is found and loaded.
        var intact = new ExtensionLoadContext(Name, Path.Combine(folder, AssemblyFile), verified);
        Assert.NotEqual(nint.Zero, LoadNative(intact, "prem_sample_lines"));
        Assert.Null(intact.Refusal);

        // Changed after it was measured at startup: measured again, not loaded,
        // and the reason kept.
        var copy = Install(list: [Windows, Linux], name: Name + "-changed");
        var measured = manifest.Files.Select(f =>
        {
            var path = Path.Combine([copy, .. f.File.Split('/')]);
            return new VerifiedFile(f.File, path, f.Sha256, File.ReadAllBytes(path));
        }).ToList();
        Tamper(copy, ThisPlatform!);
        var changed = new ExtensionLoadContext(Name, Path.Combine(copy, AssemblyFile), measured);

        Assert.Equal(nint.Zero, LoadNative(changed, "prem_sample_lines"));
        Assert.Equal(ExtensionRefusal.HashMismatch, changed.Refusal?.Reason);
        Assert.Contains($"\"{ThisPlatform}\" changed after it was measured", changed.Refusal?.Detail);
    }

    private static nint LoadNative(ExtensionLoadContext context, string name) =>
        (nint)typeof(ExtensionLoadContext)
            .GetMethod("LoadUnmanagedDll", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(context, [name])!;

    [Theory]
    [InlineData("../outside.dll")]
    [InlineData("runtimes/../../outside.dll")]
    [InlineData("runtimes\\win-x64\\native\\helper.dll")]
    [InlineData("/runtimes/win-x64/native/helper.dll")]
    [InlineData("C:/helper.dll")]
    [InlineData("runtimes//helper.dll")]
    public void A_listed_path_that_is_not_under_the_extensions_folder_is_refused(string path)
    {
        var folder = Directory.CreateDirectory(Path.Combine(_root.FullName, Name)).FullName;
        File.WriteAllText(Path.Combine(folder, ExtensionManifest.FileName), $$"""
            {
              "name": "{{Name}}", "version": "0.1.0", "assemblyFile": "{{AssemblyFile}}",
              "sha256": "{{new string('0', 64)}}",
              "files": [ { "file": {{System.Text.Json.JsonSerializer.Serialize(path)}}, "sha256": "{{new string('0', 64)}}" } ]
            }
            """);

        Assert.False(ExtensionManifest.TryRead(folder, out _, out var problem));
        Assert.Contains("a path inside the extension's own folder", problem);
    }

    [Fact]
    public void A_listed_path_under_the_extensions_folder_is_read()
    {
        var folder = Directory.CreateDirectory(Path.Combine(_root.FullName, Name)).FullName;
        File.WriteAllText(Path.Combine(folder, ExtensionManifest.FileName), $$"""
            {
              "name": "{{Name}}", "version": "0.1.0", "assemblyFile": "{{AssemblyFile}}",
              "sha256": "{{new string('0', 64)}}",
              "files": [ { "file": "{{Linux}}", "sha256": "{{new string('0', 64)}}" } ]
            }
            """);

        Assert.True(ExtensionManifest.TryRead(folder, out var manifest, out var problem), problem);
        Assert.Equal(Linux, Assert.Single(manifest!.Files).File);
    }

    /// <summary>The sample in a folder of its own: the assembly, both native files, and a manifest listing <paramref name="list"/>.</summary>
    private string Install(string[] list, string name = Name)
    {
        var built = BuiltPath();
        var from = Path.GetDirectoryName(built)!;
        var folder = Directory.CreateDirectory(Path.Combine(_root.FullName, name)).FullName;
        File.Copy(built, Path.Combine(folder, AssemblyFile), overwrite: true);
        foreach (var native in new[] { Windows, Linux })
        {
            var target = Path.Combine([folder, .. native.Split('/')]);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(Path.Combine([from, .. native.Split('/')]), target, overwrite: true);
        }

        var entries = string.Join(",\n", list.Select(file =>
            $$"""    { "file": "{{file}}", "sha256": "{{Hash(Path.Combine([folder, .. file.Split('/')]))}}" }"""));
        File.WriteAllText(Path.Combine(folder, ExtensionManifest.FileName), $$"""
            {
              "name": "{{Name}}",
              "version": "0.1.0",
              "assemblyFile": "{{AssemblyFile}}",
              "sha256": "{{Hash(Path.Combine(folder, AssemblyFile))}}",
              "files": [
            {{entries}}
              ],
              "seams": { "chunker": 1 }
            }
            """);
        return folder;
    }

    /// <summary>
    /// The pair an administrator's allow would write, taken from the manifest
    /// as it stands. A manifest that lists less than the folder holds is still
    /// allowed here, so the refusal a test expects is the host's and not the
    /// allow list's.
    /// </summary>
    private static (string Name, string Sha256) Allow(string folder)
    {
        Assert.True(ExtensionManifest.TryRead(folder, out var manifest, out var problem), problem);
        return (manifest!.Name, ExtensionManifest.ExtensionSha256(manifest.AssemblyFile, manifest.Sha256, manifest.Files));
    }

    /// <summary>One byte changed, which is what a file exchanged behind an administrator's back looks like.</summary>
    private static void Tamper(string folder, string file)
    {
        var path = Path.Combine([folder, .. file.Split('/')]);
        var bytes = File.ReadAllBytes(path);
        bytes[bytes.Length / 2]++;
        File.WriteAllBytes(path, bytes);
    }

    private static string Hash(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));

    private static string BuiltPath()
    {
        var path = typeof(NativeExtensionTests).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(attribute => attribute.Key == "Premagentic.Sample.NativeLines.Path")
            .Value;
        Assert.True(File.Exists(path), "the native sample was not built where the test project recorded it: " + path);
        return path!;
    }
}

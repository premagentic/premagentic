using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;

namespace Premagentic.Core.Extensions;

/// <summary>A file an extension's manifest lists, measured and held as the bytes that hashed to the listed value.</summary>
internal sealed record VerifiedFile(string File, string Path, string Sha256, byte[] Bytes);

/// <summary>
/// One extension's own place to load. Each extension gets one, so two
/// extensions that carry different versions of the same library each get the
/// version they shipped with instead of whichever loaded first.
/// <para>
/// What the host already has is taken from the host. The contracts an
/// extension implements are types in this process, and an extension folder
/// that happens to carry its own copy of this library would otherwise load a
/// second set of them: the extension would implement types the host has never
/// heard of, and register nothing, for no visible reason.
/// </para>
/// <para>
/// Anything else comes from the extension's folder only when its manifest
/// lists the file and the file's bytes hash to the listed value. A managed
/// file is loaded from the very bytes that were hashed. An unmanaged file can
/// only be loaded by path, so it is hashed again just before. A file the
/// extension asks for that is beside it and not listed, or whose bytes no
/// longer match, is not loaded, and the first such file is kept in
/// <see cref="Refusal"/> so the host refuses the whole extension.
/// </para>
/// <para>
/// A managed file is looked for beside the extension's assembly only. One
/// built for a single platform under <c>runtimes/&lt;rid&gt;/lib/</c>, as a
/// package lays it out when it has one assembly per platform, would never be
/// loaded, and the one beside the assembly, if any, would load on every
/// platform, so the host refuses an extension that carries one before it gets
/// here. An extension that needs one is published for its platform
/// (<c>dotnet publish -r &lt;rid&gt;</c>), which puts that platform's assembly
/// beside its own. Native libraries are looked for per platform; see
/// <see cref="UnmanagedPaths"/>.
/// </para>
/// </summary>
internal sealed class ExtensionLoadContext : AssemblyLoadContext
{
    private readonly string _folder;
    private readonly IReadOnlyList<VerifiedFile> _files;

    /// <param name="name">The extension's name, so a stack trace says whose assembly it is.</param>
    /// <param name="assemblyPath">The extension's own assembly, which its dependencies are looked for beside.</param>
    /// <param name="files">The files the manifest lists, already hashed and found to match.</param>
    public ExtensionLoadContext(string name, string assemblyPath, IReadOnlyList<VerifiedFile> files) : base(name)
    {
        _folder = Path.GetDirectoryName(Path.GetFullPath(assemblyPath))!;
        _files = files;
    }

    /// <summary>The first file this extension asked for and was not given, and why; null while there is none.</summary>
    public (ExtensionRefusal Reason, string Detail)? Refusal { get; private set; }

    /// <summary>The library the contracts live in, as this process has it.</summary>
    private static readonly Assembly Contracts = typeof(IExtension).Assembly;

    protected override Assembly? Load(AssemblyName name)
    {
        // The contracts' library is always the host's own, and its version
        // decides only one thing. An extension built against this version or an
        // older one gets the host's copy whatever version its reference names:
        // left to the runtime, a request for a version above the one it has
        // fails, and a release numbered below the one a reader was built with
        // would load no reader at all. One built against a newer version may
        // call what this version lacks, and the seam versions do not cover all of
        // that surface, so it is refused here, at startup, by name, rather than
        // loaded to fail later on a missing method.
        if (string.Equals(name.Name, Contracts.GetName().Name, StringComparison.OrdinalIgnoreCase))
        {
            if (name.Version is { } wanted && Contracts.GetName().Version is { } have && wanted > have)
            {
                Refusal ??= (ExtensionRefusal.CoreTooNew,
                    $"It was built against PremAgentic {Short(wanted)}; this is {Short(have)}, so it was not loaded.");
                return null;
            }
            return Contracts;
        }

        try
        {
            return Default.LoadFromAssemblyName(name);
        }
        catch (Exception ex) when (ex is FileNotFoundException or FileLoadException or BadImageFormatException)
        {
            // Not something this process has. Look for it beside the extension.
        }

        if (name.Name is not { Length: > 0 } simple) return null;
        var file = simple + ".dll";
        if (Listed(file) is { } listed)
        {
            using var stream = new MemoryStream(listed.Bytes, writable: false);
            return LoadFromStream(stream);
        }
        Unlisted(file);
        return null;
    }

    protected override nint LoadUnmanagedDll(string unmanagedDllName)
    {
        foreach (var candidate in UnmanagedPaths(unmanagedDllName))
        {
            if (Listed(candidate) is not { } listed) continue;
            // Loaded by path, the only way the runtime loads a native library,
            // so the bytes on disk are checked once more right before.
            byte[] bytes;
            try
            {
                bytes = File.ReadAllBytes(listed.Path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return nint.Zero;
            }
            if (!Convert.ToHexStringLower(SHA256.HashData(bytes)).Equals(listed.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                Refusal ??= (ExtensionRefusal.HashMismatch,
                    $"\"{listed.File}\" changed after it was measured: it no longer hashes to {listed.Sha256}, so it was not loaded.");
                return nint.Zero;
            }
            return LoadUnmanagedDllFromPath(listed.Path);
        }
        foreach (var candidate in UnmanagedPaths(unmanagedDllName)) Unlisted(candidate);
        return nint.Zero;
    }

    /// <summary>A version as a release names it: 0.2.0, not 0.2.0.0.</summary>
    private static string Short(Version version) => $"{version.Major}.{version.Minor}.{Math.Max(version.Build, 0)}";

    private VerifiedFile? Listed(string file) =>
        _files.FirstOrDefault(f => f.File.Equals(file, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Notes a file the extension asked for that is in its folder and not
    /// listed; a file that is not there is only missing.
    /// </summary>
    /// <param name="file">A path under the extension's folder, with forward slashes.</param>
    private void Unlisted(string file)
    {
        if (Refusal is null && ExtensionManifest.IsPathInFolder(file)
            && File.Exists(System.IO.Path.Combine([_folder, .. file.Split('/')])))
            Refusal = (ExtensionRefusal.NotAllowed,
                $"It asked for \"{file}\", which is in its folder and not in its manifest's \"files\" list, so it was not loaded.");
    }

    /// <summary>
    /// Where a native library can be in the extension's folder, in the order
    /// they are tried: under <c>runtimes/&lt;rid&gt;/native/</c> for this
    /// process's platform, most specific first, then beside the assembly.
    /// </summary>
    private static IEnumerable<string> UnmanagedPaths(string name)
    {
        foreach (var rid in RuntimeIdentifiers)
            foreach (var candidate in UnmanagedCandidates(name))
                yield return $"runtimes/{rid}/native/{candidate}";
        foreach (var candidate in UnmanagedCandidates(name))
            yield return candidate;
    }

    /// <summary>
    /// This process's platform as runtime identifiers, most specific first:
    /// <c>win-x64</c> then <c>win</c>, <c>linux-arm64</c> then <c>linux</c>.
    /// </summary>
    internal static IReadOnlyList<string> RuntimeIdentifiers { get; } = Identify();

    private static IReadOnlyList<string> Identify()
    {
        var os = OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : "linux";
        var arch = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();
        return [$"{os}-{arch}", os];
    }

    /// <summary>The file names a native library request can mean, on any platform.</summary>
    private static IEnumerable<string> UnmanagedCandidates(string name)
    {
        yield return name;
        if (System.IO.Path.HasExtension(name)) yield break;
        yield return name + ".dll";
        yield return name + ".so";
        yield return "lib" + name + ".so";
        yield return name + ".dylib";
        yield return "lib" + name + ".dylib";
    }
}

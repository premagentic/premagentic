using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Premagentic.Core.Ingestion;

namespace Premagentic.Core.Extensions;

/// <summary>
/// What an extension folder declares about itself, read from
/// <c>extension.json</c> beside the assembly it names.
/// <para>
/// A manifest is a claim, not a permission. It says which file to load, what
/// that file's contents hash to, and which seam versions the code was built
/// for. The host recomputes the hash from the bytes it is about to load and
/// looks the pair up in the administrator's allow list, so a manifest that
/// claims the wrong hash, or a correct hash nobody allowed, gets nothing
/// loaded.
/// </para>
/// </summary>
/// <param name="Name">
/// The name the allow list holds and the health page shows: up to 64 letters,
/// digits, dots, hyphens and underscores, starting with a letter or digit. The
/// folder name is only a label; this is the name that is allowed.
/// </param>
/// <param name="AssemblyFile">
/// The one assembly to load, as a file name inside the extension's own folder.
/// A path that points anywhere else is refused.
/// </param>
/// <param name="Sha256">The SHA-256 of that file, as 64 hexadecimal digits.</param>
/// <param name="Seams">
/// Seam name to the version the extension was built for, compared ignoring
/// case. A seam this process does not have, or a version above the one it
/// offers, is refused. An extension may declare none.
/// </param>
/// <param name="Files">
/// Every other file the extension loads, managed or unmanaged, each with the
/// SHA-256 of its contents: a file name beside the assembly, or a path under
/// the extension's folder written with forward slashes, such as
/// <c>runtimes/win-x64/native/helper.dll</c> for a native library built for
/// one platform. A managed library is loaded only from beside the assembly: an
/// extension that carries one built for a single platform under
/// <c>runtimes/&lt;rid&gt;/lib/</c>, listed or not, is refused, and is
/// published for its platform instead, which puts it beside the assembly. A
/// file that is not listed here is never loaded, an extension
/// that asks for one is refused, and so is one that carries a file under
/// <c>runtimes/</c> that is not listed. Empty for a manifest with no
/// <c>files</c> list, which loads its assembly and nothing else from its
/// folder.
/// </param>
public sealed record ExtensionManifest(string Name, string Version, string AssemblyFile,
    string Sha256, IReadOnlyDictionary<string, int> Seams, IReadOnlyList<ExtensionFile> Files)
{
    /// <summary>The manifest file's name, in every extension folder.</summary>
    public const string FileName = "extension.json";

    /// <summary>
    /// The largest manifest read: 64 KB. A manifest names one assembly and
    /// lists the files beside it, each in about a hundred bytes, so this holds
    /// several hundred; the largest first-party one, the PDF reader's with its
    /// seven libraries, is about 1 KB.
    /// </summary>
    public const int MaxBytes = 64 * 1024;

    // The longest value a problem sentence quotes back, so a file that is not a
    // manifest at all cannot fill a log line or a console.
    private const int QuotedLength = 40;

    private const int HexDigits = 64;

    private static readonly JsonSerializerOptions ReadOptions = new() { PropertyNameCaseInsensitive = true };

    /// <summary>
    /// The manifest in <paramref name="folder"/>, or <paramref name="problem"/>
    /// as one sentence naming what is wrong with it. Nothing here opens the
    /// assembly: this only reads what the folder claims.
    /// </summary>
    public static bool TryRead(string folder, out ExtensionManifest? manifest, out string? problem)
    {
        manifest = null;
        problem = null;
        var path = Path.Combine(folder, FileName);

        string text;
        try
        {
            // Never read more than one byte past the cap, so a manifest of any
            // size costs no more than that; the one byte tells a file longer
            // than the cap from one exactly as long.
            var bytes = new byte[MaxBytes + 1];
            var total = 0;
            using (var stream = File.OpenRead(path))
            {
                int got;
                while (total < bytes.Length && (got = stream.Read(bytes, total, bytes.Length - total)) > 0) total += got;
            }
            if (total > MaxBytes)
            {
                problem = $"{FileName} is larger than {MaxBytes / 1024} KB, the most a manifest may be.";
                return false;
            }
            // As File.ReadAllText reads: UTF-8, or what a byte order mark says.
            using var reader = new StreamReader(new MemoryStream(bytes, 0, total, writable: false), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            text = reader.ReadToEnd();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            problem = $"{FileName} could not be read: {ex.Message}";
            return false;
        }

        ManifestFile? read;
        try
        {
            read = JsonSerializer.Deserialize<ManifestFile>(text, ReadOptions);
        }
        catch (JsonException ex)
        {
            problem = $"{FileName} is not the JSON a manifest is: {ex.Message}";
            return false;
        }
        if (read is null)
        {
            problem = $"{FileName} holds null, not a manifest.";
            return false;
        }

        // A chunker's name rule, because these names are typed at a console and
        // stored in a setting for the same reasons.
        if (read.Name is null || !ChunkerRegistry.IsName(read.Name))
        {
            problem = $"\"name\" is up to 64 letters, digits, dots, hyphens and underscores, starting with a letter " +
                $"or digit, not {Quote(read.Name)}.";
            return false;
        }
        if (string.IsNullOrWhiteSpace(read.Version))
        {
            problem = $"\"version\" is the version this extension calls itself, not {Quote(read.Version)}.";
            return false;
        }
        if (read.AssemblyFile is null || !IsFileNameInFolder(read.AssemblyFile))
        {
            problem = $"\"assemblyFile\" is one file name inside the extension's own folder, not {Quote(read.AssemblyFile)}.";
            return false;
        }
        if (read.Sha256 is null || !IsHex(read.Sha256))
        {
            problem = $"\"sha256\" is the assembly's SHA-256 as {HexDigits} hexadecimal digits, not {Quote(read.Sha256)}.";
            return false;
        }

        var seams = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var (seam, version) in read.Seams ?? [])
        {
            if (version < 1)
            {
                problem = $"\"seams\" takes a version of 1 or more for every seam, not {version} for \"{Clip(seam)}\".";
                return false;
            }
            if (!seams.TryAdd(seam, version))
            {
                problem = $"\"seams\" takes each seam once, ignoring case, not \"{Clip(seam)}\" twice.";
                return false;
            }
        }

        var files = new List<ExtensionFile>();
        foreach (var file in read.Files ?? [])
        {
            if (file?.File is null || !IsPathInFolder(file.File))
            {
                problem = $"\"files\" takes a path inside the extension's own folder for every entry, such as " +
                    $"Library.dll or runtimes/linux-x64/native/libhelper.so, not {Quote(file?.File)}.";
                return false;
            }
            if (file.File.Equals(read.AssemblyFile, StringComparison.OrdinalIgnoreCase)
                || file.File.Equals(FileName, StringComparison.OrdinalIgnoreCase))
            {
                problem = $"\"files\" lists the files besides the assembly and the manifest, not \"{Clip(file.File)}\".";
                return false;
            }
            if (files.Any(f => f.File.Equals(file.File, StringComparison.OrdinalIgnoreCase)))
            {
                problem = $"\"files\" takes each file once, ignoring case, not \"{Clip(file.File)}\" twice.";
                return false;
            }
            if (file.Sha256 is null || !IsHex(file.Sha256))
            {
                problem = $"\"files\" takes the SHA-256 of \"{Clip(file.File)}\" as {HexDigits} hexadecimal digits, not {Quote(file.Sha256)}.";
                return false;
            }
            files.Add(new ExtensionFile(file.File, file.Sha256.ToLowerInvariant()));
        }

        manifest = new ExtensionManifest(read.Name, read.Version, read.AssemblyFile,
            read.Sha256.ToLowerInvariant(), seams, files);
        return true;
    }

    /// <summary>
    /// One sentence naming the seam this extension declares that
    /// <paramref name="host"/> cannot meet, or null when every declared seam is
    /// met. A seam this process does not have counts as one it cannot meet: the
    /// extension was built against something else.
    /// </summary>
    public string? SeamTooNew(Func<string, int?> host)
    {
        foreach (var (seam, wanted) in Seams)
        {
            var offered = host(seam);
            if (offered is null)
                return $"It was built for the \"{Clip(seam)}\" seam, which this version does not have.";
            if (wanted > offered)
                return $"It was built for version {wanted} of the \"{Clip(seam)}\" seam and this version offers {offered}.";
        }
        return null;
    }

    /// <summary>
    /// The hash an administrator allows: the assembly's own SHA-256 when the
    /// manifest lists no other file, which is what every allow list entry
    /// written before files could be listed holds; otherwise the SHA-256 of a
    /// short text naming the assembly and every listed file with its hash, in
    /// file name order. Either way it changes when any byte that can be loaded
    /// from the folder changes, so an allow list entry is a decision about all
    /// of them and a rewritten manifest does not carry it over.
    /// </summary>
    /// <param name="assemblySha256">The measured hash of the assembly.</param>
    /// <param name="files">The listed files with their measured hashes.</param>
    public static string ExtensionSha256(string assemblyFile, string assemblySha256, IReadOnlyList<ExtensionFile> files)
    {
        if (files.Count == 0) return assemblySha256.ToLowerInvariant();
        var text = new StringBuilder("premagentic-extension-files 1\n");
        text.Append(assemblyFile).Append('\t').Append(assemblySha256.ToLowerInvariant()).Append('\n');
        foreach (var file in files.OrderBy(f => f.File.ToLowerInvariant(), StringComparer.Ordinal))
            text.Append(file.File).Append('\t').Append(file.Sha256.ToLowerInvariant()).Append('\n');
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }

    /// <summary>
    /// A file name with no folder in it, and no way out of the folder it is in.
    /// Both separators and the drive colon are refused whatever this platform
    /// treats as a separator, because a manifest written on one platform is
    /// read on the other and a path that walks out of the folder must not
    /// depend on which one is reading it.
    /// </summary>
    private static bool IsFileNameInFolder(string file) =>
        file.Length > 0 && file == Path.GetFileName(file) && file is not ("." or "..")
        && file.IndexOfAny(['/', '\\', ':']) < 0;

    /// <summary>
    /// A path under the folder, written with forward slashes, every part of it
    /// a file name by the rule above: no backslash, no drive, no empty part,
    /// and no <c>.</c> or <c>..</c>, so it can only go down.
    /// </summary>
    internal static bool IsPathInFolder(string path) =>
        path.Length > 0 && path.Split('/').All(IsFileNameInFolder);

    /// <summary>
    /// A library under <c>runtimes/&lt;rid&gt;/lib/</c>, where a package puts a
    /// managed library built for one platform, as against
    /// <c>runtimes/&lt;rid&gt;/native/</c> for a native one.
    /// </summary>
    internal static bool IsPlatformManagedPath(string path)
    {
        var parts = path.Split('/');
        return parts.Length >= 4
               && parts[0].Equals("runtimes", StringComparison.OrdinalIgnoreCase)
               && parts[2].Equals("lib", StringComparison.OrdinalIgnoreCase)
               && parts[^1].EndsWith(".dll", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Why an extension carrying such a library cannot load, and the way
    /// round, naming the file: the host says it at start and the allow list at
    /// the allow, in the same words.
    /// </summary>
    internal static string PlatformManagedRefusal(string path) =>
        $"\"{path}\" is a managed library built for one platform, and this version loads managed libraries " +
        "from beside the extension's assembly only. Publish the extension for its platform (dotnet publish -r <rid>), " +
        "which puts that library beside the assembly.";

    /// <summary>
    /// Why an extension with a file under <c>runtimes/</c> that its manifest
    /// does not list cannot load, naming the file: every file there could be
    /// loaded, so every one is held to the list. The host says it at start and
    /// the allow list at the allow, in the same words.
    /// </summary>
    internal static string UnlistedRuntimeRefusal(string path) =>
        $"\"{path}\" is in its folder under runtimes/ and not in its manifest's \"files\" list.";

    private static bool IsHex(string text) =>
        text.Length == HexDigits && text.All(char.IsAsciiHexDigit);

    private static string Quote(string? value) => value is null ? "nothing" : $"\"{Clip(value)}\"";

    private static string Clip(string text) =>
        text.Length <= QuotedLength ? text : string.Concat(text.AsSpan(0, QuotedLength), "...");

    /// <summary>The manifest as it is written, before any of it is trusted.</summary>
    private sealed record ManifestFile(
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("version")] string? Version,
        [property: JsonPropertyName("assemblyFile")] string? AssemblyFile,
        [property: JsonPropertyName("sha256")] string? Sha256,
        [property: JsonPropertyName("seams")] Dictionary<string, int>? Seams,
        [property: JsonPropertyName("files")] List<FileEntry?>? Files);

    private sealed record FileEntry(
        [property: JsonPropertyName("file")] string? File,
        [property: JsonPropertyName("sha256")] string? Sha256);
}

/// <summary>A file an extension loads besides its assembly, and the SHA-256 its contents must have.</summary>
/// <param name="File">A file name inside the extension's own folder.</param>
public sealed record ExtensionFile(string File, string Sha256);

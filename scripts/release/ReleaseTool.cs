// PremAgentic release tool. Jobs that must give the same result in Git Bash on
// Windows and on a Linux runner, so they are not shell:
//
//   pack       one deterministic archive of a folder: entries sorted, times fixed,
//              no owner names, and modes set here instead of read from the file
//              system (a Linux publish made on Windows carries no execute bits).
//   licenses   the license text of everything a publish redistributes, read from
//              the publish's own *.deps.json, refusing every package it cannot
//              find a text for; PACKAGES.txt begins with the .NET runtime version
//              the build carries, and the SDK it came from when --sdk names it.
//   extension  the files an extension's build folder may ship: its extension.json,
//              the assembly it names and every file it lists, each checked against
//              the hash the manifest gives, and nothing else from that folder.
//   merge      several self-contained publishes laid into ONE folder, so the
//              archive carries one runtime for all of them: a file they carry
//              alike is kept once; where their copies differ, the copy whose file
//              version its own publish's deps.json records as higher is kept, and
//              on a tie the runtime pack's copy over a package's (the rules the SDK
//              applies inside one publish); any other difference, or a differing
//              copy no deps.json gives a version, refuses the build.
//   vcruntime  the four Visual C++ runtime DLLs the Windows archive ships, taken out
//              of the pinned Microsoft redistributable: the package checked against
//              its pinned size and SHA-256, its two containers found from the
//              bundle's own .wixburn section, the x64 Minimum runtime's cabinet
//              found by name in the bundle's manifest, exactly four members
//              written under their names and each checked against its pin, into an
//              empty output folder, which then holds those four files and nothing else.
//
//   dotnet run scripts/release/ReleaseTool.cs -- pack --dir <folder> --top <name>
//       (--tar <file> | --zip <file>) --mtime <epoch> [--exec <path>]...
//   dotnet run scripts/release/ReleaseTool.cs -- licenses --publish <folder>...
//       --packages <folder> --texts <folder> --out <folder> [--sdk <version>]
//   dotnet run scripts/release/ReleaseTool.cs -- extension --built <folder> --out <folder>
//   dotnet run scripts/release/ReleaseTool.cs -- merge --into <folder> --from <folder> <folder>...
//   dotnet run scripts/release/ReleaseTool.cs -- vcruntime --package <VC_redist.x64.exe>
//       --pins <scripts/download-vc-runtime.sh> --out <folder>
//
// Only the base class library is used.
// No lock file for this program: it uses the base class library only, and the
// packages a file-based program is given by default (the native AOT compiler and
// the trimmer) follow the SDK's patch, which a lock file would pin by mistake.
#:property RestorePackagesWithLockFile=false
using System.Buffers.Binary;
using System.Formats.Tar;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

try
{
    if (args.Length == 0) throw new ToolException("usage: ReleaseTool.cs pack|licenses|extension|merge|vcruntime <options>; see the comment at the top of the file");
    var options = Options.Parse(args.Skip(1));
    return args[0] switch
    {
        "pack" => Pack(options),
        "licenses" => Licenses(options),
        "extension" => Extension(options),
        "merge" => Merge(options),
        "vcruntime" => VcRuntime(options),
        _ => throw new ToolException($"unknown command '{args[0]}'; the commands are pack, licenses, extension, merge and vcruntime"),
    };
}
catch (ToolException e)
{
    Console.Error.WriteLine($"refused: {e.Message}");
    return 1;
}

static int Pack(Options o)
{
    var dir = Path.GetFullPath(o.One("dir"));
    var top = o.One("top");
    var tarPath = o.Optional("tar");
    var zipPath = o.Optional("zip");
    if ((tarPath is null) == (zipPath is null)) throw new ToolException("pack needs exactly one of --tar and --zip");
    var mtime = DateTimeOffset.FromUnixTimeSeconds(long.Parse(o.One("mtime"), CultureInfo.InvariantCulture));
    // A zip entry holds an MS-DOS date, 1980 to 2107, and the archive API throws on any
    // other: refused here by name, on every host's clock alike (the year is the UTC one).
    if (zipPath is not null && mtime.Year is < 1980 or > 2107)
        throw new ToolException($"--mtime {o.One("mtime")} is {mtime:u}, and a zip entry holds times from 1980 to 2107 only");
    var exec = o.All("exec").ToHashSet(StringComparer.Ordinal);

    if (!Directory.Exists(dir)) throw new ToolException($"{dir} is not a folder");
    var entries = Directory.EnumerateFileSystemEntries(dir, "*", SearchOption.AllDirectories)
        .Select(p => (Path: p, Name: Path.GetRelativePath(dir, p).Replace('\\', '/'), IsDir: Directory.Exists(p)))
        .OrderBy(e => e.Name, StringComparer.Ordinal)
        .ToList();
    foreach (var name in exec)
        if (!entries.Any(e => !e.IsDir && e.Name == name))
            throw new ToolException($"--exec names {name}, which is not a file in {dir}");

    const UnixFileMode FileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead;
    const UnixFileMode ExecMode = FileMode | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;

    if (tarPath is not null)
    {
        // Ustar: no per-entry access and change times, which a pax or gnu header
        // would fill from the clock.
        using var stream = File.Create(tarPath);
        using var tar = new TarWriter(stream, TarEntryFormat.Ustar, leaveOpen: false);
        tar.WriteEntry(DirEntry(top + "/", mtime));
        foreach (var e in entries)
        {
            if (e.IsDir) { tar.WriteEntry(DirEntry($"{top}/{e.Name}/", mtime)); continue; }
            var entry = new UstarTarEntry(TarEntryType.RegularFile, $"{top}/{e.Name}")
            {
                Mode = exec.Contains(e.Name) ? ExecMode : FileMode,
                ModificationTime = mtime,
            };
            using var data = File.OpenRead(e.Path);
            entry.DataStream = data;
            tar.WriteEntry(entry);
        }
    }
    else
    {
        using var stream = File.Create(zipPath!);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: false);
        foreach (var e in entries.Where(e => !e.IsDir))
        {
            var entry = zip.CreateEntry($"{top}/{e.Name}", CompressionLevel.SmallestSize);
            entry.LastWriteTime = mtime;
            // Set, not left to the platform's default, so the same tree gives the same bytes.
            entry.ExternalAttributes = (int)(exec.Contains(e.Name) ? 0x81ED0000 : 0x81A40000);
            using var target = entry.Open();
            using var data = File.OpenRead(e.Path);
            data.CopyTo(target);
        }
    }
    Console.WriteLine($"packed {entries.Count(e => !e.IsDir)} files into {tarPath ?? zipPath}");
    return 0;

    static UstarTarEntry DirEntry(string name, DateTimeOffset mtime) => new(TarEntryType.Directory, name)
    {
        Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
             | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute,
        ModificationTime = mtime,
    };
}

static int Licenses(Options o)
{
    var packages = Path.GetFullPath(o.One("packages"));
    var texts = Path.GetFullPath(o.One("texts"));
    var outDir = Path.GetFullPath(o.One("out"));
    var sdk = o.Optional("sdk");

    // What the publishes deploy, from their own dependency files: a package that
    // is in the restore graph but not deployed needs no text here.
    var deployed = new SortedDictionary<string, (string Id, string Version)>(StringComparer.Ordinal);
    var runtimePacks = new SortedDictionary<string, (string Id, string Version)>(StringComparer.Ordinal);
    var depsFiles = 0;
    foreach (var folder in o.All("publish"))
        foreach (var file in System.IO.Directory.EnumerateFiles(folder, "*.deps.json"))
        {
            depsFiles++;
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            foreach (var library in doc.RootElement.GetProperty("libraries").EnumerateObject())
            {
                var type = library.Value.GetProperty("type").GetString();
                var slash = library.Name.LastIndexOf('/');
                var (id, version) = (library.Name[..slash], library.Name[(slash + 1)..]);
                if (type == "package") deployed[library.Name] = (id, version);
                else if (type == "runtimepack") runtimePacks[library.Name] = (id["runtimepack.".Length..], version);
            }
        }
    if (depsFiles == 0) throw new ToolException("no *.deps.json in any --publish folder; there is nothing to read the packages from");

    var index = ReadIndex(texts);
    var missing = new List<string>();
    var table = new List<string>();
    var files = new SortedDictionary<string, byte[]>(StringComparer.Ordinal); // relative path in licenses/ -> bytes

    string Store(string prefix, string name, byte[] bytes)
    {
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()[..12];
        var relative = $"{prefix}/{hash}-{name}";
        files[relative] = bytes;
        return relative;
    }

    // Runtime packs first: the MIT text they carry covers the packages the .NET
    // repository builds.
    string? dotnetLicense = null;
    foreach (var (_, pack) in runtimePacks)
    {
        var dir = Path.Combine(packages, pack.Id.ToLowerInvariant(), pack.Version.ToLowerInvariant());
        var found = new List<string>();
        // The license and the notices, found by the name each file has on disk,
        // whatever its case: a case-blind file system would find a file under the
        // name asked for, and the member's name would then depend on the host
        // (LICENSE.TXT on Windows, LICENSE.txt on Linux, for one pack).
        var onDisk = Directory.Exists(dir) ? Directory.EnumerateFiles(dir).Select(f => Path.GetFileName(f)).Order(StringComparer.Ordinal).ToArray() : [];
        foreach (var wanted in new[] { "LICENSE.TXT", "THIRD-PARTY-NOTICES.TXT" })
        {
            var name = onDisk.FirstOrDefault(f => string.Equals(f, wanted, StringComparison.OrdinalIgnoreCase));
            if (name is not null) found.Add(Store("runtime", $"{pack.Id}-{pack.Version}-{name}", File.ReadAllBytes(Path.Combine(dir, name))));
        }
        if (!found.Any(f => f.Contains("LICENSE", StringComparison.OrdinalIgnoreCase)) || !found.Any(f => f.Contains("THIRD-PARTY", StringComparison.OrdinalIgnoreCase)))
        {
            missing.Add($"runtime pack {pack.Id} {pack.Version}: LICENSE.TXT or THIRD-PARTY-NOTICES.TXT not found in {dir}");
            continue;
        }
        table.Add($"runtime pack {pack.Id} {pack.Version} | MIT | {string.Join(", ", found)}");
        if (pack.Id.StartsWith("Microsoft.NETCore.App.Runtime.", StringComparison.Ordinal))
        {
            dotnetLicense = found.First(f => f.Contains("LICENSE", StringComparison.OrdinalIgnoreCase));
            var text = Encoding.UTF8.GetString(files[dotnetLicense]);
            if (!text.Contains("MIT License", StringComparison.OrdinalIgnoreCase))
                missing.Add($"runtime pack {pack.Id} {pack.Version}: its license text is not the MIT License; the .NET packages cannot be covered by it");
        }
    }

    var coveredByRuntime = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "https://github.com/dotnet/dotnet", "https://github.com/dotnet/runtime", "https://github.com/dotnet/aspnetcore",
    };
    var licenseName = new Regex(@"^(licen[cs]e|copying)(\..+)?$", RegexOptions.IgnoreCase);
    var noticeName = new Regex(@"^(notice|third[-_ ]?party[-_ ]?notices?)(\..+)?$", RegexOptions.IgnoreCase);

    foreach (var (_, package) in deployed)
    {
        var (id, version) = package;
        var dir = Path.Combine(packages, id.ToLowerInvariant(), version.ToLowerInvariant());
        if (!System.IO.Directory.Exists(dir)) { missing.Add($"{id} {version}: not in the packages folder {packages}"); continue; }
        var nuspec = System.IO.Directory.GetFiles(dir, "*.nuspec").SingleOrDefault();
        if (nuspec is null) { missing.Add($"{id} {version}: no nuspec in {dir}"); continue; }
        var meta = XDocument.Load(nuspec).Descendants().First(e => e.Name.LocalName == "metadata");
        XElement? Child(string name) => meta.Elements().FirstOrDefault(e => e.Name.LocalName == name);
        var license = Child("license");
        var licenseType = license?.Attribute("type")?.Value;
        var expression = licenseType == "expression" ? license!.Value.Trim() : licenseType == "file" ? "see the file" : "none declared";
        var repository = Child("repository")?.Attribute("url")?.Value?.Trim().TrimEnd('/');
        if (repository is not null && repository.EndsWith(".git", StringComparison.Ordinal)) repository = repository[..^4];

        var stored = new List<string>();
        // 1. The package's own license file: the one the nuspec names, else a
        //    LICENSE, LICENCE or COPYING at the package root.
        var own = new List<string>();
        if (licenseType == "file")
        {
            var named = Path.Combine(dir, license!.Value.Trim().Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(named)) own.Add(named);
        }
        else
            own.AddRange(System.IO.Directory.GetFiles(dir).Where(f => licenseName.IsMatch(Path.GetFileName(f))));
        foreach (var f in own) stored.Add(Store("texts", Path.GetFileName(f), File.ReadAllBytes(f)));

        // 2. The committed text, for a package that ships none.
        if (own.Count == 0 && index.TryGetValue(id.ToLowerInvariant(), out var committed))
        {
            var path = Path.Combine(texts, committed);
            if (!File.Exists(path)) { missing.Add($"{id} {version}: {committed} is named in {Path.Combine(texts, "index.txt")} and is not there"); continue; }
            stored.Add(Store("texts", $"{id}-LICENSE.txt", File.ReadAllBytes(path)));
        }
        // 3. Built by the .NET repository under the MIT License: the runtime
        //    pack's text, stored once.
        else if (own.Count == 0 && expression == "MIT" && repository is not null && coveredByRuntime.Contains(repository) && dotnetLicense is not null)
            stored.Add(dotnetLicense);

        if (stored.Count == 0)
        {
            missing.Add($"{id} {version} ({expression}, {repository ?? "no repository named"}): no license text in the package, none in {texts}");
            continue;
        }
        // The package's notices file, stored once per distinct content.
        foreach (var f in System.IO.Directory.GetFiles(dir).Where(f => noticeName.IsMatch(Path.GetFileName(f))))
            stored.Add(Store("notices", Path.GetFileName(f), File.ReadAllBytes(f)));
        table.Add($"{id} {version} | {expression} | {string.Join(", ", stored.Distinct())}");
    }

    if (missing.Count > 0)
    {
        Console.Error.WriteLine("license texts NOT found; the archive is refused:");
        foreach (var m in missing) Console.Error.WriteLine($"  - {m}");
        return 1;
    }

    if (System.IO.Directory.Exists(outDir)) System.IO.Directory.Delete(outDir, recursive: true);
    System.IO.Directory.CreateDirectory(outDir);
    foreach (var (relative, bytes) in files)
    {
        var target = Path.Combine(outDir, relative.Replace('/', Path.DirectorySeparatorChar));
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.WriteAllBytes(target, bytes);
    }
    // The runtime first: it is what a security patch changes, and the SDK decides it.
    var runtime = runtimePacks.Count == 0 ? Array.Empty<string>()
        : new[]
        {
            "The .NET runtime this build carries: " + string.Join(", ", runtimePacks.Values.Select(p => $"{p.Id} {p.Version}"))
                + (sdk is null ? "." : $", from the .NET SDK {sdk}, which global.json pins."),
            "",
        };
    var header = runtime.Concat(new[]
    {
        "The packages and runtime packs this build redistributes, and the license text that covers each.",
        "Paths are relative to this folder. A package built by the .NET repository under the MIT License",
        "is covered by the .NET runtime pack's license text; a package that ships no license file is",
        "covered by the text taken from its own upstream repository (see the provenance files in the source).",
        "",
    });
    File.WriteAllText(Path.Combine(outDir, "PACKAGES.txt"), string.Join('\n', header.Concat(table)) + "\n", new UTF8Encoding(false));
    if (runtime.Length > 0) Console.WriteLine(runtime[0]);
    Console.WriteLine($"licenses: {deployed.Count} packages and {runtimePacks.Count} runtime pack(s) from {depsFiles} dependency file(s), {files.Count} text file(s)");
    foreach (var row in table) Console.WriteLine("  " + row);
    return 0;
}

static int Extension(Options o)
{
    var built = Path.GetFullPath(o.One("built"));
    var outDir = Path.GetFullPath(o.One("out"));
    var manifestPath = Path.Combine(built, "extension.json");
    if (!File.Exists(manifestPath)) throw new ToolException($"{built} has no extension.json");

    string? assembly, assemblyHash;
    var listed = new List<(string File, string? Sha256)>();
    try
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath));
        var root = doc.RootElement;
        assembly = root.TryGetProperty("assemblyFile", out var a) ? a.GetString() : null;
        assemblyHash = root.TryGetProperty("sha256", out var h) ? h.GetString() : null;
        if (root.TryGetProperty("files", out var files))
            foreach (var f in files.EnumerateArray())
                listed.Add((f.GetProperty("file").GetString() ?? "", f.TryGetProperty("sha256", out var fh) ? fh.GetString() : null));
    }
    catch (Exception e) when (e is JsonException or InvalidOperationException or KeyNotFoundException)
    {
        throw new ToolException($"{manifestPath} cannot be read: {e.Message}");
    }
    if (string.IsNullOrEmpty(assembly)) throw new ToolException($"{manifestPath} names no assemblyFile");

    // The manifest's own hash for each file it names; the host checks the same.
    var ship = new List<(string File, string? Sha256)> { (assembly, assemblyHash) };
    ship.AddRange(listed);
    var problems = new List<string>();
    foreach (var (file, expected) in ship)
    {
        var relative = file.Replace('\\', '/');
        if (relative.Length == 0 || Path.IsPathRooted(relative) || relative.Split('/').Contains(".."))
        {
            problems.Add($"'{file}' is not a path inside the extension's folder");
            continue;
        }
        var path = Path.Combine(built, relative);
        if (!File.Exists(path)) { problems.Add($"{relative} is listed in extension.json and is not in {built}"); continue; }
        var actual = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            problems.Add($"{relative} hashes to {actual} and extension.json says {expected ?? "nothing"}");
    }
    if (problems.Count > 0)
        throw new ToolException($"the extension in {built} cannot ship: " + string.Join("; ", problems));

    if (Directory.Exists(outDir)) Directory.Delete(outDir, recursive: true);
    Directory.CreateDirectory(outDir);
    foreach (var (file, _) in ship.Append(("extension.json", null)))
    {
        var target = Path.Combine(outDir, file.Replace('\\', '/'));
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Copy(Path.Combine(built, file.Replace('\\', '/')), target);
    }
    var left = Directory.EnumerateFiles(built, "*", SearchOption.AllDirectories).Count() - ship.Count - 1;
    Console.WriteLine($"extension: {Path.GetFileName(outDir)}, {ship.Count + 1} file(s) shipped, {left} other file(s) in the build folder left out");
    return 0;
}

static int Merge(Options o)
{
    var into = Path.GetFullPath(o.One("into"));
    var sources = o.All("from").Select(Path.GetFullPath).ToList();
    if (sources.Count < 2) throw new ToolException("merge needs two or more --from folders");
    foreach (var source in sources)
        if (!Directory.Exists(source)) throw new ToolException($"{source} is not a folder");
    if (Directory.Exists(into) && Directory.EnumerateFileSystemEntries(into).Any())
        throw new ToolException($"{into} is not empty; merge writes into a new folder only");

    // What each publish's own deps.json records for the files it deploys.
    var recorded = sources.ToDictionary(s => s, s => RecordedVersions(s), StringComparer.Ordinal);
    string Name(string source) => Path.GetFileName(source);

    var kept = new SortedDictionary<string, (string Source, string Hash)>(StringComparer.Ordinal);
    var alike = 0;
    var decided = new List<string>();
    var problems = new List<string>();
    foreach (var source in sources)
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            var relative = Path.GetRelativePath(source, file).Replace('\\', '/');
            var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)));
            if (!kept.TryGetValue(relative, out var held)) { kept[relative] = (source, hash); continue; }
            if (held.Hash == hash) { alike++; continue; }

            var heldVersion = recorded[held.Source].GetValueOrDefault(relative);
            var newVersion = recorded[source].GetValueOrDefault(relative);
            if (heldVersion?.File is null || newVersion?.File is null)
            {
                problems.Add($"{relative}: {Name(held.Source)} and {Name(source)} carry different copies, and " +
                             $"{Name(heldVersion?.File is null ? held.Source : source)}'s deps.json gives it no file version");
                continue;
            }
            // 0.0.0.0 orders nothing: a build on a Linux host records every Windows
            // native file as 0.0.0.0, whatever its real version. Two differing copies
            // cannot be decided on it, and are refused as a copy with no version is.
            var none = new Version(0, 0, 0, 0);
            if (heldVersion.File == none || newVersion.File == none)
            {
                problems.Add($"{relative}: {Name(held.Source)} and {Name(source)} carry different copies, and " +
                             $"{Name(heldVersion.File == none ? held.Source : source)}'s deps.json records its file version as 0.0.0.0, which orders nothing");
                continue;
            }
            var order = heldVersion.CompareTo(newVersion);
            if (order == 0)
            {
                problems.Add($"{relative}: {Name(held.Source)} and {Name(source)} carry different copies of the same version, {heldVersion}");
                continue;
            }
            var (winner, loser, winning, losing) = order > 0 ? (held.Source, source, heldVersion, newVersion) : (source, held.Source, newVersion, heldVersion);
            if (order < 0) kept[relative] = (source, hash);
            decided.Add($"{relative}: {Name(winner)}'s {winning} over {Name(loser)}'s {losing}");
        }
    if (problems.Count > 0)
        throw new ToolException("the publishes cannot share one folder:\n  - " + string.Join("\n  - ", problems));

    Directory.CreateDirectory(into);
    foreach (var (relative, (source, _)) in kept)
    {
        var target = Path.Combine(into, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Copy(Path.Combine(source, relative), target);
    }

    // Every file a program's deps.json names is in the one folder, or its host
    // would refuse to start.
    var absent = sources.SelectMany(s => recorded[s].Keys.Where(f => !File.Exists(Path.Combine(into, f))).Select(f => $"{Name(s)} needs {f}")).ToList();
    if (absent.Count > 0) throw new ToolException("the merged folder lacks files a program needs: " + string.Join("; ", absent));

    Console.WriteLine($"merge: {kept.Count} files from {sources.Count} publishes into {into}; {alike} carried alike and kept once, " +
                      $"{decided.Count} different copies decided by file version");
    foreach (var line in decided) Console.WriteLine("  " + line);
    return 0;
}

// The file version a publish's deps.json records for each runtime, native and
// resource file it deploys, keyed by where the file sits in the publish folder,
// with whether it came from a runtime pack; the library's own version breaks a
// tie between two package copies of one file version.
static int VcRuntime(Options o)
{
    var package = Path.GetFullPath(o.One("package"));
    var pinsPath = Path.GetFullPath(o.One("pins"));
    var outDir = Path.GetFullPath(o.One("out"));

    // The pins, from the download script's name="value" lines, as build.sh reads the model's.
    if (!File.Exists(pinsPath)) throw new ToolException($"{pinsPath} does not exist");
    var pins = new Dictionary<string, string>(StringComparer.Ordinal);
    foreach (var line in File.ReadAllLines(pinsPath))
    {
        var m = Regex.Match(line.TrimEnd('\r'), "^([a-z0-9_]+)=\"(.*)\"$");
        if (m.Success) pins[m.Groups[1].Value] = m.Groups[2].Value;
    }
    string Pin(string name) =>
        pins.TryGetValue(name, out var value) && value.Length > 0 ? value : throw new ToolException($"{pinsPath} pins no {name}");

    // Exactly these four, which ONNX Runtime's Windows library imports; the cabinet's
    // other members (concrt140, msvcp140_2, vcomp140 and the rest) are never written.
    string[] ship = ["msvcp140.dll", "msvcp140_1.dll", "vcruntime140.dll", "vcruntime140_1.dll"];
    static string PinOf(string file) => Path.GetFileNameWithoutExtension(file) + "_sha256";
    var pinnedFiles = pins.Keys.Where(k => k.EndsWith("_sha256", StringComparison.Ordinal) && k != "vc_redist_sha256")
        .Order(StringComparer.Ordinal).ToArray();
    var shippedPins = ship.Select(PinOf).Order(StringComparer.Ordinal).ToArray();
    if (!pinnedFiles.SequenceEqual(shippedPins))
        throw new ToolException($"{pinsPath} pins {string.Join(", ", pinnedFiles)} and this tool ships the files for {string.Join(", ", shippedPins)}; the two must name the same files");

    if (!File.Exists(package)) throw new ToolException($"{package} does not exist");
    var bytes = File.ReadAllBytes(package);
    var size = long.Parse(Pin("vc_redist_size"), CultureInfo.InvariantCulture);
    if (bytes.LongLength != size) throw new ToolException($"{package} is {bytes.LongLength} bytes and the pinned size is {size}");
    var packageHash = Sha256Hex(bytes);
    if (packageHash != Pin("vc_redist_sha256"))
        throw new ToolException($"{package} hashes to {packageHash} and the pinned SHA-256 is {Pin("vc_redist_sha256")}");

    // The bundle's manifest (the first container's member "0") names the payload that
    // is the x64 Minimum runtime's cabinet; the second container holds it.
    var (uxAt, attachedAt) = BurnContainers(bytes);
    const string minimumCabinet = @"packages\vcRuntimeMinimum_amd64\cab1.cab";
    XDocument manifest;
    // Loaded from the bytes, not a string: the manifest begins with a byte order mark.
    try { manifest = XDocument.Load(new MemoryStream(Cabinet.Open(bytes, uxAt).Extract("0"))); }
    catch (System.Xml.XmlException e) { throw new ToolException($"the bundle's manifest cannot be read: {e.Message}"); }
    var payloads = manifest.Descendants()
        .Where(e => e.Name.LocalName == "Payload" && (string?)e.Attribute("FilePath") == minimumCabinet).ToList();
    if (payloads.Count != 1) throw new ToolException($"the bundle's manifest names {payloads.Count} payloads at {minimumCabinet}, not one");
    var sourcePath = (string?)payloads[0].Attribute("SourcePath")
        ?? throw new ToolException($"the bundle's manifest gives {minimumCabinet} no SourcePath");
    var runtime = Cabinet.Open(Cabinet.Open(bytes, attachedAt).Extract(sourcePath), 0);

    var files = new List<(string Name, byte[] Data)>();
    foreach (var name in ship)
    {
        var data = runtime.Extract(name + "_amd64");
        var hash = Sha256Hex(data);
        if (hash != Pin(PinOf(name))) throw new ToolException($"{name} in the package hashes to {hash} and the pinned SHA-256 is {Pin(PinOf(name))}");
        files.Add((name, data));
    }

    if (Directory.Exists(outDir) && Directory.EnumerateFileSystemEntries(outDir).Any())
        throw new ToolException($"{outDir} is not empty; the runtime files are written into an empty folder only");
    Directory.CreateDirectory(outDir);
    // Into an empty folder, so it then holds these four files and nothing else.
    foreach (var (name, data) in files) File.WriteAllBytes(Path.Combine(outDir, name), data);
    Console.WriteLine($"vcruntime: {string.Join(", ", ship)} from the Visual C++ Redistributable {Pin("vc_redist_version")}, each matching its pin");
    return 0;
}

// A WiX bundle's own table of its containers, in the bootstrapper's .wixburn section:
// the first container (the setup program's user interface and the manifest) starts
// where the engine ends, and the second (the packages) after the engine's original
// signature, which sits between the two.
static (int First, int Second) BurnContainers(byte[] b)
{
    int U16(int at) => BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(at, 2));
    int I32(int at) => BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(at, 4));
    const string refusal = "the package is not a bundle laid out as this tool reads it";
    if (b.Length < 0x40) throw new ToolException(refusal);
    var pe = I32(0x3C);
    if (pe <= 0 || pe > b.Length - 24 || I32(pe) != 0x00004550) throw new ToolException($"{refusal}: it is not a Windows program");
    var table = pe + 24 + U16(pe + 20);
    int? burn = null;
    for (var i = 0; i < U16(pe + 6); i++)
    {
        var at = table + 40 * i;
        if (at + 40 > b.Length) throw new ToolException(refusal);
        if (Encoding.ASCII.GetString(b, at, 8).TrimEnd('\0') == ".wixburn") burn = I32(at + 20);
    }
    if (burn is not { } w || w < 0 || w + 56 > b.Length || I32(w) != 0x00f14300) throw new ToolException($"{refusal}: it has no .wixburn section");
    var (stub, signatureAt, signatureSize, count) = (I32(w + 24), I32(w + 32), I32(w + 36), I32(w + 44));
    if (count != 2) throw new ToolException($"{refusal}: it has {count} containers, not two");
    var (firstSize, secondSize) = (I32(w + 48), I32(w + 52));
    var second = signatureAt + signatureSize;
    if (signatureAt < stub + firstSize || signatureAt - (stub + firstSize) >= 8 || second > b.Length - secondSize
        || Cabinet.SizeAt(b, stub) != firstSize || Cabinet.SizeAt(b, second) != secondSize)
        throw new ToolException($"{refusal}: its containers are not where its .wixburn section puts them");
    return (stub, second);
}

static string Sha256Hex(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

static Dictionary<string, DeployedVersion> RecordedVersions(string publish)
{
    var deps = Directory.GetFiles(publish, "*.deps.json");
    if (deps.Length != 1) throw new ToolException($"{publish} has {deps.Length} deps.json files; a publish has one");
    using var doc = JsonDocument.Parse(File.ReadAllText(deps[0]));
    var root = doc.RootElement;
    var targetName = root.GetProperty("runtimeTarget").GetProperty("name").GetString()
        ?? throw new ToolException($"{deps[0]} names no runtime target");
    var versions = new Dictionary<string, DeployedVersion>(StringComparer.Ordinal);
    foreach (var library in root.GetProperty("targets").GetProperty(targetName).EnumerateObject())
    {
        var libraryVersion = ParseVersion(library.Name[(library.Name.LastIndexOf('/') + 1)..]);
        var fromRuntimePack = library.Name.StartsWith("runtimepack.", StringComparison.Ordinal);
        foreach (var kind in new[] { "runtime", "native", "resources" })
        {
            if (!library.Value.TryGetProperty(kind, out var assets)) continue;
            foreach (var asset in assets.EnumerateObject())
            {
                var file = Path.GetFileName(asset.Name);
                var where = kind == "resources" && asset.Value.TryGetProperty("locale", out var locale) ? $"{locale.GetString()}/{file}" : file;
                var fileVersion = asset.Value.TryGetProperty("fileVersion", out var fv) ? ParseVersion(fv.GetString()) : null;
                versions[where] = new DeployedVersion(fileVersion, libraryVersion, fromRuntimePack);
            }
        }
    }
    return versions;

    static Version? ParseVersion(string? text) =>
        Version.TryParse(text?.Split('-', '+')[0], out var version) ? version : null;
}

static Dictionary<string, string> ReadIndex(string texts)
{
    var path = Path.Combine(texts, "index.txt");
    var index = new Dictionary<string, string>(StringComparer.Ordinal);
    if (!File.Exists(path)) return index;
    foreach (var raw in File.ReadAllLines(path))
    {
        var line = raw.Trim();
        if (line.Length == 0 || line[0] == '#') continue;
        var parts = line.Split(' ', 2, StringSplitOptions.TrimEntries);
        if (parts.Length != 2) throw new ToolException($"{path}: '{line}' is not '<package id> <text file>'");
        index[parts[0].ToLowerInvariant()] = parts[1];
    }
    return index;
}

sealed class ToolException(string message) : Exception(message);

// A deployed file's version as its publish records it: the file version first;
// on a tie, a runtime pack's copy over a package's, as the SDK keeps the
// platform's copy of one version (the runtime pack's are precompiled, so the
// bytes differ where the version does not); then the version of the library it
// came from. A copy with no recorded file version cannot be ordered and is null,
// never zero.
sealed record DeployedVersion(Version? File, Version? Library, bool FromRuntimePack) : IComparable<DeployedVersion>
{
    public int CompareTo(DeployedVersion? other)
    {
        if (other is null) return 1;
        var byFile = Comparer<Version?>.Default.Compare(File, other.File);
        if (byFile != 0) return byFile;
        if (FromRuntimePack != other.FromRuntimePack) return FromRuntimePack ? 1 : -1;
        return Comparer<Version?>.Default.Compare(Library, other.Library);
    }

    public override string ToString() =>
        $"{File?.ToString() ?? "no file version"} ({(FromRuntimePack ? "runtime pack" : "library")} {Library?.ToString() ?? "unversioned"})";
}

sealed class Options
{
    private readonly Dictionary<string, List<string>> _values = new(StringComparer.Ordinal);

    public static Options Parse(IEnumerable<string> args)
    {
        var o = new Options();
        string? name = null;
        foreach (var a in args)
        {
            if (a.StartsWith("--", StringComparison.Ordinal)) { name = a[2..]; if (!o._values.ContainsKey(name)) o._values[name] = []; }
            else if (name is not null) o._values[name].Add(a);
            else throw new ToolException($"'{a}' is not an option");
        }
        return o;
    }

    public IReadOnlyList<string> All(string name) => _values.TryGetValue(name, out var v) ? v : [];
    public string? Optional(string name) => All(name) is [var only] ? only : All(name).Count == 0 ? null : throw new ToolException($"--{name} takes one value");
    public string One(string name) => Optional(name) ?? throw new ToolException($"--{name} is required");
}

// A Microsoft cabinet (MS-CAB) read from memory: one whole cabinet, its folders
// stored or MSZIP, members looked up by name.
sealed class Cabinet
{
    private readonly byte[] _b;
    private readonly int _at;
    private readonly int _dataReserve;
    private readonly List<(int Start, int Blocks, int Compression)> _folders = [];
    private readonly Dictionary<string, (int Size, int Offset, int Folder)> _members = new(StringComparer.Ordinal);
    private readonly Dictionary<int, byte[]> _decoded = [];

    private static int U16(ReadOnlySpan<byte> s) => BinaryPrimitives.ReadUInt16LittleEndian(s);
    private static int I32(ReadOnlySpan<byte> s) => BinaryPrimitives.ReadInt32LittleEndian(s);

    // The size a cabinet at this offset gives itself, or -1 where none begins.
    public static int SizeAt(byte[] b, int at) =>
        at >= 0 && at <= b.Length - 12 && b.AsSpan(at, 4).SequenceEqual("MSCF"u8) ? I32(b.AsSpan(at + 8)) : -1;

    public static Cabinet Open(byte[] b, int at) => new(b, at);

    private Cabinet(byte[] b, int at)
    {
        var size = SizeAt(b, at);
        if (size < 36 || at + size > b.Length) throw new ToolException($"there is no whole cabinet at offset {at}");
        (_b, _at) = (b, at);
        var s = b.AsSpan(at, size);
        var (membersAt, folders, members, flags) = (I32(s[16..]), U16(s[26..]), U16(s[28..]), U16(s[30..]));
        if ((flags & ~4) != 0) throw new ToolException($"the cabinet at offset {at} continues into another cabinet; this tool reads whole ones only");
        var p = 36;
        var folderReserve = 0;
        if ((flags & 4) != 0) { folderReserve = s[38]; _dataReserve = s[39]; p = 40 + U16(s[36..]); }
        for (var i = 0; i < folders; i++, p += 8 + folderReserve)
            _folders.Add((I32(s[p..]), U16(s[(p + 4)..]), U16(s[(p + 6)..]) & 0xF));
        p = membersAt;
        for (var i = 0; i < members; i++)
        {
            var (memberSize, offset, folder) = (I32(s[p..]), I32(s[(p + 4)..]), U16(s[(p + 8)..]));
            var length = s[(p + 16)..].IndexOf((byte)0);
            var name = Encoding.Latin1.GetString(s.Slice(p + 16, length));
            p += 16 + length + 1;
            if (folder >= _folders.Count) throw new ToolException($"{name} in the cabinet at offset {at} is in a folder the cabinet does not have");
            if (!_members.TryAdd(name, (memberSize, offset, folder))) throw new ToolException($"the cabinet at offset {at} names {name} twice");
        }
    }

    public byte[] Extract(string name)
    {
        if (!_members.TryGetValue(name, out var member)) throw new ToolException($"the cabinet at offset {_at} has no member {name}");
        if (!_decoded.TryGetValue(member.Folder, out var data)) _decoded[member.Folder] = data = Decode(_folders[member.Folder]);
        if (member.Offset < 0 || member.Size < 0 || (long)member.Offset + member.Size > data.Length)
            throw new ToolException($"{name} lies outside its folder in the cabinet at offset {_at}");
        return data.AsSpan(member.Offset, member.Size).ToArray();
    }

    // MSZIP: every block is a deflate stream that may refer back into the output of the
    // blocks before it, up to 32 KB. DeflateStream takes no preset dictionary, so each
    // block is inflated behind stored (uncompressed, not final) deflate blocks carrying
    // that history, and as many bytes are dropped from the front of what comes out.
    private byte[] Decode((int Start, int Blocks, int Compression) folder)
    {
        if (folder.Compression is not (0 or 1))
            throw new ToolException($"the cabinet at offset {_at} uses compression type {folder.Compression}; this tool reads stored and MSZIP folders only");
        var output = new MemoryStream();
        var p = _at + folder.Start;
        for (var i = 0; i < folder.Blocks; i++)
        {
            if (p + 8 + _dataReserve > _b.Length) throw new ToolException($"the cabinet at offset {_at} ends inside a block");
            var (compressed, uncompressed) = (U16(_b.AsSpan(p + 4)), U16(_b.AsSpan(p + 6)));
            p += 8 + _dataReserve;
            if (p + compressed > _b.Length) throw new ToolException($"the cabinet at offset {_at} ends inside a block");
            var data = _b.AsSpan(p, compressed);
            p += compressed;
            if (folder.Compression == 0) { output.Write(data); continue; }
            if (compressed < 2 || data[0] != (byte)'C' || data[1] != (byte)'K')
                throw new ToolException($"block {i} of the cabinet at offset {_at} is not an MSZIP block");
            var written = output.GetBuffer().AsSpan(0, (int)output.Length);
            var history = written[Math.Max(0, written.Length - 32768)..];
            var input = new MemoryStream();
            for (var h = 0; h < history.Length; h += 65535)
            {
                var n = Math.Min(65535, history.Length - h);
                input.WriteByte(0);
                input.Write([(byte)n, (byte)(n >> 8), (byte)~n, (byte)(~n >> 8)]);
                input.Write(history.Slice(h, n));
            }
            input.Write(data[2..]);
            input.Position = 0;
            var block = new byte[history.Length + uncompressed];
            try
            {
                using var inflate = new DeflateStream(input, CompressionMode.Decompress);
                inflate.ReadExactly(block);
            }
            catch (Exception e) when (e is InvalidDataException or EndOfStreamException)
            {
                throw new ToolException($"block {i} of the cabinet at offset {_at} cannot be inflated: {e.Message}");
            }
            output.Write(block, history.Length, uncompressed);
        }
        return output.ToArray();
    }
}

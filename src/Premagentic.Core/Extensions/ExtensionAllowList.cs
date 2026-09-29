using System.Security.Cryptography;
using Premagentic.Core.Admin;
using Premagentic.Core.Identity;
using Premagentic.Core.Storage;

namespace Premagentic.Core.Extensions;

/// <summary>What the allow list holds now, and why a stored value could not be used.</summary>
/// <param name="Problem">
/// Set when what is stored cannot be read, in which case nothing is allowed.
/// </param>
public sealed record ExtensionAllowListReading(IReadOnlyList<(string Name, string Sha256)> Allowed, string? Problem);

/// <param name="Sha256">The hash that was allowed (<see cref="ExtensionManifest.ExtensionSha256"/>).</param>
/// <param name="Changed">False when the list already said this and nothing was written or recorded.</param>
/// <param name="AssemblySha256">The assembly's own hash, as measured.</param>
/// <param name="Files">Every other file the manifest lists, with the hash measured for it.</param>
public sealed record ExtensionAllowChange(string Name, string Sha256, bool Changed,
    string AssemblySha256, IReadOnlyList<ExtensionFile> Files);

/// <summary>What <see cref="ExtensionAllowList.Measure"/> found in an extension's folder.</summary>
/// <param name="Sha256">The hash an allow list entry holds for it.</param>
public sealed record ExtensionMeasurement(string Name, string Sha256, string AssemblyFile, string AssemblySha256,
    IReadOnlyList<ExtensionFile> Files);

/// <param name="Removed">Every pair that was taken out; empty when the name was not in the list.</param>
public sealed record ExtensionDisallowChange(string Name, IReadOnlyList<(string Name, string Sha256)> Removed)
{
    public bool Changed => Removed.Count > 0;
}

/// <summary>
/// The administrator's list of what may load: pairs of extension name and
/// extension hash (<see cref="ExtensionManifest.ExtensionSha256"/>), in the
/// setting <see cref="ExtensionSettings.Allowed"/>.
/// <para>
/// A change and its entry in the change record commit together, under the
/// tenant's administrator-change lock, as a tuning setting's does. Allowing
/// something already allowed writes nothing and records nothing.
/// </para>
/// <para>
/// Allowing reads the folder and measures the assembly and every listed file
/// itself. An administrator therefore allows what is on disk, not what a
/// manifest says is on disk, and a manifest whose hashes disagree with its own
/// files is refused here rather than becoming an allow list entry that can
/// never match.
/// </para>
/// </summary>
public sealed class ExtensionAllowList(PremagenticDatabase db, Guid tenantId, TimeProvider? time = null)
{
    /// <summary>The kind the change record gives a stored value, the same as a tuning setting's.</summary>
    public const string ChangeKind = Okf.TrustSettingsStore.ChangeKind;

    public async Task<ExtensionAllowListReading> ReadAsync(CancellationToken ct = default)
    {
        var stored = await new SettingsStore(db, tenantId).GetAsync(ExtensionSettings.Allowed, ct);
        var allowed = ExtensionSettings.AllowedFrom(stored, out var problem);
        return new ExtensionAllowListReading(allowed, problem);
    }

    /// <summary>
    /// Allows the extension in <paramref name="folder"/>: its manifest's name
    /// with the hash of the assembly beside it, and of every other file its
    /// manifest lists, measured now.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// There is no usable manifest, the assembly or a listed file cannot be
    /// read, one of them does not hash to what the manifest claims, or the
    /// folder carries a file under <c>runtimes/</c> the host would refuse: a
    /// managed library built for one platform, or a file the manifest does not
    /// list. Nothing is written.
    /// </exception>
    public async Task<ExtensionAllowChange> AllowAsync(string folder, AdminActor actor, CancellationToken ct = default)
    {
        var measured = Measure(folder);
        var (name, sha256) = (measured.Name, measured.Sha256);

        return await new AdminChanges(db, tenantId, time).RunAsync(actor, async change =>
        {
            var store = new SettingsStore(db, tenantId, change.Transaction);
            var before = await store.GetAsync(ExtensionSettings.Allowed, ct);
            var allowed = ExtensionSettings.AllowedFrom(before, out _);

            if (allowed.Any(a => a.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && a.Sha256 == sha256))
                return new ExtensionAllowChange(name, sha256, false, measured.AssemblySha256, measured.Files);

            var after = ExtensionSettings.ToStored([.. allowed, (name, sha256)]);
            await store.SetAsync(ExtensionSettings.Allowed, after, ct);
            // The old and the new list, both of which hold extension hashes.
            // A hash is not a secret: it is the whole of what an administrator
            // decided, and an auditor asking what was allowed and when has
            // nowhere else to read it.
            change.Record(ChangeKind, ExtensionSettings.Allowed, before, after);
            return new ExtensionAllowChange(name, sha256, true, measured.AssemblySha256, measured.Files);
        }, ct);
    }

    /// <summary>
    /// Takes every entry named <paramref name="name"/> out of the list, so an
    /// extension of that name stops loading when the service is next started.
    /// Nothing on disk is touched.
    /// </summary>
    public async Task<ExtensionDisallowChange> DisallowAsync(string name, AdminActor actor, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        return await new AdminChanges(db, tenantId, time).RunAsync(actor, async change =>
        {
            var store = new SettingsStore(db, tenantId, change.Transaction);
            var before = await store.GetAsync(ExtensionSettings.Allowed, ct);
            var allowed = ExtensionSettings.AllowedFrom(before, out _);

            var removed = allowed.Where(a => a.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (removed.Length == 0) return new ExtensionDisallowChange(name, removed);

            var after = ExtensionSettings.ToStored(allowed.Except(removed));
            await store.SetAsync(ExtensionSettings.Allowed, after, ct);
            change.Record(ChangeKind, ExtensionSettings.Allowed, before, after);
            return new ExtensionDisallowChange(name, removed);
        }, ct);
    }

    /// <summary>
    /// The name and the measured hashes of the extension in
    /// <paramref name="folder"/>: its assembly's, each listed file's, and the
    /// one an allow list entry holds, which covers all of them.
    /// </summary>
    /// <exception cref="ArgumentException">The folder does not hold an extension that could ever load.</exception>
    public static ExtensionMeasurement Measure(string folder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        if (!Directory.Exists(folder))
            throw new ArgumentException($"There is no folder '{folder}'.");
        if (!ExtensionManifest.TryRead(folder, out var manifest, out var problem))
            throw new ArgumentException($"'{folder}' does not hold an extension: {problem}");

        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(Path.Combine(folder, manifest!.AssemblyFile));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            throw new ArgumentException(
                $"'{folder}' names \"{manifest!.AssemblyFile}\", which could not be read: {ex.Message}");
        }

        var sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes));
        if (!sha256.Equals(manifest.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException(
                $"\"{manifest.AssemblyFile}\" hashes to {sha256} and its manifest says {manifest.Sha256}. " +
                "Allowing it would allow a pair that can never match. Write the manifest for the assembly that is there.");

        var files = new List<ExtensionFile>();
        foreach (var listed in manifest.Files)
        {
            byte[] fileBytes;
            try
            {
                fileBytes = File.ReadAllBytes(Path.Combine([folder, .. listed.File.Split('/')]));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                throw new ArgumentException($"'{folder}' lists \"{listed.File}\", which could not be read: {ex.Message}");
            }
            var fileSha256 = Convert.ToHexStringLower(SHA256.HashData(fileBytes));
            if (!fileSha256.Equals(listed.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException(
                    $"\"{listed.File}\" hashes to {fileSha256} and its manifest says {listed.Sha256}. " +
                    "Allowing it would allow an extension that can never load. Write the manifest for the files that are there.");
            files.Add(new ExtensionFile(listed.File, fileSha256));
        }

        // The host looks at every file under runtimes/ when it starts: a
        // managed library built for one platform refuses the extension, listed
        // or not, and so does any other file the manifest does not list. Each
        // is refused here first, where the administrator is, in the host's
        // words and in the host's order.
        var runtimes = Path.Combine(folder, "runtimes");
        if (Directory.Exists(runtimes))
            foreach (var file in Directory.EnumerateFiles(runtimes, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
            {
                var relative = Path.GetRelativePath(folder, file).Replace('\\', '/');
                if (ExtensionManifest.IsPlatformManagedPath(relative))
                    throw new ArgumentException(ExtensionManifest.PlatformManagedRefusal(relative));
                if (!files.Any(f => f.File.Equals(relative, StringComparison.OrdinalIgnoreCase)))
                    throw new ArgumentException(ExtensionManifest.UnlistedRuntimeRefusal(relative));
            }

        return new ExtensionMeasurement(manifest.Name, ExtensionManifest.ExtensionSha256(manifest.AssemblyFile, sha256, files),
            manifest.AssemblyFile, sha256, files);
    }
}

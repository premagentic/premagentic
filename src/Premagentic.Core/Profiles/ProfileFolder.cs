using System.Text.Json;
using Premagentic.Core.Sources.Registry;

namespace Premagentic.Core.Profiles;

/// <summary>
/// Something wrong with a profile: which file it is in, and one plain sentence
/// an operator can act on. Every check collects these rather than throwing, so
/// one run says everything that is wrong instead of the first thing.
/// </summary>
public sealed record ProfileProblem(string File, string Problem)
{
    public override string ToString() => $"{File}: {Problem}";
}

/// <summary>What <c>profile.json</c> says about the profile itself.</summary>
/// <param name="Seams">
/// The seam versions the profile was written against, by seam name. A version
/// ABOVE what this build has is refused: the profile expects something that is
/// not here. Below is accepted, because a seam only ever gains.
/// </param>
public sealed record ProfileManifest(
    string? Name,
    string? Version,
    string? Description,
    IReadOnlyDictionary<string, int>? Seams);

/// <summary>
/// One source as a profile states it, with the folder rule that decides who may
/// read it.
/// </summary>
/// <param name="Rule">
/// The rule entries, in order, as an administrator would write them:
/// <c>allow group:Staff</c>, <c>deny user:bob</c>, <c>allow everyone</c>. Order
/// is part of the rule. Null leaves whatever rule is already there, which is
/// not the same as an empty list: an empty list denies everyone.
/// </param>
/// <param name="Owner">
/// The sign-in name of the person answerable for these documents, or null to
/// leave it. It decides nothing: who may read them is the rule.
/// </param>
/// <param name="Hosted">
/// Whether these documents may be served to an agent whose model runs outside
/// the network. Null leaves a registered source as it is, and means "never
/// leaves" for a source the profile is adding, which is the safe reading of
/// silence. It is a hold on the source's folder beside the rule, so it may be
/// set with or without a rule, and a rule the profile writes cannot undo it.
/// </param>
public sealed record ProfileSource(
    string? Name,
    string? Folder,
    string? Prefix,
    bool OkfBundle = false,
    bool UndeclaredAsMachine = false,
    string? Chunker = null,
    IReadOnlyList<string>? Rule = null,
    string? Owner = null,
    bool? Hosted = null);

/// <summary>
/// A profile as it was read off disk: what the files say, with nothing yet
/// checked against a running deployment.
/// </summary>
public sealed record ProfileFolder(
    string Path,
    ProfileManifest Manifest,
    IReadOnlyDictionary<string, JsonElement> Settings,
    IReadOnlyList<ProfileSource> Sources,
    string? GoldenSetFile,
    IReadOnlyList<string?> Groups)
{
    /// <summary>The name and version, for a message or a record.</summary>
    public string Describe() => $"{Manifest.Name} {Manifest.Version}";
}

/// <summary>
/// Reads a profile folder. A profile is plain files, so it is read the way any
/// file an administrator wrote is read: every problem is collected and returned,
/// and nothing is interpreted against a deployment here.
/// <para>
/// A file in the folder that this build does not know is a REFUSAL, not a
/// warning. A profile that quietly ignores a file is a profile that quietly
/// does not configure something, and the operator would find out from the
/// behavior of the deployment rather than from the tool.
/// </para>
/// </summary>
public static class ProfileReader
{
    public const string ManifestFile = "profile.json";
    public const string SettingsFile = "settings.json";
    public const string GroupsFile = "groups.json";
    public const string SourcesFile = "sources.json";
    public const string ToolsFile = "tools.json";
    public const string ExtensionsFile = "extensions.json";
    public const string GoldenSetFile = "golden-set.json";

    /// <summary>
    /// Every file name a profile may hold. A name here that this build cannot
    /// yet apply is refused by name when it is present, which is a different
    /// answer than "unknown file" and tells the operator the profile is for a
    /// later build rather than misspelled.
    /// </summary>
    public static IReadOnlyList<string> KnownFiles { get; } =
        [ManifestFile, SettingsFile, GroupsFile, SourcesFile, ToolsFile, ExtensionsFile, GoldenSetFile];

    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    /// <summary>
    /// Reads the folder, or says why it cannot be read. Returns false with at
    /// least one problem whenever the profile is null.
    /// </summary>
    public static bool TryRead(string folder, out ProfileFolder? profile, out IReadOnlyList<ProfileProblem> problems)
    {
        profile = null;
        var found = new List<ProfileProblem>();
        problems = found;

        if (!Directory.Exists(folder))
        {
            found.Add(new ProfileProblem(folder, "There is no folder there."));
            return false;
        }

        foreach (var file in Directory.EnumerateFiles(folder).Select(System.IO.Path.GetFileName).Order(StringComparer.OrdinalIgnoreCase))
            if (!KnownFiles.Contains(file!, StringComparer.OrdinalIgnoreCase))
                found.Add(new ProfileProblem(file!,
                    $"A profile holds only {string.Join(", ", KnownFiles)}. This build would not apply this file, so it refuses rather than ignore it."));

        var manifest = Read<ProfileManifest>(folder, ManifestFile, required: true, found);
        if (manifest is null)
        {
            if (found.Count == 0)
                found.Add(new ProfileProblem(ManifestFile, "A profile needs this file, with a name and a version."));
            return false;
        }

        if (string.IsNullOrWhiteSpace(manifest.Name))
            found.Add(new ProfileProblem(ManifestFile, "The profile has no name."));
        // The name becomes a file name, the golden set's copy, so it follows the
        // rule sources are named by, which leaves no room for a folder in it.
        else if (!SourceRegistry.IsName(manifest.Name))
            found.Add(new ProfileProblem(ManifestFile,
                $"The profile's name is up to 64 letters, digits, dots, hyphens and underscores, starting with a letter or digit, not '{manifest.Name}'."));
        if (string.IsNullOrWhiteSpace(manifest.Version))
            found.Add(new ProfileProblem(ManifestFile, "The profile has no version. Any text will do, as long as it changes when the profile does."));

        var settings = Read<Dictionary<string, JsonElement>>(folder, SettingsFile, required: false, found)
            ?? new Dictionary<string, JsonElement>();
        // The names of the groups the profile needs. A group is created when
        // no group of that name exists and left alone when one does, so
        // applying a profile twice makes nothing twice.
        var groups = Read<List<string?>>(folder, GroupsFile, required: false, found) ?? [];
        var sources = Read<List<ProfileSource>>(folder, SourcesFile, required: false, found) ?? [];

        // Held back until the settings they apply through exist. Named rather
        // than treated as unknown, so the operator knows the profile is ahead
        // of the build and not misspelled.
        foreach (var file in new[] { ToolsFile, ExtensionsFile })
            if (File.Exists(System.IO.Path.Combine(folder, file)))
                found.Add(new ProfileProblem(file,
                    "This build has no setting to apply this file through yet, so applying the profile would leave it out. Remove it or use a build that has one."));

        var golden = System.IO.Path.Combine(folder, GoldenSetFile);

        if (found.Count > 0) return false;

        profile = new ProfileFolder(
            System.IO.Path.GetFullPath(folder), manifest,
            settings.ToDictionary(p => p.Key, p => p.Value.Clone(), StringComparer.Ordinal),
            sources,
            File.Exists(golden) ? golden : null,
            groups);
        return true;
    }

    private static T? Read<T>(string folder, string file, bool required, List<ProfileProblem> problems) where T : class
    {
        var path = System.IO.Path.Combine(folder, file);
        if (!File.Exists(path))
        {
            if (required) problems.Add(new ProfileProblem(file, "A profile needs this file, with a name and a version."));
            return null;
        }

        try
        {
            var read = JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options);
            if (read is null) problems.Add(new ProfileProblem(file, "The file holds JSON null."));
            return read;
        }
        catch (JsonException ex)
        {
            // The parser's own message names the line and position, which is
            // what an operator fixing the file needs.
            problems.Add(new ProfileProblem(file, $"The file is not JSON of the shape a profile expects. {ex.Message}"));
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            problems.Add(new ProfileProblem(file, $"The file cannot be read. {ex.Message}"));
            return null;
        }
    }
}

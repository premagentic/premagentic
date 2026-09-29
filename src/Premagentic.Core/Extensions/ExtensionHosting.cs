using System.Text.Json;
using Premagentic.Core.Identity;

namespace Premagentic.Core.Extensions;

/// <summary>
/// How a host builds its <see cref="ExtensionHost"/>: the folder and the allow
/// list as this deployment has them stored, with
/// <see cref="ExtensionSettings.FolderVariable"/> as the fallback for the
/// folder. The API, the CLI and the portal all come through here, so an
/// installation that adds a chunker adds it once and every one of them has it.
/// <para>
/// Read once, while a host starts. Nothing here reloads: what a deployment is
/// running cannot change under a request, and a change to either setting
/// applies when the service is next started, which is also when an assembly
/// could safely be replaced.
/// </para>
/// </summary>
public static class ExtensionHosting
{
    /// <param name="note">Told what loaded, as one sentence. Null tells nobody.</param>
    /// <param name="warn">
    /// Told of each setting that could not be used and each extension that was
    /// refused, as one sentence. A deployment that logs nothing else should log
    /// these: they are the difference between an extension that is off and one
    /// an administrator believes is on.
    /// </param>
    public static async Task<ExtensionHost> LoadAsync(
        SettingsStore settings, Action<string>? note = null, Action<string>? warn = null, CancellationToken ct = default)
    {
        var stored = await settings.GetManyAsync(ExtensionSettings.Keys, ct);

        var folder = ExtensionSettings.FolderFrom(Value(stored, ExtensionSettings.Folder),
            Environment.GetEnvironmentVariable(ExtensionSettings.FolderVariable), out var folderProblem);
        if (folderProblem is not null)
            warn?.Invoke($"The stored setting {ExtensionSettings.Folder} is not used. {folderProblem}");

        var allowed = ExtensionSettings.AllowedFrom(Value(stored, ExtensionSettings.Allowed), out var allowedProblem);
        if (allowedProblem is not null)
            warn?.Invoke(
                $"The stored setting {ExtensionSettings.Allowed} is not used, so no extension is allowed. {allowedProblem}");

        var host = ExtensionHost.Load(folder, allowed);

        foreach (var refused in host.Refused)
            warn?.Invoke($"The extension in {refused.Folder} was not loaded ({Reason(refused.Reason)}). {refused.Detail}");
        // A way of signing in that an extension brought is said out loud at
        // every start, as the trusted-header mode is. Whether a deployment can
        // be signed in to by something other than what shipped is not a detail
        // to find by reading a folder, and neither is that the groups it
        // reports mean nothing here for want of a mapper.
        if (SignInLine([.. host.SignInAdapters.Select(a => a.Name)], host.PrincipalMapper?.Name) is { } signIn)
            warn?.Invoke(signIn);
        // A mapper an administrator allowed is named where what loaded is
        // named: a note, which the API logs and the command line does not
        // repeat on every call.
        if (MapperLine(host.PrincipalMapper?.Name) is { } mapper)
            note?.Invoke(mapper);
        note?.Invoke(host.Folder is null
            ? "No extensions folder is set, so this deployment runs the built-in readers, chunkers and embedding providers."
            : $"Extensions in {host.Folder}: {host.Loaded.Count} loaded, {host.Refused.Count} refused." +
              (host.Loaded.Count == 0 ? "" : " " + string.Join(", ", host.Loaded.Select(l => $"{l.Name} {l.Version}"))));

        return host;
    }

    /// <summary>
    /// The start's warning about the ways of signing in extensions brought,
    /// or null when they brought none. With no principal mapper, the groups
    /// they report are ignored, and the warning says so rather than leaving an
    /// administrator to wonder why a directory's groups reach nothing.
    /// </summary>
    /// <param name="signInAdapters">The names of the ways of signing in the loaded extensions brought.</param>
    /// <param name="principalMapper">The loaded principal mapper's name, or null when there is none.</param>
    internal static string? SignInLine(IReadOnlyList<string> signInAdapters, string? principalMapper) =>
        signInAdapters.Count == 0
            ? null
            : "People can sign in to this deployment through an extension: " +
              $"{string.Join(", ", signInAdapters)}. " +
              "Each is asked after every built-in way, and reaches only accounts an administrator made." +
              (principalMapper is null ? " " + NoMapperSentence : "");

    /// <summary>The start's note naming the loaded principal mapper, or null when there is none.</summary>
    internal static string? MapperLine(string? principalMapper) =>
        principalMapper is null
            ? null
            : $"What groups and principals from outside mean here is decided by an extension, the principal mapper {principalMapper}. " +
              "It can map them only into live groups an administrator made.";

    /// <summary>The start's sentence for a deployment whose extensions sign people in and map nothing.</summary>
    internal const string NoMapperSentence =
        "No principal mapper is loaded, so any group they report means nothing here and is ignored.";

    /// <summary>The refusal as a person reads it, for a log line and the health page.</summary>
    public static string Reason(ExtensionRefusal reason) => reason switch
    {
        ExtensionRefusal.BadManifest => "bad manifest",
        ExtensionRefusal.HashMismatch => "hash mismatch",
        ExtensionRefusal.NotAllowed => "not allowed",
        ExtensionRefusal.SeamTooNew => "seam too new",
        ExtensionRefusal.DidNotLoad => "did not load",
        ExtensionRefusal.NameTaken => "name taken",
        ExtensionRefusal.CoreTooNew => "core too new",
        _ => reason.ToString(),
    };

    private static JsonElement? Value(IReadOnlyDictionary<string, JsonElement> stored, string key) =>
        stored.TryGetValue(key, out var value) ? value : null;
}

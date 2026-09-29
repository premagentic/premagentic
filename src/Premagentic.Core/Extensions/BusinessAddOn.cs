namespace Premagentic.Core.Extensions;

/// <summary>
/// What the business add-on brings, by name only, so a deployment without it
/// answers a person who asks for one of its commands, settings or pages with
/// one sentence naming the add-on instead of a bare "unknown". Nothing here
/// runs any of it: the add-on is an extension like any other, allowed by hash
/// and loaded by the host, and while it is loaded its own commands answer.
/// </summary>
public static class BusinessAddOn
{
    /// <summary>The name the add-on's manifest carries, which is the name an administrator allows.</summary>
    public const string ExtensionName = "premagentic-business";

    /// <summary>The add-on as a sentence names it.</summary>
    public const string DisplayName = "the PremAgentic business add-on";

    /// <summary>The commands of its own the add-on brings.</summary>
    public static IReadOnlyList<string> Nouns { get; } = ["audit"];

    /// <summary>
    /// The subcommands the add-on brings, as noun and verb: its own under
    /// <c>audit</c>, and directory group mapping under <c>groups</c>, a
    /// built-in command it adds subcommands to.
    /// </summary>
    public static IReadOnlyList<(string Noun, string Verb)> Verbs { get; } =
        [("audit", "prune"), ("audit", "export"), ("groups", "map"), ("groups", "unmap"), ("groups", "mappings")];

    /// <summary>The settings the add-on defines.</summary>
    public static IReadOnlyList<string> Settings { get; } = ["audit.retention_days"];

    /// <summary>The portal addresses the add-on brings, as a person or a link types them: the audit trail's export.</summary>
    public static IReadOnlyList<string> Pages { get; } = ["/portal/export/audit.jsonl"];

    /// <summary>
    /// The sentence for a <c>prem</c> call that names one of the add-on's
    /// commands, or null for any other call. <c>prem help noun</c> counts as
    /// naming the noun.
    /// </summary>
    /// <param name="host">The loaded extensions, or null where no database is configured and none could load.</param>
    public static string? Refusal(IReadOnlyList<string> args, ExtensionHost? host)
    {
        ArgumentNullException.ThrowIfNull(args);
        var words = args.Count > 0 && args[0] == "help" ? args.Skip(1).ToArray() : args.ToArray();
        if (words.Length == 0) return null;
        var noun = words[0];
        var verb = words.Length > 1 && !words[1].StartsWith('-') ? words[1] : null;
        if (verb is not null && Verbs.Contains((noun, verb))) return Sentence($"'prem {noun} {verb}'", host);
        return Nouns.Contains(noun) ? Sentence($"'prem {noun}'", host) : null;
    }

    /// <summary>The sentence for one of the add-on's settings, or null for any other key.</summary>
    public static string? SettingRefusal(string key, ExtensionHost? host) =>
        Settings.Contains(key) ? Sentence($"The setting {key}", host) : null;

    /// <summary>
    /// The sentence for one of the add-on's portal addresses, compared
    /// ignoring case as the portal's routes are, or null for any other path.
    /// With the add-on loaded, the command line gives the same rows, and the
    /// portal cannot show an add-on's page in this version, so that is what it
    /// says.
    /// </summary>
    public static string? PageRefusal(string path, ExtensionHost? host)
    {
        if (Pages.FirstOrDefault(p => string.Equals(path, p, StringComparison.OrdinalIgnoreCase)) is not { } page) return null;
        if (host is not null && host.Loaded.Any(l => l.Name.Equals(ExtensionName, StringComparison.OrdinalIgnoreCase)))
            return "The audit trail download is not offered in the portal in this version; 'prem audit export' gives the same rows.";
        return Sentence($"'{page}'", host);
    }

    /// <summary>
    /// One sentence naming the add-on, for whichever of four states this
    /// deployment is in: no database, so nothing could load; not installed;
    /// installed and refused, with the reason; loaded, in a version without it.
    /// </summary>
    public static string Sentence(string what, ExtensionHost? host)
    {
        if (host is null)
            return $"{what} comes with {DisplayName}, and no extension loads until a database is configured.";
        if (host.Loaded.FirstOrDefault(l => l.Name.Equals(ExtensionName, StringComparison.OrdinalIgnoreCase)) is { } loaded)
            return $"{what} comes with {DisplayName}, and the version loaded here ({loaded.Version}) does not have it.";
        if (host.Refused.FirstOrDefault(r => r.Name.Equals(ExtensionName, StringComparison.OrdinalIgnoreCase)) is { } refused)
            return $"{what} comes with {DisplayName}, which is installed here and was refused ({ExtensionHosting.Reason(refused.Reason)}); prem extensions list says why.";
        return $"{what} comes with {DisplayName}, which is not installed here.";
    }
}

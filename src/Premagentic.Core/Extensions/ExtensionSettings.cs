using System.Text.Json;
using Premagentic.Core.Ingestion;

namespace Premagentic.Core.Extensions;

/// <summary>
/// The two settings that decide what may load, kept in
/// <c>prem_config.setting</c> as JSON and read like the retrieval keys:
/// <list type="bullet">
/// <item><c>extensions.allowed</c>: a list of objects such as
/// <c>[{"name": "sentence-chunker", "sha256": "0a1b..."}]</c>. An extension
/// loads only when its name and its hash are both in this list: the hash of
/// its assembly on disk, or, when its manifest lists other files, the hash
/// that covers the assembly and each of them
/// (<see cref="ExtensionManifest.ExtensionSha256"/>). It is written by
/// <c>prem extensions allow</c>, which computes the hash itself, and every
/// change to it is in the change record.</item>
/// <item><c>extensions.folder</c>: the full path of the folder whose subfolders
/// hold extensions. The environment variable <c>PREM_EXTENSIONS_DIR</c> says
/// the same thing for a deployment that would rather set it there, and the
/// setting wins when both are set. Neither one means no extensions, which is
/// not an error.</item>
/// </list>
/// <para>
/// A stored value that cannot be used is reported, never thrown: a mistyped
/// allow list must not stop a deployment from serving. It loads no extension
/// instead, which is the safe direction.
/// </para>
/// </summary>
public static class ExtensionSettings
{
    public const string Allowed = "extensions.allowed";
    public const string Folder = "extensions.folder";

    /// <summary>Where the extensions folder is set when the setting does not set it.</summary>
    public const string FolderVariable = "PREM_EXTENSIONS_DIR";

    public static IReadOnlyList<string> Keys { get; } = [Allowed, Folder];

    private const string NameMember = "name";
    private const string Sha256Member = "sha256";
    private const int HexDigits = 64;
    private const int QuotedLength = 40;

    /// <summary>What <paramref name="key"/> accepts, as a phrase that completes "takes ...".</summary>
    /// <exception cref="ArgumentException">The key is not an extension setting.</exception>
    public static string Accepts(string key) => key switch
    {
        Allowed => """a list of objects such as [{"name": "sentence-chunker", "sha256": "0a1b..."}], each hash 64 hexadecimal digits""",
        Folder => "the full path of a folder",
        _ => throw UnknownKey(key),
    };

    /// <summary>
    /// True when <paramref name="value"/> is allowed for <paramref name="key"/>.
    /// Otherwise <paramref name="problem"/> is one sentence naming what is
    /// allowed. An unknown key is not allowed, and the sentence names the keys.
    /// </summary>
    public static bool TryParse(string key, JsonElement value, out string? problem) => key switch
    {
        Allowed => TryReadAllowed(value, out _, out problem),
        Folder => TryReadFolder(value, out _, out problem),
        _ => Refuse(out problem, UnknownKey(key).Message),
    };

    /// <summary>
    /// The allowed pairs in <paramref name="stored"/>, or none when there is
    /// nothing stored or what is stored cannot be used, which is reported in
    /// <paramref name="problem"/>. An allow list that cannot be read allows
    /// nothing, so a typo turns extensions off rather than on.
    /// </summary>
    public static IReadOnlyList<(string Name, string Sha256)> AllowedFrom(JsonElement? stored, out string? problem)
    {
        problem = null;
        if (stored is not { } value) return [];
        return TryReadAllowed(value, out var pairs, out problem) ? pairs! : [];
    }

    /// <summary>
    /// The extensions folder: the setting when it holds a usable path, the
    /// environment variable when it does not, and null when neither says. A
    /// stored value that cannot be used is reported and passed over, as an
    /// absent one is.
    /// </summary>
    public static string? FolderFrom(JsonElement? stored, string? environment, out string? problem)
    {
        problem = null;
        if (stored is { } value && TryReadFolder(value, out var folder, out problem)) return folder;
        return string.IsNullOrWhiteSpace(environment) ? null : environment;
    }

    /// <summary>The allow list as it is stored, with <paramref name="pairs"/> in it.</summary>
    public static JsonElement ToStored(IEnumerable<(string Name, string Sha256)> pairs) =>
        JsonSerializer.SerializeToElement(pairs.Select(p => new Dictionary<string, string>
        {
            [NameMember] = p.Name,
            [Sha256Member] = p.Sha256.ToLowerInvariant(),
        }).ToArray());

    private static bool TryReadAllowed(JsonElement value, out IReadOnlyList<(string Name, string Sha256)>? pairs, out string? problem)
    {
        pairs = null;
        problem = null;
        if (value.ValueKind != JsonValueKind.Array)
            return Refuse(out problem, $"{Allowed} takes {Accepts(Allowed)}, not {Quote(value)}.");

        var read = new List<(string Name, string Sha256)>();
        foreach (var entry in value.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object)
                return Refuse(out problem, $"{Allowed} takes an object for every entry, not {Quote(entry)}.");

            string? name = null, sha256 = null;
            foreach (var member in entry.EnumerateObject())
                switch (member.Name)
                {
                    case NameMember when member.Value.ValueKind == JsonValueKind.String:
                        name = member.Value.GetString();
                        break;
                    case Sha256Member when member.Value.ValueKind == JsonValueKind.String:
                        sha256 = member.Value.GetString();
                        break;
                    case NameMember or Sha256Member:
                        return Refuse(out problem, $"{Allowed} takes text for \"{member.Name}\", not {Quote(member.Value)}.");
                    default:
                        return Refuse(out problem,
                            $"{Allowed} takes only \"{NameMember}\" and \"{Sha256Member}\" in an entry, not \"{Clip(member.Name)}\".");
                }

            // The same name rule the manifest holds the extension to, because
            // this is where that name is matched.
            if (name is null || !ChunkerRegistry.IsName(name))
                return Refuse(out problem,
                    $"{Allowed} takes an extension name of up to 64 letters, digits, dots, hyphens and underscores, " +
                    $"starting with a letter or digit, not {Quote(name)}.");
            if (sha256 is null || sha256.Length != HexDigits || !sha256.All(char.IsAsciiHexDigit))
                return Refuse(out problem, $"{Allowed} takes a hash of {HexDigits} hexadecimal digits, not {Quote(sha256)}.");

            var pair = (Name: name, Sha256: sha256.ToLowerInvariant());
            if (read.Any(p => p.Name.Equals(pair.Name, StringComparison.OrdinalIgnoreCase) && p.Sha256 == pair.Sha256))
                return Refuse(out problem, $"{Allowed} takes each name and hash once, not \"{Clip(name)}\" with that hash twice.");
            read.Add(pair);
        }

        pairs = read;
        return true;
    }

    private static bool TryReadFolder(JsonElement value, out string? folder, out string? problem)
    {
        folder = null;
        problem = null;
        if (value.ValueKind != JsonValueKind.String || value.GetString() is not { } text || string.IsNullOrWhiteSpace(text))
            return Refuse(out problem, $"{Folder} takes {Accepts(Folder)}, not {Quote(value)}.");
        // A relative path would mean a different folder to the service and to
        // the person at the console, and the difference would show as an
        // extension that loads in one and not the other.
        if (!Path.IsPathRooted(text))
            return Refuse(out problem, $"{Folder} takes a full path, not \"{Clip(text)}\", which is relative to wherever a command was run.");

        folder = text;
        return true;
    }

    private static bool Refuse(out string? problem, string sentence)
    {
        problem = sentence;
        return false;
    }

    private static string Quote(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Number => Clip(value.GetRawText()),
        JsonValueKind.String => $"the text \"{Clip(value.GetString() ?? "")}\"",
        JsonValueKind.Object => "an object",
        JsonValueKind.Array => "a list",
        JsonValueKind.True or JsonValueKind.False => value.GetRawText(),
        JsonValueKind.Null => "null",
        _ => "nothing",
    };

    private static string Quote(string? text) => text is null ? "nothing" : $"the text \"{Clip(text)}\"";

    private static string Clip(string text) =>
        text.Length <= QuotedLength ? text : string.Concat(text.AsSpan(0, QuotedLength), "...");

    private static ArgumentException UnknownKey(string key) =>
        new($"There is no extension setting '{Clip(key)}': the extension settings are {string.Join(", ", Keys)}.");
}

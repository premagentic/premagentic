using System.Text.RegularExpressions;

namespace Premagentic.Core.Extensions;

/// <summary>
/// The rules of the <c>prem</c> command line that a command an extension adds
/// is held to as well: how a usage text names its subcommands and its flags,
/// and how a call's flags and words are read. The CLI and the extension host
/// read the same rules, so a command is checked the same way whoever wrote it.
/// </summary>
public static class CommandLine
{
    /// <summary>The longest noun or verb a command may have.</summary>
    public const int MaxWordLength = 32;

    /// <summary>
    /// True for a word a command can be called by: lower-case letters, digits
    /// and hyphens, starting with a letter, as <c>set-password</c> and
    /// <c>rebuild-index</c> are.
    /// </summary>
    public static bool IsWord(string? word) =>
        word is { Length: > 0 and <= MaxWordLength }
        && char.IsAsciiLetterLower(word[0])
        && word.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-');

    /// <summary>The flags a usage text names, which are the flags its command accepts.</summary>
    public static IReadOnlySet<string> FlagsIn(string usage) =>
        Regex.Matches(usage, @"(?<![\w-])--[a-z][a-z0-9-]*").Select(m => m.Value).ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// The subcommands a usage text names: the word after <c>prem noun</c> on
    /// each of its lines, split at <c>|</c>. Empty for a command that has none.
    /// </summary>
    public static IReadOnlySet<string> SubcommandsIn(string noun, string usage) =>
        usage.Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.StartsWith($"prem {noun} ", StringComparison.Ordinal))
            .Select(line => line[$"prem {noun} ".Length..].Split(' ')[0])
            .Where(word => word.Length > 0 && char.IsLetter(word[0]))
            .SelectMany(word => word.Split('|'))
            .ToHashSet(StringComparer.Ordinal);

    /// <summary>The value after <paramref name="flag"/>, or null when the flag is absent or has no value.</summary>
    public static string? Value(IReadOnlyList<string> args, string flag)
    {
        for (var i = 0; i < args.Count; i++)
            if (args[i] == flag)
                return i + 1 < args.Count && !args[i + 1].StartsWith("--", StringComparison.Ordinal) ? args[i + 1] : null;
        return null;
    }

    /// <summary>The value after every occurrence of <paramref name="flag"/>, in order.</summary>
    public static IReadOnlyList<string> Values(IReadOnlyList<string> args, string flag)
    {
        var values = new List<string>();
        for (var i = 0; i < args.Count - 1; i++)
            if (args[i] == flag && !args[i + 1].StartsWith("--", StringComparison.Ordinal)) values.Add(args[i + 1]);
        return values;
    }

    public static bool Has(IReadOnlyList<string> args, string flag) => args.Contains(flag);

    /// <summary>
    /// The arguments after the first <paramref name="skip"/> that are not flags
    /// and not the value of a flag named in <paramref name="flagsWithValues"/>.
    /// </summary>
    public static IReadOnlyList<string> Positionals(IReadOnlyList<string> args, int skip, params string[] flagsWithValues)
    {
        var positionals = new List<string>();
        for (var i = skip; i < args.Count; i++)
        {
            if (args[i].StartsWith("--", StringComparison.Ordinal))
            {
                if (flagsWithValues.Contains(args[i]) && i + 1 < args.Count) i++;
                continue;
            }
            positionals.Add(args[i]);
        }
        return positionals;
    }
}

/// <summary>
/// The commands <c>prem</c> has built in, so the extension host can refuse a
/// command that would stand in for one of them. A test holds these lists to
/// the CLI's own table, so the two cannot drift apart.
/// </summary>
public static class BuiltInCommands
{
    /// <summary>Every word <c>prem</c> answers itself, commands and <c>help</c> alike.</summary>
    public static IReadOnlyList<string> Names { get; } =
    [
        "help", "migrate", "init-db", "setup", "remove", "rebuild-index", "ingest", "search", "section", "eval",
        "users", "groups", "agents", "tokens", "oauth", "rules", "settings", "sources", "extensions", "profile",
        "reminders",
    ];

    /// <summary>
    /// The built-in commands an extension may add subcommands to, each with the
    /// subcommands it already has. An extension may add none to any other
    /// built-in command, so it cannot put a verb among the ones that decide who
    /// signs in, what an agent holds, or what code loads.
    /// </summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> Open { get; } =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
        {
            ["groups"] = ["add", "rename", "remove", "members", "list"],
        };
}

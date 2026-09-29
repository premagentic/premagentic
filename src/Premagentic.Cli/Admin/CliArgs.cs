using Premagentic.Core.Extensions;

namespace Premagentic.Cli.Admin;

/// <summary>
/// Flag parsing for the administration verbs: the rules of
/// <see cref="CommandLine"/>, which a command an extension adds reads too.
/// </summary>
internal static class CliArgs
{
    /// <summary>The value after <paramref name="flag"/>, or null when the flag is absent or has no value.</summary>
    public static string? Value(string[] args, string flag) => CommandLine.Value(args, flag);

    /// <summary>The value after every occurrence of <paramref name="flag"/>, in order.</summary>
    public static IReadOnlyList<string> Values(string[] args, string flag) => CommandLine.Values(args, flag);

    /// <summary>A comma-separated value, split and trimmed.</summary>
    public static string[] List(string[] args, string flag) =>
        Value(args, flag)?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [];

    public static bool Has(string[] args, string flag) => CommandLine.Has(args, flag);

    /// <summary>
    /// The arguments after the first <paramref name="skip"/> that are not flags
    /// and not the value of a flag named in <paramref name="flagsWithValues"/>.
    /// </summary>
    public static IReadOnlyList<string> Positionals(string[] args, int skip, params string[] flagsWithValues) =>
        CommandLine.Positionals(args, skip, flagsWithValues);
}

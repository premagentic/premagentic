using System.Text;

namespace Premagentic.Core.Storage;

/// <summary>
/// Whether this process normalizes Unicode the way password hashing needs.
/// A password is normalized to NFKC before it is hashed, so a hash made on one
/// server verifies on another only if both normalize alike. A .NET runtime in
/// invariant globalization mode does not normalize at all: it hands back the
/// text unchanged, and every password that was not already in NFKC stops
/// matching, silently. Premagentic ships its own ICU so that every platform
/// normalizes alike; this is the check that it does, made by setup before it
/// touches anything and by anything that hashes a password before it does.
/// </summary>
public static class TextCheck
{
    private const string InvariantVariable = "DOTNET_SYSTEM_GLOBALIZATION_INVARIANT";

    // Fixed by the Unicode standard, not by any library version.
    private static readonly (string Input, string Nfkc)[] KnownAnswers =
    [
        ("cafe\u0301", "caf\u00E9"),   // a decomposed accent composes
        ("\uFB01le", "file"),          // a ligature splits
        ("\uFF21\uFF22", "AB"),        // fullwidth letters become plain
        ("\u212B", "\u00C5"),          // the angstrom sign becomes the letter
    ];

    /// <summary>Null when normalization works; otherwise what is wrong and what to do, for the person running setup.</summary>
    public static string? Problem()
    {
        var works = KnownAnswers.All(k =>
        {
            try { return k.Input.Normalize(NormalizationForm.FormKC) == k.Nfkc; }
            catch (PlatformNotSupportedException) { return false; }
        });
        if (works) return null;

        var variable = Environment.GetEnvironmentVariable(InvariantVariable);
        return variable is "1" or "true"
            ? $"The environment variable {InvariantVariable} is set to {variable}, which turns off Unicode text handling in .NET. " +
              "Passwords with accented or special characters would then not match on another server. " +
              $"Remove {InvariantVariable} from this account's environment (and from any service or container that runs Premagentic) " +
              "and run setup again."
            : "This copy of Premagentic is not handling Unicode text, so passwords with accented or special characters would not " +
              "match on another server. Its Unicode library (the icu files beside the program) is missing or was turned off. " +
              "Reinstall Premagentic from its release package without changing its runtimeconfig.json, and run setup again.";
    }

    /// <summary>Which ICU this process uses, for the report.</summary>
    public static string IcuDescription() =>
        AppContext.GetData("System.Globalization.AppLocalIcu") is string version
            ? $"ICU {version}, shipped with Premagentic"
            : "the operating system's ICU";
}

using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Premagentic.Core.Identity;

/// <summary>
/// An access or refresh token of the flow, issued: the plain text exists only
/// here and in the response that carries it; <see cref="SecretHash"/> is what
/// is stored.
/// </summary>
internal sealed record IssuedOAuthSecret(string Id, string PlainText, byte[] SecretHash)
{
    /// <summary>Leaves the token out, so the record can be logged or printed by accident without harm.</summary>
    public override string ToString() => $"IssuedOAuthSecret {{ Id = {Id} }}";
}

/// <summary>A token of the flow as a client presented it, parsed but not yet checked.</summary>
internal sealed record PresentedOAuthSecret(string Id, byte[] SecretHash)
{
    public override string ToString() => $"PresentedOAuthSecret {{ Id = {Id} }}";
}

/// <summary>
/// The flow's values: access and refresh tokens in the agent token's shape
/// (a prefix, 24 lower-case hex, an underscore, 32 random bytes in unpadded
/// base64url), authorization codes (32 random bytes), and the ids the server
/// makes. Only SHA-256 of a secret is stored, and it is compared in fixed time.
/// </summary>
internal static class OAuthSecrets
{
    private const int IdBytes = 12;
    private const int SecretBytes = 32;
    private static readonly int SecretLength = (SecretBytes * 4 + 2) / 3;

    public static IssuedOAuthSecret Issue(string prefix)
    {
        var id = NewId();
        var secret = RandomNumberGenerator.GetBytes(SecretBytes);
        try
        {
            return new IssuedOAuthSecret(id, prefix + id + "_" + StrictBase64Url.Encode(secret), SHA256.HashData(secret));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
        }
    }

    /// <summary>Splits a presented token of the given prefix into its id and the hash of its secret. Anything else is false. Never throws.</summary>
    public static bool TryParse(string? text, string prefix, [NotNullWhen(true)] out PresentedOAuthSecret? presented)
    {
        presented = null;
        if (text is null || text.Length != prefix.Length + IdBytes * 2 + 1 + SecretLength) return false;
        if (!text.StartsWith(prefix, StringComparison.Ordinal)) return false;
        var id = text.Substring(prefix.Length, IdBytes * 2);
        if (!id.All(char.IsAsciiHexDigitLower) || text[prefix.Length + IdBytes * 2] != '_') return false;
        if (!StrictBase64Url.TryDecode(text[(prefix.Length + IdBytes * 2 + 1)..], out var secret) || secret.Length != SecretBytes) return false;
        try
        {
            presented = new PresentedOAuthSecret(id, SHA256.HashData(secret));
            return true;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
        }
    }

    /// <summary>True when the presented secret hashes to the stored hash, compared in fixed time.</summary>
    public static bool Matches(PresentedOAuthSecret presented, byte[]? stored) =>
        stored is { Length: 32 } && CryptographicOperations.FixedTimeEquals(presented.SecretHash, stored);

    /// <summary>A new authorization code, and the hash that is stored for it.</summary>
    public static (string Code, byte[] Hash) NewCode()
    {
        var bytes = RandomNumberGenerator.GetBytes(SecretBytes);
        try
        {
            var code = StrictBase64Url.Encode(bytes);
            return (code, SHA256.HashData(Encoding.ASCII.GetBytes(code)));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    /// <summary>The hash a presented code is looked up by, or null when it cannot be a code this server made.</summary>
    public static byte[]? CodeHash(string? code) =>
        code is { Length: > 0 and <= 128 } && code.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')
            ? SHA256.HashData(Encoding.ASCII.GetBytes(code))
            : null;

    /// <summary>A new id: 24 lower-case hex, for grants and the flow's tokens.</summary>
    public static string NewId() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(IdBytes));

    /// <summary>A new client id: <c>prem_cli_</c> and 24 lower-case hex.</summary>
    public static string NewClientId() => OAuthPrefixes.ClientId + NewId();

    /// <summary>
    /// PKCE S256: whether the verifier, 43 to 128 unreserved characters, hashes
    /// to the challenge the authorization request carried. Fixed time.
    /// </summary>
    public static bool VerifierMatches(string? verifier, string challenge)
    {
        if (verifier is null || verifier.Length is < 43 or > 128
            || !verifier.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '.' or '_' or '~'))
            return false;
        var computed = Encoding.ASCII.GetBytes(StrictBase64Url.Encode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))));
        var expected = Encoding.ASCII.GetBytes(challenge);
        return CryptographicOperations.FixedTimeEquals(computed, expected);
    }

    /// <summary>A PKCE S256 challenge: exactly 43 unpadded base64url characters.</summary>
    public static bool IsChallenge(string? challenge) =>
        challenge is { Length: 43 } && challenge.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    /// <summary>Seconds since 1970, as a registration response writes a time.</summary>
    public static long UnixSeconds(DateTimeOffset at) => at.ToUnixTimeSeconds();

    public static string Seconds(TimeSpan span) => ((long)span.TotalSeconds).ToString(CultureInfo.InvariantCulture);
}

/// <summary>
/// Text a client or an administrator gives for the consent page to show: a
/// name, a software id. Trimmed, never cut, and refused when it holds a
/// character that is invisible or turns text around it: controls, format
/// characters (the bidirectional marks and the zero-width ones among them),
/// line and paragraph separators, private-use and unassigned characters, and
/// half of a surrogate pair.
/// </summary>
public static class OAuthText
{
    /// <returns>The trimmed text, or null with the reason.</returns>
    public static string? Check(string? text, int maxLength, string what, out string? problem)
    {
        problem = null;
        var trimmed = text?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            problem = $"{what} is required and cannot be only white space";
            return null;
        }
        if (trimmed.Length > maxLength)
        {
            problem = $"{what} is at most {maxLength} characters";
            return null;
        }

        var span = trimmed.AsSpan();
        while (!span.IsEmpty)
        {
            // A JSON body's lone surrogate reaches here already replaced with
            // U+FFFD, so the replacement character is refused as well: it only
            // stands for text that arrived damaged.
            if (Rune.DecodeFromUtf16(span, out var rune, out var used) != OperationStatus.Done || rune == Rune.ReplacementChar)
            {
                problem = $"{what} holds half of a character, or a character that arrived damaged";
                return null;
            }
            if (Rune.GetUnicodeCategory(rune) is UnicodeCategory.Control or UnicodeCategory.Format
                or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator
                or UnicodeCategory.PrivateUse or UnicodeCategory.OtherNotAssigned)
            {
                problem = $"{what} holds a character that is invisible or changes the direction of the text around it";
                return null;
            }
            span = span[used..];
        }
        return trimmed;
    }
}

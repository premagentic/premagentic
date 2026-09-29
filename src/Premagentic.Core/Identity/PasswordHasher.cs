using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Premagentic.Core.Storage;

namespace Premagentic.Core.Identity;

/// <summary>
/// Password hashing for local accounts: PBKDF2 with HMAC-SHA512, a random salt
/// per password, and an encoded string that records everything needed to check
/// the password again, so the work factor can rise without breaking the hashes
/// already stored.
/// <para>
/// Encoded form, version 1:
/// <c>$pbkdf2-sha512$v=1$i=&lt;iterations&gt;$&lt;salt&gt;$&lt;hash&gt;</c>, with the
/// salt and the hash in unpadded base64url. The parser accepts exactly that
/// form and nothing looser.
/// </para>
/// <para>
/// A password is normalized to Unicode form NFKC and encoded as UTF-8 before
/// hashing, so the same password typed on two systems that compose accented
/// characters differently still matches. That choice is part of version 1 and
/// cannot change without a new version.
/// </para>
/// </summary>
public sealed class PasswordHasher
{
    /// <summary>
    /// The iteration count for new hashes: the current OWASP Password Storage
    /// Cheat Sheet recommendation for PBKDF2-HMAC-SHA512. Raise it when that
    /// guidance rises; <see cref="NeedsRehash"/> then flags older hashes.
    /// </summary>
    public const int DefaultIterations = 220_000;

    /// <summary>Random salt per password, in bytes.</summary>
    public const int SaltBytes = 16;

    /// <summary>Derived key length in bytes: the full SHA-512 output.</summary>
    public const int HashBytes = 64;

    /// <summary>
    /// Longer input is refused rather than hashed. Generous for a passphrase,
    /// small enough that no one sign-in attempt can be made expensive.
    /// </summary>
    public const int MaxPasswordLength = 1024;

    private const string Algorithm = "pbkdf2-sha512";
    private const string Version = "v=1";

    // A process that does not normalize Unicode (invariant globalization mode)
    // would store hashes no other server can verify. Checked once, by the same
    // known answers and with the same sentence as prem setup.
    private static readonly string? TextProblem = TextCheck.Problem();

    // A stored hash claiming more work than this is refused unchecked, so a
    // tampered row cannot pin a processor on every sign-in attempt.
    private const int MaxAcceptedIterations = 10_000_000;
    private const int MaxAcceptedSaltBytes = 1024;

    private readonly string? _textProblem;

    public PasswordHasher() : this(DefaultIterations) { }

    /// <summary>A hasher that writes new hashes with more iterations than the default. Fewer is refused.</summary>
    public PasswordHasher(int iterations) : this(iterations, TextProblem) { }

    /// <summary>A hasher that takes <paramref name="textProblem"/> as this process's text check, for tests.</summary>
    internal PasswordHasher(int iterations, string? textProblem)
    {
        if (iterations < DefaultIterations || iterations > MaxAcceptedIterations)
            throw new ArgumentOutOfRangeException(nameof(iterations), iterations,
                $"Iterations must be between {DefaultIterations} and {MaxAcceptedIterations}.");
        Iterations = iterations;
        _textProblem = textProblem;
    }

    /// <summary>The iteration count this hasher writes into new hashes.</summary>
    public int Iterations { get; }

    /// <summary>Hashes a new password. Throws on a null, empty, overlong or malformed password.</summary>
    public string Hash(string password)
    {
        ArgumentNullException.ThrowIfNull(password);
        if (_textProblem is not null)
            throw new InvalidOperationException(_textProblem);
        if (!TryEncodePassword(password, out var passwordBytes))
            throw new ArgumentException(
                $"A password must be 1 to {MaxPasswordLength} characters of well-formed Unicode.", nameof(password));

        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        try
        {
            var hash = Rfc2898DeriveBytes.Pbkdf2(passwordBytes, salt, Iterations, HashAlgorithmName.SHA512, HashBytes);
            return string.Join('$', "", Algorithm, Version, "i=" + Iterations.ToString(CultureInfo.InvariantCulture),
                StrictBase64Url.Encode(salt), StrictBase64Url.Encode(hash));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(passwordBytes);
        }
    }

    /// <summary>
    /// True only when <paramref name="password"/> matches <paramref name="encoded"/>.
    /// Anything malformed, on either side, is false. Throws only as <see cref="Hash"/>
    /// does when this process cannot normalize text: answering false there would
    /// refuse a right password without saying why.
    /// </summary>
    public bool Verify(string? password, string? encoded)
    {
        if (_textProblem is not null)
            throw new InvalidOperationException(_textProblem);
        if (password is null || !TryDecode(encoded, out var iterations, out var salt, out var expected)) return false;
        if (!TryEncodePassword(password, out var passwordBytes)) return false;

        try
        {
            var actual = Rfc2898DeriveBytes.Pbkdf2(passwordBytes, salt, iterations, HashAlgorithmName.SHA512, HashBytes);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(passwordBytes);
        }
    }

    /// <summary>
    /// True when a stored hash was made with less work than this hasher uses, or
    /// is not a hash this version can read. Meant for the moment right after a
    /// successful <see cref="Verify"/>, when the plain password is in hand to
    /// hash again.
    /// </summary>
    public bool NeedsRehash(string? encoded) =>
        !TryDecode(encoded, out var iterations, out _, out _) || iterations < Iterations;

    private static bool TryEncodePassword(string password, out byte[] bytes)
    {
        bytes = [];
        if (password.Length == 0 || password.Length > MaxPasswordLength) return false;

        string normalized;
        try
        {
            normalized = password.Normalize(NormalizationForm.FormKC);
        }
        catch (ArgumentException)
        {
            // Not well-formed Unicode, such as an unpaired surrogate.
            return false;
        }

        bytes = Encoding.UTF8.GetBytes(normalized);
        return true;
    }

    private static bool TryDecode(string? encoded, out int iterations, out byte[] salt, out byte[] hash)
    {
        iterations = 0;
        salt = [];
        hash = [];
        if (encoded is null) return false;

        var parts = encoded.Split('$');
        if (parts.Length != 6 || parts[0].Length != 0) return false;
        if (parts[1] != Algorithm || parts[2] != Version) return false;
        if (!TryParseIterations(parts[3], out iterations)) return false;
        if (!StrictBase64Url.TryDecode(parts[4], out salt) || salt.Length < SaltBytes || salt.Length > MaxAcceptedSaltBytes) return false;
        if (!StrictBase64Url.TryDecode(parts[5], out hash) || hash.Length != HashBytes) return false;
        return true;
    }

    // "i=" then a canonical positive decimal: digits only, no sign, no leading zero.
    private static bool TryParseIterations(string part, out int iterations)
    {
        iterations = 0;
        if (!part.StartsWith("i=", StringComparison.Ordinal)) return false;

        var digits = part.AsSpan(2);
        if (digits.IsEmpty || digits.Length > 9 || digits[0] == '0') return false;
        foreach (var c in digits)
        {
            if (!char.IsAsciiDigit(c)) return false;
        }

        iterations = int.Parse(digits, NumberStyles.None, CultureInfo.InvariantCulture);
        return iterations <= MaxAcceptedIterations;
    }
}

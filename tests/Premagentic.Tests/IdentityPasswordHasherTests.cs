using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using Premagentic.Core.Identity;

namespace Premagentic.Tests;

public class IdentityPasswordHasherTests
{
    private static readonly PasswordHasher Hasher = new();

    // Builds an encoded hash by hand, straight from the primitive, so the format
    // and the algorithm are checked independently of the class under test.
    private static string HandBuilt(string password, byte[] salt, int iterations)
    {
        var hash = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA512, 64);
        return $"$pbkdf2-sha512$v=1$i={iterations}${Base64Url.EncodeToString(salt)}${Base64Url.EncodeToString(hash)}";
    }

    private static readonly byte[] FixedSalt = Enumerable.Range(1, 16).Select(i => (byte)i).ToArray();

    [Fact]
    public void A_process_that_cannot_normalize_text_refuses_to_hash_or_to_verify()
    {
        const string problem = "This process does not normalize Unicode as password hashing needs.";
        var stored = Hasher.Hash("correct horse");
        var refusing = new PasswordHasher(PasswordHasher.DefaultIterations, problem);

        Assert.Equal(problem, Assert.Throws<InvalidOperationException>(() => refusing.Hash("correct horse")).Message);
        Assert.Equal(problem, Assert.Throws<InvalidOperationException>(() => refusing.Verify("correct horse", stored)).Message);

        // The controls: with no problem the same calls answer, and this process has none.
        Assert.True(new PasswordHasher(PasswordHasher.DefaultIterations, null).Verify("correct horse", stored));
        Assert.True(Hasher.Verify("correct horse", stored));
    }

    [Fact]
    public void A_hash_verifies_its_own_password_and_no_other()
    {
        var encoded = Hasher.Hash("correct horse battery staple");

        Assert.True(Hasher.Verify("correct horse battery staple", encoded));
        Assert.False(Hasher.Verify("correct horse battery stapl", encoded));
        Assert.False(Hasher.Verify("Correct horse battery staple", encoded));
    }

    [Fact]
    public void The_encoded_form_records_algorithm_version_iterations_salt_and_hash()
    {
        var parts = Hasher.Hash("pw").Split('$');

        Assert.Equal(6, parts.Length);
        Assert.Equal("", parts[0]);
        Assert.Equal("pbkdf2-sha512", parts[1]);
        Assert.Equal("v=1", parts[2]);
        Assert.Equal("i=220000", parts[3]);
        Assert.Equal(16, Base64Url.DecodeFromChars(parts[4]).Length);
        Assert.Equal(64, Base64Url.DecodeFromChars(parts[5]).Length);
    }

    [Fact]
    public void The_default_work_factor_is_the_owasp_figure_for_sha512()
    {
        Assert.Equal(220_000, PasswordHasher.DefaultIterations);
        Assert.Equal(220_000, Hasher.Iterations);
    }

    [Fact]
    public void Each_hash_gets_its_own_salt()
    {
        Assert.NotEqual(Hasher.Hash("same"), Hasher.Hash("same"));
    }

    [Fact]
    public void A_hash_built_directly_from_pbkdf2_verifies()
    {
        var encoded = HandBuilt("pw", FixedSalt, PasswordHasher.DefaultIterations);

        Assert.True(Hasher.Verify("pw", encoded));
        Assert.False(Hasher.Verify("pW", encoded));
        Assert.False(Hasher.NeedsRehash(encoded));
    }

    [Fact]
    public void A_weaker_hash_still_verifies_and_is_flagged_for_rehash()
    {
        var weaker = HandBuilt("pw", FixedSalt, 1_000);

        Assert.True(Hasher.Verify("pw", weaker));
        Assert.True(Hasher.NeedsRehash(weaker));
    }

    [Fact]
    public void Raising_the_work_factor_flags_existing_hashes()
    {
        var today = Hasher.Hash("pw");
        var stronger = new PasswordHasher(300_000);

        Assert.True(stronger.NeedsRehash(today));
        Assert.True(stronger.Verify("pw", today));
        Assert.False(stronger.NeedsRehash(stronger.Hash("pw")));
    }

    [Fact]
    public void The_work_factor_cannot_be_set_below_the_default()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new PasswordHasher(PasswordHasher.DefaultIterations - 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PasswordHasher(0));
    }

    [Fact]
    public void Passwords_are_normalized_before_hashing()
    {
        // "e" plus a combining acute accent, against the precomposed character.
        const char combiningAcute = (char)0x0301;
        const char precomposedE = (char)0x00E9;
        var encoded = Hasher.Hash("cafe" + combiningAcute);
        Assert.True(Hasher.Verify("caf" + precomposedE, encoded));
        Assert.False(Hasher.Verify("cafe", encoded));
    }

    [Fact]
    public void Unusable_passwords_are_refused_by_Hash()
    {
        Assert.Throws<ArgumentNullException>(() => Hasher.Hash(null!));
        Assert.Throws<ArgumentException>(() => Hasher.Hash(""));
        Assert.Throws<ArgumentException>(() => Hasher.Hash(new string('a', PasswordHasher.MaxPasswordLength + 1)));
        Assert.Throws<ArgumentException>(() => Hasher.Hash("pw" + (char)0xD800));
    }

    [Fact]
    public void The_longest_allowed_password_hashes_and_one_more_character_never_verifies()
    {
        var longest = new string('a', PasswordHasher.MaxPasswordLength);
        var encoded = Hasher.Hash(longest);

        Assert.True(Hasher.Verify(longest, encoded));
        Assert.False(Hasher.Verify(longest + "a", encoded));
    }

    [Fact]
    public void Unusable_passwords_never_verify()
    {
        var encoded = HandBuilt("pw", FixedSalt, PasswordHasher.DefaultIterations);

        Assert.False(Hasher.Verify(null, encoded));
        Assert.False(Hasher.Verify("", encoded));
        Assert.False(Hasher.Verify("pw" + (char)0xD800, encoded));
    }

    public static TheoryData<string?> MalformedEncodings()
    {
        var good = HandBuilt("pw", FixedSalt, PasswordHasher.DefaultIterations);
        var parts = good.Split('$');
        string With(int index, string value)
        {
            var copy = (string[])parts.Clone();
            copy[index] = value;
            return string.Join('$', copy);
        }

        return new TheoryData<string?>
        {
            null,
            "",
            "$",
            good[1..],
            good + "$",
            good + "$extra",
            string.Join('$', parts.Take(5)),
            With(1, "pbkdf2-sha256"),
            With(1, "PBKDF2-SHA512"),
            With(2, "v=2"),
            With(2, "v=01"),
            With(3, "i=0"),
            With(3, "i=-220000"),
            With(3, "i=+220000"),
            With(3, "i=0220000"),
            With(3, "i=220000 "),
            With(3, "i=22e4"),
            With(3, "i="),
            With(3, "220000"),
            With(3, "i=10000001"),
            With(3, "i=2147483648"),
            With(4, Base64Url.EncodeToString(new byte[15])),
            With(4, Convert.ToBase64String(FixedSalt)),
            With(4, parts[4] + "="),
            With(4, ""),
            With(5, Base64Url.EncodeToString(new byte[63])),
            With(5, Base64Url.EncodeToString(new byte[65])),
            With(5, parts[5] + " "),
            With(5, parts[5][..^1] + "!"),
            good.Replace("$", "$ "),
        };
    }

    [Theory]
    [MemberData(nameof(MalformedEncodings))]
    public void Malformed_encodings_never_verify_and_never_throw(string? encoded)
    {
        Assert.False(Hasher.Verify("pw", encoded));
        Assert.True(Hasher.NeedsRehash(encoded));
    }

    [Fact]
    public void A_noncanonical_final_character_is_refused()
    {
        // 16 bytes encode to 22 characters; the last one carries 2 data bits and
        // 4 bits that must be zero. Setting them changes the text, not the bytes.
        var good = HandBuilt("pw", FixedSalt, PasswordHasher.DefaultIterations);
        var parts = good.Split('$');
        var salt = parts[4];
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_";
        var last = alphabet.IndexOf(salt[^1]);
        var noncanonical = salt[..^1] + alphabet[last | 0b0001];
        Assert.NotEqual(salt, noncanonical);
        parts[4] = noncanonical;

        Assert.True(Hasher.Verify("pw", good));
        Assert.False(Hasher.Verify("pw", string.Join('$', parts)));
    }
}

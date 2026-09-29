using System.Security.Cryptography;
using System.Text;

namespace Premagentic.Cli.Setup;

/// <summary>Generated passwords, and the form PostgreSQL stores them in.</summary>
internal static class Secrets
{
    /// <summary>32 random bytes, base64url without padding: 43 characters that need no quoting anywhere.</summary>
    public static string NewPassword() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>
    /// A SCRAM-SHA-256 verifier for <paramref name="password"/>, in the form
    /// PostgreSQL stores. Handing the server the verifier instead of the password
    /// means the password itself never reaches the server, its logs or its
    /// statement statistics. A generated password is plain ASCII, so the SASLprep
    /// step the standard calls for leaves it unchanged.
    /// </summary>
    public static string ScramSha256Verifier(string password, int iterations = 4096)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var salted = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, 32);
        var clientKey = HMACSHA256.HashData(salted, "Client Key"u8);
        var storedKey = SHA256.HashData(clientKey);
        var serverKey = HMACSHA256.HashData(salted, "Server Key"u8);
        CryptographicOperations.ZeroMemory(salted);
        return $"SCRAM-SHA-256${iterations}:{Convert.ToBase64String(salt)}${Convert.ToBase64String(storedKey)}:{Convert.ToBase64String(serverKey)}";
    }
}

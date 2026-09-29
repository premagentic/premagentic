using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;

namespace Premagentic.Core.Identity;

/// <summary>
/// Agent access tokens, in the form <c>prem_agt_&lt;tokenId&gt;_&lt;secret&gt;</c>.
/// <para>
/// The token id is public, 24 lower-case hex characters, and is how a presented
/// token is looked up. The secret is 32 random bytes in unpadded base64url
/// (whose alphabet includes the underscore, which is why the id never does).
/// Only the SHA-256 of the secret is stored, so the plain token exists once, in
/// what <see cref="Issue"/> returns, and cannot be recovered from storage. A slow
/// password hash is not needed here: the secret is long and random, not chosen
/// by a person, so there is nothing to guess.
/// </para>
/// </summary>
public static class AgentTokens
{
    public const string Prefix = "prem_agt_";

    /// <summary>Random bytes in a token id, written as twice as many hex characters.</summary>
    public const int TokenIdBytes = 12;

    /// <summary>Random bytes in a token secret.</summary>
    public const int SecretBytes = 32;

    private const int TokenIdLength = TokenIdBytes * 2;

    // Unpadded base64url length of SecretBytes.
    private const int SecretLength = (SecretBytes * 4 + 2) / 3;

    /// <summary>
    /// Creates a token for <paramref name="agentId"/>. The plain token is in the
    /// result and nowhere else; <see cref="IssuedAgentToken.Record"/> is what to store.
    /// </summary>
    public static IssuedAgentToken Issue(Guid agentId, DateTimeOffset createdAt, DateTimeOffset expiresAt)
    {
        if (expiresAt <= createdAt)
            throw new ArgumentOutOfRangeException(nameof(expiresAt), "A token must expire after it is created.");

        var tokenId = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(TokenIdBytes));
        var secret = RandomNumberGenerator.GetBytes(SecretBytes);
        try
        {
            var plainText = Prefix + tokenId + "_" + StrictBase64Url.Encode(secret);
            var record = new AgentTokenRecord(tokenId, agentId, SHA256.HashData(secret), createdAt, expiresAt,
                RevokedAt: null, LastUsedAt: null);
            return new IssuedAgentToken(plainText, record);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
        }
    }

    /// <summary>Splits a presented token into its id and secret. Anything malformed returns false. Never throws.</summary>
    public static bool TryParse(string? text, [NotNullWhen(true)] out PresentedAgentToken? token)
    {
        token = null;
        if (text is null || text.Length != Prefix.Length + TokenIdLength + 1 + SecretLength) return false;
        if (!text.StartsWith(Prefix, StringComparison.Ordinal)) return false;

        var tokenId = text.Substring(Prefix.Length, TokenIdLength);
        foreach (var c in tokenId)
        {
            if (!char.IsAsciiHexDigitLower(c)) return false;
        }

        if (text[Prefix.Length + TokenIdLength] != '_') return false;

        var secretText = text[(Prefix.Length + TokenIdLength + 1)..];
        if (!StrictBase64Url.TryDecode(secretText, out var secret) || secret.Length != SecretBytes) return false;

        token = new PresentedAgentToken(tokenId, secret);
        return true;
    }

    /// <summary>
    /// True when the presented secret hashes to <paramref name="storedSecretHash"/>.
    /// The comparison takes the same time wherever the bytes differ.
    /// </summary>
    public static bool Matches(PresentedAgentToken token, byte[]? storedSecretHash)
    {
        ArgumentNullException.ThrowIfNull(token);
        if (storedSecretHash is null || storedSecretHash.Length != SHA256.HashSizeInBytes) return false;
        return CryptographicOperations.FixedTimeEquals(token.SecretHash, storedSecretHash);
    }
}

/// <summary>
/// A newly created token. <see cref="PlainText"/> is shown once to whoever
/// created it and is never stored or logged; <see cref="Record"/> is what gets stored.
/// </summary>
public sealed class IssuedAgentToken
{
    internal IssuedAgentToken(string plainText, AgentTokenRecord record)
    {
        PlainText = plainText;
        Record = record;
    }

    public string PlainText { get; }

    public AgentTokenRecord Record { get; }

    /// <summary>Names the token without its secret, so it is safe in a log line.</summary>
    public override string ToString() => $"agent token {Record.Id}";
}

/// <summary>A token as a caller presented it, parsed but not yet checked against storage.</summary>
public sealed class PresentedAgentToken
{
    private readonly byte[] _secretHash;

    internal PresentedAgentToken(string tokenId, byte[] secret)
    {
        TokenId = tokenId;
        _secretHash = SHA256.HashData(secret);
        CryptographicOperations.ZeroMemory(secret);
    }

    public string TokenId { get; }

    internal ReadOnlySpan<byte> SecretHash => _secretHash;

    /// <summary>Names the token without its secret, so it is safe in a log line.</summary>
    public override string ToString() => $"agent token {TokenId}";
}

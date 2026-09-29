using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Premagentic.Core.Identity;

namespace Premagentic.Tests;

public class IdentityAgentTokenTests
{
    private static readonly Guid AgentId = Guid.Parse("6f9619ff-8b86-d011-b42d-00c04fc964ff");
    private static readonly DateTimeOffset Created = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Expires = Created.AddDays(90);

    private static IssuedAgentToken Issue() => AgentTokens.Issue(AgentId, Created, Expires);

    [Fact]
    public void An_issued_token_has_the_documented_shape()
    {
        var issued = Issue();

        Assert.Matches(new Regex("^prem_agt_[0-9a-f]{24}_[A-Za-z0-9_-]{43}$"), issued.PlainText);
        Assert.Equal(issued.Record.Id, issued.PlainText.Substring(AgentTokens.Prefix.Length, 24));
    }

    [Fact]
    public void The_record_stores_the_sha256_of_the_secret_and_nothing_else_secret()
    {
        var issued = Issue();
        var secretText = issued.PlainText[(AgentTokens.Prefix.Length + 24 + 1)..];
        var expected = SHA256.HashData(Base64Url.DecodeFromChars(secretText));

        Assert.Equal(expected, issued.Record.SecretHash);
        Assert.Equal(AgentId, issued.Record.AgentId);
        Assert.Equal(Created, issued.Record.CreatedAt);
        Assert.Equal(Expires, issued.Record.ExpiresAt);
        Assert.Null(issued.Record.RevokedAt);
        Assert.Null(issued.Record.LastUsedAt);

        Assert.DoesNotContain(secretText, issued.Record.ToString());
        Assert.DoesNotContain(secretText, issued.ToString());
    }

    [Fact]
    public void An_issued_token_parses_and_matches_its_record()
    {
        var issued = Issue();

        Assert.True(AgentTokens.TryParse(issued.PlainText, out var presented));
        Assert.Equal(issued.Record.Id, presented.TokenId);
        Assert.True(AgentTokens.Matches(presented, issued.Record.SecretHash));
        Assert.DoesNotContain(issued.PlainText[32..], presented.ToString());
    }

    [Fact]
    public void Two_tokens_share_nothing()
    {
        var a = Issue();
        var b = Issue();

        Assert.NotEqual(a.Record.Id, b.Record.Id);
        Assert.NotEqual(a.Record.SecretHash, b.Record.SecretHash);
        Assert.True(AgentTokens.TryParse(a.PlainText, out var presentedA));
        Assert.False(AgentTokens.Matches(presentedA, b.Record.SecretHash));
    }

    [Fact]
    public void A_changed_secret_parses_but_does_not_match()
    {
        var issued = Issue();
        var text = issued.PlainText;
        var i = 40;
        var tampered = text[..i] + (text[i] == 'A' ? 'B' : 'A') + text[(i + 1)..];

        Assert.True(AgentTokens.TryParse(tampered, out var presented));
        Assert.False(AgentTokens.Matches(presented, issued.Record.SecretHash));
    }

    [Fact]
    public void A_missing_or_wrong_length_stored_hash_never_matches()
    {
        var issued = Issue();
        Assert.True(AgentTokens.TryParse(issued.PlainText, out var presented));

        Assert.False(AgentTokens.Matches(presented, null));
        Assert.False(AgentTokens.Matches(presented, []));
        Assert.False(AgentTokens.Matches(presented, issued.Record.SecretHash[..31]));
        Assert.False(AgentTokens.Matches(presented, [.. issued.Record.SecretHash, 0]));
    }

    [Fact]
    public void A_secret_containing_the_separator_still_parses()
    {
        // 0xFF bytes encode to underscores in base64url, the same character that
        // separates the id from the secret.
        var secret = Enumerable.Repeat((byte)0xFF, 32).ToArray();
        var secretText = Base64Url.EncodeToString(secret);
        Assert.Contains('_', secretText);

        Assert.True(AgentTokens.TryParse("prem_agt_0123456789abcdef01234567_" + secretText, out var presented));
        Assert.Equal("0123456789abcdef01234567", presented.TokenId);
        Assert.True(AgentTokens.Matches(presented, SHA256.HashData(secret)));
    }

    public static TheoryData<string?> MalformedTokens()
    {
        const string id = "0123456789abcdef01234567";
        var secret = Base64Url.EncodeToString(Enumerable.Range(0, 32).Select(i => (byte)i).ToArray());
        var good = $"prem_agt_{id}_{secret}";

        return new TheoryData<string?>
        {
            null,
            "",
            "prem_agt_",
            good[..^1],
            good + "A",
            good + " ",
            " " + good,
            good.Replace("prem_agt_", "prem_agx_"),
            good.Replace("prem_agt_", "PREM_AGT_"),
            good.Replace(id, id.ToUpperInvariant()),
            good.Replace(id, id[..^1] + "g"),
            good.Replace(id + "_", id + "-"),
            $"prem_agt_{id[..^1]}_{secret}A",
            $"prem_agt_{id}_{secret[..^1]}+",
            $"prem_agt_{id}_{secret[..^1]}/",
            $"prem_agt_{id}_{secret[..^1]}=",
            $"prem_agt_{id}_{secret[..^1]}\n",
            $"prem_agt_{id}_{Convert.ToBase64String(new byte[32])}",
            new string('a', 100_000),
        };
    }

    [Theory]
    [MemberData(nameof(MalformedTokens))]
    public void Malformed_tokens_are_rejected_without_throwing(string? text)
    {
        Assert.False(AgentTokens.TryParse(text, out var presented));
        Assert.Null(presented);
    }

    [Fact]
    public void A_noncanonical_final_character_is_refused()
    {
        // 32 bytes encode to 43 characters; the last carries 4 data bits and 2
        // bits that must be zero.
        const string id = "0123456789abcdef01234567";
        var secret = Base64Url.EncodeToString(new byte[32]);
        Assert.Equal('A', secret[^1]);

        Assert.True(AgentTokens.TryParse($"prem_agt_{id}_{secret}", out _));
        Assert.False(AgentTokens.TryParse($"prem_agt_{id}_{secret[..^1]}B", out _));
    }

    [Fact]
    public void A_token_must_expire_after_it_is_created()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => AgentTokens.Issue(AgentId, Created, Created));
        Assert.Throws<ArgumentOutOfRangeException>(() => AgentTokens.Issue(AgentId, Created, Created.AddTicks(-1)));
    }

    [Fact]
    public void A_record_is_usable_until_the_exact_instant_of_expiry()
    {
        var record = Issue().Record;

        Assert.True(record.IsUsableAt(Expires.AddTicks(-1)));
        Assert.False(record.IsUsableAt(Expires));
        Assert.False(record.IsUsableAt(Expires.AddTicks(1)));
        Assert.False((record with { RevokedAt = Created }).IsUsableAt(Created.AddDays(1)));
    }
}

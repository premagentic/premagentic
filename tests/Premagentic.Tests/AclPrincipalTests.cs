using Premagentic.Core.Security.Acl;

namespace Premagentic.Tests;

public class AclPrincipalTests
{
    [Theory]
    [InlineData("user:42", PrincipalKind.User, "42")]
    [InlineData("group:0f8fad5b-d9cb-469f-a165-70867728950e", PrincipalKind.Group, "0f8fad5b-d9cb-469f-a165-70867728950e")]
    [InlineData("agent:7", PrincipalKind.Agent, "7")]
    [InlineData("sid:S-1-5-21-1004336348-1177238915-682003330-512", PrincipalKind.Sid, "S-1-5-21-1004336348-1177238915-682003330-512")]
    [InlineData("uid:1000", PrincipalKind.Uid, "1000")]
    [InlineData("gid:100", PrincipalKind.Gid, "100")]
    [InlineData("group:Domain Users", PrincipalKind.Group, "Domain Users")]
    [InlineData("group:a:b", PrincipalKind.Group, "a:b")]
    public void Well_formed_principals_parse_and_round_trip(string text, PrincipalKind kind, string value)
    {
        Assert.True(Principal.TryParse(text, out var principal));
        Assert.Equal(kind, principal.Kind);
        Assert.Equal(value, principal.Value);
        Assert.Equal(text, principal.ToString());
    }

    [Fact]
    public void Everyone_parses_to_the_one_well_known_instance()
    {
        Assert.True(Principal.TryParse("everyone", out var principal));
        Assert.Same(Principal.Everyone, principal);
        Assert.Equal(PrincipalKind.Everyone, principal.Kind);
        Assert.Equal("", principal.Value);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("user")]
    [InlineData("user:")]
    [InlineData(":42")]
    [InlineData("User:42")]
    [InlineData("USER:42")]
    [InlineData("role:admin")]
    [InlineData("users:42")]
    [InlineData(" user:42")]
    [InlineData("user :42")]
    [InlineData("user: 42")]
    [InlineData("user:42 ")]
    [InlineData("user:4\n2")]
    [InlineData("user:4\r2")]
    [InlineData("user:4\t2")]
    [InlineData("Everyone")]
    [InlineData("everyone:")]
    [InlineData("everyone:x")]
    [InlineData(" everyone")]
    [InlineData("everyone ")]
    public void Malformed_principals_are_rejected_not_repaired(string? text)
    {
        Assert.False(Principal.TryParse(text, out var principal));
        Assert.Null(principal);
    }

    [Fact]
    public void Control_characters_and_unpaired_surrogates_are_rejected()
    {
        // Built in code rather than in attributes, which store strings as UTF-8
        // and would turn a lone surrogate into U+FFFD before the test ran.
        const char nul = (char)0x0000;
        const char nextLine = (char)0x0085;
        const char high = (char)0xD83D;
        const char low = (char)0xDE00;

        foreach (var text in new[]
                 {
                     "user:4" + nul + "2",
                     "user:4" + nextLine + "2",
                     "user:" + high,
                     "user:a" + low,
                     "group:" + low + high,
                 })
        {
            Assert.False(Principal.TryParse(text, out _));
        }

        // A properly paired surrogate is an ordinary character.
        var paired = "" + high + low;
        Assert.True(Principal.TryParse("group:" + paired, out var principal));
        Assert.Equal(paired, principal.Value);
    }

    [Fact]
    public void Parse_throws_on_malformed_text()
    {
        Assert.Throws<FormatException>(() => Principal.Parse("role:admin"));
    }

    [Fact]
    public void Factories_refuse_invalid_values()
    {
        Assert.Throws<ArgumentException>(() => Principal.User(""));
        Assert.Throws<ArgumentException>(() => Principal.Group(" hr"));
        Assert.Throws<ArgumentException>(() => Principal.Agent("a\nb"));
        Assert.Throws<ArgumentException>(() => Principal.Of(PrincipalKind.Everyone, "x"));
        Assert.Throws<ArgumentOutOfRangeException>(() => Principal.Of((PrincipalKind)0, "x"));
        Assert.Throws<ArgumentOutOfRangeException>(() => Principal.Of((PrincipalKind)99, "x"));
    }

    [Fact]
    public void Equality_is_ordinal_over_kind_and_value()
    {
        Assert.Equal(Principal.Parse("group:hr"), Principal.Group("hr"));
        Assert.True(Principal.Parse("group:hr") == Principal.Group("hr"));
        Assert.NotEqual(Principal.Parse("group:hr"), Principal.Parse("group:HR"));
        Assert.NotEqual(Principal.Parse("user:1000"), Principal.Parse("uid:1000"));
        Assert.NotEqual(Principal.Parse("group:1000"), Principal.Parse("gid:1000"));
        Assert.Equal(Principal.Parse("group:hr").GetHashCode(), Principal.Group("hr").GetHashCode());
    }
}

public class AclPrincipalSetTests
{
    [Fact]
    public void Contains_matches_by_value_not_by_instance()
    {
        var set = PrincipalSet.Of(Principal.Group("staff"), Principal.Everyone);
        Assert.True(set.Contains(Principal.Parse("group:staff")));
        Assert.True(set.Contains(Principal.Everyone));
        Assert.False(set.Contains(Principal.Parse("group:Staff")));
        Assert.False(set.Contains(null!));
    }

    [Fact]
    public void Duplicates_collapse()
    {
        var set = PrincipalSet.Of(Principal.Group("staff"), Principal.Parse("group:staff"), Principal.Everyone);
        Assert.Equal(2, set.Count);
    }

    [Fact]
    public void The_empty_set_holds_nothing_not_even_everyone()
    {
        Assert.Empty(PrincipalSet.Empty);
        Assert.False(PrincipalSet.Empty.Contains(Principal.Everyone));
        Assert.Same(PrincipalSet.Empty, PrincipalSet.From([]));
    }

    [Fact]
    public void A_null_principal_is_refused()
    {
        Assert.Throws<ArgumentException>(() => PrincipalSet.Of(Principal.Everyone, null!));
    }

    [Fact]
    public void TryParse_is_all_or_nothing()
    {
        Assert.True(PrincipalSet.TryParse(["user:1", "group:2", "everyone"], out var good));
        Assert.Equal(3, good.Count);

        // One bad principal loses the whole set, never just itself.
        Assert.False(PrincipalSet.TryParse(["user:1", "role:admin", "everyone"], out var bad));
        Assert.Null(bad);
        Assert.False(PrincipalSet.TryParse(["user:1", null], out _));
        Assert.False(PrincipalSet.TryParse(null, out _));

        Assert.True(PrincipalSet.TryParse([], out var empty));
        Assert.Empty(empty);
    }
}

using System.Security.Cryptography;
using System.Text;
using Premagentic.Core.Security.Acl;

namespace Premagentic.Tests;

public class AclEntryTests
{
    [Theory]
    [InlineData("allow group:hr", AclEffect.Allow, "group:hr")]
    [InlineData("deny everyone", AclEffect.Deny, "everyone")]
    [InlineData("deny group:Domain Users", AclEffect.Deny, "group:Domain Users")]
    public void Canonical_lines_parse_and_round_trip(string text, AclEffect effect, string principal)
    {
        Assert.True(AclEntry.TryParse(text, out var entry));
        Assert.Equal(effect, entry.Effect);
        Assert.Equal(Principal.Parse(principal), entry.Principal);
        Assert.Equal(text, entry.ToString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("allow")]
    [InlineData("allow ")]
    [InlineData(" allow group:hr")]
    [InlineData("Allow group:hr")]
    [InlineData("ALLOW group:hr")]
    [InlineData("allow  group:hr")]
    [InlineData("allow\tgroup:hr")]
    [InlineData("grant group:hr")]
    [InlineData("allow role:hr")]
    [InlineData("allow group:hr ")]
    [InlineData("allow group:hr\r")]
    public void Malformed_lines_are_rejected(string? text)
    {
        Assert.False(AclEntry.TryParse(text, out var entry));
        Assert.Null(entry);
    }

    [Fact]
    public void An_undefined_effect_is_refused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new AclEntry(default, Principal.Everyone));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AclEntry((AclEffect)3, Principal.Everyone));
        Assert.Throws<ArgumentNullException>(() => new AclEntry(AclEffect.Allow, null!));
    }

    [Fact]
    public void Entries_compare_by_value()
    {
        Assert.Equal(AclEntry.Allow(Principal.Group("hr")), AclEntry.Allow(Principal.Parse("group:hr")));
        Assert.NotEqual(AclEntry.Allow(Principal.Group("hr")), AclEntry.Deny(Principal.Group("hr")));
    }
}

public class AclSetTests
{
    [Fact]
    public void Canonical_text_is_one_line_per_entry_in_the_given_order()
    {
        var set = AclSet.Of(AclEntry.Allow(Principal.Group("hr")), AclEntry.Deny(Principal.Everyone));
        Assert.Equal("allow group:hr\ndeny everyone\n", set.CanonicalText);
    }

    [Fact]
    public void The_hash_is_sha256_of_the_canonical_text_and_does_not_drift()
    {
        var set = AclSet.Of(AclEntry.Allow(Principal.Group("hr")), AclEntry.Deny(Principal.Everyone));

        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(set.CanonicalText))), set.Hash);

        // Pinned. Stored sets are deduplicated by this value, so a change to the
        // canonical form must be a deliberate migration, never an accident.
        Assert.Equal("3ecc7b89038900f44c485dabc2299dd273a947b53098dbf85df414dc851750e2", set.Hash);
        Assert.Equal("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855", AclSet.Empty.Hash);
    }

    [Fact]
    public void Identical_lists_deduplicate_to_one_set()
    {
        var a = AclSet.Of(AclEntry.Deny(Principal.Group("contractors")), AclEntry.Allow(Principal.Group("staff")));
        Assert.True(AclSet.TryParse(["deny group:contractors", "allow group:staff"], out var b));

        Assert.Equal(a, b);
        Assert.Equal(a.Hash, b.Hash);
        Assert.Single(new[] { a, b }.Distinct());
    }

    [Fact]
    public void Order_is_part_of_identity()
    {
        var denyFirst = AclSet.Of(AclEntry.Deny(Principal.Group("contractors")), AclEntry.Allow(Principal.Group("staff")));
        var allowFirst = AclSet.Of(AclEntry.Allow(Principal.Group("staff")), AclEntry.Deny(Principal.Group("contractors")));

        Assert.NotEqual(denyFirst, allowFirst);
        Assert.NotEqual(denyFirst.Hash, allowFirst.Hash);
    }

    [Fact]
    public void Duplicate_entries_are_kept_as_given()
    {
        Assert.True(AclSet.TryParse(["allow group:hr", "allow group:hr"], out var set));
        Assert.Equal(2, set.Entries.Count);
        Assert.NotEqual(AclSet.Of(AclEntry.Allow(Principal.Group("hr"))), set);
    }

    [Fact]
    public void The_empty_set_is_empty_and_denies()
    {
        Assert.True(AclSet.Empty.IsEmpty);
        Assert.Equal("", AclSet.Empty.CanonicalText);
        Assert.Same(AclSet.Empty, AclSet.Create([]));
        Assert.False(AclEvaluator.CanRead(AclSet.Empty, PrincipalSet.Of(Principal.User("1"), Principal.Everyone)));
    }

    [Fact]
    public void TryParse_rejects_the_whole_list_for_one_bad_line()
    {
        Assert.False(AclSet.TryParse(["deny group:contractors", "allow role:staff", "allow group:staff"], out var set));
        Assert.Null(set);
        Assert.False(AclSet.TryParse(["allow group:staff", null], out _));
        Assert.False(AclSet.TryParse(null, out _));
    }

    [Fact]
    public void Entries_cannot_be_changed_through_the_list()
    {
        var set = AclSet.Of(AclEntry.Allow(Principal.Group("hr")));
        Assert.False(set.Entries is AclEntry[]);
        Assert.Throws<NotSupportedException>(() => ((IList<AclEntry>)set.Entries)[0] = AclEntry.Allow(Principal.Everyone));
    }

    [Fact]
    public void Create_refuses_a_null_entry()
    {
        Assert.Throws<ArgumentException>(() => AclSet.Of(AclEntry.Allow(Principal.Everyone), null!));
    }

    [Fact]
    public void Canonical_text_round_trips()
    {
        var set = AclSet.Of(AclEntry.Allow(Principal.Group("hr")), AclEntry.Deny(Principal.Everyone));
        Assert.True(AclSet.TryParseCanonical(set.CanonicalText, out var again));
        Assert.Equal(set, again);

        Assert.True(AclSet.TryParseCanonical("", out var empty));
        Assert.Same(AclSet.Empty, empty);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("allow group:hr")]
    [InlineData("allow group:hr\r\n")]
    [InlineData("allow group:hr\n\n")]
    [InlineData("\nallow group:hr\n")]
    [InlineData("allow group:hr\ndeny role:x\n")]
    [InlineData("\n")]
    public void TryParseCanonical_accepts_only_the_exact_canonical_form(string? text)
    {
        Assert.False(AclSet.TryParseCanonical(text, out var set));
        Assert.Null(set);
    }
}

public class AclPermittedSetsTests
{
    private static readonly PrincipalSet Bob =
        PrincipalSet.Of(Principal.User("bob"), Principal.Group("staff"), Principal.Group("contractors"), Principal.Everyone);

    private static AclSet Parse(params string[] lines) =>
        AclSet.TryParse(lines, out var set) ? set : throw new InvalidOperationException("bad fixture");

    [Fact]
    public void Returns_the_permitted_ids_ascending_and_only_those()
    {
        var sets = new[]
        {
            new StoredAclSet(30, Parse("allow group:staff")),
            new StoredAclSet(10, Parse("deny group:contractors", "allow group:staff")),
            new StoredAclSet(20, Parse("allow group:staff", "deny group:contractors")),
            new StoredAclSet(40, AclSet.Empty),
            new StoredAclSet(50, Parse("allow group:hr")),
            new StoredAclSet(5, Parse("allow everyone")),
        };

        Assert.Equal([5L, 20L, 30L], PermittedSets.Resolve(sets, Bob));
    }

    [Fact]
    public void A_caller_with_no_principals_is_permitted_nothing()
    {
        var sets = new[] { new StoredAclSet(1, Parse("allow everyone")), new StoredAclSet(2, Parse("allow group:staff")) };
        Assert.Empty(PermittedSets.Resolve(sets, PrincipalSet.Empty));
    }

    [Fact]
    public void No_sets_means_nothing_permitted()
    {
        Assert.Empty(PermittedSets.Resolve([], Bob));
    }

    [Fact]
    public void An_id_given_twice_is_permitted_only_if_both_permit()
    {
        var conflicting = new[]
        {
            new StoredAclSet(7, Parse("allow everyone")),
            new StoredAclSet(7, Parse("deny group:contractors", "allow everyone")),
            new StoredAclSet(8, Parse("allow group:staff")),
            new StoredAclSet(8, Parse("allow everyone")),
        };

        Assert.Equal([8L], PermittedSets.Resolve(conflicting, Bob));
    }

    [Fact]
    public void Null_arguments_throw()
    {
        Assert.Throws<ArgumentNullException>(() => PermittedSets.Resolve(null!, Bob));
        Assert.Throws<ArgumentNullException>(() => PermittedSets.Resolve([], null!));
        Assert.Throws<ArgumentNullException>(() => new StoredAclSet(1, null!));
    }
}

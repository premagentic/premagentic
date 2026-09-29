using Premagentic.Core.Security.Acl;

namespace Premagentic.Tests;

public class AclFolderRuleTests
{
    private static readonly AclSet Everyone = AclSet.Of(AclEntry.Allow(Principal.Everyone));
    private static readonly AclSet HrOnly = AclSet.Of(AclEntry.Allow(Principal.Group("hr")));
    private static readonly AclSet PayrollOnly = AclSet.Of(AclEntry.Allow(Principal.Group("payroll")));

    private static FolderRuleMatcher Matcher() => new([
        new FolderRule("corp", "", Everyone),
        new FolderRule("corp", "hr", HrOnly),
        new FolderRule("corp", "hr/pay", PayrollOnly),
    ]);

    [Theory]
    [InlineData("handbook.md", "")]
    [InlineData("hr", "hr")]
    [InlineData("hr/policy.md", "hr")]
    [InlineData("hr/pay", "hr/pay")]
    [InlineData("hr/pay/bands.md", "hr/pay")]
    [InlineData("hr/payments.md", "hr")]
    [InlineData("hr-archive/pay.md", "")]
    [InlineData("hrx", "")]
    [InlineData("HR/policy.md", "")]
    public void The_longest_prefix_at_a_segment_boundary_wins(string path, string expectedPrefix)
    {
        var rule = Matcher().Match("corp", path);
        Assert.NotNull(rule);
        Assert.Equal(expectedPrefix, rule.PathPrefix);
    }

    [Fact]
    public void A_prefix_does_not_match_a_longer_sibling_name()
    {
        var matcher = new FolderRuleMatcher([new FolderRule("corp", "hr", HrOnly)]);
        Assert.Null(matcher.Match("corp", "hr-archive/x"));
        Assert.NotNull(matcher.Match("corp", "hr/x"));
    }

    [Fact]
    public void No_matching_rule_yields_the_empty_set_which_denies()
    {
        var matcher = new FolderRuleMatcher([new FolderRule("corp", "hr", HrOnly)]);
        var acl = matcher.AclFor("corp", "finance/q3.md");

        Assert.Same(AclSet.Empty, acl);
        Assert.False(AclEvaluator.CanRead(acl, PrincipalSet.Of(Principal.Group("hr"), Principal.Everyone)));
    }

    [Fact]
    public void Rules_apply_only_to_their_own_source()
    {
        Assert.Null(Matcher().Match("wiki", "handbook.md"));
        Assert.Null(Matcher().Match("Corp", "handbook.md"));
        Assert.Same(AclSet.Empty, Matcher().AclFor("wiki", "hr/policy.md"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("/hr/policy.md")]
    [InlineData("hr/policy.md/")]
    [InlineData("hr//policy.md")]
    [InlineData("hr\\policy.md")]
    [InlineData("hr/../pay/bands.md")]
    [InlineData("./hr/policy.md")]
    [InlineData("hr/\npolicy.md")]
    public void A_path_not_in_clean_relative_form_matches_nothing_even_a_root_rule(string path)
    {
        Assert.Null(Matcher().Match("corp", path));
        Assert.Same(AclSet.Empty, Matcher().AclFor("corp", path));
    }

    [Fact]
    public void Null_source_or_path_matches_nothing()
    {
        Assert.Null(Matcher().Match(null!, "hr/policy.md"));
        Assert.Null(Matcher().Match("corp", null!));
        Assert.False(new FolderRule("corp", "", Everyone).Covers(null!));
    }

    [Theory]
    [InlineData("/hr")]
    [InlineData("hr/")]
    [InlineData("hr//pay")]
    [InlineData("hr\\pay")]
    [InlineData("..")]
    [InlineData("hr/.")]
    [InlineData("hr/\tpay")]
    public void Malformed_prefixes_are_rejected(string prefix)
    {
        Assert.Throws<ArgumentException>(() => new FolderRule("corp", prefix, HrOnly));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" corp")]
    [InlineData("corp ")]
    [InlineData("co\nrp")]
    public void Malformed_source_names_are_rejected(string source)
    {
        Assert.Throws<ArgumentException>(() => new FolderRule(source, "hr", HrOnly));
    }

    [Fact]
    public void Two_rules_for_one_folder_are_refused()
    {
        Assert.Throws<ArgumentException>(() => new FolderRuleMatcher([
            new FolderRule("corp", "hr", HrOnly),
            new FolderRule("corp", "hr", Everyone),
        ]));
    }

    [Fact]
    public void The_same_prefix_in_two_sources_is_two_rules()
    {
        var matcher = new FolderRuleMatcher([
            new FolderRule("corp", "hr", HrOnly),
            new FolderRule("wiki", "hr", Everyone),
        ]);
        Assert.Same(HrOnly, matcher.AclFor("corp", "hr/a.md"));
        Assert.Same(Everyone, matcher.AclFor("wiki", "hr/a.md"));
    }

    [Fact]
    public void Rule_order_in_the_list_does_not_matter()
    {
        var reversed = new FolderRuleMatcher([
            new FolderRule("corp", "hr/pay", PayrollOnly),
            new FolderRule("corp", "hr", HrOnly),
            new FolderRule("corp", "", Everyone),
        ]);
        Assert.Same(PayrollOnly, reversed.AclFor("corp", "hr/pay/bands.md"));
        Assert.Same(HrOnly, reversed.AclFor("corp", "hr/policy.md"));
    }
}

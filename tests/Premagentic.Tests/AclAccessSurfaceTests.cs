using Premagentic.Core;
using Premagentic.Core.Identity;
using Premagentic.Core.Retrieval.Gates;
using Premagentic.Core.Security;
using Premagentic.Core.Security.Acl;
using Npgsql;

namespace Premagentic.Tests;

/// <summary>
/// The connector-facing and host-facing surface, converted to access lists and
/// principal sets with the strict parsers, and the one place that decides a
/// document's list. No database.
/// </summary>
public class AclAccessSurfaceTests
{
    private static AclSet Parse(params string[] lines) =>
        AclSet.TryParse(lines, out var set) ? set : throw new InvalidOperationException("bad fixture");

    [Fact]
    public void Connector_values_become_ordered_lists()
    {
        Assert.Equal(Parse("allow everyone"), DocumentAccess.Everyone.ConnectorAcl());
        Assert.Equal(Parse("allow group:hr", "allow user:1"), DocumentAccess.For("group:hr", "", "user:1", "group:hr").ConnectorAcl());
        Assert.Same(AclSet.Empty, DocumentAccess.NoOne.ConnectorAcl());

        var fromSource = Parse("deny group:c", "allow group:s");
        Assert.Same(fromSource, DocumentAccess.FromSource(fromSource).ConnectorAcl());
    }

    [Fact]
    public void A_connector_principal_that_does_not_parse_makes_the_document_reach_nobody()
    {
        // "hr" has no kind. Dropping it would leave user:1; guessing a kind for it
        // would match the wrong caller. The whole value fails closed instead.
        var access = DocumentAccess.For("hr", "user:1");
        Assert.Same(AclSet.Empty, access.ConnectorAcl());
        Assert.True(access.DeniesEveryone);
    }

    [Fact]
    public void The_folder_rules_value_is_decided_by_the_rules_not_counted_here()
    {
        Assert.True(DocumentAccess.FolderRules.UsesFolderRules);
        Assert.False(DocumentAccess.FolderRules.DeniesEveryone);
        Assert.False(DocumentAccess.Everyone.UsesFolderRules);
    }

    [Fact]
    public void The_decision_takes_the_connector_value_first_then_the_rules_then_nobody()
    {
        var rules = new FolderRuleMatcher([new FolderRule("s", "hr", Parse("allow group:hr"))]);

        var connector = AccessDecision.For(DocumentAccess.Everyone, "s", "hr/a.md", rules);
        Assert.Equal(Parse("allow everyone"), connector.Set);
        Assert.False(connector.FromFolderRule);

        var ruled = AccessDecision.For(DocumentAccess.FolderRules, "s", "hr/a.md", rules);
        Assert.Equal(Parse("allow group:hr"), ruled.Set);
        Assert.True(ruled.FromFolderRule);

        var uncovered = AccessDecision.For(DocumentAccess.FolderRules, "s", "finance/a.md", rules);
        Assert.Same(AclSet.Empty, uncovered.Set);
        Assert.True(uncovered.FromFolderRule);

        // A path the matcher refuses stays denied and is never marked as the rules',
        // so no later rule change can reach it by prefix.
        var unclean = AccessDecision.For(DocumentAccess.FolderRules, "s", "hr//a.md", rules);
        Assert.Same(AclSet.Empty, unclean.Set);
        Assert.False(unclean.FromFolderRule);
    }

    [Fact]
    public void A_host_scope_holds_its_principals_and_everyone()
    {
        var scope = AccessScope.ForPrincipals("t", "group:hr", " ", "user:1");
        Assert.Equal(["group:hr", "user:1"], scope.Principals);
        Assert.True(scope.Holds.Contains(Principal.Group("hr")));
        Assert.True(scope.Holds.Contains(Principal.Everyone));
        Assert.Equal(3, scope.Holds.Count);

        Assert.Equal([Principal.Everyone], AccessScope.PublicOnly.Holds);
        Assert.Empty(AccessScope.PublicOnly.Principals);
    }

    [Fact]
    public void A_host_scope_with_one_malformed_principal_holds_nothing_at_all()
    {
        var scope = AccessScope.ForPrincipals("t", "group:hr", "hr");
        Assert.Empty(scope.Holds);
        Assert.False(scope.CanRead(Parse("allow everyone")));
    }

    [Fact]
    public void Unrestricted_reads_everything_and_only_it_does()
    {
        Assert.True(AccessScope.UnrestrictedAudited("r").CanRead(AclSet.Empty));
        Assert.False(AccessScope.PublicOnly.CanRead(AclSet.Empty));
    }

    [Fact]
    public void An_agent_acting_for_a_user_can_be_narrowed_and_never_widened()
    {
        var user = Guid.NewGuid();
        var agent = Guid.NewGuid();
        var u = $"user:{CallerResolver.IdText(user)}";
        var a = $"agent:{CallerResolver.IdText(agent)}";
        var principals = PrincipalSet.Of(Principal.Parse(u), Principal.Everyone);

        var asUser = AccessScope.ForCaller(new ResolvedCaller(CallerStatus.Resolved, principals, user, null, null));
        var asAgent = AccessScope.ForCaller(new ResolvedCaller(
            CallerStatus.Resolved, principals, user, agent, "t", NarrowedBy: Principal.Parse(a)));

        var denyTheAgent = Parse($"deny {a}", $"allow {u}");
        Assert.True(asUser.CanRead(denyTheAgent));
        Assert.False(asAgent.CanRead(denyTheAgent));

        var allowOnlyTheAgent = Parse($"allow {a}");
        Assert.False(asAgent.CanRead(allowOnlyTheAgent));

        var both = Parse($"allow {u}");
        Assert.True(asAgent.CanRead(both));
        Assert.Equal(user, asAgent.UserId);
        Assert.Equal(agent, asAgent.AgentId);
        Assert.StartsWith($"agent:{CallerResolver.IdText(agent)}", asAgent.AuditLabel);
    }

    [Fact]
    public void A_refused_caller_holds_nothing_and_its_label_says_why()
    {
        var scope = AccessScope.ForCaller(new ResolvedCaller(CallerStatus.TokenExpired, PrincipalSet.Empty, null, Guid.NewGuid(), "t"));
        Assert.Empty(scope.Holds);
        Assert.Equal("refused:TokenExpired", scope.AuditLabel);
        Assert.False(scope.CanRead(Parse("allow everyone")));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(true, "deny group:x")]
    [InlineData(false, "allow group:x")]
    [InlineData(true, "deny everyone", "allow group:x")]
    [InlineData(false, "deny group:x", "allow everyone")]
    [InlineData(true, "deny group:x", "deny everyone", "allow user:1")]
    public void A_list_denies_everyone_when_no_allow_comes_before_a_deny_for_everyone(bool denies, params string[] lines)
    {
        Assert.Equal(denies, Parse(lines).DeniesEveryone);
    }

    [Fact]
    public void The_gate_refuses_a_scope_nobody_resolved()
    {
        var gates = GateSet.Default;
        using var cmd = new NpgsqlCommand();

        var ex = Assert.Throws<InvalidOperationException>(() =>
            gates.AddParameters(cmd, new GateContext(new SearchOptions(AccessScope.PublicOnly))));
        Assert.Contains("not resolved", ex.Message);

        // Unrestricted needs no lists, and says so to the database.
        gates.AddParameters(cmd, new GateContext(new SearchOptions(AccessScope.UnrestrictedAudited("r"))));
        Assert.True((bool)cmd.Parameters["unrestricted"].Value!);
        Assert.Empty((long[])cmd.Parameters["permitted"].Value!);
    }
}

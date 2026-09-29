using Premagentic.Core;
using Premagentic.Core.Okf;
using Premagentic.Core.Retrieval.Gates;
using Premagentic.Core.Security;

namespace Premagentic.Tests;

public class TrustPolicyTests
{
    [Fact]
    public void A_search_that_names_no_policy_runs_under_the_strict_one()
    {
        var options = new SearchOptions(AccessScope.PublicOnly);

        Assert.Null(options.Trust);
        Assert.Equal(TrustPolicy.Strict, options.EffectiveTrust);
        Assert.Equal(new TrustPolicy(OkfTrustTier.HumanReviewed, IncludeStale: false), TrustPolicy.Strict);
    }

    [Fact]
    public void The_defaults_hold_agents_to_reviewed_fresh_content_and_show_people_everything()
    {
        Assert.Equal(new TrustPolicy(OkfTrustTier.HumanReviewed, false), TrustPolicy.Resolve(CallerKind.Agent, null));
        Assert.Equal(new TrustPolicy(OkfTrustTier.Unverified, true), TrustPolicy.Resolve(CallerKind.Person, null));
    }

    [Theory]
    [InlineData("unverified", OkfTrustTier.Unverified)]
    [InlineData("machine-confirmed", OkfTrustTier.MachineConfirmed)]
    [InlineData("Machine_Confirmed", OkfTrustTier.MachineConfirmed)]
    [InlineData("MachineConfirmed", OkfTrustTier.MachineConfirmed)]
    [InlineData(" human-reviewed ", OkfTrustTier.HumanReviewed)]
    [InlineData("HUMAN REVIEWED", OkfTrustTier.HumanReviewed)]
    public void A_per_agent_minimum_overrides_the_deployment_setting(string stored, OkfTrustTier expected)
    {
        var loose = new TrustSettings(AgentMinimumTier: OkfTrustTier.Unverified);

        Assert.Equal(expected, TrustPolicy.Resolve(CallerKind.Agent, stored, loose).MinimumMachineTier);
        Assert.Equal(expected, TrustPolicy.Resolve(CallerKind.Agent, stored).MinimumMachineTier);
    }

    [Theory]
    [InlineData("trusted")]
    [InlineData("0")]
    [InlineData("1")]
    [InlineData("unverified please")]
    public void An_unreadable_per_agent_minimum_is_the_strictest(string stored)
    {
        // Even when the deployment lets agents see everything.
        var loose = new TrustSettings(AgentMinimumTier: OkfTrustTier.Unverified, Stale: StaleVisibility.ShownToEveryone);

        Assert.Equal(OkfTrustTier.HumanReviewed, TrustPolicy.Resolve(CallerKind.Agent, stored, loose).MinimumMachineTier);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void An_agent_with_no_minimum_of_its_own_gets_the_deployment_setting(string? stored)
    {
        var settings = new TrustSettings(AgentMinimumTier: OkfTrustTier.MachineConfirmed);

        Assert.Equal(OkfTrustTier.MachineConfirmed, TrustPolicy.Resolve(CallerKind.Agent, stored, settings).MinimumMachineTier);
    }

    [Fact]
    public void A_person_is_never_held_to_an_agent_minimum()
    {
        Assert.Equal(OkfTrustTier.Unverified, TrustPolicy.Resolve(CallerKind.Person, "human-reviewed").MinimumMachineTier);
        Assert.Equal(OkfTrustTier.MachineConfirmed,
            TrustPolicy.Resolve(CallerKind.Person, "unverified", new TrustSettings(PersonMinimumTier: OkfTrustTier.MachineConfirmed)).MinimumMachineTier);
    }

    [Theory]
    [InlineData(StaleVisibility.ShownToPeopleOnly, true, false)]
    [InlineData(StaleVisibility.HiddenFromEveryone, false, false)]
    [InlineData(StaleVisibility.ShownToEveryone, true, true)]
    public void The_stale_setting_decides_who_sees_stale_content(StaleVisibility stale, bool person, bool agent)
    {
        var settings = new TrustSettings(Stale: stale);

        Assert.Equal(person, TrustPolicy.Resolve(CallerKind.Person, null, settings).IncludeStale);
        Assert.Equal(agent, TrustPolicy.Resolve(CallerKind.Agent, null, settings).IncludeStale);
    }

    [Fact]
    public void Values_outside_the_enums_resolve_to_the_strictest_reading()
    {
        var corrupt = new TrustSettings((OkfTrustTier)(-1), (OkfTrustTier)7, (StaleVisibility)9);

        Assert.Equal(TrustPolicy.Strict, TrustPolicy.Resolve(CallerKind.Agent, null, corrupt));
        Assert.Equal(TrustPolicy.Strict, TrustPolicy.Resolve(CallerKind.Person, null, corrupt));
        // A caller that is neither a person nor an agent is treated as an agent.
        Assert.Equal(TrustPolicy.Strict, TrustPolicy.Resolve((CallerKind)0, null));
    }

    [Theory]
    [InlineData(OkfTrustTier.Unverified)]
    [InlineData(OkfTrustTier.MachineConfirmed)]
    [InlineData(OkfTrustTier.HumanReviewed)]
    public void The_stored_name_of_a_tier_reads_back_as_the_same_tier(OkfTrustTier tier)
    {
        Assert.True(TrustPolicy.TryParseTier(TrustPolicy.TierKey(tier), out var back));
        Assert.Equal(tier, back);
    }

    [Fact]
    public void The_stored_numbers_never_change()
    {
        Assert.Equal([0, 1, 2], new[] { OkfTrustTier.Unverified, OkfTrustTier.MachineConfirmed, OkfTrustTier.HumanReviewed }.Select(t => (int)t));
        Assert.Equal([0, 1, 2], new[] { OkfAuthorship.Unknown, OkfAuthorship.Human, OkfAuthorship.Machine }.Select(a => (int)a));
    }
}

public class TrustGateContextTests
{
    [Fact]
    public void Now_is_fixed_once_from_the_options_and_kept_in_utc()
    {
        var asOf = new DateTimeOffset(2026, 7, 1, 8, 0, 0, TimeSpan.FromHours(-4));
        var context = new GateContext(new SearchOptions(AccessScope.PublicOnly, AsOf: asOf), new FixedClock(DateTimeOffset.UnixEpoch));

        Assert.Equal(asOf, context.Now);
        Assert.Equal(TimeSpan.Zero, context.Now.Offset);
    }

    [Fact]
    public void Without_an_instant_in_the_options_the_clock_is_read_once()
    {
        var clock = new FixedClock(new DateTimeOffset(2026, 9, 21, 12, 0, 0, TimeSpan.Zero));
        var context = new GateContext(new SearchOptions(AccessScope.PublicOnly), clock);

        Assert.Equal(clock.GetUtcNow(), context.Now);
        Assert.Equal(context.Now, context.Now);
    }

    [Fact]
    public void The_trust_and_freshness_gates_name_only_the_document_alias()
    {
        foreach (var gate in new IGate[] { new TrustGate(), new FreshnessGate() })
        {
            Assert.Matches(@"\bd\.", gate.Sql);
            Assert.DoesNotMatch(@"\bc\.", gate.Sql);
        }
    }

    [Fact]
    public void Both_gates_are_in_the_default_set_and_in_the_section_lookup()
    {
        Assert.Contains(TrustGate.GateName, GateSet.Default.Names);
        Assert.Contains(FreshnessGate.GateName, GateSet.Default.Names);
        Assert.Contains(TrustGate.GateName, GateSet.Default.Without(LifecycleGate.GateName).Names);
        Assert.Contains(FreshnessGate.GateName, GateSet.Default.Without(LifecycleGate.GateName).Names);
    }
}

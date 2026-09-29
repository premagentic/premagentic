using Premagentic.Core.Okf;

namespace Premagentic.Tests;

/// <summary>
/// How stored trust settings are read, with no database: a missing key is its
/// default, an unreadable value is the strictest, and a value is spelled one
/// way when stored.
/// </summary>
public class TrustSettingsTests
{
    private static Dictionary<string, string> All(string json) =>
        TrustSettingsStore.Keys.ToDictionary(k => k, _ => json);

    [Fact]
    public void Nothing_stored_is_the_defaults()
    {
        Assert.Equal(TrustSettings.Default, TrustSettingsStore.FromStored(new Dictionary<string, string>()));
    }

    [Fact]
    public void Stored_values_are_read()
    {
        var settings = TrustSettingsStore.FromStored(new Dictionary<string, string>
        {
            [TrustSettingsStore.AgentsMinimumTier] = "\"machine-confirmed\"",
            [TrustSettingsStore.PeopleMinimumTier] = "\"human-reviewed\"",
            [TrustSettingsStore.Stale] = "\"shown-to-everyone\"",
        });

        Assert.Equal(
            new TrustSettings(OkfTrustTier.MachineConfirmed, OkfTrustTier.HumanReviewed, StaleVisibility.ShownToEveryone),
            settings);
    }

    [Theory]
    [InlineData("\"nonsense\"")]
    [InlineData("\"\"")]
    [InlineData("2")]
    [InlineData("null")]
    [InlineData("true")]
    [InlineData("{\"tier\": \"unverified\"}")]
    [InlineData("[\"unverified\"]")]
    [InlineData("not json at all")]
    public void An_unreadable_stored_value_is_the_strictest_never_the_default(string json)
    {
        var settings = TrustSettingsStore.FromStored(All(json));

        Assert.Equal(
            new TrustSettings(OkfTrustTier.HumanReviewed, OkfTrustTier.HumanReviewed, StaleVisibility.HiddenFromEveryone),
            settings);
    }

    [Theory]
    [InlineData(TrustSettingsStore.AgentsMinimumTier, "Machine Confirmed", "machine-confirmed")]
    [InlineData(TrustSettingsStore.PeopleMinimumTier, "HUMAN_REVIEWED", "human-reviewed")]
    [InlineData(TrustSettingsStore.Stale, "shown to everyone", "shown-to-everyone")]
    [InlineData(TrustSettingsStore.Stale, "HiddenFromEveryone", "hidden-from-everyone")]
    public void A_value_is_read_loosely_and_stored_one_way(string key, string text, string stored)
    {
        Assert.True(TrustSettingsStore.TryNormalize(key, text, out var value));
        Assert.Equal(stored, value);
    }

    [Theory]
    [InlineData(TrustSettingsStore.AgentsMinimumTier, "2")]
    [InlineData(TrustSettingsStore.AgentsMinimumTier, "reviewed")]
    [InlineData(TrustSettingsStore.PeopleMinimumTier, "")]
    [InlineData(TrustSettingsStore.Stale, "sometimes")]
    [InlineData(TrustSettingsStore.Stale, "human-reviewed")]
    public void A_value_outside_the_allowed_ones_does_not_parse(string key, string text)
    {
        Assert.False(TrustSettingsStore.TryNormalize(key, text, out _));
    }

    [Fact]
    public void An_unknown_key_is_refused_and_named_with_the_known_ones()
    {
        var ex = Assert.Throws<ArgumentException>(() => TrustSettingsStore.TryNormalize("trust.default_tier", "unverified", out _));

        Assert.Contains("trust.default_tier", ex.Message);
        Assert.Contains("trust.agents_minimum_tier, trust.people_minimum_tier, trust.stale", ex.Message);
    }

    [Theory]
    [InlineData(TrustSettingsStore.AgentsMinimumTier, "human-reviewed", "machine-confirmed", true)]
    [InlineData(TrustSettingsStore.AgentsMinimumTier, "machine-confirmed", "unverified", true)]
    [InlineData(TrustSettingsStore.AgentsMinimumTier, "unverified", "human-reviewed", false)]
    [InlineData(TrustSettingsStore.AgentsMinimumTier, "human-reviewed", "human-reviewed", false)]
    [InlineData(TrustSettingsStore.PeopleMinimumTier, "human-reviewed", "unverified", true)]
    [InlineData(TrustSettingsStore.Stale, "hidden-from-everyone", "shown-to-people-only", true)]
    [InlineData(TrustSettingsStore.Stale, "shown-to-people-only", "shown-to-everyone", true)]
    [InlineData(TrustSettingsStore.Stale, "shown-to-everyone", "hidden-from-everyone", false)]
    [InlineData(TrustSettingsStore.Stale, "shown-to-people-only", "shown-to-people-only", false)]
    public void Looser_means_more_gets_through(string key, string from, string to, bool looser)
    {
        Assert.Equal(looser, TrustSettingsStore.IsLooser(key, from, to));
    }

    [Fact]
    public void The_defaults_are_the_safe_values_the_resolver_starts_from()
    {
        Assert.Equal("human-reviewed", TrustSettingsStore.DefaultValue(TrustSettingsStore.AgentsMinimumTier));
        Assert.Equal("unverified", TrustSettingsStore.DefaultValue(TrustSettingsStore.PeopleMinimumTier));
        Assert.Equal("shown-to-people-only", TrustSettingsStore.DefaultValue(TrustSettingsStore.Stale));
    }
}

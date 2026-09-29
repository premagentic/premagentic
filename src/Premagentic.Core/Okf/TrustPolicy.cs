namespace Premagentic.Core.Okf;

/// <summary>Who is asking. What each may see is set separately.</summary>
public enum CallerKind
{
    Person = 1,
    Agent = 2,
}

/// <summary>How content past its <c>stale_after</c> is treated, deployment wide.</summary>
public enum StaleVisibility
{
    /// <summary>Hidden from agents and shown to people with a stale flag. How every install starts.</summary>
    ShownToPeopleOnly = 0,

    /// <summary>Hidden from everyone.</summary>
    HiddenFromEveryone = 1,

    /// <summary>Shown to everyone, flagged.</summary>
    ShownToEveryone = 2,
}

/// <summary>
/// The deployment-wide settings a search policy is resolved from. The defaults
/// are how every install starts: agents see machine-written content only once
/// a person has reviewed it, people see everything with its tier on it, and
/// stale content is hidden from agents and flagged for people.
/// </summary>
/// <param name="AgentMinimumTier">What agents may see of machine-written content.</param>
/// <param name="PersonMinimumTier">What people may see of machine-written content.</param>
/// <param name="Stale">What happens to content past its <c>stale_after</c>.</param>
public sealed record TrustSettings(
    OkfTrustTier AgentMinimumTier = OkfTrustTier.HumanReviewed,
    OkfTrustTier PersonMinimumTier = OkfTrustTier.Unverified,
    StaleVisibility Stale = StaleVisibility.ShownToPeopleOnly)
{
    public static TrustSettings Default { get; } = new();
}

/// <summary>
/// What one search may return of machine-written content and of stale content.
/// The trust gate and the freshness gate apply it when the question is asked,
/// against values stored per document at ingest, so a change takes effect on
/// the next search with no re-ingest.
/// <para>
/// Whatever the policy, every result still carries its trust tier, authorship
/// and stale flag, so whoever receives a passage can tell what it is.
/// </para>
/// </summary>
/// <param name="MinimumMachineTier">
/// The lowest trust tier at which machine-written content is served.
/// Human-written content, and content whose authorship is unknown, which is
/// every ordinary file, are never held back by it.
/// </param>
/// <param name="IncludeStale">Whether content past its <c>stale_after</c> is served, flagged, or left out.</param>
public sealed record TrustPolicy(OkfTrustTier MinimumMachineTier, bool IncludeStale)
{
    /// <summary>
    /// Machine-written content only once a person has reviewed it, and nothing
    /// stale. This is what a search gets when no policy is given, so a host that
    /// forgets to set one fails safe.
    /// </summary>
    public static TrustPolicy Strict { get; } = new(OkfTrustTier.HumanReviewed, IncludeStale: false);

    /// <summary>
    /// Resolves the policy for one caller. Pure: the same inputs always give the
    /// same policy, and nothing is read from anywhere else.
    /// </summary>
    /// <param name="caller">A person, or an agent. Anything else is resolved as an agent, the stricter reading.</param>
    /// <param name="agentMinimumTier">
    /// The per-agent override, as stored on the agent. Null or blank means none
    /// is set and the deployment setting applies. A value that is set and cannot
    /// be read means human-reviewed, the strictest, because an override nobody
    /// can read must not loosen anything.
    /// Ignored for a person.
    /// </param>
    /// <param name="settings">The deployment settings, or the defaults when null.</param>
    public static TrustPolicy Resolve(CallerKind caller, string? agentMinimumTier, TrustSettings? settings = null)
    {
        settings ??= TrustSettings.Default;

        if (caller == CallerKind.Person)
            return new TrustPolicy(
                Known(settings.PersonMinimumTier),
                IncludeStale: settings.Stale is StaleVisibility.ShownToPeopleOnly or StaleVisibility.ShownToEveryone);

        var minimum = string.IsNullOrWhiteSpace(agentMinimumTier)
            ? Known(settings.AgentMinimumTier)
            : TryParseTier(agentMinimumTier, out var tier) ? tier : OkfTrustTier.HumanReviewed;

        return new TrustPolicy(minimum, IncludeStale: settings.Stale == StaleVisibility.ShownToEveryone);
    }

    /// <summary>
    /// Reads a tier written as the spec names it (<c>unverified</c>,
    /// <c>machine-confirmed</c>, <c>human-reviewed</c>) or as the enum names it,
    /// ignoring case, spaces, hyphens and underscores. Numbers are not accepted.
    /// </summary>
    public static bool TryParseTier(string? text, out OkfTrustTier tier)
    {
        var key = new string((text ?? "").Where(c => c is not ('-' or '_' or ' ')).ToArray()).ToLowerInvariant();
        (var ok, tier) = key switch
        {
            "unverified" => (true, OkfTrustTier.Unverified),
            "machineconfirmed" => (true, OkfTrustTier.MachineConfirmed),
            "humanreviewed" => (true, OkfTrustTier.HumanReviewed),
            _ => (false, OkfTrustTier.HumanReviewed),
        };
        return ok;
    }

    /// <summary>The spec's name for a tier, which is the form to store when a tier is kept as text.</summary>
    public static string TierKey(OkfTrustTier tier) => tier switch
    {
        OkfTrustTier.Unverified => "unverified",
        OkfTrustTier.MachineConfirmed => "machine-confirmed",
        OkfTrustTier.HumanReviewed => "human-reviewed",
        _ => throw new ArgumentOutOfRangeException(nameof(tier), tier, "Not a trust tier."),
    };

    // A setting outside the enum, from a corrupt or hand-edited store, is read
    // as the strictest tier rather than compared as a number.
    private static OkfTrustTier Known(OkfTrustTier tier) =>
        Enum.IsDefined(tier) ? tier : OkfTrustTier.HumanReviewed;
}

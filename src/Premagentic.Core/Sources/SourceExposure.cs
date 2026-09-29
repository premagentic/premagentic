using Premagentic.Core.Identity;
using Premagentic.Core.Security.Acl;

namespace Premagentic.Core.Sources;

/// <summary>
/// Whether a source's documents may reach a model running outside the network.
/// </summary>
public enum SourceExposureState
{
    /// <summary>
    /// The source's folder is held (<see cref="Security.HostedHolds"/>), which
    /// is the switch being off. Nothing at or beneath it is served to an agent
    /// whose model runs outside the network, whatever rule decides it.
    /// </summary>
    NeverLeaves = 0,

    /// <summary>
    /// Nothing holds the folder back, and its rule does not deny the
    /// hosted-model agents group, so an agent with a hosted model reads this
    /// source like any other caller.
    /// </summary>
    MayBeServed = 1,

    /// <summary>
    /// The folder is not held, but its rule denies the group: somebody wrote
    /// the entry into the rule by hand. The entry decides only where nothing
    /// decided before it: an entry above it in the rule may allow first, and a
    /// longer rule beneath the folder decides for itself. Reported in its own
    /// words rather than as either of the other two.
    /// </summary>
    DeniedByHand = 2,
}

/// <summary>
/// The "may be served to hosted models" switch. Off is a hold on the source's
/// folder (<see cref="Security.HostedHolds"/>), kept beside the folder rules
/// where no rule write reaches it, and enforced in the lists held documents
/// store and again in what a hosted-model agent may read.
/// <para>
/// Until migration 0141 the switch was a deny entry at the top of the folder's
/// own rule, which held only while that rule decided: a longer rule beneath
/// the folder carried no entry, and a rule written at the folder replaced it.
/// Such an entry may still sit in an old rule; the migration made it a hold,
/// and turning the switch back on removes it.
/// </para>
/// </summary>
public static class SourceExposure
{
    /// <summary>The words for each state, for a page, a list or a report.</summary>
    public static string Describe(SourceExposureState state) => state switch
    {
        SourceExposureState.NeverLeaves => "never served to hosted-model agents",
        SourceExposureState.MayBeServed => "may be served to hosted models",
        _ => "not held by the switch: a deny entry for hosted-model agents in this folder's rule decides only where no entry before it, and no rule beneath the folder, decides first",
    };

    /// <summary>
    /// What a folder says about hosted models: held is
    /// <see cref="SourceExposureState.NeverLeaves"/>, whatever its rule says.
    /// Not held, a rule that denies the group anywhere is
    /// <see cref="SourceExposureState.DeniedByHand"/>, and anything else,
    /// no rule included, is <see cref="SourceExposureState.MayBeServed"/>.
    /// </summary>
    public static SourceExposureState StateOf(bool held, AclSet? rule, Guid hostedModelAgentsGroupId)
    {
        if (held) return SourceExposureState.NeverLeaves;
        return rule is not null && rule.Entries.Contains(Denial(hostedModelAgentsGroupId))
            ? SourceExposureState.DeniedByHand
            : SourceExposureState.MayBeServed;
    }

    /// <summary>
    /// A rule's entries without the switch's entry from before migration 0141:
    /// the denial when it is the first entry, and nothing else. A denial
    /// written lower down by hand is somebody's rule and stays.
    /// </summary>
    public static IReadOnlyList<AclEntry> WithoutLegacyEntry(IReadOnlyList<AclEntry> entries, Guid hostedModelAgentsGroupId) =>
        entries.Count > 0 && entries[0] == Denial(hostedModelAgentsGroupId) ? [.. entries.Skip(1)] : entries;

    /// <summary>The denial of the hosted-model agents group, which a held document's list carries first.</summary>
    public static AclEntry Denial(Guid hostedModelAgentsGroupId) => Security.HostedHolds.Denial(hostedModelAgentsGroupId);
}

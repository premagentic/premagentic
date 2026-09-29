using Premagentic.Core.Admin;
using Premagentic.Core.Identity;
using Premagentic.Core.Security;
using Premagentic.Core.Security.Acl;
using Premagentic.Core.Storage;

namespace Premagentic.Core.Sources;

/// <summary>
/// One turn of the switch: the state before and after, whether the switch
/// changed, and how many indexed documents' lists moved.
/// </summary>
/// <param name="Moved">
/// Documents whose stored list changed. It can be above zero when nothing
/// changed: turning off a folder already held stamps again any document under
/// it stored without the denial.
/// </param>
public readonly record struct SourceExposureTurn(SourceExposureState Before, SourceExposureState After, bool Changed, int Moved)
{
    public void Deconstruct(out SourceExposureState before, out SourceExposureState after, out bool changed) =>
        (before, after, changed) = (Before, After, Changed);
}

/// <summary>
/// Reads and sets the "may be served to hosted models" switch on a source's
/// folder. Off is a hold (<see cref="HostedHolds"/>): every document of the
/// source at or beneath the folder is kept from agents whose model runs outside
/// the network, whatever rule decides it, and no rule write undoes that.
/// <para>
/// A folder is held when a hold covers it: its own, or one on a folder above
/// it. The words for the state name that hold when it is not the folder's own.
/// </para>
/// <para>
/// A change to who may read a folder has to leave a trail, so every turn of the
/// switch is recorded, as <see cref="ChangeKind"/>, and so is a turn that only
/// stamped documents again.
/// </para>
/// </summary>
public sealed class SourceExposureStore(PremagenticDatabase db, Guid tenantId)
{
    /// <summary>The connector a folder rule names; the same one the CLI's rules commands default to.</summary>
    public const string RuleSource = "filesystem";

    /// <summary>The change record entry a turn of the switch writes.</summary>
    public const string ChangeKind = "source.hosted";

    /// <summary>What <paramref name="pathPrefix"/> says about hosted models.</summary>
    /// <exception cref="InvalidOperationException">
    /// The reserved group is not there. It is created with the tenant and
    /// cannot be deleted, so its absence means the deployment is not what it
    /// should be. That is said out loud rather than answered with "may be
    /// served", which would be a reassuring sentence about a question this
    /// cannot actually answer.
    /// </exception>
    public async Task<SourceExposureState> ReadAsync(string pathPrefix, CancellationToken ct = default) =>
        (await ExplainAsync(pathPrefix, ct)).State;

    /// <summary>
    /// What <paramref name="pathPrefix"/> says about hosted models, and the
    /// words for it, which name the hold that decides when that hold is on a
    /// folder above.
    /// </summary>
    /// <inheritdoc cref="ReadAsync" path="/exception"/>
    public async Task<(SourceExposureState State, string Words)> ExplainAsync(string pathPrefix, CancellationToken ct = default)
    {
        var group = await RequireGroupAsync(ct);
        var covering = HostedHolds.Covering(await HostedHolds.ListAsync(db, tenantId, ct), RuleSource, pathPrefix);
        var state = SourceExposure.StateOf(covering is not null, await RuleAsync(new AclStore(db, tenantId), pathPrefix, ct), group);
        return (state, covering is not null && covering.PathPrefix != pathPrefix
            ? $"{SourceExposure.Describe(state)}, by the switch on {HostedHolds.Name(covering)}"
            : SourceExposure.Describe(state));
    }

    /// <summary>
    /// Turns the switch, and records it. Off holds the folder and stamps every
    /// document at or beneath it; a folder already held is stamped again, and
    /// that is recorded when it moved anything. On releases the folder's own
    /// hold and gives the documents back the lists their rules decide; where
    /// the hold was a legacy one, made by migration 0141, it also removes the
    /// entry the switch wrote at the top of the folder's rule before 0141. A
    /// folder with no rule may be held: the hold is not a rule, and invents
    /// none. A hold on a folder above is not this folder's to release.
    /// </summary>
    public async Task<SourceExposureTurn> SetAsync(
        string pathPrefix, bool mayBeServed, AdminActor actor, CancellationToken ct = default)
    {
        var group = await RequireGroupAsync(ct);
        var before = await ReadAsync(pathPrefix, ct);

        var (changed, moved) = await new AdminChanges(db, tenantId).RunAsync(actor, async admin =>
        {
            if (!mayBeServed)
            {
                var (held, stamped) = await admin.Rules.HoldAsync(RuleSource, pathPrefix, ct);
                if (held || stamped > 0) Record(admin, pathPrefix, wasServed: held, mayBeServed, stamped);
                return (held, stamped);
            }

            var (released, restored, legacy) = await admin.Rules.ReleaseAsync(RuleSource, pathPrefix, ct);
            if (!released) return (false, 0);

            // A legacy hold's folder rule may still open with the entry the
            // switch wrote before 0141; it goes with the hold, so the rule
            // reads as what its author wrote. No other release touches a
            // rule: the same entry at the top of any other rule was written
            // by a person and is theirs.
            var rule = await RuleAsync(admin.Rules, pathPrefix, ct);
            (string Before, string After)? stripped = null;
            if (legacy && rule is not null && SourceExposure.WithoutLegacyEntry(rule.Entries, group) is { } kept && kept.Count != rule.Entries.Count)
            {
                var after = AclSet.Create(kept);
                restored += await admin.Rules.SetRuleAsync(new FolderRule(RuleSource, pathPrefix, after), ct);
                stripped = (rule.CanonicalText, after.CanonicalText);
            }
            Record(admin, pathPrefix, wasServed: false, mayBeServed, restored, stripped);
            return (true, restored);
        }, ct);

        return new SourceExposureTurn(before, changed || moved > 0 ? await ReadAsync(pathPrefix, ct) : before, changed, moved);
    }

    /// <summary>
    /// One row per turn, in the target shape a rule change uses, so the folder
    /// reads alike in both kinds of row. A release of a legacy hold that took
    /// the switch's old entry out of the rule carries the rule before and
    /// after, as a rule change does.
    /// </summary>
    private static void Record(
        AdminChange admin, string pathPrefix, bool wasServed, bool mayBeServed, int moved,
        (string Before, string After)? stripped = null) =>
        admin.Record(ChangeKind, $"{RuleSource}:{pathPrefix}",
            new { hosted_models = wasServed, rule_entries = stripped?.Before },
            new
            {
                hosted_models = mayBeServed,
                documents_moved = moved,
                legacy_entry_removed = stripped is not null,
                rule_entries = stripped?.After,
            });

    /// <summary>
    /// The reserved group's id. It is created with the tenant and cannot be
    /// deleted or renamed, so not finding it is a broken deployment rather than
    /// a state to report.
    /// </summary>
    private async Task<Guid> RequireGroupAsync(CancellationToken ct) =>
        (await new IdentityStore(db, tenantId).FindSystemGroupAsync(SystemGroups.HostedModelAgents, ct))?.Id
        ?? throw new InvalidOperationException(
            "This deployment has no hosted-model agents group. It is created with the tenant and cannot be removed, " +
            "so something has changed the configuration database by hand. Whether a folder is held back from hosted " +
            "models cannot be answered until that is put right.");

    private static async Task<AclSet?> RuleAsync(AclStore rules, string pathPrefix, CancellationToken ct) =>
        (await rules.ListRulesAsync(ct))
        .FirstOrDefault(r => r.Rule.Source == RuleSource && r.Rule.PathPrefix == pathPrefix)?.Rule.Acl;
}

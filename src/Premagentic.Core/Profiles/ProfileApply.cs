using Npgsql;
using Premagentic.Core.Admin;
using Premagentic.Core.Evaluation;
using Premagentic.Core.Identity;
using Premagentic.Core.Ingestion;
using Premagentic.Core.Okf;
using Premagentic.Core.Security;
using Premagentic.Core.Security.Acl;
using Premagentic.Core.Sources;
using Premagentic.Core.Sources.Registry;
using Premagentic.Core.Storage;

namespace Premagentic.Core.Profiles;

/// <summary>A profile that was applied to this deployment.</summary>
/// <param name="AppliedFrom">
/// The folder it was applied from, when the row recorded one. Advisory: the
/// folder may be gone or on another machine, so anything that reads it says so
/// rather than treating its absence as "no drift".
/// </param>
public sealed record AppliedProfile(
    string Name, string Version, DateTimeOffset AppliedAt, string AppliedBy, string? AppliedFrom);

/// <summary>
/// What an apply did. <see cref="NotApplied"/> is empty on a complete run, and
/// on a failed one it names every change that was not made, so an operator can
/// see the deployment's real state instead of guessing at it.
/// </summary>
public sealed record ProfileApplyReport(
    ProfileFolder Profile,
    IReadOnlyList<ProfileChange> Applied,
    IReadOnlyList<ProfileChange> NotApplied,
    string? Problem)
{
    public bool Complete => Problem is null;
}

/// <summary>
/// Applies a plan through the administrator paths, one change at a time.
/// <para>
/// The apply is NOT one transaction and cannot be: the settings, sources and
/// rules stores each open their own transaction and take the same tenant lock,
/// so putting them inside one would deadlock against itself. What holds
/// instead is that nothing is applied unless everything validated, and the
/// planner validates hard enough that apply has nothing left to refuse: every
/// folder was found, every name resolved, every value parsed, and the rules
/// were built. If an apply still fails, on a folder that vanished between the
/// two steps or a database that went away, it stops at once and reports what
/// was applied and what was not.
/// </para>
/// </summary>
public static class ProfileApply
{
    /// <summary>The change record entry for applying a profile, written once per apply.</summary>
    public const string AppliedKind = "profile.applied";

    /// <summary>The entry a rule written by a profile is recorded under, the same kind the portal writes.</summary>
    public const string RuleKind = "rule.set";

    /// <summary>The entry a group made by a profile is recorded under, the same kind the portal writes.</summary>
    public const string GroupKind = "group.add";

    /// <summary>
    /// Applies every change in the plan, in a fixed order: settings, then the
    /// golden set, then groups, then sources, then the rules that decide who
    /// may read them.
    /// </summary>
    public static async Task<ProfileApplyReport> RunAsync(
        ProfilePlan plan, PremagenticDatabase db, Guid tenantId, AdminActor actor,
        ChunkerRegistry? chunkers = null, CancellationToken ct = default)
    {
        var ordered = Ordered(plan.Changes);
        var applied = new List<ProfileChange>();

        foreach (var change in ordered)
        {
            try
            {
                await ApplyAsync(change, db, tenantId, actor, chunkers, ct);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException
                                          or IOException or UnauthorizedAccessException or PostgresException)
            {
                return new ProfileApplyReport(plan.Profile, applied, [.. ordered.Skip(applied.Count)], ex.Message);
            }

            applied.Add(change);
        }

        await RecordAppliedAsync(plan, db, tenantId, actor, ct);
        return new ProfileApplyReport(plan.Profile, applied, [], null);
    }

    /// <summary>
    /// A rule cannot be written before the source it covers exists, or before
    /// the groups it names do, and a setting is cheapest to undo by hand, so
    /// the order runs from the least to the most entangled. Holds come first
    /// and releases last: a hold needs no source or rule, so every document
    /// a source or rule written after it reaches is stamped as it is written,
    /// and a folder a profile releases stays held until everything else it
    /// writes is in place.
    /// </summary>
    private static IReadOnlyList<ProfileChange> Ordered(IReadOnlyList<ProfileChange> changes) =>
    [
        .. changes.OfType<HostedChange>().Where(h => !h.MayBeServed),
        .. changes.OfType<SettingChange>(),
        .. changes.OfType<GoldenSetChange>(),
        .. changes.OfType<GroupChange>(),
        .. changes.OfType<SourceChange>(),
        .. changes.OfType<SourceOwnerChange>(),
        .. changes.OfType<RuleChange>(),
        .. changes.OfType<HostedChange>().Where(h => h.MayBeServed),
    ];

    private static async Task ApplyAsync(
        ProfileChange change, PremagenticDatabase db, Guid tenantId, AdminActor actor,
        ChunkerRegistry? chunkers, CancellationToken ct)
    {
        switch (change)
        {
            case SettingChange setting when TrustSettingsStore.Keys.Contains(setting.Key):
                await new TrustSettingsStore(db, tenantId).SetAsync(setting.Key, setting.Text, actor, ct);
                return;

            case SettingChange { Unsets: true } setting:
                await new TuningSettingsStore(db, tenantId).UnsetAsync(setting.Key, actor, ct);
                return;

            case SettingChange setting:
                await new TuningSettingsStore(db, tenantId).SetAsync(setting.Key, setting.Value, actor, ct);
                return;

            case GoldenSetChange golden:
                Directory.CreateDirectory(Path.GetDirectoryName(golden.To)!);
                File.Copy(golden.From, golden.To, overwrite: true);
                await new TuningSettingsStore(db, tenantId).SetAsync(
                    TuningSettingsStore.GoldenSetPath, System.Text.Json.JsonSerializer.SerializeToElement(golden.To), actor, ct);
                return;

            case GroupChange group:
                await new AdminChanges(db, tenantId).RunAsync(actor, async admin =>
                {
                    var made = await admin.Identity.CreateGroupAsync(group.Name, ct);
                    // The target and shape the groups page writes, so a group
                    // is one kind of row however it was made.
                    admin.Record(GroupKind, made.Name, null, new { name = made.Name });
                    return made;
                }, ct);
                return;

            case SourceChange source:
                await ApplySourceAsync(source, db, tenantId, actor, chunkers, ct);
                return;

            case SourceOwnerChange owner:
                await new SourceRegistry(db, tenantId, chunkers)
                    .SetOwnerAsync(owner.Source, owner.OwnerUserId, actor, ct);
                return;

            case RuleChange rule:
                await ApplyRuleAsync(rule, db, tenantId, actor, ct);
                return;

            case HostedChange hosted:
                // Through the switch's own store, so a profile's turn of it is
                // held, stamped and recorded exactly as the CLI's and the portal's.
                await new SourceExposureStore(db, tenantId).SetAsync(hosted.Prefix, hosted.MayBeServed, actor, ct);
                return;

            default:
                throw new InvalidOperationException($"A profile plan holds a change of a kind nothing applies: {change.GetType().Name}.");
        }
    }

    private static async Task ApplySourceAsync(
        SourceChange change, PremagenticDatabase db, Guid tenantId, AdminActor actor,
        ChunkerRegistry? chunkers, CancellationToken ct)
    {
        var registry = new SourceRegistry(db, tenantId, chunkers);
        var wanted = change.Wanted;

        if (change.Existing is null)
        {
            await registry.AddAsync(
                wanted.Name!, change.Folder, change.Prefix, wanted.OkfBundle, wanted.UndeclaredAsMachine,
                actor, wanted.Chunker ?? ChunkerRegistry.DefaultName, ct);
            return;
        }

        await registry.UpdateAsync(
            wanted.Name!, wanted.OkfBundle, wanted.UndeclaredAsMachine, actor, change.Chunker, ct);
    }

    /// <summary>
    /// Writes the rule and records it. The rules store writes no change record
    /// entry of its own, so a profile records its own rule writes: a
    /// configuration that changes who may read a folder has to leave a trail.
    /// </summary>
    private static async Task ApplyRuleAsync(
        RuleChange change, PremagenticDatabase db, Guid tenantId, AdminActor actor, CancellationToken ct) =>
        await new AdminChanges(db, tenantId).RunAsync(actor, async admin =>
        {
            var rule = await ResolvePendingAsync(change, admin.Identity, ct);
            var moved = await admin.Rules.SetRuleAsync(rule, ct);
            // The target and shape the rules page writes, so a rule change is
            // one kind of row however it was made.
            admin.Record(RuleKind, change.RecordTarget,
                change.Running is null ? null : new { entries = change.Running },
                new { entries = rule.Acl.CanonicalText, documents_moved = moved });
            return moved;
        }, ct);

    /// <summary>
    /// The rule with each placeholder replaced by the id of the group it stands
    /// for, which the group step has created by now. A group that is not there
    /// stops the apply rather than writing a rule naming nobody.
    /// </summary>
    private static async Task<FolderRule> ResolvePendingAsync(RuleChange change, IdentityStore identity, CancellationToken ct)
    {
        if (change.PendingGroups is not { Count: > 0 } pending) return change.Rule;

        var ids = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (placeholder, name) in pending)
        {
            var group = await identity.FindGroupByNameAsync(name, ct)
                ?? throw new InvalidOperationException(
                    $"The rule for {change.RecordTarget} names the group '{name}', which the profile creates, and it does not exist yet.");
            ids[CallerResolver.IdText(placeholder)] = CallerResolver.IdText(group.Id);
        }

        var entries = change.Rule.Acl.Entries.Select(e =>
            e.Principal.Kind == PrincipalKind.Group && ids.TryGetValue(e.Principal.Value, out var id)
                ? new AclEntry(e.Effect, Principal.Group(id))
                : e);
        return new FolderRule(change.Rule.Source, change.Rule.PathPrefix, AclSet.Create(entries));
    }

    /// <summary>
    /// Writes the applied row and its change record entry in one transaction,
    /// so the state and its audit land together or not at all.
    /// </summary>
    private static async Task RecordAppliedAsync(
        ProfilePlan plan, PremagenticDatabase db, Guid tenantId, AdminActor actor, CancellationToken ct) =>
        await new AdminChanges(db, tenantId).RunAsync(actor, async admin =>
        {
            await using var insert = new NpgsqlCommand("""
                INSERT INTO prem_config.profile_applied(tenant_id, name, version, applied_by, applied_from)
                VALUES(@tenant, @name, @version, @by, @from)
                """, admin.Transaction.Connection, admin.Transaction);
            insert.Parameters.AddWithValue("tenant", tenantId);
            insert.Parameters.AddWithValue("name", plan.Profile.Manifest.Name!);
            insert.Parameters.AddWithValue("version", plan.Profile.Manifest.Version!);
            insert.Parameters.AddWithValue("by", actor.Describe());
            insert.Parameters.AddWithValue("from", plan.Profile.Path);
            await insert.ExecuteNonQueryAsync(ct);

            admin.Record(AppliedKind, plan.Profile.Manifest.Name!, null, new
            {
                version = plan.Profile.Manifest.Version,
                folder = plan.Profile.Path,
                changes = plan.Changes.Count,
            });
            return true;
        }, ct);

    /// <summary>
    /// The profile this deployment was last configured from, or null when none
    /// ever was.
    /// </summary>
    public static async Task<AppliedProfile?> LatestAsync(
        PremagenticDatabase db, Guid tenantId, CancellationToken ct = default)
    {
        await using var cmd = db.DataSource.CreateCommand("""
            SELECT name, version, applied_at, applied_by, applied_from FROM prem_config.profile_applied
            WHERE tenant_id = @tenant ORDER BY id DESC LIMIT 1
            """);
        cmd.Parameters.AddWithValue("tenant", tenantId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct)
            ? new AppliedProfile(
                reader.GetString(0), reader.GetString(1),
                reader.GetFieldValue<DateTimeOffset>(2), reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4))
            : null;
    }
}

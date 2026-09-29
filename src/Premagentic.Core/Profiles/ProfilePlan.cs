using System.Text.Json;
using Premagentic.Core.Evaluation;
using Premagentic.Core.Extensions;
using Premagentic.Core.Identity;
using Premagentic.Core.Ingestion;
using Premagentic.Core.Okf;
using Premagentic.Core.Retrieval;
using Premagentic.Core.Security;
using Premagentic.Core.Security.Acl;
using Premagentic.Core.Sources;
using Premagentic.Core.Sources.Registry;
using Premagentic.Core.Storage;

namespace Premagentic.Core.Profiles;

/// <summary>
/// One thing a profile would change about a running deployment. A plan is the
/// whole list of them, so the same list is the drift a profile shows, the work
/// an apply does, and the nothing that a profile already applied produces.
/// </summary>
public abstract record ProfileChange
{
    /// <summary>What is being changed, in the words an operator uses for it.</summary>
    public abstract string Target { get; }

    /// <summary>The change itself, running value first.</summary>
    public abstract string Describe();

    /// <summary>
    /// The sentence an operator reads. SEALED on purpose: a derived record
    /// synthesizes its own ToString, and the synthesized one wins, so without
    /// this word every change prints as its record text and this line is never
    /// reached.
    /// </summary>
    public sealed override string ToString() => $"{Target}: {Describe()}";
}

/// <param name="Value">The wanted value, or JSON null to unset the key, as 'prem settings unset' does.</param>
/// <param name="Running">The value in force now, which is a default when nothing is stored.</param>
public sealed record SettingChange(string Key, JsonElement Value, string Running) : ProfileChange
{
    public override string Target => Key;
    public override string Describe() => $"{Running} becomes {Text}";

    /// <summary>True when the profile unsets the key, so its default applies, or nothing.</summary>
    public bool Unsets => Value.ValueKind == JsonValueKind.Null;

    /// <summary>The wanted value as an operator would type it.</summary>
    public string Text => Unsets
        ? SettingsCatalog.Find(Key)?.Default is { } fallback ? $"unset, so its default {Plain(fallback)}" : "unset"
        : Plain(Value);

    private static string Plain(JsonElement value) =>
        value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : value.GetRawText();
}

/// <param name="Folder">
/// The folder as this machine sees it. A profile may state it relative to
/// itself, so a profile that ships its own documents is portable; it is
/// resolved once, while the plan is built, and never again.
/// </param>
/// <param name="Existing">Null when the source is not registered yet.</param>
public sealed record SourceChange(ProfileSource Wanted, string Folder, string Prefix, RegisteredSource? Existing) : ProfileChange
{
    public override string Target => $"source {Wanted.Name}";

    public override string Describe() => Existing is null
        ? $"added, reading {Folder} at {(Prefix.Length == 0 ? "the whole index" : Prefix)}"
        : $"settings change ({Describe(Existing.OkfBundle, Wanted.OkfBundle, "okf bundle")}"
          + $"{Describe(Existing.UndeclaredIsMachine, Wanted.UndeclaredAsMachine, ", undeclared as machine")}"
          + $"{(Chunker is null ? "" : $", chunker {Existing.Chunker} becomes {Chunker}")})";

    /// <summary>The chunker to set, or null to leave the one in force.</summary>
    public string? Chunker => Wanted.Chunker is { Length: > 0 } c && !c.Equals(Existing?.Chunker, StringComparison.OrdinalIgnoreCase) ? c : null;

    private static string Describe(bool from, bool to, string what) =>
        from == to ? "" : $"{what} {(from ? "on" : "off")} becomes {(to ? "on" : "off")}";
}

/// <param name="Name">The group's name, as the profile writes it.</param>
/// <param name="Placeholder">
/// The id the plan's rules use for this group until it exists. Applying the
/// group creates it with an id of the database's choosing, and each rule that
/// names it swaps the placeholder for that id as it is written.
/// </param>
public sealed record GroupChange(string Name, Guid Placeholder) : ProfileChange
{
    public override string Target => $"group {Name}";
    public override string Describe() => "added";
}

/// <param name="Rule">Constructed while the plan was built, so applying it cannot fail on its own shape.</param>
/// <param name="Running">The canonical text of the rule in force, or null when there is none.</param>
/// <param name="PendingGroups">
/// The groups this rule names that the same profile creates, by the
/// placeholder id standing in for each, with its name. Empty when every name
/// in the rule resolved to a group that exists already.
/// </param>
public sealed record RuleChange(
    FolderRule Rule, IReadOnlyList<string> Entries, string? Running,
    IReadOnlyDictionary<Guid, string>? PendingGroups = null) : ProfileChange
{
    public override string Target =>
        $"rule {Rule.Source}/{(Rule.PathPrefix.Length == 0 ? "" : Rule.PathPrefix)}";

    /// <summary>
    /// What the change record calls this rule, which is what the rules page
    /// calls it, so one kind of change is one kind of row wherever it was made.
    /// </summary>
    public string RecordTarget => $"{Rule.Source}:{Rule.PathPrefix}";

    public override string Describe() =>
        (Running is null ? "no rule" : $"[{Flatten(Running)}]") + $" becomes [{string.Join(", ", Entries)}]";

    private static string Flatten(string canonical) =>
        string.Join(", ", canonical.Split('\n', StringSplitOptions.RemoveEmptyEntries));
}

/// <summary>
/// The "may be served to hosted models" switch on a source's folder: a hold
/// kept beside the rules, so a profile sets it with or without a rule.
/// </summary>
/// <param name="MayBeServed">What the profile wants: false holds the folder back from hosted-model agents.</param>
/// <param name="Held">Whether the folder is held now.</param>
public sealed record HostedChange(string Source, string Prefix, bool MayBeServed, bool Held) : ProfileChange
{
    public override string Target => $"source {Source} hosted models";

    public override string Describe() => $"{Words(!Held)} becomes {Words(MayBeServed)}";

    private static string Words(bool mayBeServed) =>
        SourceExposure.Describe(mayBeServed ? SourceExposureState.MayBeServed : SourceExposureState.NeverLeaves);
}

/// <param name="OwnerUserId">Null clears the owner.</param>
public sealed record SourceOwnerChange(string Source, string? Owner, Guid? OwnerUserId, string Running) : ProfileChange
{
    public override string Target => $"source {Source} owner";
    public override string Describe() => $"{Running} becomes {Owner ?? "nobody"}";
}

/// <param name="To">Where the file is copied so the server can read it.</param>
public sealed record GoldenSetChange(string From, string To, string Running) : ProfileChange
{
    public override string Target => TuningSettingsStore.GoldenSetPath;
    public override string Describe() => $"{Running} becomes {To}, copied from the profile";
}

/// <summary>
/// What a profile would do to a deployment, worked out in full before anything
/// is written. Empty changes means the deployment already matches the profile.
/// </summary>
public sealed record ProfilePlan(ProfileFolder Profile, IReadOnlyList<ProfileChange> Changes)
{
    public bool UpToDate => Changes.Count == 0;
}

/// <summary>
/// Works out what a profile would change, and refuses the whole profile rather
/// than part of it.
/// <para>
/// Everything a profile can set, it sets through the same path an administrator
/// uses, so a profile can never reach past what an administrator may do. That
/// is the whole of invariant 5, and it is kept structurally: the planner knows
/// how to name a setting, a group, a source and a rule, and knows nothing at
/// all about users, roles or membership, so there is no path here to create a
/// person, grant a role or put anybody in a group. A group is created the way
/// the portal's groups page creates one, which an administrator may do.
/// </para>
/// <para>
/// The plan carries objects the apply step reuses rather than text it would
/// have to parse again: a folder rule and its access list are built here, so a
/// profile that gets past planning cannot fail on their shape halfway through
/// being applied.
/// </para>
/// </summary>
public static class ProfilePlanner
{
    /// <summary>
    /// Works out the plan, or every reason there is not one.
    /// </summary>
    /// <param name="goldenSetFolder">
    /// Where a profile's golden set is copied to. It has to be readable by the
    /// account the server runs as, which is not always the account applying the
    /// profile, so the caller decides it rather than this library guessing at
    /// the installation's layout.
    /// </param>
    public static async Task<(ProfilePlan? Plan, IReadOnlyList<ProfileProblem> Problems)> BuildAsync(
        ProfileFolder profile, PremagenticDatabase db, Guid tenantId, string goldenSetFolder,
        ChunkerRegistry? chunkers = null, DeploymentSettings? settings = null, CancellationToken ct = default)
    {
        var problems = new List<ProfileProblem>();
        var changes = new List<ProfileChange>();

        CheckSeams(profile, problems);
        await PlanSettingsAsync(profile, db, tenantId, settings ?? DeploymentSettings.BuiltIn, changes, problems, ct);
        await PlanGoldenSetAsync(profile, db, tenantId, goldenSetFolder, changes, problems, ct);
        var pending = await PlanGroupsAsync(profile, db, tenantId, changes, problems, ct);
        await PlanSourcesAsync(profile, db, tenantId, chunkers ?? ChunkerRegistry.BuiltIn, pending, changes, problems, ct);

        return problems.Count > 0 ? (null, problems) : (new ProfilePlan(profile, changes), problems);
    }

    private static void CheckSeams(ProfileFolder profile, List<ProfileProblem> problems)
    {
        foreach (var (seam, wanted) in profile.Manifest.Seams ?? new Dictionary<string, int>())
        {
            if (SeamVersions.Of(seam) is not { } have)
            {
                problems.Add(new ProfileProblem(ProfileReader.ManifestFile,
                    $"The profile expects a seam called '{seam}', which this build does not have. It has: {string.Join(", ", SeamVersions.Names)}."));
                continue;
            }

            // Above is refused, below is fine: a seam only ever gains, so an
            // older profile still means what it said.
            if (wanted > have)
                problems.Add(new ProfileProblem(ProfileReader.ManifestFile,
                    $"The profile expects version {wanted} of the {seam} seam and this build has version {have}. Use a build that has it."));
        }
    }

    private static async Task PlanSettingsAsync(
        ProfileFolder profile, PremagenticDatabase db, Guid tenantId, DeploymentSettings defined,
        List<ProfileChange> changes, List<ProfileProblem> problems, CancellationToken ct)
    {
        var trust = new TrustSettingsStore(db, tenantId);
        var settings = new SettingsStore(db, tenantId);

        foreach (var (key, value) in profile.Settings.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            if (TrustSettingsStore.Keys.Contains(key))
            {
                if (value.ValueKind != JsonValueKind.String)
                {
                    problems.Add(new ProfileProblem(ProfileReader.SettingsFile,
                        $"'{key}' is one of {string.Join(", ", TrustSettingsStore.AllowedValues(key))}, written as text."));
                    continue;
                }

                if (!TrustSettingsStore.TryNormalize(key, value.GetString(), out var normalized))
                {
                    problems.Add(new ProfileProblem(ProfileReader.SettingsFile,
                        $"'{value.GetString()}' is not a value for '{key}'. It is one of {string.Join(", ", TrustSettingsStore.AllowedValues(key))}."));
                    continue;
                }

                var running = await trust.ReadAsync(key, ct);
                if (!string.Equals(running.Value, normalized, StringComparison.Ordinal))
                    changes.Add(new SettingChange(key, Json(normalized), running.Value));
                continue;
            }

            // What an extension may load is decided by extensions.json, which
            // this build does not take yet; settings.json is not a way around it.
            if (key.StartsWith("extensions.", StringComparison.Ordinal))
            {
                problems.Add(new ProfileProblem(ProfileReader.SettingsFile,
                    $"'{key}' decides what code this server loads, which a profile does not set in this build. Set it with 'prem extensions'."));
                continue;
            }

            // An extension's setting is set with 'prem settings', which knows
            // it while the extension is loaded; a profile does not set one in
            // this build.
            if (defined.ExtensionOf(key) is { } extension)
            {
                problems.Add(new ProfileProblem(ProfileReader.SettingsFile,
                    $"'{key}' is a setting the extension {extension} adds, and a profile does not set an extension's settings in this build. Set it with 'prem settings set'."));
                continue;
            }
            if (BusinessAddOn.Settings.Contains(key))
            {
                problems.Add(new ProfileProblem(ProfileReader.SettingsFile,
                    $"'{key}' is a setting of {BusinessAddOn.DisplayName}, and a profile does not set an extension's settings in this build. Set it with 'prem settings set' where the add-on is loaded."));
                continue;
            }

            if (SettingsCatalog.Find(key) is { IsTrust: false } definition)
            {
                if (key == TuningSettingsStore.GoldenSetPath && profile.GoldenSetFile is not null)
                {
                    problems.Add(new ProfileProblem(ProfileReader.SettingsFile,
                        $"The profile sets '{key}' and also carries {ProfileReader.GoldenSetFile}. Keep one: the file is copied to the server and the path is set from it."));
                    continue;
                }

                // Null unsets the key, as 'prem settings unset' does: a change
                // only when something is stored.
                if (value.ValueKind == JsonValueKind.Null)
                {
                    if (await settings.GetAsync(key, ct) is { } storedNow)
                        changes.Add(new SettingChange(key, value, Describe(storedNow)));
                    continue;
                }

                // The retrieval keys and the golden set path keep the sentences
                // they had; every other key is checked by its definition.
                var problem = key == TuningSettingsStore.GoldenSetPath || RetrievalSettings.Keys.Contains(key)
                    ? Parses(key, value, out var parsed) ? null : parsed
                    : definition.Problem(value);
                if (problem is not null)
                {
                    problems.Add(new ProfileProblem(ProfileReader.SettingsFile, $"'{key}': {problem}"));
                    continue;
                }

                // What is in force now: the stored value when it can be used,
                // otherwise the default, or nothing for a key with none.
                var stored = await settings.GetAsync(key, ct);
                var running = stored is { } s && definition.Problem(s) is null ? stored : definition.Default;
                if (running is not { } now || !JsonElement.DeepEquals(now, value))
                    changes.Add(new SettingChange(key, value, running is { } was ? Describe(was) : "not set"));
                continue;
            }

            // Invariant 5, at the only point it can be enforced: a key that has
            // no administrator path has no profile path either.
            problems.Add(new ProfileProblem(ProfileReader.SettingsFile,
                $"'{key}' is not a setting an administrator can change, so a profile cannot change it either. The settings are: {string.Join(", ", SettingsCatalog.All.Select(d => d.Key).Where(k => !k.StartsWith("extensions.", StringComparison.Ordinal)).Order(StringComparer.Ordinal))}."));
        }

        await CheckFlowAddressAsync(profile, settings, changes, problems, ct);
    }

    /// <summary>
    /// The authorization flow's one check that spans two settings, applied to
    /// the whole profile as the deployment would stand once it is applied,
    /// never key by key in file order: the flow is never on without a usable
    /// public address. The refusals are the sentences 'prem settings' gives.
    /// A profile that names neither key leaves the flow as it is.
    /// </summary>
    private static async Task CheckFlowAddressAsync(
        ProfileFolder profile, SettingsStore settings,
        List<ProfileChange> changes, List<ProfileProblem> problems, CancellationToken ct)
    {
        var namesEnabled = profile.Settings.TryGetValue(OAuthSettings.Enabled, out var enabled);
        var namesUrl = profile.Settings.TryGetValue(OAuthSettings.PublicUrl, out var url);
        if (!namesEnabled && !namesUrl) return;

        var urlAfter = namesUrl
            ? url.ValueKind == JsonValueKind.Null ? null : url
            : await settings.GetAsync(OAuthSettings.PublicUrl, ct);

        // A profile that names the switch is judged by what it turns it to;
        // one that does not leaves it as the database has it.
        if (namesEnabled)
        {
            if (enabled.ValueKind == JsonValueKind.True && !OAuthSettingRules.AddressUsable(urlAfter))
                problems.Add(new ProfileProblem(ProfileReader.SettingsFile, OAuthSettingRules.EnableNeedsAddress));
        }
        else if (url.ValueKind == JsonValueKind.Null
                 && await settings.GetAsync(OAuthSettings.Enabled, ct) is { ValueKind: JsonValueKind.True })
            problems.Add(new ProfileProblem(ProfileReader.SettingsFile, OAuthSettingRules.AddressNeededWhileOn));

        // Applied in plan order, which is key order, and an apply that stops
        // part way keeps what it made. Turning the flow on goes last, so a
        // stop never leaves it on without the address this profile sets.
        var turnOn = changes.OfType<SettingChange>().FirstOrDefault(c =>
            c.Key == OAuthSettings.Enabled && c.Value.ValueKind == JsonValueKind.True);
        if (turnOn is not null && changes.Remove(turnOn)) changes.Add(turnOn);
    }

    private static bool Parses(string key, JsonElement value, out string? problem)
    {
        if (key != TuningSettingsStore.GoldenSetPath) return RetrievalSettings.TryParse(key, value, out problem);

        if (value.ValueKind != JsonValueKind.String)
        {
            problem = "the path to the golden question set, written as text.";
            return false;
        }
        return TuningSettingsStore.TryCheckGoldenSetPath(value.GetString(), out problem);
    }

    private static async Task PlanGoldenSetAsync(
        ProfileFolder profile, PremagenticDatabase db, Guid tenantId, string goldenSetFolder,
        List<ProfileChange> changes, List<ProfileProblem> problems, CancellationToken ct)
    {
        if (profile.GoldenSetFile is null) return;

        // Read it here, so a profile whose golden set is unreadable is refused
        // before anything is applied rather than at the next evaluation.
        if (!EvalRunner.TryLoadCases(profile.GoldenSetFile, out _, out var problem))
        {
            problems.Add(new ProfileProblem(ProfileReader.GoldenSetFile, problem ?? "The golden question set cannot be read."));
            return;
        }

        var folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(goldenSetFolder));
        var target = Path.GetFullPath(Path.Combine(folder, $"{profile.Manifest.Name}.json"));
        // The reader holds the name to the naming rule; this holds for a
        // profile made any other way, since the copy replaces whatever is there.
        if (!string.Equals(Path.GetDirectoryName(target), folder, StringComparison.Ordinal))
        {
            problems.Add(new ProfileProblem(ProfileReader.ManifestFile,
                $"The golden set would be copied to {target}, outside {folder}. A profile's name cannot name another folder."));
            return;
        }
        if (!TuningSettingsStore.TryCheckGoldenSetPath(target, out var pathProblem))
        {
            problems.Add(new ProfileProblem(ProfileReader.GoldenSetFile,
                $"The golden set would be copied to {target}, which will not do: {pathProblem}"));
            return;
        }

        var running = await new TuningSettingsStore(db, tenantId).ReadGoldenSetPathAsync(ct);

        // Already in force when the path points at the copy and the copy holds
        // the profile's bytes. Anything else is a change, including a copy
        // somebody edited by hand, which the profile puts back.
        if (running.Path is { } path
            && string.Equals(Path.GetFullPath(path), target, StringComparison.OrdinalIgnoreCase)
            && SameBytes(profile.GoldenSetFile, target))
            return;

        changes.Add(new GoldenSetChange(profile.GoldenSetFile, target, running.Path ?? "no golden set"));
    }

    private static bool SameBytes(string a, string b)
    {
        try
        {
            return File.Exists(b) && File.ReadAllBytes(a).AsSpan().SequenceEqual(File.ReadAllBytes(b));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A copy that cannot be read is not in force; copying it again is
            // the change that shows why.
            return false;
        }
    }

    /// <summary>
    /// The groups the profile names that do not exist yet, each as a change,
    /// and the placeholder id each one's rules use until it does. A group that
    /// exists is left as it is: the profile says it should exist, and it does.
    /// </summary>
    private static async Task<IReadOnlyDictionary<string, Guid>> PlanGroupsAsync(
        ProfileFolder profile, PremagenticDatabase db, Guid tenantId,
        List<ProfileChange> changes, List<ProfileProblem> problems, CancellationToken ct)
    {
        var pending = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        if (profile.Groups.Count == 0) return pending;

        var identity = new IdentityStore(db, tenantId);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var wanted in profile.Groups)
        {
            string name;
            try
            {
                // The same check the identity store makes when the group is
                // created, so a name that would be refused then is refused now.
                name = IdentityNames.Require(wanted, "group name");
            }
            catch (ArgumentException ex)
            {
                problems.Add(new ProfileProblem(ProfileReader.GroupsFile, $"'{wanted}' cannot name a group. {ex.Message}"));
                continue;
            }

            if (!seen.Add(name))
            {
                problems.Add(new ProfileProblem(ProfileReader.GroupsFile, $"The group '{name}' is listed twice."));
                continue;
            }

            if (await identity.FindGroupByNameAsync(name, ct) is not null) continue;

            var change = new GroupChange(name, Guid.NewGuid());
            pending[name] = change.Placeholder;
            changes.Add(change);
        }

        return pending;
    }

    private static async Task PlanSourcesAsync(
        ProfileFolder profile, PremagenticDatabase db, Guid tenantId, ChunkerRegistry chunkers,
        IReadOnlyDictionary<string, Guid> pendingGroups,
        List<ProfileChange> changes, List<ProfileProblem> problems, CancellationToken ct)
    {
        if (profile.Sources.Count == 0) return;

        var registry = new SourceRegistry(db, tenantId, chunkers);
        var rules = new AclStore(db, tenantId);
        var identity = new IdentityStore(db, tenantId);
        var names = new PrincipalNames(identity);
        var running = await rules.ListRulesAsync(ct);
        var holds = await HostedHolds.ListAsync(db, tenantId, ct);
        var legacyHolds = await HostedHolds.LegacyAsync(db, tenantId, ct);
        var hostedModelAgents = (await identity.FindSystemGroupAsync(SystemGroups.HostedModelAgents, ct))?.Id
            ?? throw new InvalidOperationException(
                "This deployment has no hosted-model agents group, which is created with the tenant and cannot be " +
                "removed. A profile cannot say what hosted-model agents may be served until that is put right.");

        foreach (var wanted in profile.Sources)
        {
            var file = ProfileReader.SourcesFile;
            if (string.IsNullOrWhiteSpace(wanted.Name) || !SourceRegistry.IsName(wanted.Name))
            {
                problems.Add(new ProfileProblem(file,
                    $"'{wanted.Name}' cannot name a source. A name is up to 64 letters, digits, dots, hyphens and underscores, starting with a letter or digit."));
                continue;
            }

            if (string.IsNullOrWhiteSpace(wanted.Folder))
            {
                problems.Add(new ProfileProblem(file, $"The source '{wanted.Name}' says no folder."));
                continue;
            }

            // A relative folder is resolved against the profile, so a profile
            // can ship the documents it configures. An absolute one, which is
            // what a real share is, is taken as given.
            var folder = Path.GetFullPath(wanted.Folder!, profile.Path);

            // The registry refuses a folder that is not there, so a profile
            // written for another machine is refused here rather than halfway
            // through being applied.
            if (!Directory.Exists(folder))
                problems.Add(new ProfileProblem(file,
                    $"The source '{wanted.Name}' reads {folder}, which is not a folder on this machine."));

            if (wanted.Chunker is { Length: > 0 } chunker && chunkers.Find(chunker) is null)
                problems.Add(new ProfileProblem(file,
                    $"The source '{wanted.Name}' names the chunker '{chunker}', which this build does not have. It has: {string.Join(", ", chunkers.Names)}."));

            var prefix = SourceRegistry.NormalizePrefix(wanted.Prefix ?? "");
            var existing = await registry.FindAsync(wanted.Name!, ct);
            if (existing is not null && !MayUpdate(existing, wanted, folder, prefix, file, problems)) continue;

            if (existing is null
                || existing.OkfBundle != wanted.OkfBundle
                || existing.UndeclaredIsMachine != wanted.UndeclaredAsMachine
                || (wanted.Chunker is { Length: > 0 } c && !c.Equals(existing.Chunker, StringComparison.OrdinalIgnoreCase)))
                changes.Add(new SourceChange(wanted, folder, prefix, existing));

            if (wanted.Owner is not null)
            {
                var owner = wanted.Owner.Length == 0 ? null : await identity.FindUserByNameAsync(wanted.Owner, ct);
                if (wanted.Owner.Length > 0 && owner is null)
                    problems.Add(new ProfileProblem(file,
                        $"The source '{wanted.Name}' names '{wanted.Owner}' as its owner, and no user signs in as that."));
                else if (existing?.OwnerUserId != owner?.Id)
                    changes.Add(new SourceOwnerChange(
                        wanted.Name!, owner?.Name, owner?.Id,
                        existing?.OwnerUserId is null ? "nobody" : await OwnerNameAsync(identity, existing.OwnerUserId.Value, ct)));
            }

            var inForce = running.FirstOrDefault(r =>
                r.Rule.Source == AdminSourceName && r.Rule.PathPrefix == prefix)?.Rule.Acl;
            // Held is covered: by the folder's own hold, or by one above it.
            var covering = HostedHolds.Covering(holds, AdminSourceName, prefix);
            var held = covering is not null;
            var ownHold = covering is not null && covering.PathPrefix == prefix;

            // The switch is a hold beside the rule, so it is planned on its
            // own, with or without a rule. Silence means "never leaves" for a
            // source being added, and leaves a registered one as it is.
            var wantHeld = wanted.Hosted is { } mayBeServed ? !mayBeServed : existing is null || held;
            if (wantHeld && !held)
                changes.Add(new HostedChange(wanted.Name!, prefix, MayBeServed: false, Held: false));
            else if (!wantHeld && ownHold)
                changes.Add(new HostedChange(wanted.Name!, prefix, MayBeServed: true, Held: true));
            else if (!wantHeld && held)
                // Releasing this folder would leave it held by the hold above,
                // so the plan would ask for it again on every run.
                problems.Add(new ProfileProblem(file,
                    $"The source '{wanted.Name}' says hosted: true, and its folder is held back by the switch on " +
                    $"{HostedHolds.Name(covering!)}, which covers it. Release that hold first."));

            if (wanted.Rule is null) continue;

            FolderRule? rule = null;
            var named = new Dictionary<Guid, string>();
            try
            {
                // Built here, with the names resolved here: an entry naming
                // somebody who does not exist is the refusal that stops a
                // profile from writing a rule an administrator could not. A
                // group the profile itself creates resolves to its placeholder.
                var stated = await ToAclSetAsync(wanted.Rule, names, pendingGroups, named, ct);
                rule = new FolderRule(AdminSourceName, prefix, stated);
            }
            catch (ArgumentException ex)
            {
                problems.Add(new ProfileProblem(file, $"The rule for '{wanted.Name}' will not do. {ex.Message}"));
            }

            if (rule is null) continue;

            // A folder under a legacy hold may still open its rule with the
            // entry the switch wrote before migration 0141. That entry is the
            // hold's, not the profile's, so a rule that differs only by it has
            // not drifted; a rule the profile states with the entry in it
            // matches as it stands. Either way the plan converges.
            var legacy = ownHold && legacyHolds.Contains(covering!);
            var drifted = inForce?.CanonicalText != rule.Acl.CanonicalText
                && !(legacy && inForce is not null
                     && AclSet.Create(SourceExposure.WithoutLegacyEntry(inForce.Entries, hostedModelAgents)).CanonicalText
                        == rule.Acl.CanonicalText);
            if (drifted)
                changes.Add(new RuleChange(rule, [.. rule.Acl.Entries.Select(e => e.ToString())], inForce?.CanonicalText, named));
        }
    }

    /// <summary>
    /// The rule's entries as an access list, read the way
    /// <see cref="PrincipalNames.ToAclSetAsync"/> reads them, except that
    /// <c>group:name</c> for a group this profile creates resolves to that
    /// group's placeholder, which is recorded in <paramref name="named"/>.
    /// </summary>
    private static async Task<AclSet> ToAclSetAsync(
        IReadOnlyList<string> entries, PrincipalNames names, IReadOnlyDictionary<string, Guid> pendingGroups,
        Dictionary<Guid, string> named, CancellationToken ct)
    {
        const string group = "group:";
        var parsed = new List<AclEntry>();
        foreach (var raw in entries)
        {
            var entry = raw.Trim();
            var space = entry.IndexOf(' ');
            var effect = space < 0 ? entry : entry[..space];
            var who = space < 0 ? "" : entry[(space + 1)..].Trim();
            if (effect is not ("allow" or "deny"))
                throw new ArgumentException($"'{raw}' is not 'allow <principal>' or 'deny <principal>'.");

            Principal principal;
            if (who.StartsWith(group, StringComparison.Ordinal) && pendingGroups.TryGetValue(who[group.Length..], out var placeholder))
            {
                principal = Principal.Group(CallerResolver.IdText(placeholder));
                named[placeholder] = who[group.Length..];
            }
            else
                principal = await names.ToPrincipalAsync(who, ct);

            parsed.Add(effect == "allow" ? AclEntry.Allow(principal) : AclEntry.Deny(principal));
        }
        return AclSet.Create(parsed);
    }

    /// <summary>
    /// The connector a folder rule names. Rules are keyed on the connector and
    /// the path prefix, not on a registered source's name.
    /// </summary>
    public const string AdminSourceName = "filesystem";

    private static bool MayUpdate(
        RegisteredSource existing, ProfileSource wanted, string folder, string prefix, string file, List<ProfileProblem> problems)
    {
        // A source's folder and prefix cannot be changed in place, and a
        // profile must not imply removing and re-adding one: that soft-deletes
        // the source and ingests the whole corpus again. An operator does that
        // deliberately or not at all.
        if (!string.Equals(existing.Folder, folder, StringComparison.OrdinalIgnoreCase))
        {
            problems.Add(new ProfileProblem(file,
                $"The source '{wanted.Name}' already reads {existing.Folder} and the profile says {folder}. A source's folder cannot be changed. Remove the source first if that is really what you want."));
            return false;
        }

        if (!string.Equals(existing.PathPrefix, prefix, StringComparison.Ordinal))
        {
            problems.Add(new ProfileProblem(file,
                $"The source '{wanted.Name}' is indexed at '{existing.PathPrefix}' and the profile says '{prefix}'. A source's prefix cannot be changed, because every document's path is built from it."));
            return false;
        }

        return true;
    }

    private static async Task<string> OwnerNameAsync(IdentityStore identity, Guid id, CancellationToken ct) =>
        (await identity.FindUserAsync(id, ct))?.Name ?? "a user who no longer signs in";

    private static JsonElement Json(string value) => JsonSerializer.SerializeToElement(value);

    private static string Describe(JsonElement value) =>
        value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : value.GetRawText();
}

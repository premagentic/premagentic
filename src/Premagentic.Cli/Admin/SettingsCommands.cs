using System.Text.Json;
using Premagentic.Core.Admin;
using Premagentic.Core.Evaluation;
using Premagentic.Core.Extensions;
using Premagentic.Core.Identity;
using Premagentic.Core.Okf;
using Premagentic.Core.Retrieval;
using Premagentic.Core.Storage;

namespace Premagentic.Cli.Admin;

/// <summary>
/// <c>prem settings</c>: every setting the deployment defines, read from one
/// list (<see cref="SettingsCatalog"/>), and the change record every change is
/// written to. Nobody signs in to the CLI, so a change made here is recorded
/// against the operating-system account that ran it.
/// </summary>
internal static class SettingsCommands
{
    public const string Verb = "settings";

    public const string Usage = """
        prem settings list
        prem settings get <key>
        prem settings set <key> <value>
        prem settings unset <key>
        prem settings history [--limit N]

          The deployment's settings, and the change record every change is written to. list shows every
          key the deployment defines, and get says what one does and what it accepts. set takes JSON,
          such as 40, 0.5 or {"default": 1.0}, except for a path or a trust value, which is plain text.
          unset puts any setting but a trust one back to its default, or to not set.

          --limit N              history: how many changes, newest first (default: 50)
        """;

    private const int ShownLength = 24;

    /// <param name="host">
    /// The loaded extensions, whose settings are listed, read and changed here
    /// like the built-in ones while they are loaded. Null is the built-ins alone.
    /// </param>
    public static async Task<int> RunAsync(string[] args, PremagenticDatabase db, Guid tenantId, ExtensionHost? host = null)
    {
        host ??= ExtensionHost.BuiltIn;
        try
        {
            return args.ElementAtOrDefault(1) switch
            {
                "list" => await ListAsync(db, tenantId, host.Settings),
                "get" => await GetAsync(args, db, tenantId, host),
                "set" => await SetAsync(args, db, tenantId, host),
                "unset" => await UnsetAsync(args, db, tenantId, host),
                "history" => await HistoryAsync(args, new ChangeRecord(db, tenantId)),
                _ => Fail("Usage:\n" + Usage),
            };
        }
        catch (ArgumentException ex)
        {
            return Fail(ex.Message);
        }
    }

    private static async Task<int> ListAsync(PremagenticDatabase db, Guid tenantId, DeploymentSettings defined)
    {
        var trust = (await new TrustSettingsStore(db, tenantId).ReadAllAsync()).ToDictionary(t => t.Key);
        var stored = await new SettingsStore(db, tenantId, settings: defined).GetManyAsync(defined.All.Select(d => d.Key).ToArray());
        foreach (var d in defined.All)
        {
            var (shown, state) = d.IsTrust
                ? (trust[d.Key].Value, State(trust[d.Key]))
                : Read(d, stored.TryGetValue(d.Key, out var v) ? v : null);
            Console.WriteLine($"{d.Key,-34} {Clip(shown),-24} {state}{From(defined, d.Key)}");
        }
        Console.WriteLine();
        Console.WriteLine(
            "Each applies the next time it is read, which for the trust and retrieval settings is the next search; the " +
            "no-answer floor and the golden set path are read only by the golden-set evaluation. 'prem settings get " +
            "<key>' says what a key does and what it accepts.");
        return 0;
    }

    private static async Task<int> GetAsync(string[] args, PremagenticDatabase db, Guid tenantId, ExtensionHost host)
    {
        var positionals = CliArgs.Positionals(args, 2);
        if (positionals.Count != 1) throw new ArgumentException("Give one setting: prem settings get <key>.");
        var d = Definition(positionals[0], host);

        var (shown, state) = d.IsTrust
            ? await ReadTrustAsync(d.Key, new TrustSettingsStore(db, tenantId))
            : Read(d, await new SettingsStore(db, tenantId, settings: host.Settings).GetAsync(d.Key));
        Console.WriteLine($"{d.Key} = {shown} ({state})");
        Console.WriteLine(d.Meaning);
        if (host.Settings.ExtensionOf(d.Key) is { } extension)
            Console.WriteLine($"Added by the extension {extension}; it is defined only while that extension is loaded.");
        Console.WriteLine(d.Default is { } def
            ? $"Default: {Plain(def)}. It takes {d.Accepts}."
            : $"Not set: {d.NotSet}. It takes {d.Accepts}.");
        return 0;
    }

    private static async Task<int> SetAsync(string[] args, PremagenticDatabase db, Guid tenantId, ExtensionHost host)
    {
        var positionals = CliArgs.Positionals(args, 2);
        if (positionals.Count != 2) throw new ArgumentException("Give a setting and a value: prem settings set <key> <value>.");
        var (key, text) = (positionals[0], positionals[1]);
        var d = Definition(key, host);

        if (d.IsTrust) return await SetTrustAsync(key, text, new TrustSettingsStore(db, tenantId));

        JsonElement value;
        if (d.TakesText) value = JsonSerializer.SerializeToElement(text);
        else if (!TryJson(text, out value)) return Fail($"{key} takes {d.Accepts}, not '{text}'.");
        if (d.Problem(value) is { } problem) return Fail(problem);

        // The flow's one check that spans two keys: it is never turned on
        // without the address every URL it advertises is made from.
        if (key == OAuthSettings.Enabled && value.ValueKind == JsonValueKind.True
            && !OAuthSettingRules.AddressUsable(await new SettingsStore(db, tenantId).GetAsync(OAuthSettings.PublicUrl)))
            return Fail(OAuthSettingRules.EnableNeedsAddress);

        var change = await new TuningSettingsStore(db, tenantId, settings: host.Settings).SetAsync(key, value, AdminActor.Cli());
        if (!change.Changed)
        {
            Console.WriteLine($"{key} is already {Plain(value)}. Nothing changed and nothing was recorded.");
            return 0;
        }

        Console.WriteLine(
            $"{key} is now {Plain(value)} (was {Was(d, change.Before)}). {Applies(key)} " +
            "The change is in the change record ('prem settings history').");
        if (key == OAuthSettings.Enabled && value.ValueKind == JsonValueKind.False)
            await SayLiveGrantsAsync(db, tenantId);
        if (key == TuningSettingsStore.GoldenSetPath && !File.Exists(text))
            Console.WriteLine(
                $"WARNING: this machine has no file at {text}. The golden-set run refuses until the machine that runs it " +
                "can read one there.");
        return 0;
    }

    private static async Task<int> SetTrustAsync(string key, string text, TrustSettingsStore store)
    {
        if (!TrustSettingsStore.TryNormalize(key, text, out var normalized))
            return Fail($"'{text}' is not a value of {key}. Allowed: {string.Join(", ", TrustSettingsStore.AllowedValues(key))}.");

        // The warning comes before the save, so it is read while there is still
        // time to stop.
        var current = await store.ReadAsync(key);
        if (TrustSettingsStore.IsLooser(key, current.Value, normalized))
            Console.WriteLine($"WARNING: {normalized} is looser than {current.Value}. {Consequence(key, normalized)}");

        var change = await store.SetAsync(key, normalized, AdminActor.Cli());
        Console.WriteLine(change.Changed
            ? $"{key} is now {normalized} (was {change.Before.Value}, {State(change.Before)}). It applies to the next search, " +
              "and the change is in the change record ('prem settings history')."
            : $"{key} is already {normalized}. Nothing changed and nothing was recorded.");
        return 0;
    }

    private static async Task<int> UnsetAsync(string[] args, PremagenticDatabase db, Guid tenantId, ExtensionHost host)
    {
        var positionals = CliArgs.Positionals(args, 2);
        if (positionals.Count != 1) throw new ArgumentException("Give one setting: prem settings unset <key>.");
        var d = Definition(positionals[0], host);
        var key = d.Key;

        if (d.IsTrust)
            return Fail($"{key} is a trust setting and cannot be unset. Set one of its values instead; " +
                        $"'prem settings get {key}' lists them.");
        if (key == OAuthSettings.PublicUrl
            && await new SettingsStore(db, tenantId).GetAsync(OAuthSettings.Enabled) is { ValueKind: JsonValueKind.True })
            return Fail(OAuthSettingRules.AddressNeededWhileOn);

        var change = await new TuningSettingsStore(db, tenantId, settings: host.Settings).UnsetAsync(key, AdminActor.Cli());
        if (!change.Changed)
        {
            Console.WriteLine(d.Default is null
                ? $"{key} is not set. Nothing changed and nothing was recorded."
                : $"{key} is not set, so it already has its default. Nothing changed and nothing was recorded.");
            return 0;
        }

        Console.WriteLine(d.Default is { } def
            ? $"{key} is back to its default, {Plain(def)} (was {Plain(change.Before!.Value)}). {Applies(key)} " +
              "The change is in the change record ('prem settings history')."
            : $"{key} is no longer set (was {Plain(change.Before!.Value)}). Not set: {d.NotSet}. " +
              "The change is in the change record ('prem settings history').");
        if (key == OAuthSettings.Enabled) await SayLiveGrantsAsync(db, tenantId);
        return 0;
    }

    /// <summary>
    /// Turning the flow off suspends its grants and ends none; this says how
    /// many are live and how to end them.
    /// </summary>
    private static async Task SayLiveGrantsAsync(PremagenticDatabase db, Guid tenantId)
    {
        var live = await OAuthStatus.LiveGrantsAsync(db, tenantId, DateTimeOffset.UtcNow);
        Console.WriteLine(
            $"{live} grant(s) are live. Turning the flow off suspends them and ends none: 'prem oauth grants revoke --all' ends them.");
    }

    private static async Task<int> HistoryAsync(string[] args, ChangeRecord record)
    {
        var limitText = CliArgs.Value(args, "--limit");
        var limit = 50;
        if (limitText is not null && (!int.TryParse(limitText, out limit) || limit <= 0))
            throw new ArgumentException("--limit is a positive number.");

        var events = await record.ListAsync(limit);
        if (events.Count == 0)
        {
            Console.WriteLine("The change record is empty.");
            return 0;
        }
        foreach (var e in events)
            Console.WriteLine(
                $"{e.OccurredAt:u}  {e.Kind,-14} {e.Target,-28} {Show(e.OldValue)} -> {Show(e.NewValue)}  by {e.Actor.Describe()}");
        return 0;
    }

    /// <summary>
    /// A key as it stands: the value in force, and where it came from. A stored
    /// value that cannot be used is named in the state, and the value shown is
    /// the one that applies instead: the default, or nothing.
    /// </summary>
    private static (string Shown, string State) Read(SettingDefinition d, JsonElement? stored) => stored switch
    {
        { } v when d.Problem(v) is null => (Plain(v), "set"),
        { } v => (InForce(d), d.Default is not null
            ? $"UNUSABLE: {d.Problem(v)} The default applies."
            : $"UNUSABLE: {d.Problem(v)} It reads as not set: {d.NotSet}."),
        null when d.Default is not null => (InForce(d), "default"),
        null => (InForce(d), $"not set: {d.NotSet}"),
    };

    private static string InForce(SettingDefinition d) => d.Default is { } def ? Plain(def) : "(not set)";

    // A trust setting reads through its own store, which gives an unreadable
    // value its strictest reading.
    private static async Task<(string Shown, string State)> ReadTrustAsync(string key, TrustSettingsStore store)
    {
        var reading = await store.ReadAsync(key);
        return (reading.Value, State(reading));
    }

    private static string State(TrustSettingReading s) => s.Source switch
    {
        TrustSettingSource.Default => "default",
        TrustSettingSource.Stored => "set",
        _ => $"UNREADABLE: the stored value {s.StoredJson} cannot be read, so the strictest value applies",
    };

    private static string Applies(string key) => key switch
    {
        RetrievalSettings.NoAnswerDistanceFloor => "It applies to the next golden-set evaluation; a search is not filtered by it.",
        TuningSettingsStore.GoldenSetPath => "The next golden-set run reads it.",
        McpSettings.MaxRequestBytes => "This applies when the server next starts; restart it.",
        _ when OAuthSettings.ReadAtStart.Contains(key) =>
            "This applies when the server next starts; restart it. To stop live clients now: prem oauth grants revoke --all.",
        _ when RetrievalSettings.Keys.Contains(key) => "It applies to the next search.",
        _ => "It applies the next time it is read.",
    };

    private static string Was(SettingDefinition d, JsonElement? before) => before is { } b
        ? Plain(b)
        : d.Default is { } def ? $"the default, {Plain(def)}" : "not set";

    private static bool TryJson(string text, out JsonElement value)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            value = doc.RootElement.Clone();
            return true;
        }
        catch (JsonException)
        {
            value = default;
            return false;
        }
    }

    private static string Consequence(string key, string value) => (key, value) switch
    {
        (TrustSettingsStore.AgentsMinimumTier, "unverified") =>
            "Agents will be served machine-written content that nobody has confirmed, so one agent's unreviewed output " +
            "can become another agent's input.",
        (TrustSettingsStore.AgentsMinimumTier, _) =>
            "Agents will be served machine-written content that only a machine has confirmed.",
        (TrustSettingsStore.PeopleMinimumTier, _) =>
            $"People will be served machine-written content at {value}, flagged with its tier.",
        (_, "shown-to-everyone") =>
            "Agents will be served content past its stale date, flagged as stale.",
        _ =>
            "People will be served content past its stale date, flagged as stale.",
    };

    // A path reads as itself; every other value as the JSON it is stored as.
    private static string Plain(JsonElement value) =>
        value.ValueKind == JsonValueKind.String ? value.GetString()! : value.GetRawText();

    private static string Show(JsonElement? value) => value is { } v ? v.GetRawText() : "(none)";

    private static string Clip(string text) =>
        text.Length <= ShownLength ? text : string.Concat(text.AsSpan(0, ShownLength - 3), "...");

    private static SettingDefinition Definition(string key, ExtensionHost host) =>
        host.Settings.Find(key) ?? throw UnknownKey(key, host);

    private static ArgumentException UnknownKey(string key, ExtensionHost host) =>
        new(BusinessAddOn.SettingRefusal(key, host)
            ?? $"There is no setting '{key}'. The settings are: {string.Join(", ", host.Settings.All.Select(d => d.Key))}.");

    /// <summary>Which extension a key came from, for the list; nothing for a built-in key.</summary>
    private static string From(DeploymentSettings defined, string key) =>
        defined.ExtensionOf(key) is { } extension ? $" (added by {extension})" : "";

    private static int Fail(string message)
    {
        Console.Error.WriteLine(message);
        return 1;
    }
}

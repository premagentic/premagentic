using System.Text.Json;
using Premagentic.Core.Admin;
using Premagentic.Core.Evaluation;
using Premagentic.Core.Extensions;
using Premagentic.Core.Mcp;
using Premagentic.Core.Okf;
using Premagentic.Core.Retrieval;

namespace Premagentic.Core.Identity;

/// <summary>One setting a deployment defines: what it means, what it takes, and what applies when nothing is set.</summary>
/// <param name="Accepts">What the key takes, as a phrase that completes "takes ...".</param>
/// <param name="Default">
/// The value that applies when nothing is stored, as it would be stored, or
/// null when nothing stored means the feature is simply not set.
/// </param>
/// <param name="NotSet">What an unset key means, in a few words, for a key with no <paramref name="Default"/>.</param>
/// <param name="Problem">One sentence naming what is allowed when a value cannot be used, or null when it can.</param>
/// <param name="TakesText">
/// True when the command line value is taken as plain text, such as a path,
/// rather than as JSON.
/// </param>
/// <param name="IsTrust">
/// True for a trust setting, which is set by its own store with its own
/// warning and is never unset: it always has one of its values.
/// </param>
public sealed record SettingDefinition(
    string Key,
    string Meaning,
    string Accepts,
    JsonElement? Default,
    string? NotSet,
    Func<JsonElement, string?> Problem,
    bool TakesText = false,
    bool IsTrust = false);

/// <summary>
/// Every setting a deployment defines, in one list, so the four <c>prem
/// settings</c> verbs read the same keys the stores write. The settings store
/// refuses a key that is not here, so a key added to a store and not to this
/// list fails the first time anything reads or writes it.
/// </summary>
public static class SettingsCatalog
{
    /// <summary>The key of the connect page's snippet templates, per assistant kind.</summary>
    public const string ConnectSnippets = "connect.snippets";

    /// <summary>
    /// The kinds the connect page offers. The portal's list is the one the form
    /// shows; a test asserts the two are the same, so a kind added to one and
    /// not the other fails the build's tests.
    /// </summary>
    public static readonly string[] ConnectSnippetKinds =
        ["claude-desktop", "chatgpt", "copilot", "coding-tool", "local-mcp", "other"];

    public const int ConnectSnippetMaxLength = 4_000;

    public static IReadOnlyList<SettingDefinition> All { get; } = Build();

    private static readonly Dictionary<string, SettingDefinition> ByKey =
        All.ToDictionary(d => d.Key, StringComparer.Ordinal);

    public static bool Contains(string key) => ByKey.ContainsKey(key);

    /// <summary>The definition of <paramref name="key"/>, or null when the deployment defines no such key.</summary>
    public static SettingDefinition? Find(string key) => ByKey.GetValueOrDefault(key);

    private static List<SettingDefinition> Build()
    {
        var all = new List<SettingDefinition>();

        foreach (var key in TrustSettingsStore.Keys)
            all.Add(new SettingDefinition(key, TrustMeaning(key),
                $"one of {string.Join(", ", TrustSettingsStore.AllowedValues(key))}",
                JsonSerializer.SerializeToElement(TrustSettingsStore.DefaultValue(key)), null,
                v => v.ValueKind == JsonValueKind.String && TrustSettingsStore.TryNormalize(key, v.GetString(), out _)
                    ? null
                    : $"{key} takes one of {string.Join(", ", TrustSettingsStore.AllowedValues(key))}.",
                TakesText: true, IsTrust: true));

        foreach (var key in RetrievalSettings.Keys)
            all.Add(new SettingDefinition(key, RetrievalMeaning(key), RetrievalSettings.Allowed(key),
                RetrievalSettings.DefaultValue(key), null,
                v => RetrievalSettings.TryParse(key, v, out var problem) ? null : problem));

        all.Add(new SettingDefinition(TuningSettingsStore.GoldenSetPath,
            "The golden question set the portal's evaluation runs, as an absolute path on the machine that runs " +
            "Premagentic. It is set here only: the portal shows it and cannot change it, because the server reads " +
            "whatever file it names.",
            "an absolute path to a golden question set file", null,
            "the golden-set run refuses until it is set",
            v => v.ValueKind != JsonValueKind.String
                ? $"{TuningSettingsStore.GoldenSetPath} takes a path as text."
                : TuningSettingsStore.TryCheckGoldenSetPath(v.GetString(), out var problem) ? null : problem,
            TakesText: true));

        all.Add(new SettingDefinition(McpToolDescriptions.Key,
            "What this deployment calls its two MCP tools. An assistant reads a tool's description to decide whether " +
            "to call it, so naming the corpus gets the tool called when it should be. A tool left out keeps the " +
            "shipped text.",
            McpToolDescriptions.Allowed, null, "both tools keep the shipped text",
            McpToolDescriptions.Problem));

        all.Add(new SettingDefinition(McpSettings.Instructions,
            "What this deployment tells every assistant that connects: house rules, what the corpus holds, how to " +
            "cite. It is sent at connect as the server's instructions and is readable as the resource " +
            "premagentic://instructions. Instructions are obeyed, so only an administrator writes them. The server " +
            "reads them when it starts.",
            McpSettings.InstructionsAccepts, null, "an assistant is told nothing beyond the tools' descriptions",
            McpSettings.InstructionsProblem, TakesText: true));

        all.Add(new SettingDefinition(McpSettings.MaxRequestBytes,
            "The largest request an assistant may send to /mcp, in bytes. A larger one is refused with HTTP 413 and a " +
            "JSON-RPC error before anything reads past this bound. The stdio bridge reaches the server through /mcp, so " +
            "the same bound covers it. The server reads it when it starts, so a change applies after a restart.",
            McpSettings.MaxRequestBytesAccepts, JsonSerializer.SerializeToElement(McpSettings.MaxRequestBytesDefault), null,
            McpSettings.MaxRequestBytesProblem));

        all.Add(new SettingDefinition(ExtensionSettings.Folder,
            $"The folder whose subfolders hold extensions. {ExtensionSettings.FolderVariable} says the same thing, and " +
            "this setting wins when both are set.",
            ExtensionSettings.Accepts(ExtensionSettings.Folder), null,
            $"{ExtensionSettings.FolderVariable} decides, and with neither no extension loads",
            v => ExtensionSettings.TryParse(ExtensionSettings.Folder, v, out var problem) ? null : problem,
            TakesText: true));

        all.Add(new SettingDefinition(ExtensionSettings.Allowed,
            "The extensions allowed to load, each by its name and the hash of its assembly. 'prem extensions allow' " +
            "writes it and computes the hash itself.",
            ExtensionSettings.Accepts(ExtensionSettings.Allowed), null, "no extension loads",
            v => ExtensionSettings.TryParse(ExtensionSettings.Allowed, v, out var problem) ? null : problem));

        all.Add(new SettingDefinition(AgentSettings.SelfServiceMax,
            "How many agents one person may create for themself on the connect page. Each acts as that person and " +
            "nothing more.",
            AgentSettings.SelfServiceAccepts,
            JsonSerializer.SerializeToElement(AgentSettings.SelfServiceDefault), null,
            AgentSettings.SelfServiceProblem));

        AddOAuth(all);

        all.Add(new SettingDefinition(ConnectSnippets,
            "The configuration the connect page shows for each kind of assistant: a JSON object of assistant kind to a " +
            "text template, where {address} is the server's address and {token} the new token. A kind left out keeps " +
            "the shipped template.",
            $"an object of assistant kinds ({string.Join(", ", ConnectSnippetKinds)}) to templates of 1 to " +
            $"{ConnectSnippetMaxLength:N0} characters, each holding {{token}}", null,
            "every kind keeps the shipped template",
            ConnectSnippetsProblem));

        return all;
    }

    /// <summary>The settings of the MCP authorization flow, off by default.</summary>
    private static void AddOAuth(List<SettingDefinition> all)
    {
        const string restart = " Read when the server starts, so a change applies after a restart.";

        all.Add(new SettingDefinition(OAuthSettings.Enabled,
            "Turns on the MCP authorization flow, so an assistant can sign a person in through the browser instead of " +
            "being given a token. Off by default; while off, none of the flow's addresses exist." + restart +
            " With it on and no usable public address, the server starts with the flow off and says so.",
            "true or false", JsonSerializer.SerializeToElement(false), null,
            v => OAuthSettingRules.BooleanProblem(OAuthSettings.Enabled, v)));

        all.Add(new SettingDefinition(OAuthSettings.PublicUrl,
            "This server's address as assistants reach it, such as https://prem.example.internal:8443. Every address " +
            "the flow advertises, and the audience every access token is bound to, is made from it, never from a " +
            "request. Written in one spelling only." + restart,
            "an https address with no path, in lowercase, and no :443", null,
            "the flow cannot start",
            OAuthSettingRules.PublicUrlProblem, TakesText: true));

        all.Add(new SettingDefinition(OAuthSettings.DynamicRegistration,
            "Lets an assistant register itself with this server. Off, only an administrator registers one, with " +
            "'prem oauth clients add'." + restart,
            "true or false", JsonSerializer.SerializeToElement(true), null,
            v => OAuthSettingRules.BooleanProblem(OAuthSettings.DynamicRegistration, v)));

        all.Add(new SettingDefinition(OAuthSettings.DynamicRedirectUris,
            "The https addresses an assistant that registered itself may send its answer to, each exactly. Empty, such " +
            "an assistant may answer only to a program on the computer the browser runs on." + restart,
            OAuthSettingRules.DynamicRedirectUrisAccepts, JsonSerializer.SerializeToElement(Array.Empty<string>()), null,
            OAuthSettingRules.DynamicRedirectUrisProblem));

        AddNumber(all, OAuthSettings.MaxPendingClients,
            "The most assistants that registered themselves and were never approved. Assistants an administrator " +
            "registered, and assistants approved once, never count.",
            OAuthSettings.MaxPendingClientsDefault, 1, 10_000);
        AddNumber(all, OAuthSettings.PendingClientsPerAddress,
            "The most never-approved self-registered assistants one address may hold (an IPv6 address counts by its /64).",
            OAuthSettings.PendingClientsPerAddressDefault, 1, 1_000);
        AddNumber(all, OAuthSettings.RegistrationsPerHour,
            "How many times one address may register an assistant in an hour. Counted in each server process's memory.",
            OAuthSettings.RegistrationsPerHourDefault, 1, 1_000);
        AddNumber(all, OAuthSettings.AccessTokenMinutes,
            "How long an access token lasts, in minutes. The assistant refreshes it without asking the person.",
            OAuthSettings.AccessTokenMinutesDefault, 5, 1_440);
        AddNumber(all, OAuthSettings.RefreshTokenDays,
            "How long a refresh token lasts unused, in days. Each refresh issues a new one.",
            OAuthSettings.RefreshTokenDaysDefault, 1, 365);
        AddNumber(all, OAuthSettings.GrantDays,
            "How long a person's approval of an assistant lasts however it is used, in days, before they approve again.",
            OAuthSettings.GrantDaysDefault, 1, 365);
        AddNumber(all, OAuthSettings.CodeSeconds,
            "How long the code an approval sends back to the assistant lasts, in seconds. It is used once.",
            OAuthSettings.CodeSecondsDefault, 10, 600);
    }

    private static void AddNumber(List<SettingDefinition> all, string key, string meaning, int fallback, int min, int max) =>
        all.Add(new SettingDefinition(key, meaning,
            $"a whole number from {min.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)} to {max.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)}",
            JsonSerializer.SerializeToElement(fallback), null, OAuthSettingRules.Range(key, min, max)));

    /// <summary>Null when the value can be stored, otherwise the sentence that says why not.</summary>
    public static string? ConnectSnippetsProblem(JsonElement value)
    {
        const string key = ConnectSnippets;
        if (value.ValueKind != JsonValueKind.Object)
            return $"{key} takes an object of assistant kinds to templates. The kinds are {string.Join(", ", ConnectSnippetKinds)}.";

        foreach (var member in value.EnumerateObject())
        {
            if (!ConnectSnippetKinds.Contains(member.Name, StringComparer.Ordinal))
                return $"{key} has no assistant kind '{member.Name}'. The kinds are {string.Join(", ", ConnectSnippetKinds)}.";
            if (member.Value.ValueKind != JsonValueKind.String || member.Value.GetString() is not { Length: > 0 and <= ConnectSnippetMaxLength } text)
                return $"{key} takes a text of 1 to {ConnectSnippetMaxLength:N0} characters for '{member.Name}'.";
            if (!text.Contains("{token}", StringComparison.Ordinal))
                return $"{key} for '{member.Name}' does not say where the token goes: write {{token}} where it belongs.";
        }
        return null;
    }

    private static string TrustMeaning(string key) => key switch
    {
        TrustSettingsStore.AgentsMinimumTier =>
            "The lowest trust tier at which agents are served machine-written content. An agent with a minimum of its own " +
            "uses that instead. Content written by people, and ordinary files, are never held back by it.",
        TrustSettingsStore.PeopleMinimumTier =>
            "The lowest trust tier at which people are served machine-written content. Whatever it is, every result says " +
            "its tier and who wrote it.",
        _ =>
            "Who is served content past its stale_after: nobody, people only, or everyone. Whoever is served it sees it " +
            "flagged as stale.",
    };

    private static string RetrievalMeaning(string key) => key switch
    {
        RetrievalSettings.NoAnswerDistanceFloor =>
            "The cosine distance above which a passage found only by meaning does not count as an answer, when the " +
            "golden-set evaluation judges a question that should have none. A search is not filtered by it. It moves " +
            "with the embedding model and the corpus, so calibrate it against this deployment's golden set.",
        RetrievalSettings.RrfK =>
            "The smoothing constant of the fusion that merges the word match with the meaning match. A larger value " +
            "narrows the gap between the first results and the later ones.",
        RetrievalSettings.FallbackRrfWeight =>
            "How much the word match counts for a passage that matched only some of the query's words, against one that " +
            "matched them all. 0 gives it nothing from the word match; 1 counts it in full.",
        _ =>
            "A weight per document class that multiplies a passage's score, after the gates have decided what may be " +
            "returned. Flat unless this deployment has measured otherwise against its own golden set: a weighting taken " +
            "from another deployment is worse than none.",
    };
}

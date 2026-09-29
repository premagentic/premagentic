using System.Text.Json;
using Premagentic.Core.Identity;

namespace Premagentic.Core.Mcp;

/// <summary>The tool text in force, and the stored value if it could not be used.</summary>
/// <param name="ByTool">
/// The text to describe each tool with. A tool with no entry keeps the text this
/// version ships, which is why a partial setting is not a mistake: a deployment
/// may want to say something of its own about one tool and nothing about the
/// other.
/// </param>
/// <param name="Problem">
/// One sentence naming what is allowed, when a value is stored that cannot be
/// used. The shipped text stands in that case: a description an assistant reads
/// decides when it calls the tool at all, so a typo must not leave a deployment
/// with no description, and must not stop the server either.
/// </param>
public sealed record McpToolDescriptionsReading(IReadOnlyDictionary<string, string> ByTool, string? Problem)
{
    public static McpToolDescriptionsReading Shipped { get; } =
        new(new Dictionary<string, string>(StringComparer.Ordinal), null);

    /// <summary>The text for one tool, or null to keep the text this version ships.</summary>
    public string? For(string toolName) => ByTool.TryGetValue(toolName, out var text) ? text : null;
}

/// <summary>
/// What this deployment calls its two tools, kept in <c>prem_config.setting</c>
/// as JSON under <c>mcp.tool_descriptions</c>:
/// <code>
/// {"search_knowledge": "Search the Contoso handbook and client files.",
///  "get_document_section": "Fetch the full text of a section search cited."}
/// </code>
/// <para>
/// Why it is worth setting: a tool's description is what an assistant reads to
/// decide whether to call it at all. The shipped text describes a general
/// document search, and a deployment that names its own corpus ("the Contoso
/// handbook") gets called when it should be and left alone when it should not.
/// </para>
/// <para>
/// Either tool may be left out and keeps the shipped text. A stored value that
/// cannot be used is reported and the shipped text stands, the same way a
/// mistyped retrieval setting changes no ordering: a description is not worth
/// refusing to serve over.
/// </para>
/// </summary>
public static class McpToolDescriptions
{
    public const string Key = "mcp.tool_descriptions";

    /// <summary>The MCP tool that searches. Named here so the setting and the tool cannot drift apart.</summary>
    public const string SearchKnowledge = "search_knowledge";

    /// <summary>The MCP tool that fetches one section.</summary>
    public const string GetDocumentSection = "get_document_section";

    public static IReadOnlyList<string> ToolNames { get; } = [SearchKnowledge, GetDocumentSection];

    /// <summary>
    /// The longest text a deployment may give one tool. Long enough for a
    /// paragraph naming a corpus and its rules, short enough that the tool list
    /// cannot be used to push a wall of text into every assistant's context.
    /// </summary>
    public const int MaxLength = 2_000;

    /// <summary>What the key accepts, as a phrase that completes "takes ...".</summary>
    public static string Allowed =>
        "an object of tool names to descriptions, such as {\"" + SearchKnowledge + "\": \"Search the handbook.\"}, " +
        $"each a text of 1 to {MaxLength:N0} characters; the tool names are {string.Join(" and ", ToolNames)}";

    /// <summary>True when the stored value can be used, with the reason when it cannot.</summary>
    public static bool TryRead(JsonElement value, out McpToolDescriptionsReading reading, out string? problem)
    {
        reading = McpToolDescriptionsReading.Shipped;
        problem = null;

        if (value.ValueKind != JsonValueKind.Object)
            return Refuse(out problem, $"{Key} takes {Allowed}, not {Describe(value)}.");

        var byTool = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var member in value.EnumerateObject())
        {
            if (!ToolNames.Contains(member.Name, StringComparer.Ordinal))
                return Refuse(out problem, $"{Key} has no tool '{Clip(member.Name)}'. The tools are {string.Join(" and ", ToolNames)}.");
            if (member.Value.ValueKind != JsonValueKind.String)
                return Refuse(out problem, $"{Key} takes a text for '{member.Name}', not {Describe(member.Value)}.");

            var text = member.Value.GetString()!;
            if (string.IsNullOrWhiteSpace(text))
                return Refuse(out problem, $"{Key} takes a text for '{member.Name}' that is not blank. Remove the member to keep the shipped text.");
            if (text.Length > MaxLength)
                return Refuse(out problem, $"{Key} takes at most {MaxLength:N0} characters for '{member.Name}', not {text.Length:N0}.");
            byTool[member.Name] = text;
        }

        reading = new McpToolDescriptionsReading(byTool, null);
        return true;
    }

    /// <summary>The problem with a stored value, or null when it can be used.</summary>
    public static string? Problem(JsonElement value) => TryRead(value, out _, out var problem) ? null : problem;

    /// <summary>
    /// What this deployment calls its tools, from the stored setting. Nothing
    /// stored, or something that cannot be used, keeps the shipped text and says
    /// so in <see cref="McpToolDescriptionsReading.Problem"/>.
    /// </summary>
    public static async Task<McpToolDescriptionsReading> ReadAsync(SettingsStore settings, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (await settings.GetAsync(Key, ct) is not { } value) return McpToolDescriptionsReading.Shipped;
        return TryRead(value, out var reading, out var problem)
            ? reading
            : McpToolDescriptionsReading.Shipped with { Problem = problem };
    }

    private static bool Refuse(out string? problem, string sentence)
    {
        problem = sentence;
        return false;
    }

    private static string Describe(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => "a text where an object belongs",
        JsonValueKind.Number => "a number",
        JsonValueKind.True or JsonValueKind.False => "true or false",
        JsonValueKind.Null => "null",
        JsonValueKind.Array => "a list",
        _ => "that",
    };

    private static string Clip(string text) => text.Length <= 40 ? text : text[..40] + "...";
}

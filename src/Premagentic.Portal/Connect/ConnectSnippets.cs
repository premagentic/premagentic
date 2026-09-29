using System.Text.Json;
using Premagentic.Core.Identity;
using Premagentic.Core.Storage;

namespace Premagentic.Portal.Connect;

/// <summary>
/// The configuration a person pastes into their assistant, one text template
/// per kind of assistant, with the server's address and the new token filled
/// in. The shipped templates are <c>Connect/snippets.json</c>, carried in the
/// assembly; the setting <see cref="Key"/> overrides any kind's template, and
/// a kind it leaves out keeps the shipped one.
/// <para>
/// A template names <see cref="AddressField"/> where the server's address goes
/// and <see cref="TokenField"/> where the token goes. Nothing else in it is
/// read, so a template is text taken as it is written.
/// </para>
/// </summary>
internal static class ConnectSnippets
{
    public const string Key = "connect.snippets";

    public const string AddressField = "{address}";
    public const string TokenField = "{token}";

    /// <summary>The longest template the setting takes for one kind.</summary>
    public const int MaxLength = 4_000;

    /// <summary>
    /// The kinds of assistant the connect form offers, in the order it offers
    /// them, each with the words it shows. The shipped file holds exactly
    /// these.
    /// </summary>
    public static IReadOnlyList<(string Kind, string Label)> Kinds { get; } =
    [
        ("claude-desktop", "Claude Desktop"),
        ("chatgpt", "ChatGPT"),
        ("copilot", "Copilot in VS Code"),
        ("coding-tool", "A coding tool, such as Claude Code"),
        ("local-mcp", "A local MCP client"),
        ("other", "Something else"),
    ];

    public static string Label(string kind) =>
        Kinds.FirstOrDefault(k => k.Kind == kind).Label ?? kind;

    /// <summary>The templates the portal ships, by kind.</summary>
    public static IReadOnlyDictionary<string, string> Shipped { get; } = LoadShipped();

    /// <summary>
    /// The templates in force: the shipped ones, with any kind the setting
    /// holds replaced by its text. A stored value that cannot be used is not
    /// used at all, and <c>Problem</c> says why.
    /// </summary>
    public static async Task<(IReadOnlyDictionary<string, string> Templates, string? Problem)> InForceAsync(
        PremagenticDatabase db, Guid tenant, CancellationToken ct)
    {
        if (await new SettingsStore(db, tenant).GetAsync(Key, ct) is not { } value) return (Shipped, null);
        if (!TryRead(value, out var overrides, out var problem)) return (Shipped, problem);

        var merged = new Dictionary<string, string>(Shipped, StringComparer.Ordinal);
        foreach (var (kind, template) in overrides) merged[kind] = template;
        return (merged, null);
    }

    /// <summary>The template for a kind with the address and the token filled in.</summary>
    public static string Render(string template, string address, string token) =>
        template.Replace(AddressField, address, StringComparison.Ordinal).Replace(TokenField, token, StringComparison.Ordinal);

    /// <summary>
    /// Whether a stored value can be used: an object of kinds to templates,
    /// each kind one the form offers, each template a text of 1 to
    /// <see cref="MaxLength"/> characters that says where the token goes.
    /// </summary>
    public static bool TryRead(JsonElement value, out IReadOnlyDictionary<string, string> overrides, out string? problem)
    {
        var read = new Dictionary<string, string>(StringComparer.Ordinal);
        overrides = read;
        problem = null;

        if (value.ValueKind != JsonValueKind.Object)
        {
            problem = $"{Key} takes an object of assistant kinds to templates. The kinds are {string.Join(", ", Kinds.Select(k => k.Kind))}.";
            return false;
        }

        foreach (var member in value.EnumerateObject())
        {
            if (Kinds.All(k => k.Kind != member.Name))
                problem = $"{Key} has no assistant kind '{member.Name}'. The kinds are {string.Join(", ", Kinds.Select(k => k.Kind))}.";
            else if (member.Value.ValueKind != JsonValueKind.String || member.Value.GetString() is not { Length: > 0 and <= MaxLength } text)
                problem = $"{Key} takes a text of 1 to {MaxLength:N0} characters for '{member.Name}'.";
            else if (!text.Contains(TokenField, StringComparison.Ordinal))
                problem = $"{Key} for '{member.Name}' does not say where the token goes: write {TokenField} where it belongs.";
            else
            {
                read[member.Name] = text;
                continue;
            }
            return false;
        }

        return true;
    }

    private static IReadOnlyDictionary<string, string> LoadShipped()
    {
        using var stream = typeof(ConnectSnippets).Assembly.GetManifestResourceStream("Premagentic.Portal.Connect.snippets.json")
            ?? throw new InvalidOperationException("The portal was built without Connect/snippets.json.");
        return JsonSerializer.Deserialize<Dictionary<string, string>>(stream)
            ?? throw new InvalidOperationException("Connect/snippets.json holds JSON null.");
    }
}

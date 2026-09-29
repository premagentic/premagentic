using System.Globalization;
using Premagentic.Core.Identity;
using Premagentic.Portal.Connect;
using Premagentic.Portal.Html;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Premagentic.Portal.Pages;

/// <summary>
/// Connect an assistant: a signed-in person makes the agent that acts as
/// them, sees its token once with the configuration for the assistant they
/// use, and can give it a new key, revoke it or remove it, a new key and a
/// removal each after a page that asks. Every person may use it, and every person sees
/// only their own agents here; administrators see everyone's on the agents
/// page.
/// <para>
/// The token is in the response to the request that made it and nowhere else:
/// not in a redirect, a link, a later page, the change record or a log. The
/// agent can reach nothing its person cannot, because it has no field for a
/// group or a rate to ask for more.
/// </para>
/// </summary>
internal static class ConnectPages
{
    public const string Path = "/portal/connect";

    /// <summary>Where a person revokes one of their own grants; mapped only while the authorization flow is on.</summary>
    public const string GrantRevokePath = Path + "/grants/revoke";

    // Hosted first and chosen by default, as on the agents page: it is the
    // answer that keeps a new agent out of a folder marked never-leaves until
    // somebody says otherwise.
    private static readonly (string, string)[] Models =
        [("hosted", "a hosted model, outside your network"), ("local", "a local model, inside your network")];

    public static async Task<IResult> Show(HttpContext http)
    {
        var r = PortalRequest.Of(http);
        var own = Store(r);
        var agents = await own.ListAsync(r.Person.User.Id, r.Aborted);
        var connected = await own.ConnectedCountAsync(r.Person.User.Id, r.Aborted);
        var max = await SelfServiceMaxAsync(r);
        var (_, problem) = await ConnectSnippets.InForceAsync(r.Db, r.Tenant, r.Aborted);

        // The assistants that asked through the authorization flow, only while it
        // is on: while it is off none is listed, and no table of the flow is read.
        var oauth = http.RequestServices.GetService<OAuthDeployment>();
        var grants = http.RequestServices.GetService<IOAuthGrants>() is { } flow
            ? await flow.ListOwnAsync(r.Person.User.Id, r.Aborted)
            : null;

        var rows = agents.Select(a => Layout.Row(
            a.Name, ConnectSnippets.Label(a.AssistantKind),
            a.ModelVendor is { } vendor ? $"{a.ModelLocation} ({vendor})" : a.ModelLocation,
            $"{a.RatePerMinute}/min", Layout.When(a.CreatedAt),
            a.LastUsedAt is { } used ? Layout.When(used) : M.H($"never"),
            StateWords(a.State),
            RowButtons(r, a)));

        var form = max == 0
            ? M.H($"<p class=\"note\">An administrator turned this off: people on this deployment cannot connect an assistant themselves. Ask an administrator for an agent.</p>")
            : connected >= max
                ? M.H($"<p class=\"note\">You have {connected} assistant(s) connected, which is as many as this deployment allows each person ({max}). Revoke or remove one to connect another.</p>")
                : Layout.OwnForm(r, Path, M.H($"""
                    {Layout.Field("Name", "name", required: true)}
                    {Layout.Select("Assistant", "kind", ConnectSnippets.Kinds.Select(k => (k.Kind, k.Label)))}
                    {Layout.Select("Where its model runs", "model", Models, "hosted")}
                    {Layout.Field("Model vendor, for a hosted model", "vendor")}
                    """), "Connect");

        return Layout.Page(r, "Connect an assistant", M.H($"""
            <p class="note">An assistant you connect here acts as you: it reads what you may read, as your access is at each call, and nothing more.
            It reads and never writes, runs at {SelfServeAgents.RatePerMinute} requests a minute, and its token lasts {SelfServeAgents.TokenLifetime.Days} days.
            You can give it a new key, revoke it or remove it here at any time; its next call after a revoke or a remove reaches nothing.
            An agent an administrator registered for you is not listed here.</p>
            <h2>Your assistants</h2>
            {(max == 0 ? Markup.Empty : M.H($"<p class=\"note\">Connected now: {connected} of the {max} this deployment allows each person{(grants is null ? "" : ", counting the assistants that asked to connect")}.</p>"))}
            {Layout.Table(["Name", "Assistant", "Where its model runs", "Rate", "Connected", "Last used", "State", ""], rows)}
            {GrantList(r, oauth, grants)}
            <h2>Connect an assistant</h2>
            {form}
            {(r.IsAdministrator && problem is not null ? M.H($"<p class=\"warning\">The deployment's {ConnectSnippets.Key} setting is not used, so every assistant gets the shipped configuration: {problem}</p>") : Markup.Empty)}
            """));
    }

    /// <summary>
    /// How an assistant asks to connect, and the ones that asked to search as
    /// the person through the authorization flow and that the person approved,
    /// ended ones included, each with a Revoke while it holds or waits for its
    /// assistant. The address given is the server's public one, never the one
    /// this request was built from. Whatever a client said about itself is
    /// inside its own <c>&lt;bdi&gt;</c>. Nothing while the flow is off.
    /// </summary>
    private static Markup GrantList(PortalRequest r, OAuthDeployment? oauth, IReadOnlyList<OAuthGrantView>? grants) =>
        oauth is null || grants is null ? Markup.Empty : M.H($"""
        <h2>Assistants that asked to connect</h2>
        <p class="note">An assistant can also ask to connect by itself: give it the address <code>{oauth.Resource}</code>, then approve it here when it asks.
        {(string.Equals(Address(r.Http.Request), oauth.PublicUrl, StringComparison.OrdinalIgnoreCase) ? Markup.Empty : M.H($"That is the server's public address, not the one your browser used for this page; give the assistant that one."))}
        {(oauth.DynamicRegistration ? Markup.Empty : M.H($"Your administrator registers each assistant that connects this way, and gives you the client id to enter in it."))}
        An assistant that can only identify itself by a web address cannot connect here on its own; your administrator can register it.</p>
        <p class="note">Each one below asked to search as you, and you approved it. Revoking one ends it, and its next call reaches nothing.</p>
        {Layout.Table(["Assistant", "State", "Approved", "Ends", "Refreshed", ""], grants.Select(g => Layout.Row(
            OAuthPages.ClientCell(g.ClientName, g.ClientRegistration, g.ClientId),
            OAuthPages.GrantState(g),
            Layout.When(g.CreatedAt),
            Layout.When(g.ExpiresAt),
            OAuthPages.Refreshed(g),
            OAuthPages.Revocable(g.Status)
                ? Layout.OwnForm(r, GrantRevokePath, Layout.Hidden("grant", g.GrantId), "Revoke", danger: true)
                : Markup.Empty)), "flow")}
        """);

    /// <summary>Where one of the person's own agents stands, in words. Only a live one holds a slot.</summary>
    internal static string StateWords(OwnAgentState state) => state switch
    {
        OwnAgentState.Live => "live",
        OwnAgentState.Revoked => "revoked",
        OwnAgentState.Ended => "ended by a disable or a password change",
        OwnAgentState.Expired => "its token expired or was revoked",
        _ => state.ToString(),
    };

    /// <summary>
    /// The person's own agents as this process runs the authorization flow:
    /// while it is on, the bound also counts the person's grants; while it is
    /// off, no table of the flow is read.
    /// </summary>
    private static SelfServeAgents Store(PortalRequest r) =>
        new(r.Db, r.Tenant, r.Http.RequestServices.GetService<OAuthDeployment>(), r.Clock);

    /// <summary>
    /// Makes the agent and shows its token in this response only, beside the
    /// configuration for the assistant the person named.
    /// </summary>
    public static async Task<IResult> Create(HttpContext http)
    {
        var r = PortalRequest.Of(http);
        var form = await r.FormAsync();
        var name = form["name"].ToString().Trim();
        var kind = form["kind"].ToString();
        var model = form["model"].ToString();
        var vendor = form["vendor"].ToString().Trim();

        if (await SelfServiceMaxAsync(r) == 0)
            return Layout.After(Path, error: "An administrator turned this off: people on this deployment cannot connect an assistant themselves.");
        if (name.Length == 0) return Layout.After(Path, error: "Give the assistant a name.");
        if (ConnectSnippets.Kinds.All(k => k.Kind != kind)) return Layout.After(Path, error: "Choose the kind of assistant you use.");
        if (!ModelLocations.TryParse(model, out var location))
            return Layout.After(Path, error: "Say whether its model runs inside your network or outside it.");
        if (location == ModelLocation.Hosted && vendor.Length == 0)
            return Layout.After(Path, error: "Name who runs the hosted model, such as the company whose assistant it is.");
        if (location == ModelLocation.Local && vendor.Length > 0)
            return Layout.After(Path, error: "A local model has no vendor. Name a vendor only for a hosted model.");

        CreatedAgent created;
        try
        {
            created = await Store(r).CreateAsync(
                r.Person.User.Id,
                new SelfServeAgentRequest(name, ModelLocations.Text(location), vendor.Length == 0 ? null : vendor, kind),
                r.Aborted);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return Layout.After(Path, error: ex.Message);
        }

        return await TokenPageAsync(r, $"'{name}' is connected", kind, created.Token, Markup.Empty);
    }

    /// <summary>
    /// The one page a new token is ever shown on, after a connect and after a
    /// reissue: the configuration for the assistant's kind with the token in
    /// it, the token alone, the administrator's instructions, each with its Copy.
    /// </summary>
    private static async Task<IResult> TokenPageAsync(PortalRequest r, string title, string kind, string token, Markup after)
    {
        var (templates, _) = await ConnectSnippets.InForceAsync(r.Db, r.Tenant, r.Aborted);
        var snippet = ConnectSnippets.Render(templates[kind], Address(r.Http.Request), token);
        var instructions = await InstructionsAsync(r, kind);

        return Layout.Page(r, title, M.H($"""
            <p class="warning">This is the only time the token is shown. PremAgentic keeps only its hash and cannot show it again.
            Copy the configuration into your assistant now, then leave this page.</p>
            {after}
            <h2>The configuration: {ConnectSnippets.Label(kind)}</h2>
            <pre class="token snippet" id="snippet">{snippet}</pre>
            <p><button type="button" data-copy="snippet">Copy</button></p>
            <h2>The token alone</h2>
            <div class="token" id="token">{token}</div>
            <p><button type="button" class="secondary" data-copy="token">Copy the token</button></p>
            {instructions}
            <p>{Layout.Link(Path, "Back to your assistants")}</p>
            """));
    }

    /// <summary>
    /// A row's buttons: a live assistant can be given a new key, revoked or
    /// removed; any other one only removed, since a new key never brings back
    /// one that a revoke, a disable or a password change ended.
    /// </summary>
    private static Markup RowButtons(PortalRequest r, OwnAgent a) =>
        a.State == OwnAgentState.Live
            ? M.H($"""
                {Layout.OwnForm(r, $"{Path}/{a.AgentId}/reissue", Markup.Empty, "Reissue key", secondary: true)}
                {Layout.OwnForm(r, $"{Path}/{a.AgentId}/revoke", Markup.Empty, "Revoke", danger: true)}
                {Layout.OwnForm(r, $"{Path}/{a.AgentId}/remove", Markup.Empty, "Remove", danger: true)}
                """)
            : Layout.OwnForm(r, $"{Path}/{a.AgentId}/remove", Markup.Empty, "Remove", danger: true);

    /// <summary>
    /// One of the person's own assistants, or null, which the pages answer as
    /// the store answers an assistant that is not theirs.
    /// </summary>
    private static async Task<OwnAgent?> OwnAsync(PortalRequest r, Guid id) =>
        (await Store(r).ListAsync(r.Person.User.Id, r.Aborted)).FirstOrDefault(a => a.AgentId == id);

    /// <summary>
    /// Gives one of the person's own assistants a new key in place of its live
    /// one, after a page that says what happens and a second form that says yes.
    /// The new key is shown once, on the page a connect shows it on.
    /// </summary>
    public static async Task<IResult> Reissue(HttpContext http, Guid id)
    {
        var r = PortalRequest.Of(http);
        var confirmed = (await r.FormAsync())["confirm"].ToString() == "yes";
        if (await OwnAsync(r, id) is not { } agent) return Layout.After(Path, error: SelfServeAgents.NotYours);

        if (!confirmed)
            return Layout.Page(r, $"Give '{agent.Name}' a new key?", M.H($"""
                <p class="warning">Its current key stops working at once. You get a new one on the next page, once, to paste into your assistant.</p>
                {Layout.OwnForm(r, $"{Path}/{agent.AgentId}/reissue", Layout.Hidden("confirm", "yes"), "Give it a new key")}
                <p>{Layout.Link(Path, "Keep the current key")}</p>
                """));

        Premagentic.Core.Admin.ReissuedAgentToken reissued;
        try
        {
            reissued = await Store(r).ReissueAsync(r.Person.User.Id, id, r.Aborted);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return Layout.After(Path, error: ex.Message);
        }
        return await TokenPageAsync(r, $"'{agent.Name}' has a new key", agent.AssistantKind, reissued.New.PlainText,
            M.H($"<p class=\"note\">The old key has stopped working: its next call reaches nothing.</p>"));
    }

    /// <summary>
    /// Removes one of the person's own assistants, in any state, after a page
    /// that says what happens and a second form that says yes. It leaves the
    /// list; what it asked stays in the audit under its name.
    /// </summary>
    public static async Task<IResult> Remove(HttpContext http, Guid id)
    {
        var r = PortalRequest.Of(http);
        var confirmed = (await r.FormAsync())["confirm"].ToString() == "yes";
        if (await OwnAsync(r, id) is not { } agent) return Layout.After(Path, error: SelfServeAgents.NotYours);

        if (!confirmed)
            return Layout.Page(r, $"Remove '{agent.Name}'?", M.H($"""
                <p class="warning">{(agent.State == OwnAgentState.Live ? "Its key stops working at once and it leaves your list." : "It leaves your list.")}
                What it asked stays in the audit, under its name, marked removed.</p>
                {Layout.OwnForm(r, $"{Path}/{agent.AgentId}/remove", Layout.Hidden("confirm", "yes"), "Remove it", danger: true)}
                <p>{Layout.Link(Path, "Keep it")}</p>
                """));

        try
        {
            var removed = await Store(r).RemoveAsync(r.Person.User.Id, id, r.Aborted);
            return Layout.After(Path, done: $"'{removed.Name}' removed from your list. Its history stays in the audit.");
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return Layout.After(Path, error: ex.Message);
        }
    }

    /// <summary>
    /// Where the deployment's instructions go, per kind of assistant. An MCP
    /// client that reads the instructions the server sends when it connects is
    /// given them there; a
    /// tool that reads a file wants them pasted into that file.
    /// </summary>
    internal static readonly IReadOnlyDictionary<string, string> InstructionsGoTo = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["claude-desktop"] = "Claude Desktop is sent these when it connects, and uses them if it reads what an MCP server sends at connect. To be sure they are followed, add them to the instructions of the project you use it in.",
        ["chatgpt"] = "ChatGPT cannot reach PremAgentic inside your network directly, so paste these into the instructions of the project you use it in.",
        ["copilot"] = "VS Code is sent these when it connects, and uses them if it reads what an MCP server sends at connect. To be sure Copilot follows them, put them in .github/copilot-instructions.md in your workspace, which VS Code reads on its own, or in AGENTS.md at its root with the chat.useAgentsMdFile setting on.",
        ["coding-tool"] = "Put these in AGENTS.md at the root of your project; for Claude Code, in CLAUDE.md; for Cursor, in the project rules. Your tool also receives them when it connects, if it reads what an MCP server sends at connect.",
        ["local-mcp"] = "Your client receives these when it connects, if it reads what an MCP server sends at connect. If it does not, paste them where it keeps standing instructions.",
        ["other"] = "Your client receives these when it connects, if it reads what an MCP server sends at connect. If it does not, paste them where it keeps standing instructions.",
    };

    /// <summary>
    /// The deployment's instructions for assistants as a paste-in block, with
    /// where they go for this kind, or nothing when an administrator has set
    /// none. The text is an administrator's, shown as written.
    /// </summary>
    private static async Task<Markup> InstructionsAsync(PortalRequest r, string kind)
    {
        if (await new SettingsStore(r.Db, r.Tenant).GetAsync(McpSettings.Instructions, r.Aborted) is not { ValueKind: System.Text.Json.JsonValueKind.String } value
            || value.GetString() is not { Length: > 0 } text)
            return Markup.Empty;

        return M.H($"""
            <h2>Instructions from your administrator</h2>
            <p class="note">{InstructionsGoTo[kind]}</p>
            <pre class="token snippet" id="instructions">{text}</pre>
            <p><button type="button" class="secondary" data-copy="instructions">Copy the instructions</button></p>
            """);
    }

    /// <summary>
    /// Revokes one of the signed-in person's own agents. The store refuses an
    /// agent that is somebody else's, and that refusal is shown as it is.
    /// </summary>
    public static async Task<IResult> Revoke(HttpContext http, Guid id)
    {
        var r = PortalRequest.Of(http);
        try
        {
            await Store(r).RevokeAsync(r.Person.User.Id, id, r.Aborted);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return Layout.After(Path, error: ex.Message);
        }
        return Layout.After(Path, done: "Revoked. Its next call reaches nothing.");
    }

    /// <summary>
    /// Revokes one of the signed-in person's own grants. The flow answers a
    /// grant that is somebody else's as it answers one that does not exist, and
    /// that refusal is shown as it is.
    /// </summary>
    public static async Task<IResult> RevokeGrant(HttpContext http)
    {
        var r = PortalRequest.Of(http);
        var id = (await r.FormAsync())["grant"].ToString();
        try
        {
            await http.RequestServices.GetRequiredService<IOAuthGrants>().RevokeOwnAsync(r.Person.User.Id, id, r.Aborted);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return Layout.After(Path, error: ex.Message);
        }
        return Layout.After(Path, done: "Revoked. Its assistant's next call reaches nothing.");
    }

    /// <summary>
    /// How many live agents one person may connect, read by the rule the store
    /// bounds creation with, so the form and the store cannot disagree.
    /// </summary>
    internal static Task<int> SelfServiceMaxAsync(PortalRequest r) =>
        AgentSettings.SelfServiceMaxAsync(new SettingsStore(r.Db, r.Tenant), r.Aborted);

    /// <summary>
    /// The address an assistant reaches this server at, as the person's
    /// browser reached it: the portal and the MCP endpoint are served by the
    /// same process.
    /// </summary>
    internal static string Address(HttpRequest request) =>
        string.Create(CultureInfo.InvariantCulture, $"{request.Scheme}://{request.Host}");
}

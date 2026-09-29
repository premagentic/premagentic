using System.Text;
using Premagentic.Core.Identity;
using Premagentic.Portal.Html;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Primitives;

namespace Premagentic.Portal.Pages;

/// <summary>
/// The MCP authorization flow's pages: the consent page a person sees when an
/// assistant asks to search as them, and the administrator's grants and
/// clients. They are mapped only while the flow is on; while it is off every
/// one of their paths answers as a path that was never mapped.
/// <para>
/// The consent page never sends the browser anywhere by itself. Approve and
/// Deny answer with a page holding one link to the assistant's address, which
/// the portal's script follows, because the policy's <c>form-action 'self'</c>
/// stops a redirect after a post. An error that has an address to return to
/// shows it as a link the person clicks, never followed on its own.
/// </para>
/// <para>
/// Everything a client said about itself (its name, the address its answers
/// go to) is shown inside its own <c>&lt;bdi&gt;</c>, apart from the server's
/// own words, so text in a right-to-left script or with direction marks cannot
/// rearrange the sentence around it.
/// </para>
/// </summary>
internal static class OAuthPages
{
    private const string Title = "An assistant asks to connect";

    /// <summary>The prefix the authorization request's parameters carry in the consent forms, apart from the form's own fields.</summary>
    internal const string ParameterPrefix = "oauth.";

    private static readonly (string, string)[] Models =
        [(ModelLocations.Hosted, "a hosted model, outside your network"), (ModelLocations.Local, "a local model, inside your network")];

    private static readonly (string, string)[] ClientModels =
        [("", "each person says"), (ModelLocations.Hosted, "a hosted model, outside your network"), (ModelLocations.Local, "a local model, inside your network")];

    // --- The consent page ---

    public static async Task<IResult> Consent(HttpContext http)
    {
        var r = PortalRequest.Of(http);
        return await ShowAsync(r, FromQuery(r.Http.Request.Query), error: null);
    }

    /// <summary>
    /// Approve or Deny, from one of the page's two forms. Each carries the
    /// request's parameters, which are checked again, and its own decision.
    /// </summary>
    public static async Task<IResult> Answer(HttpContext http)
    {
        var r = PortalRequest.Of(http);
        var consent = r.Http.RequestServices.GetRequiredService<IOAuthConsent>();
        var form = await r.FormAsync();
        var parameters = FromForm(form);
        var user = r.Person.User.Id;
        string target;
        bool approved;
        try
        {
            switch (form["decision"].ToString())
            {
                case "approve":
                    if (!ModelLocations.TryParse(form["model"].ToString(), out var location))
                        return await ShowAsync(r, parameters, "Say where the assistant's model runs.");
                    var vendor = form["vendor"].ToString().Trim();
                    target = await consent.ApproveAsync(user, parameters, location, vendor.Length == 0 ? null : vendor, r.Aborted);
                    approved = true;
                    break;
                case "deny":
                    target = await consent.DenyAsync(user, parameters, r.Aborted);
                    approved = false;
                    break;
                default:
                    return await ShowAsync(r, parameters, "Choose Approve or Deny.");
            }
        }
        catch (ArgumentException ex)
        {
            // A choice the request does not allow: nothing was written, and the
            // same question is asked again with the reason.
            return await ShowAsync(r, parameters, ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            // The request no longer stands (already answered, the client gone,
            // the limit reached): nothing was written, and there is nowhere to go.
            return Layout.Page(r, Title, M.H($"<p class=\"error\">{ex.Message}</p>"), StatusCodes.Status409Conflict);
        }

        if (!Openable(target))
            return Layout.Page(r, Title, M.H($"<p class=\"error\">The address to continue to is not one a browser can open.</p>"), StatusCodes.Status500InternalServerError);
        var origin = OriginOf(target);
        return Layout.Page(r, approved ? "Approved" : "Denied", M.H($"""
            <p>{(approved
                ? "You approved it. The assistant finishes connecting when it receives the answer."
                : "You denied it. The assistant is told, and nothing was connected.")}</p>
            <p class="continue"><a href="{target}" data-continue="{target}">Continue to <bdi>{origin}</bdi></a></p>
            """));
    }

    private static async Task<IResult> ShowAsync(PortalRequest r, IReadOnlyDictionary<string, IReadOnlyList<string>> parameters, string? error)
    {
        var consent = r.Http.RequestServices.GetRequiredService<IOAuthConsent>();
        var check = await consent.CheckAsync(parameters, r.Aborted);
        switch (check.Outcome)
        {
            case AuthorizationCheckOutcome.Valid when check.Request is { } request:
                var view = await consent.ViewAsync(r.Person.User.Id, request, r.Aborted);
                return Layout.Page(r, Title, ConsentForm(r, view, parameters, error),
                    error is null ? StatusCodes.Status200OK : StatusCodes.Status400BadRequest);

            case AuthorizationCheckOutcome.ReturnError when check.ReturnUrl is { } back && Openable(back):
                return Layout.Page(r, Title, M.H($"""
                    <p class="error">{check.Message ?? "This request cannot be answered."}</p>
                    <p><a href="{back}">Return to <bdi>{OriginOf(back)}</bdi></a></p>
                    """), StatusCodes.Status400BadRequest);

            default:
                // No link, and nothing sent anywhere: the client or its address
                // is not one to trust with an answer.
                return Layout.Page(r, Title, M.H($"<p class=\"error\">{check.Message ?? "This request cannot be answered."}</p>"),
                    StatusCodes.Status400BadRequest);
        }
    }

    private static Markup ConsentForm(PortalRequest r, ConsentView view, IReadOnlyDictionary<string, IReadOnlyList<string>> parameters, string? error)
    {
        var client = view.Request.Client;
        var carried = Carry(parameters);
        var token = r.Person.AntiForgeryToken ?? "";

        var where = view.LoopbackRedirect
            ? M.H($"<p>The answer goes to whatever program is listening at <bdi>{view.RedirectOrigin}</bdi> on this computer. On a shared computer that can be another person's program. Approve only if you started this from <bdi>{client.Name}</bdi> just now.</p>")
            : M.H($"<p>The answer goes to <bdi>{view.RedirectOrigin}</bdi>, a website; that company's servers make the assistant's calls.</p>");

        var kept = view.KeptAgentModelLocation is { } keptLocation
            ? M.H($"<p class=\"note\">Your assistant {view.AgentName} has {ModelWords(keptLocation, view.KeptAgentModelVendor)}.{(view.ReplacesGrant ? M.H($" Approving keeps it. A different answer to where its model runs makes a new assistant and ends the old one.") : Markup.Empty)}</p>")
            : Markup.Empty;

        var bound = view.ConnectedAssistants >= view.Bound && !view.ReplacesGrant
            ? M.H($"<p class=\"note\">You have {view.ConnectedAssistants} of {view.Bound} connected assistants. Approving needs room for one more; {Layout.Link("/portal/connect", "revoke one on the connect page")} first.</p>")
            : Markup.Empty;

        Markup question;
        if (client.StatedModelLocation is { } stated)
            question = M.H($"""
                {Layout.Hidden("model", ModelLocations.Text(stated))}{Layout.Hidden("vendor", client.StatedModelVendor ?? "")}
                <p>Where its model runs: {ModelWords(stated, client.StatedModelVendor)}, as an administrator registered it.</p>
                """);
        else if (!view.LocalAllowed)
            question = M.H($"""
                {Layout.Hidden("model", ModelLocations.Hosted)}
                <p class="note">This assistant receives answers at <bdi>{view.RedirectOrigin}</bdi>, outside this computer, so what it is served leaves the network. An administrator can register it if it runs inside your network.</p>
                {Layout.Field("Model vendor, for a hosted model", "vendor", view.KeptAgentModelVendor ?? "")}
                """);
        else
            question = M.H($"""
                {Layout.Select("Where its model runs", "model", Models, ModelLocations.Hosted)}
                {Layout.Field("Model vendor, for a hosted model", "vendor", view.KeptAgentModelVendor ?? "")}
                """);

        return M.H($"""
            {(error is null ? Markup.Empty : M.H($"<p class=\"error\">{error}</p>"))}
            <section class="consent">
            <p class="lead">An assistant asks to search as you: <bdi class="client">{client.Name}</bdi></p>
            <p class="note">{(client.RegisteredBy == OAuthClientRegistration.Administrator ? "It was registered by an administrator." : "It registered itself.")} Client id <code>{client.Id}</code></p>
            <p class="label">The answer goes to</p>
            <p class="origin"><bdi>{view.RedirectOrigin}</bdi></p>
            {where}
            <dl class="facts">
            <dt>What it can do</dt><dd>Search and fetch documents you can read, read-only. It can never change anything.</dd>
            <dt>Assistant</dt><dd>{view.AgentName}</dd>
            <dt>Connected assistants</dt><dd>{view.ConnectedAssistants} of {view.Bound}</dd>
            </dl>
            {kept}{bound}
            <div class="consent-actions">
            <form method="post" action="{OAuthPaths.Consent}" class="inline" data-consent>
            <input type="hidden" name="{Layout.AntiForgeryField}" value="{token}">
            <input type="hidden" name="decision" value="approve">
            {carried}{question}
            <button type="submit">Approve</button>
            </form>
            <form method="post" action="{OAuthPaths.Consent}" class="inline" data-consent>
            <input type="hidden" name="{Layout.AntiForgeryField}" value="{token}">
            <input type="hidden" name="decision" value="deny">
            {carried}
            <button type="submit" class="secondary">Deny</button>
            </form>
            </div>
            </section>
            """);
    }

    // --- Grants, for auditors and administrators ---

    public static async Task<IResult> Grants(HttpContext http)
    {
        var r = PortalRequest.Of(http);
        var all = r.Query("show") == "all";
        var grants = await r.Http.RequestServices.GetRequiredService<IOAuthGrants>()
            .ListAllAsync(all ? OAuthGrantFilter.All : OAuthGrantFilter.Live, r.Aborted);

        var rows = grants.Select(g => Layout.Row(
            M.H($"<code>{g.GrantId}</code>"),
            g.UserName,
            Layout.Link("/portal/agents/" + M.Segment(g.AgentName), g.AgentName),
            ClientCell(g.ClientName, g.ClientRegistration, g.ClientId),
            GrantState(g),
            Layout.When(g.CreatedAt),
            Layout.When(g.ExpiresAt),
            Refreshed(g),
            Revocable(g.Status)
                ? Layout.Form(r, OAuthPaths.Grants + "/revoke", Layout.Hidden("grant", g.GrantId), "Revoke", danger: true)
                : Markup.Empty));

        return Layout.Page(r, "Grants", M.H($"""
            <p class="note">What each person approved when an assistant asked to search as them. Revoking a grant ends it, disables its
            assistant, and makes its tokens reach nothing from their next call.</p>
            <p class="note">Show: {(all ? Layout.Link(OAuthPaths.Grants, "live grants") : M.H($"<strong>live grants</strong>"))}
            · {(all ? M.H($"<strong>every grant</strong>") : Layout.Link(M.Url(OAuthPaths.Grants, ("show", "all")), "every grant"))}</p>
            {Layout.Table(["Grant", "Person", "Assistant", "Client", "State", "Approved", "Ends", "Refreshed", ""], rows, "flow")}
            """));
    }

    public static async Task<IResult> RevokeGrant(HttpContext http)
    {
        var r = PortalRequest.Of(http);
        var id = (await r.FormAsync())["grant"].ToString();
        try
        {
            await r.Http.RequestServices.GetRequiredService<IOAuthGrants>().RevokeAsync(r.Actor, id, r.Aborted);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return Layout.After(OAuthPaths.Grants, error: ex.Message);
        }
        return Layout.After(OAuthPaths.Grants, done: $"Grant {id} revoked. Its assistant is disabled, and its tokens reach nothing from their next call.");
    }

    // --- Clients, for auditors and administrators ---

    public static async Task<IResult> Clients(HttpContext http)
    {
        var r = PortalRequest.Of(http);
        var clients = await r.Http.RequestServices.GetRequiredService<IOAuthClients>().ListAsync(r.Aborted);
        var users = (await r.Identity().ListUsersAsync(r.Aborted)).ToDictionary(u => u.Id);

        var rows = clients.Select(c => Layout.Row(
            M.H($"<bdi class=\"client\">{c.Client.Name}</bdi>"),
            M.H($"<code>{c.Client.Id}</code>{StoredDocument(c.Document)}"),
            c.Client.RegisteredBy == OAuthClientRegistration.Administrator
                ? $"registered by an administrator, {RegisteredBy(c.Client.RegisteredByActor, users)}"
                : c.RegisteredFrom is { Length: > 0 } from ? $"registered itself, from {from}" : "registered itself",
            M.Each(c.Client.RedirectUris, uri => M.H($"<bdi>{uri}</bdi><br>")),
            c.Client.StatedModelLocation is { } stated ? ModelWords(stated, c.Client.StatedModelVendor) : "each person says",
            Layout.When(c.Client.CreatedAt),
            c.FirstApprovedAt is { } first ? Layout.When(first) : M.H($"never"),
            c.LiveGrants,
            c.Client.Disabled ? "disabled" : "enabled",
            ClientActions(r, c)));

        var add = Layout.Form(r, OAuthPaths.Clients, M.H($"""
            {Layout.Field("Name", "name", required: true)}
            {Layout.Field("Client id: an https address the assistant uses as its id, or blank for one made here", "id")}
            <label>Redirect addresses, one a line <textarea name="redirectUris" rows="3" required></textarea></label>
            {Layout.Select("Where its model runs", "model", ClientModels, "")}
            {Layout.Field("Model vendor, for a hosted model", "vendor")}
            """), "Register client");

        var fromDocument = Layout.UploadForm(r, OAuthPaths.Clients + "/document", M.H($"""
            {Layout.Field("Client id: the https address the assistant names itself by", "id", required: true)}
            {DocumentFields()}
            {Layout.Select("Where its model runs", "model", ClientModels, "")}
            {Layout.Field("Model vendor, for a hosted model", "vendor")}
            """), "Register from the document");

        return Layout.Page(r, "Clients", M.H($"""
            <p class="note">The assistants registered to ask people for access. Disabling or removing one ends every grant it has and
            disables each grant's assistant; enabling it again brings none back. A client that registered itself is one
            registration, not the software: its name is what it says it is.</p>
            {Layout.Table(["Client", "Id", "Registered", "Redirect addresses", "Its model", "Registered at", "First approved", "Live grants", "State", ""], rows, "flow")}
            {(r.IsAdministrator ? M.H($"""
                <h2>Register a client</h2>{add}
                <h2>Register from a metadata document</h2>
                <p class="note">For an assistant that names itself by an https address: save the metadata document its vendor
                publishes, then paste it or choose the file. Its name and redirect addresses are taken from it; only
                its hash and when it was stored are kept; nothing is ever fetched from the address. A browser sends pasted text with
                its line breaks as CR LF, so a pasted document's hash can differ from the published file's; to compare the hash with
                the file later, choose the file.</p>
                {fromDocument}
                """) : Markup.Empty)}
            """));
    }

    /// <summary>
    /// Registers an assistant from its metadata document, pasted or as a file,
    /// through the flow's own check: the flow decides, and a refusal is shown in
    /// the flow's own sentence.
    /// </summary>
    public static async Task<IResult> AddClientFromDocument(HttpContext http)
    {
        var r = PortalRequest.Of(http);
        var form = await r.FormAsync();
        ModelLocation? location = null;
        if (form["model"].ToString() is { Length: > 0 } model)
        {
            if (!ModelLocations.TryParse(model, out var parsed))
                return Layout.After(OAuthPaths.Clients, error: "Where its model runs is hosted, local, or left for each person to say.");
            location = parsed;
        }
        var vendor = form["vendor"].ToString().Trim();
        try
        {
            var document = await r.Http.RequestServices.GetRequiredService<IOAuthClients>().AddFromDocumentAsync(
                r.Actor, form["id"].ToString().Trim(), DocumentBytes(form), location, vendor.Length == 0 ? null : vendor, r.Aborted);
            return Layout.After(OAuthPaths.Clients, done:
                $"Client '{document.Name}' registered from its metadata document, SHA-256 {document.Sha256}.{DocumentDetail(document)} " +
                $"Nothing was fetched from its address. The assistant is set up with this client id: {document.ClientId}");
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return Layout.After(OAuthPaths.Clients, error: ex.Message);
        }
    }

    /// <summary>
    /// A new copy of a client's stored metadata document, for the same address,
    /// through the flow's own check; a refusal is shown in the flow's own sentence.
    /// </summary>
    public static async Task<IResult> ReplaceClientDocument(HttpContext http)
    {
        var r = PortalRequest.Of(http);
        var form = await r.FormAsync();
        try
        {
            var replaced = await r.Http.RequestServices.GetRequiredService<IOAuthClients>().ReplaceDocumentAsync(
                r.Actor, form["client"].ToString(), DocumentBytes(form), r.Aborted);
            var document = replaced.Document;
            return Layout.After(OAuthPaths.Clients, done: replaced.Changed
                ? $"Client {document.ClientId}'s metadata document replaced: SHA-256 {replaced.PreviousSha256} is now {document.Sha256}. " +
                  $"Its name is '{document.Name}'.{DocumentDetail(document)} Every grant stands. A code is exchanged only for a " +
                  "redirect address the new document lists. Nothing was fetched."
                : $"The document is the one client {document.ClientId} is stored with already (SHA-256 {document.Sha256}); nothing changed.");
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return Layout.After(OAuthPaths.Clients, error: ex.Message);
        }
    }

    /// <summary>
    /// The document's bytes: the file chosen, read never more than one byte past
    /// the flow's bound, or the text pasted, as the browser sent it. One of the
    /// two, never both; the flow checks the bytes, the bound among them.
    /// </summary>
    /// <exception cref="ArgumentException">Both were given, or neither.</exception>
    internal static byte[] DocumentBytes(IFormCollection form)
    {
        var file = form.Files.GetFile("documentFile");
        var pasted = form["document"].ToString();
        var hasFile = file is { Length: > 0 };
        var hasPasted = pasted.Trim().Length > 0;
        if (hasFile && hasPasted) throw new ArgumentException("Paste the document or choose its file, not both.");
        if (!hasFile && !hasPasted) throw new ArgumentException("Paste the metadata document or choose its file.");
        if (!hasFile) return Encoding.UTF8.GetBytes(pasted);
        using var stream = file!.OpenReadStream();
        return OAuthClientDocument.ReadBytes(stream);
    }

    /// <summary>What the flow took and left out of a document, in the command line's words.</summary>
    private static string DocumentDetail(OAuthClientDocument document) =>
        $" Its redirect addresses: {string.Join(' ', document.RedirectUris)}." +
        (document.DroppedRedirects.Count > 0 ? $" Dropped, a private scheme: {string.Join(' ', document.DroppedRedirects)}." : "") +
        (document.LeftOut.Count > 0 ? $" Left out, and never stored: {string.Join(", ", document.LeftOut)}." : "");

    private static Markup DocumentFields() => M.H($"""
        <label>The document, pasted, at most {OAuthClientDocument.MaxBytes / 1024} KB <textarea name="document" rows="6" maxlength="{OAuthClientDocument.MaxBytes}"></textarea></label>
        <label>Or its file, at most {OAuthClientDocument.MaxBytes / 1024} KB <input type="file" name="documentFile" accept="application/json,.json"></label>
        """);

    public static async Task<IResult> AddClient(HttpContext http)
    {
        var r = PortalRequest.Of(http);
        var form = await r.FormAsync();
        var id = form["id"].ToString().Trim();
        var redirectUris = form["redirectUris"].ToString()
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        ModelLocation? location = null;
        if (form["model"].ToString() is { Length: > 0 } model)
        {
            if (!ModelLocations.TryParse(model, out var parsed))
                return Layout.After(OAuthPaths.Clients, error: "Where its model runs is hosted, local, or left for each person to say.");
            location = parsed;
        }
        var vendor = form["vendor"].ToString().Trim();
        try
        {
            var made = await r.Http.RequestServices.GetRequiredService<IOAuthClients>().AddAsync(
                r.Actor, id.Length == 0 ? null : id, form["name"].ToString(), redirectUris, location, vendor.Length == 0 ? null : vendor, r.Aborted);
            return Layout.After(OAuthPaths.Clients, done: $"Client registered, with the id {made}.");
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return Layout.After(OAuthPaths.Clients, error: ex.Message);
        }
    }

    public static Task<IResult> DisableClient(HttpContext http) =>
        ChangeClientAsync(http, (clients, actor, id, ct) => clients.DisableAsync(actor, id, ct),
            id => $"Client {id} disabled. Every grant it had is ended, and each of their assistants disabled.");

    public static Task<IResult> EnableClient(HttpContext http) =>
        ChangeClientAsync(http, (clients, actor, id, ct) => clients.EnableAsync(actor, id, ct),
            id => $"Client {id} enabled. No grant it had comes back; people approve it again.");

    public static Task<IResult> RemoveClient(HttpContext http) =>
        ChangeClientAsync(http, (clients, actor, id, ct) => clients.RemoveAsync(actor, id, ct),
            id => $"Client {id} removed. Every grant it had is ended, and each of their assistants disabled.");

    private static async Task<IResult> ChangeClientAsync(HttpContext http,
        Func<IOAuthClients, Core.Admin.AdminActor, string, CancellationToken, Task> change, Func<string, string> done)
    {
        var r = PortalRequest.Of(http);
        var id = (await r.FormAsync())["client"].ToString();
        try
        {
            await change(r.Http.RequestServices.GetRequiredService<IOAuthClients>(), r.Actor, id, r.Aborted);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return Layout.After(OAuthPaths.Clients, error: ex.Message);
        }
        return Layout.After(OAuthPaths.Clients, done: done(id));
    }

    private static Markup ClientActions(PortalRequest r, OAuthClientListing listing)
    {
        var client = listing.Client;
        // Only a client stored from a metadata document has one to replace.
        var replace = listing.Document is null || !r.IsAdministrator ? Markup.Empty : M.H($"""
            <details><summary>Replace its document</summary>
            {Layout.UploadForm(r, OAuthPaths.Clients + "/replace", M.H($"{Layout.Hidden("client", client.Id)}{DocumentFields()}"), "Replace the document")}
            </details>
            """);
        return M.H($"""
            {Layout.Form(r, OAuthPaths.Clients + (client.Disabled ? "/enable" : "/disable"), Layout.Hidden("client", client.Id), client.Disabled ? "Enable" : "Disable", danger: !client.Disabled)}
            {Layout.Form(r, OAuthPaths.Clients + "/remove", Layout.Hidden("client", client.Id), "Remove", danger: true)}
            {replace}
            """);
    }

    // --- Words and pieces ---

    /// <summary>A grant's state in words, the same on every page that lists grants.</summary>
    internal static string StatusWords(OAuthGrantStatus status) => status switch
    {
        OAuthGrantStatus.Live => "live",
        OAuthGrantStatus.Pending => "waiting for its assistant",
        OAuthGrantStatus.Expired => "expired",
        OAuthGrantStatus.Revoked => "revoked",
        OAuthGrantStatus.EndedByDisable => "ended by a disable or a password change",
        OAuthGrantStatus.EndedByClient => "ended: its client was disabled or removed",
        OAuthGrantStatus.EndedByAddressChange => "ended: this server's address changed",
        _ => status.ToString(),
    };

    /// <summary>A grant's state in words, with the reason it was ended when one was written.</summary>
    internal static Markup GrantState(OAuthGrantView grant) =>
        M.H($"{StatusWords(grant.Status)}{(grant.RevokedReason is { Length: > 0 } reason ? M.H($" ({reason})") : Markup.Empty)}");

    /// <summary>How often a grant's assistant refreshed its access, and when it last did.</summary>
    internal static Markup Refreshed(OAuthGrantView grant) =>
        grant.RefreshCount == 0 ? M.H($"never") : M.H($"{grant.RefreshCount} times, last {Layout.When(grant.LastRefreshedAt)}");

    /// <summary>Whether a grant can still be revoked: it holds its assistant, or waits for it to take its first access.</summary>
    internal static bool Revocable(OAuthGrantStatus status) =>
        status is OAuthGrantStatus.Live or OAuthGrantStatus.Pending;

    /// <summary>A client as a list shows it: its own name in its own <c>&lt;bdi&gt;</c>, then the server's words and the id.</summary>
    internal static Markup ClientCell(string name, OAuthClientRegistration registration, string id) => M.H(
        $"<bdi class=\"client\">{name}</bdi><br><span class=\"note\">{(registration == OAuthClientRegistration.Administrator ? "registered by an administrator" : "registered itself")}</span> <code>{id}</code>");

    /// <summary>
    /// The metadata document a client was stored from, by its hash and when, in the
    /// command line's words; nothing for a client that has none. The document itself
    /// is not kept, so the hash is what an administrator compares with the file.
    /// </summary>
    internal static Markup StoredDocument(OAuthStoredDocument? document) => document is { } d
        ? M.H($"<br><span class=\"note\">from its metadata document, SHA-256 <code>{d.Sha256}</code>, stored {Layout.When(d.StoredAt)}</span>")
        : Markup.Empty;

    private static string ModelWords(ModelLocation location, string? vendor) =>
        location == ModelLocation.Local
            ? "a local model, inside your network"
            : vendor is { Length: > 0 } ? $"a hosted model, outside your network, run by {vendor}" : "a hosted model, outside your network";

    private static string RegisteredBy(string? actor, IReadOnlyDictionary<Guid, UserAccount> users)
    {
        if (actor is null) return "not recorded";
        var colon = actor.IndexOf(':');
        var (surface, who) = colon > 0 ? (actor[..colon], actor[(colon + 1)..]) : (actor, "");
        return surface switch
        {
            "portal" => Guid.TryParse(who, out var id) && users.TryGetValue(id, out var user) ? $"in the portal, by {user.SignInName}" : "in the portal",
            "cli" => $"at the command line, by {who}",
            _ => actor,
        };
    }

    /// <summary>The scheme, host and port of an address, which is what a person needs to recognize where they are going.</summary>
    internal static string OriginOf(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.GetLeftPart(UriPartial.Authority) : url;

    /// <summary>
    /// An address a browser opens as a page: one that begins with
    /// <c>https://</c> or <c>http://</c> exactly, and parses as a whole address.
    /// The link on the page is always the flow's own answer, as it came back;
    /// this is the last look at it before it becomes a link.
    /// </summary>
    internal static bool Openable(string url) =>
        (url.StartsWith("https://", StringComparison.Ordinal) || url.StartsWith("http://", StringComparison.Ordinal))
        && Uri.TryCreate(url, UriKind.Absolute, out _);

    /// <summary>The request's parameters from the query, each value kept, repeats included, so the check can refuse a repeat.</summary>
    internal static IReadOnlyDictionary<string, IReadOnlyList<string>> FromQuery(IQueryCollection query) =>
        query.ToDictionary(p => p.Key, p => Values(p.Value), StringComparer.Ordinal);

    /// <summary>The request's parameters carried back in a consent form, and nothing else the form holds.</summary>
    internal static IReadOnlyDictionary<string, IReadOnlyList<string>> FromForm(IFormCollection form) =>
        form.Where(f => f.Key.StartsWith(ParameterPrefix, StringComparison.Ordinal) && f.Key.Length > ParameterPrefix.Length)
            .ToDictionary(f => f.Key[ParameterPrefix.Length..], f => Values(f.Value), StringComparer.Ordinal);

    private static IReadOnlyList<string> Values(StringValues values) => values.Where(v => v is not null).Select(v => v!).ToArray();

    /// <summary>Every parameter as a hidden field, each value its own, under the prefix that keeps it apart from the form's fields.</summary>
    private static Markup Carry(IReadOnlyDictionary<string, IReadOnlyList<string>> parameters) =>
        M.Each(parameters.OrderBy(p => p.Key, StringComparer.Ordinal),
            p => M.Each(p.Value, value => Layout.Hidden(ParameterPrefix + p.Key, value)));
}

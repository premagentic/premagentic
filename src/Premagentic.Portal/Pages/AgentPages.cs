using Premagentic.Core.Admin;
using Premagentic.Core.Identity;
using Premagentic.Core.Okf;
using Premagentic.Portal.Html;
using Microsoft.AspNetCore.Http;

namespace Premagentic.Portal.Pages;

/// <summary>
/// Agents: register, the mode and the owner, the rate limit and the minimum
/// trust tier, the groups a service agent holds, and tokens. A token is shown
/// once, in the response to the request that issued it, and nowhere else: not
/// in a redirect, a link, a later page, the change record or a log. Only its
/// hash is kept.
/// </summary>
internal static class AgentPages
{
    private static readonly (string, string)[] Modes = [("acts-for-user", "acts for its owner"), ("service", "service")];

    // Hosted first and chosen by default: it is the answer that keeps a new
    // agent out of a folder marked never-leaves until somebody says otherwise.
    private static readonly (string, string)[] Models =
        [("hosted", "a hosted model, outside your network"), ("local", "a local model, inside your network")];

    private static readonly (string, string)[] Tiers =
        [("", "the deployment's setting"), ("unverified", "unverified"), ("machine-confirmed", "machine-confirmed"), ("human-reviewed", "human-reviewed")];

    public static async Task<IResult> List(HttpContext http)
    {
        var r = PortalRequest.Of(http);
        var identity = r.Identity();
        var users = (await identity.ListUsersAsync(r.Aborted)).ToDictionary(u => u.Id);
        var selfMadeOnly = r.Query("made") == AgentOrigin.SelfKind;
        var removedOnly = !selfMadeOnly && r.Query("view") == "removed";
        var agents = (await identity.ListAgentsAsync(r.Aborted))
            .Where(a => !selfMadeOnly || MadeForThemselves(a.Origin)).ToList();
        // The removed ones, each as it was removed: its name, whose it was, when
        // and by whom. No link and no form: there is nothing left to change.
        var table = removedOnly
            ? Layout.Table(["Agent", "Owner", "Removed", "Removed by"],
                (await identity.ListRemovedAgentsAsync(r.Aborted)).Select(a =>
                    Layout.Row(a.Name, a.OwnerSignInName, Layout.When(a.RemovedAt), RemovedBy(a.RemovedBy, users))))
            : Layout.Table(["Agent", "Mode", "Owner", "State", "Model", "Rate", "Minimum tier", "Made"],
                agents.Select(a => Layout.Row(
                    Layout.Link(Path(a.Name), a.Name), ModeName(a.Mode), users.TryGetValue(a.OwnerUserId, out var o) ? o.SignInName : "(gone)",
                    a.Disabled ? "disabled" : "enabled",
                    a.ModelVendor is { } vendor ? $"{ModelLocations.Text(a.ModelLocation)} ({vendor})" : ModelLocations.Text(a.ModelLocation),
                    $"{a.RequestsPerMinute}/min", a.MinimumTrustTier ?? "the deployment's setting",
                    MadeBy(a.Origin, users))));

        var create = Layout.Form(r, "/portal/agents", M.H($"""
            {Layout.Field("Name", "name", required: true)}
            {Layout.Select("Owner", "owner", users.Values.Select(u => (u.SignInName, u.SignInName)))}
            {Layout.Select("Mode", "mode", Modes, "acts-for-user")}
            {Layout.Select("Where its model runs", "model", Models, "hosted")}
            {Layout.Field("Model vendor, for a hosted model", "vendor")}
            {Layout.Field("Requests per minute", "rate", "60", "number")}
            {Layout.Select("Minimum trust tier", "minTier", Tiers, "")}
            """), "Register agent");

        return Layout.Page(r, "Agents", M.H($"""
            <p class="note">Show: {(selfMadeOnly || removedOnly ? Layout.Link("/portal/agents", "every agent") : M.H($"<strong>every agent</strong>"))}
            · {(selfMadeOnly ? M.H($"<strong>the ones people made for themselves</strong>") : Layout.Link("/portal/agents?made=self", "the ones people made for themselves"))}
            · {(removedOnly ? M.H($"<strong>the removed ones</strong>") : Layout.Link("/portal/agents?view=removed", "the removed ones"))}</p>
            {table}
            {(r.IsAdministrator && !removedOnly ? M.H($"<h2>Register an agent</h2>{create}") : Markup.Empty)}
            """));
    }

    public static async Task<IResult> Show(HttpContext http, string name)
    {
        var r = PortalRequest.Of(http);
        var identity = r.Identity();
        var agent = await identity.FindAgentByNameAsync(name, r.Aborted);
        if (agent is null) return Layout.Refused(r, StatusCodes.Status404NotFound, $"There is no agent named '{name}'.");

        var owner = await identity.FindUserAsync(agent.OwnerUserId, r.Aborted);
        var granted = await identity.GroupsGrantedToAgentAsync(agent.Id, r.Aborted);
        var groups = await identity.ListGroupsAsync(r.Aborted);
        var tokens = await identity.ListTokensAsync(agent.Id, r.Aborted);
        var questions = await new AuditTrail(r.Db, r.Tenant).PageAsync(20, agentId: agent.Id, ct: r.Aborted);
        var now = r.Clock.GetUtcNow();
        var path = Path(agent.Name);
        // A reissue is offered where it would be made: a usable token of an
        // enabled agent whose person is live. Anywhere else it is refused.
        var reissuable = !agent.Disabled && owner is { Disabled: false };

        var tokenRows = tokens.Select(t => Layout.Row(
            M.H($"<code>{t.Id}</code>"),
            TokenState(t, now),
            Layout.When(t.ExpiresAt), t.LastUsedAt is { } used ? Layout.When(used) : "never",
            M.H($"""
                {(reissuable && t.IsUsableAt(now) ? Layout.Form(r, "/portal/tokens/" + M.Segment(t.Id) + "/reissue", Markup.Empty, "Reissue", secondary: true) : Markup.Empty)}
                {(t.RevokedAt is null ? Layout.Form(r, "/portal/tokens/" + M.Segment(t.Id) + "/revoke", Markup.Empty, "Revoke", danger: true) : Markup.Empty)}
                """)));

        // An assistant connected through the authorization flow holds no token
        // of its own to show or issue: its credentials belong to its grant.
        var viaGrant = agent.Origin?.Kind == AgentOrigin.OAuthKind;
        var tokenSection = viaGrant
            ? M.H($"<p class=\"note\">This assistant's credentials belong to its grant, and end when the grant is revoked or ends.{(Layout.FlowOn(r.Http) ? M.H($" {Layout.Link(OAuthPaths.Grants, "See the grants")}.") : Markup.Empty)}</p>")
            : Layout.Table(["Token id", "State", "Expires", "Last used", ""], tokenRows);

        var grantRows = granted.Select(g => Layout.Row(
            Layout.Link("/portal/groups/" + M.Segment(g.Name), g.Name),
            Layout.Form(r, path + "/grants", Layout.Hidden("ungrant", g.Name), "Take back")));

        var forms = !r.IsAdministrator ? Markup.Empty : M.H($"""
            <h2>Change</h2>
            {(viaGrant ? Markup.Empty : Layout.Form(r, path + "/tokens", Layout.Field("Days until it expires", "days", "90", "number"), "Issue a token"))}
            {Layout.Form(r, path + "/limits", M.H($"{Layout.Field("Requests per minute", "rate", agent.RequestsPerMinute.ToString(System.Globalization.CultureInfo.InvariantCulture), "number")}{Layout.Select("Minimum trust tier", "minTier", Tiers, agent.MinimumTrustTier ?? "")}"), "Save limits")}
            {Layout.Form(r, path + "/model", M.H($"{Layout.Select("Where its model runs", "model", Models, ModelLocations.Text(agent.ModelLocation))}{Layout.Field("Model vendor, for a hosted model", "vendor", agent.ModelVendor ?? "")}"), "Save where the model runs")}
            <p class="note">Moving an agent to a hosted model puts it in the reserved hosted-model agents group, and moving it back
            takes it out. A source whose switch is off denies that group, so this changes what this agent can read, at once, on its next call.</p>
            {Layout.Form(r, path + "/enabled", Layout.Hidden("enabled", agent.Disabled ? "on" : "off"), agent.Disabled ? "Enable" : "Disable", danger: !agent.Disabled)}
            {Layout.Form(r, path + "/remove", Markup.Empty, "Remove agent", danger: true)}
            """);

        return Layout.Page(r, agent.Name, M.H($"""
            <dl class="facts">
            <dt>Mode</dt><dd>{ModeName(agent.Mode)}</dd>
            <dt>Owner</dt><dd>{(owner is null ? M.H($"(gone)") : Layout.Link("/portal/users/" + M.Segment(owner.Name), owner.Name))}</dd>
            <dt>State</dt><dd>{(agent.Disabled ? "disabled" : "enabled")}</dd>
            <dt>Where its model runs</dt><dd>{(agent.ModelLocation == ModelLocation.Local ? "a local model, inside your network" : $"a hosted model, outside your network{(agent.ModelVendor is { } vendor ? $", run by {vendor}" : "")}")}</dd>
            <dt>Rate limit</dt><dd>{agent.RequestsPerMinute} requests a minute</dd>
            <dt>Minimum trust tier</dt><dd>{agent.MinimumTrustTier ?? "the deployment's setting"}</dd>
            </dl>
            <h2>Groups it holds</h2>
            {(agent.Mode == AgentMode.Service
                ? M.H($"{Layout.Table(["Group", ""], grantRows)}{(r.IsAdministrator && groups.Count > granted.Count ? Layout.Form(r, path + "/grants", Layout.Select("Grant a group", "grant", groups.Where(g => granted.All(x => x.Id != g.Id)).Select(g => (g.Name, g.Name))), "Grant") : Markup.Empty)}")
                : M.H($"<p class=\"note\">It acts for its owner and holds exactly the owner's groups, as they are at each call.</p>"))}
            <h2>Tokens</h2>
            {tokenSection}
            {forms}
            <h2>Recent questions</h2>
            {Layout.Table(["When", "Kind", "Question", "Passages"], questions.Select(q => Layout.Row(Layout.When(q.At), q.Kind, q.Query, q.Passages.Count)))}
            """));
    }

    public static async Task<IResult> Create(HttpContext http)
    {
        var r = PortalRequest.Of(http);
        var form = await r.FormAsync();
        var name = form["name"].ToString().Trim();
        if (name.Length == 0) return Layout.After("/portal/agents", error: "Give the agent a name.");
        var owner = await r.Identity().FindUserByNameAsync(form["owner"].ToString(), r.Aborted);
        if (owner is null) return Layout.After("/portal/agents", error: "Choose the user responsible for the agent.");
        AgentMode? mode = form["mode"].ToString() switch { "acts-for-user" => AgentMode.ActsForUser, "service" => AgentMode.Service, _ => null };
        if (mode is null) return Layout.After("/portal/agents", error: "Choose acts-for-user or service.");
        if (!TryRate(form["rate"].ToString(), out var rate)) return Layout.After("/portal/agents", error: "Requests per minute is a whole number from 1 to 100000.");
        if (!TryTier(form["minTier"].ToString(), out var tier)) return Layout.After("/portal/agents", error: "Choose a trust tier, or the deployment's setting.");
        if (!ModelLocations.TryParse(form["model"].ToString(), out var model))
            return Layout.After("/portal/agents", error: "Say whether this agent's model runs inside your network or outside it.");
        var vendor = form["vendor"].ToString().Trim();
        if (vendor.Length > 0 && model == ModelLocation.Local)
            return Layout.After("/portal/agents", error: "A local model has no vendor. Name a vendor only for a hosted model.");

        return await r.ChangeAsync(Path(name), async change =>
        {
            var agent = await change.Identity.CreateAgentAsync(
                name, owner.Id, mode.Value, rate, tier, model, vendor.Length == 0 ? null : vendor,
                AgentOrigin.Portal(r.Person.User.Id), r.Aborted);
            change.Record("agent.add", agent.Name, null,
                new
                {
                    owner = owner.Name, mode = ModeKey(agent.Mode), requests_per_minute = rate, minimum_trust_tier = tier,
                    model_location = ModelLocations.Text(agent.ModelLocation), model_vendor = agent.ModelVendor,
                });
            return $"Agent '{agent.Name}' registered, owned by '{owner.Name}'. Issue it a token below.";
        });
    }

    public static async Task<IResult> SetEnabled(HttpContext http, string name)
    {
        var r = PortalRequest.Of(http);
        var enable = (await r.FormAsync())["enabled"].ToString() == "on";
        var agent = await r.Identity().FindAgentByNameAsync(name, r.Aborted);
        if (agent is null) return Layout.After("/portal/agents", error: $"There is no agent named '{name}'.");
        return await r.ChangeAsync(Path(agent.Name), async change =>
        {
            if (!await change.Identity.SetAgentDisabledAsync(agent.Id, !enable, r.Aborted))
                return $"'{agent.Name}' was already {(enable ? "enabled" : "disabled")}.";
            change.Record(enable ? "agent.enable" : "agent.disable", agent.Name, new { disabled = agent.Disabled }, new { disabled = !enable });
            return enable ? $"'{agent.Name}' enabled." : $"'{agent.Name}' disabled: its next call reaches nothing.";
        });
    }

    public static async Task<IResult> SetLimits(HttpContext http, string name)
    {
        var r = PortalRequest.Of(http);
        var form = await r.FormAsync();
        var agent = await r.Identity().FindAgentByNameAsync(name, r.Aborted);
        if (agent is null) return Layout.After("/portal/agents", error: $"There is no agent named '{name}'.");
        if (!TryRate(form["rate"].ToString(), out var rate)) return Layout.After(Path(agent.Name), error: "Requests per minute is a whole number from 1 to 100000.");
        if (!TryTier(form["minTier"].ToString(), out var tier)) return Layout.After(Path(agent.Name), error: "Choose a trust tier, or the deployment's setting.");

        return await r.ChangeAsync(Path(agent.Name), async change =>
        {
            await AgentLimits.SetAsync(change.Identity, agent.Id, rate, tier, r.Aborted);
            change.Record("agent.limits", agent.Name,
                new { requests_per_minute = agent.RequestsPerMinute, minimum_trust_tier = agent.MinimumTrustTier },
                new { requests_per_minute = rate, minimum_trust_tier = tier });
            return $"Limits saved for '{agent.Name}'. They apply to its next call.";
        });
    }

    /// <summary>
    /// Where an agent's model runs. It is not a label: the reserved group's
    /// membership follows it, and a source held back from hosted models denies
    /// that group, so this is a change to what the agent may read.
    /// </summary>
    public static async Task<IResult> SetModel(HttpContext http, string name)
    {
        var r = PortalRequest.Of(http);
        var form = await r.FormAsync();
        var agent = await r.Identity().FindAgentByNameAsync(name, r.Aborted);
        if (agent is null) return Layout.After("/portal/agents", error: $"There is no agent named '{name}'.");
        if (!ModelLocations.TryParse(form["model"].ToString(), out var model))
            return Layout.After(Path(agent.Name), error: "Say whether its model runs locally or is hosted.");

        // A local model with a vendor is refused by the identity store itself,
        // and its message is the one to show: a copy here would be a second
        // statement of the same rule, free to go stale against it.
        var vendor = form["vendor"].ToString().Trim();

        return await r.ChangeAsync(Path(agent.Name), async change =>
        {
            await change.Identity.SetAgentModelAsync(agent.Id, model, vendor.Length == 0 ? null : vendor, r.Aborted);
            change.Record("agent.model", agent.Name,
                new { model_location = ModelLocations.Text(agent.ModelLocation), model_vendor = agent.ModelVendor },
                new { model_location = ModelLocations.Text(model), model_vendor = vendor.Length == 0 ? null : vendor });
            return model == agent.ModelLocation
                ? $"Saved for '{agent.Name}'. Where its model runs did not change."
                : model == ModelLocation.Hosted
                    ? $"'{agent.Name}' now runs a hosted model. It is in the hosted-model agents group, so any source held "
                      + "back from hosted models stops answering it, on its next call."
                    : $"'{agent.Name}' now runs a local model. It has left the hosted-model agents group, so sources held "
                      + "back from hosted models answer it again, on its next call.";
        });
    }

    public static async Task<IResult> Grants(HttpContext http, string name)
    {
        var r = PortalRequest.Of(http);
        var form = await r.FormAsync();
        var agent = await r.Identity().FindAgentByNameAsync(name, r.Aborted);
        if (agent is null) return Layout.After("/portal/agents", error: $"There is no agent named '{name}'.");
        var (groupName, grant) = form["grant"].ToString() is { Length: > 0 } g ? (g, true) : (form["ungrant"].ToString(), false);
        var group = await r.Identity().FindGroupByNameAsync(groupName, r.Aborted);
        if (group is null) return Layout.After(Path(agent.Name), error: $"There is no group named '{groupName}'.");

        return await r.ChangeAsync(Path(agent.Name), async change =>
        {
            var changed = grant
                ? await change.Identity.GrantGroupAsync(agent.Id, group.Id, r.Aborted)
                : await change.Identity.RevokeGroupAsync(agent.Id, group.Id, r.Aborted);
            if (!changed) return grant ? $"'{agent.Name}' already held '{group.Name}'." : $"'{agent.Name}' did not hold '{group.Name}'.";
            change.Record(grant ? "agent.grant" : "agent.ungrant", agent.Name, null, new { group = group.Name });
            return grant ? $"'{agent.Name}' now holds '{group.Name}'." : $"'{agent.Name}' no longer holds '{group.Name}'.";
        });
    }

    /// <summary>
    /// Issues a token and shows it in this response only. The response is not
    /// cached, the token is not in the address or in any later page, and what
    /// the change record keeps is the token's id and expiry.
    /// </summary>
    public static async Task<IResult> IssueToken(HttpContext http, string name)
    {
        var r = PortalRequest.Of(http);
        var daysText = (await r.FormAsync())["days"].ToString();
        var agent = await r.Identity().FindAgentByNameAsync(name, r.Aborted);
        if (agent is null) return Layout.After("/portal/agents", error: $"There is no agent named '{name}'.");
        if (!int.TryParse(daysText, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var days) || days is < 1 or > 3650)
            return Layout.After(Path(agent.Name), error: "Days is a whole number from 1 to 3650.");

        IssuedAgentToken issued;
        try
        {
            issued = await r.Changes().RunAsync(r.Actor, async change =>
            {
                var token = await change.Identity.IssueTokenAsync(agent.Id, TimeSpan.FromDays(days), r.Aborted);
                change.Record("token.issue", agent.Name, null, new { token_id = token.Record.Id, expires_at = token.Record.ExpiresAt });
                return token;
            }, r.Aborted);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return Layout.After(Path(agent.Name), error: ex.Message);
        }

        return Layout.Page(r, $"A token for '{agent.Name}'", M.H($"""
            <p class="warning">This is the only time the token is shown. Premagentic keeps only its hash and cannot show it again.
            Store it where the agent reads it now, then leave this page.</p>
            <div class="token">{issued.PlainText}</div>
            <dl class="facts">
            <dt>Token id</dt><dd><code>{issued.Record.Id}</code></dd>
            <dt>Expires</dt><dd>{Layout.When(issued.Record.ExpiresAt)}</dd>
            </dl>
            <p>{Layout.Link(Path(agent.Name), "Back to the agent")}</p>
            """));
    }

    public static async Task<IResult> RevokeToken(HttpContext http, string id)
    {
        var r = PortalRequest.Of(http);
        var identity = r.Identity();
        var token = await identity.FindTokenAsync(id, r.Aborted);
        var agent = token is null ? null : await identity.FindAgentAsync(token.AgentId, r.Aborted);
        var back = agent is null ? "/portal/agents" : Path(agent.Name);
        if (token is null) return Layout.After(back, error: $"No token has the id {id}.");

        return await r.ChangeAsync(back, async change =>
        {
            if (!await change.Identity.RevokeTokenAsync(id, r.Aborted)) return $"Token {id} was already revoked.";
            change.Record("token.revoke", agent?.Name ?? id, null, new { token_id = id });
            return $"Token {id} revoked. The next call that presents it reaches nothing.";
        });
    }

    /// <summary>
    /// Replaces a live token with a new one in one step, after a page that says
    /// what happens and a second form that says yes. The new token lasts as long
    /// as the one it replaces did, counted from now, and is shown once, as an
    /// issued one is.
    /// </summary>
    public static async Task<IResult> ReissueToken(HttpContext http, string id)
    {
        var r = PortalRequest.Of(http);
        var confirmed = (await r.FormAsync())["confirm"].ToString() == "yes";
        var identity = r.Identity();
        var token = await identity.FindTokenAsync(id, r.Aborted);
        var agent = token is null ? null : await identity.FindAgentAsync(token.AgentId, r.Aborted);
        if (token is null || agent is null) return Layout.After("/portal/agents", error: AgentLifecycle.NoSuchToken);
        var back = Path(agent.Name);

        if (!confirmed)
            return Layout.Page(r, $"Reissue token {id} of '{agent.Name}'?", M.H($"""
                <p class="warning">Token <code>{id}</code> stops working at once, and a new one replaces it. The new one lasts as long as this one did,
                counted from now, and is shown once, on the next page.</p>
                {Layout.Form(r, "/portal/tokens/" + M.Segment(id) + "/reissue", Layout.Hidden("confirm", "yes"), "Reissue")}
                <p>{Layout.Link(back, "Keep it")}</p>
                """));

        ReissuedAgentToken reissued;
        try
        {
            reissued = await r.Changes().RunAsync(r.Actor, change => AgentLifecycle.ReissueAsync(change, id, null, r.Aborted), r.Aborted);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return Layout.After(back, error: ex.Message);
        }

        return Layout.Page(r, $"A new token for '{agent.Name}'", M.H($"""
            <p class="warning">This is the only time the token is shown. Premagentic keeps only its hash and cannot show it again.
            Store it where the agent reads it now, then leave this page.</p>
            <p class="note">Token <code>{reissued.ReplacedTokenId}</code> is revoked: the next call that presents it reaches nothing.</p>
            <div class="token">{reissued.New.PlainText}</div>
            <dl class="facts">
            <dt>Token id</dt><dd><code>{reissued.New.Record.Id}</code></dd>
            <dt>Expires</dt><dd>{Layout.When(reissued.New.Record.ExpiresAt)}</dd>
            </dl>
            <p>{Layout.Link(back, "Back to the agent")}</p>
            """));
    }

    /// <summary>
    /// Removes an agent in any state, after a page that names it and a second
    /// form that says yes: its tokens revoked, gone from every list, and every
    /// row that names it kept, marked removed.
    /// </summary>
    public static async Task<IResult> Remove(HttpContext http, string name)
    {
        var r = PortalRequest.Of(http);
        var confirmed = (await r.FormAsync())["confirm"].ToString() == "yes";
        var agent = await r.Identity().FindAgentByNameAsync(name, r.Aborted);
        if (agent is null) return Layout.After("/portal/agents", error: $"There is no agent named '{name}'.");

        if (!confirmed)
            return Layout.Page(r, $"Remove agent '{agent.Name}'?", M.H($"""
                <p class="warning">Its live tokens are revoked and it leaves the agents list. The audit, the usage and the change record
                keep every row that names it, marked removed. This cannot be undone.</p>
                {Layout.Form(r, Path(agent.Name) + "/remove", Layout.Hidden("confirm", "yes"), $"Remove agent '{agent.Name}'", danger: true)}
                <p>{Layout.Link(Path(agent.Name), "Keep it")}</p>
                """));

        return await r.ChangeAsync("/portal/agents", async change =>
        {
            var removed = await AgentLifecycle.RemoveAsync(change, agent.Id, r.Aborted);
            return $"Agent '{removed.Name}' removed. Its tokens are revoked; its history still names it.";
        });
    }

    private static bool TryRate(string text, out int rate) =>
        int.TryParse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out rate) && rate is >= 1 and <= 100_000;

    /// <summary>Blank means the deployment's setting; anything else must be a tier, stored in the spec's spelling.</summary>
    private static bool TryTier(string text, out string? tier)
    {
        tier = null;
        if (string.IsNullOrWhiteSpace(text)) return true;
        if (!TrustPolicy.TryParseTier(text, out var parsed)) return false;
        tier = TrustPolicy.TierKey(parsed);
        return true;
    }

    /// <summary>
    /// Who made an agent, in words: a person for themself on the connect page,
    /// an administrator in the portal, an account at the command line, a
    /// profile, or not recorded for an agent made before the record existed.
    /// </summary>
    /// <summary>
    /// Who removed an agent, as the change record describes the actor, with a
    /// person's id read as their sign-in name where the person is still known.
    /// </summary>
    internal static string RemovedBy(string actor, IReadOnlyDictionary<Guid, UserAccount> users) =>
        System.Text.RegularExpressions.Regex.Replace(actor, "user ([0-9a-fA-F-]{36})", m =>
            Guid.TryParse(m.Groups[1].Value, out var id) && users.TryGetValue(id, out var u) ? $"user {u.SignInName}" : m.Value);

    internal static string MadeBy(AgentOrigin? origin, IReadOnlyDictionary<Guid, UserAccount> users)
    {
        string Person(string? id) =>
            Guid.TryParse(id, out var guid) && users.TryGetValue(guid, out var user) ? user.SignInName : "a user who no longer signs in";
        return (origin ?? AgentOrigin.Unknown).Kind switch
        {
            AgentOrigin.SelfKind => $"by {Person(origin!.UserId)}, for themself",
            AgentOrigin.PortalKind => $"in the portal, by {Person(origin!.UserId)}",
            AgentOrigin.CliKind => $"at the command line, by {origin!.UserId}",
            AgentOrigin.ProfileKind => $"by the profile {origin!.UserId}",
            AgentOrigin.OAuthKind => $"through the assistant {origin!.UserId}",
            _ => "not recorded",
        };
    }

    /// <summary>
    /// Whether a person made the agent for themself: on the connect page, or by
    /// approving an assistant that asked through the authorization flow.
    /// </summary>
    internal static bool MadeForThemselves(AgentOrigin? origin) =>
        origin?.Kind is AgentOrigin.SelfKind or AgentOrigin.OAuthKind;

    /// <summary>
    /// A token's state in words. A token ended by its owner's or its agent's
    /// disable, or its owner's password change, says so rather than "expired":
    /// re-enabling does not bring it back, and the person needs to know why.
    /// </summary>
    internal static string TokenState(AgentTokenRecord token, DateTimeOffset now) =>
        token.RevokedAt is not null ? "revoked"
        : token.Superseded ? "ended by a disable or a password change"
        : token.IsUsableAt(now) ? "usable"
        : "expired";

    private static string ModeName(AgentMode mode) => mode == AgentMode.ActsForUser ? "acts for its owner" : "service";

    private static string ModeKey(AgentMode mode) => mode == AgentMode.ActsForUser ? "acts-for-user" : "service";

    private static string Path(string name) => "/portal/agents/" + M.Segment(name);
}

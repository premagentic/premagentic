using Premagentic.Core.Identity;
using Premagentic.Core.Security;
using Premagentic.Core.Security.Acl;
using Premagentic.Portal.Html;
using Microsoft.AspNetCore.Http;

namespace Premagentic.Portal.Pages;

/// <summary>
/// Groups: create, rename, members, and remove. Removing a group that a live
/// folder rule names asks first and shows the rules, because a deny entry for a
/// removed group matches nobody, so its former members fall through to what
/// the list allows after it.
/// </summary>
internal static class GroupPages
{
    public static async Task<IResult> List(HttpContext http)
    {
        var r = PortalRequest.Of(http);
        var identity = r.Identity();
        var rows = new List<Markup>();
        foreach (var g in await identity.ListGroupsAsync(r.Aborted))
            rows.Add(Layout.Row(Layout.Link(Path(g.Name), g.Name), (await identity.ListMembersAsync(g.Id, r.Aborted)).Count));

        var create = Layout.Form(r, "/portal/groups", Layout.Field("Name", "name", required: true), "Add group");
        return Layout.Page(r, "Groups", M.H($"""
            {Layout.Table(["Group", "Members"], rows)}
            {(r.IsAdministrator ? M.H($"<h2>Add a group</h2>{create}") : Markup.Empty)}
            """));
    }

    public static async Task<IResult> Show(HttpContext http, string name)
    {
        var r = PortalRequest.Of(http);
        var identity = r.Identity();
        var group = await identity.FindGroupByNameAsync(name, r.Aborted);
        if (group is null) return Layout.Refused(r, StatusCodes.Status404NotFound, $"There is no group named '{name}'.");

        var members = await identity.ListMembersAsync(group.Id, r.Aborted);
        var others = (await identity.ListUsersAsync(r.Aborted)).Where(u => members.All(m => m.Id != u.Id)).ToArray();
        var granted = new List<Agent>();
        foreach (var agent in await identity.ListAgentsAsync(r.Aborted))
            if ((await identity.GroupsGrantedToAgentAsync(agent.Id, r.Aborted)).Any(g => g.Id == group.Id)) granted.Add(agent);
        var rules = await NamingRulesAsync(r, group);
        var path = Path(group.Name);

        var memberRows = members.Select(m => Layout.Row(
            Layout.Link("/portal/users/" + M.Segment(m.Name), m.Name), m.Disabled ? "disabled" : "enabled",
            Layout.Form(r, path + "/members", Layout.Hidden("remove", m.Name), "Remove")));

        var forms = !r.IsAdministrator ? Markup.Empty : M.H($"""
            <h2>Change</h2>
            {(others.Length == 0 ? Markup.Empty : Layout.Form(r, path + "/members", Layout.Select("Add a member", "add", others.Select(u => (u.SignInName, u.SignInName))), "Add"))}
            {Layout.Form(r, path + "/rename", Layout.Field("New name", "newName", required: true), "Rename")}
            {Layout.Form(r, path + "/remove", Markup.Empty, "Remove group", danger: true)}
            """);

        return Layout.Page(r, group.Name, M.H($"""
            <h2>Members</h2>
            {Layout.Table(["Member", "State", ""], memberRows)}
            <h2>Agents granted this group</h2>
            {(granted.Count == 0 ? M.H($"<p class=\"empty\">None.</p>") : M.Each(granted, a => M.H($"{Layout.Link("/portal/agents/" + M.Segment(a.Name), a.Name)} ")))}
            <h2>Folder rules that name it</h2>
            {RulesTable(rules)}
            {forms}
            """));
    }

    public static async Task<IResult> Create(HttpContext http)
    {
        var r = PortalRequest.Of(http);
        var name = (await r.FormAsync())["name"].ToString().Trim();
        if (name.Length == 0) return Layout.After("/portal/groups", error: "Give the group a name.");
        return await r.ChangeAsync(Path(name), async change =>
        {
            var group = await change.Identity.CreateGroupAsync(name, r.Aborted);
            change.Record("group.add", group.Name, null, new { name = group.Name });
            return $"Group '{group.Name}' added.";
        });
    }

    public static async Task<IResult> Members(HttpContext http, string name)
    {
        var r = PortalRequest.Of(http);
        var form = await r.FormAsync();
        var group = await r.Identity().FindGroupByNameAsync(name, r.Aborted);
        if (group is null) return Layout.After("/portal/groups", error: $"There is no group named '{name}'.");
        var (userName, add) = form["add"].ToString() is { Length: > 0 } a ? (a, true) : (form["remove"].ToString(), false);
        var user = await r.Identity().FindUserByNameAsync(userName, r.Aborted);
        if (user is null) return Layout.After(Path(group.Name), error: $"No user signs in as '{userName}'.");

        return await r.ChangeAsync(Path(group.Name), async change =>
        {
            var changed = add
                ? await change.Identity.AddMemberAsync(group.Id, user.Id, r.Aborted)
                : await change.Identity.RemoveMemberAsync(group.Id, user.Id, r.Aborted);
            if (!changed) return add ? $"'{user.Name}' was already a member." : $"'{user.Name}' was not a member.";
            change.Record(add ? "group.member.add" : "group.member.remove", group.Name, null, new { user = user.Name });
            return add ? $"Added '{user.Name}'. It applies to the next search." : $"Removed '{user.Name}'. It applies to the next search.";
        });
    }

    public static async Task<IResult> Rename(HttpContext http, string name)
    {
        var r = PortalRequest.Of(http);
        var newName = (await r.FormAsync())["newName"].ToString().Trim();
        var group = await r.Identity().FindGroupByNameAsync(name, r.Aborted);
        if (group is null) return Layout.After("/portal/groups", error: $"There is no group named '{name}'.");
        if (newName.Length == 0) return Layout.After(Path(group.Name), error: "Give the new name.");

        return await r.ChangeAsync(Path(newName), async change =>
        {
            if (!await change.Identity.RenameGroupAsync(group.Id, newName, r.Aborted))
                throw new InvalidOperationException($"There is no group named '{group.Name}'.");
            change.Record("group.rename", newName, new { name = group.Name }, new { name = newName });
            return $"Group '{group.Name}' renamed to '{newName}'. Rules that name it are unchanged, because they name it by id.";
        });
    }

    /// <summary>
    /// Removes a group, asking first when a live rule names it. The question is
    /// a page with the rules and a second form that says yes.
    /// </summary>
    public static async Task<IResult> Remove(HttpContext http, string name)
    {
        var r = PortalRequest.Of(http);
        var confirmed = (await r.FormAsync())["confirm"].ToString() == "yes";
        var group = await r.Identity().FindGroupByNameAsync(name, r.Aborted);
        if (group is null) return Layout.After("/portal/groups", error: $"There is no group named '{name}'.");

        var rules = await NamingRulesAsync(r, group);
        if (rules.Count > 0 && !confirmed)
            return Layout.Page(r, $"Remove group '{group.Name}'?", M.H($"""
                <p class="warning">{rules.Count} folder rule(s) name this group. A rule entry for a removed group matches nobody,
                so if an entry denies this group, its former members fall through to whatever the list allows after it,
                and may read more than before. A new group with the same name does not inherit these rules.</p>
                {RulesTable(rules)}
                {Layout.Form(r, Path(group.Name) + "/remove", Layout.Hidden("confirm", "yes"), "Remove it anyway", danger: true)}
                <p>{Layout.Link(Path(group.Name), "Keep the group")} and change those rules first.</p>
                """));

        return await r.ChangeAsync("/portal/groups", async change =>
        {
            if (!await change.Identity.DeleteGroupAsync(group.Id, r.Aborted))
                throw new InvalidOperationException($"There is no group named '{group.Name}'.");
            change.Record("group.remove", group.Name, new { name = group.Name, rules_naming_it = rules.Count }, null);
            return $"Group '{group.Name}' removed with its memberships and grants.";
        });
    }

    internal static async Task<IReadOnlyList<(StoredFolderRule Rule, string Description)>> NamingRulesAsync(PortalRequest r, Group group)
    {
        var names = new PrincipalNames(r.Identity());
        var rules = await new AclStore(r.Db, r.Tenant).RulesNamingAsync(Principal.Group(CallerResolver.IdText(group.Id)), r.Aborted);
        var described = new List<(StoredFolderRule, string)>();
        foreach (var rule in rules) described.Add((rule, await names.DescribeAsync(rule.Rule.Acl, r.Aborted)));
        return described;
    }

    private static Markup RulesTable(IReadOnlyList<(StoredFolderRule Rule, string Description)> rules) =>
        Layout.Table(["Source", "Folder", "Who may read, in order"],
            rules.Select(x => Layout.Row(x.Rule.Rule.Source, x.Rule.Rule.PathPrefix.Length == 0 ? "(whole source)" : x.Rule.Rule.PathPrefix, x.Description)));

    private static string Path(string name) => "/portal/groups/" + M.Segment(name);
}

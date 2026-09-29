using Premagentic.Core.Identity;
using Premagentic.Portal.Html;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Premagentic.Portal.Pages;

/// <summary>
/// People: create, disable and enable, a new password entered once and never
/// shown again, and the role. Every change goes to the change record with the
/// signed-in administrator, in the change's own transaction.
/// </summary>
internal static class UserPages
{
    /// <summary>The shortest password the portal accepts.</summary>
    public const int MinimumPasswordLength = 12;

    private static readonly (string, string)[] Roles =
        [("member", "member"), ("auditor", "auditor"), ("administrator", "administrator")];

    public static async Task<IResult> List(HttpContext http)
    {
        var r = PortalRequest.Of(http);
        var users = await r.Identity().ListUsersAsync(r.Aborted);

        var create = Layout.Form(r, "/portal/users", M.H($"""
            {Layout.Field("Sign-in name", "signInName", required: true)}
            {Layout.Field("Display name", "displayName")}
            {Layout.Select("Role", "role", Roles, "member")}
            {Layout.Field("Password (optional, entered once)", "password", type: "password")}
            """), "Add user");

        return Layout.Page(r, "Users", M.H($"""
            {Layout.Table(["Sign-in name", "Display name", "Role", "State", "Password", "Created"],
                users.Select(u => Layout.Row(
                    Layout.Link("/portal/users/" + M.Segment(u.SignInName), u.SignInName), u.DisplayName, Layout.RoleName(u.Role),
                    u.Disabled ? "disabled" : "enabled", u.HasPassword ? "set" : "none", Layout.When(u.CreatedAt))))}
            {(r.IsAdministrator ? M.H($"<h2>Add a user</h2>{create}") : Markup.Empty)}
            """));
    }

    public static async Task<IResult> Show(HttpContext http, string name)
    {
        var r = PortalRequest.Of(http);
        var identity = r.Identity();
        var account = (await identity.ListUsersAsync(r.Aborted)).FirstOrDefault(u => u.SignInName.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (account is null) return Layout.Refused(r, StatusCodes.Status404NotFound, $"No user signs in as '{name}'.");

        var groups = await identity.GroupsOfUserAsync(account.Id, r.Aborted);
        var agents = (await identity.ListAgentsAsync(r.Aborted)).Where(a => a.OwnerUserId == account.Id).ToArray();
        var path = "/portal/users/" + M.Segment(account.SignInName);

        var forms = !r.IsAdministrator ? Markup.Empty : M.H($"""
            <h2>Change</h2>
            {Layout.Form(r, path + "/enabled", Layout.Hidden("enabled", account.Disabled ? "on" : "off"), account.Disabled ? "Enable" : "Disable", danger: !account.Disabled)}
            {Layout.Form(r, path + "/role", Layout.Select("Role", "role", Roles, Layout.RoleName(account.Role)), "Change role")}
            {Layout.Form(r, path + "/password", Layout.Field($"New password (at least {MinimumPasswordLength} characters, never shown again)", "password", type: "password", required: true), "Set password")}
            """);

        return Layout.Page(r, account.SignInName, M.H($"""
            <dl class="facts">
            <dt>Display name</dt><dd>{account.DisplayName}</dd>
            <dt>Role</dt><dd>{Layout.RoleName(account.Role)}</dd>
            <dt>State</dt><dd>{(account.Disabled ? "disabled: signs in to nothing, and every agent it owns reaches nothing" : "enabled")}</dd>
            <dt>Password</dt><dd>{(account.HasPassword ? "set" : "none: cannot sign in with a password")}</dd>
            <dt>Groups</dt><dd>{(groups.Count == 0 ? M.H($"none") : M.Each(groups, g => M.H($"{Layout.Link("/portal/groups/" + M.Segment(g.Name), g.Name)} ")))}</dd>
            <dt>Agents owned</dt><dd>{(agents.Length == 0 ? M.H($"none") : M.Each(agents, a => M.H($"{Layout.Link("/portal/agents/" + M.Segment(a.Name), a.Name)} ")))}</dd>
            </dl>
            {forms}
            """));
    }

    public static async Task<IResult> Create(HttpContext http)
    {
        var r = PortalRequest.Of(http);
        var form = await r.FormAsync();
        var name = form["signInName"].ToString().Trim();
        var display = form["displayName"].ToString().Trim();
        if (display.Length == 0) display = name;
        var password = form["password"].ToString();
        if (name.Length == 0) return Layout.After("/portal/users", error: "Give a sign-in name.");
        if (!TryRole(form["role"].ToString(), out var role)) return Layout.After("/portal/users", error: "Choose member, auditor or administrator.");
        if (password.Length > 0 && password.Length < MinimumPasswordLength)
            return Layout.After("/portal/users", error: $"A password has at least {MinimumPasswordLength} characters.");
        var hash = password.Length > 0 ? Hasher(http).Hash(password) : null;

        return await r.ChangeAsync("/portal/users/" + M.Segment(name), async change =>
        {
            var user = await change.Identity.CreateUserAsync(name, display, role, r.Aborted);
            if (hash is not null) await change.Identity.SetPasswordHashAsync(user.Id, hash, r.Aborted);
            change.Record("user.add", user.Name, null, new { display_name = display, role = Layout.RoleName(role), password_set = hash is not null });
            return $"User '{user.Name}' added as {Layout.RoleName(role)}.";
        });
    }

    public static async Task<IResult> SetEnabled(HttpContext http, string name)
    {
        var r = PortalRequest.Of(http);
        var enable = (await r.FormAsync())["enabled"].ToString() == "on";
        var user = await r.Identity().FindUserByNameAsync(name, r.Aborted);
        if (user is null) return Layout.After("/portal/users", error: $"No user signs in as '{name}'.");
        var path = "/portal/users/" + M.Segment(user.Name);
        if (!enable && user.Id == r.Person.User.Id)
            return Layout.After(path, error: "You cannot disable the account you are signed in with.");

        return await r.ChangeAsync(path, async change =>
        {
            // Compared first, under the change's lock, as the command line does:
            // an account already in this state is not written and not recorded.
            if (await change.Identity.FindUserAsync(user.Id, r.Aborted) is not { } current
                || current.Disabled == !enable
                || !await change.Identity.SetUserDisabledAsync(user.Id, !enable, r.Aborted))
                return $"'{user.Name}' was already {(enable ? "enabled" : "disabled")}. Nothing changed and nothing was recorded.";
            change.Record(enable ? "user.enable" : "user.disable", user.Name, new { disabled = current.Disabled }, new { disabled = !enable });
            return enable
                ? $"'{user.Name}' enabled."
                : $"'{user.Name}' disabled: its sessions ended, and every agent it owns now reaches nothing.";
        });
    }

    public static async Task<IResult> SetPassword(HttpContext http, string name)
    {
        var r = PortalRequest.Of(http);
        var password = (await r.FormAsync())["password"].ToString();
        var user = await r.Identity().FindUserByNameAsync(name, r.Aborted);
        if (user is null) return Layout.After("/portal/users", error: $"No user signs in as '{name}'.");
        var path = "/portal/users/" + M.Segment(user.Name);
        if (password.Length < MinimumPasswordLength)
            return Layout.After(path, error: $"A password has at least {MinimumPasswordLength} characters.");
        var hash = Hasher(http).Hash(password);

        return await r.ChangeAsync(path, async change =>
        {
            await change.Identity.SetPasswordHashAsync(user.Id, hash, r.Aborted);
            // That it changed, never the password or its hash.
            change.Record("user.password", user.Name, null, new { password_set = true });
            return $"Password set for '{user.Name}'. Every session it had has ended.";
        });
    }

    public static async Task<IResult> SetRole(HttpContext http, string name)
    {
        var r = PortalRequest.Of(http);
        var roleText = (await r.FormAsync())["role"].ToString();
        var user = await r.Identity().FindUserByNameAsync(name, r.Aborted);
        if (user is null) return Layout.After("/portal/users", error: $"No user signs in as '{name}'.");
        var path = "/portal/users/" + M.Segment(user.Name);
        if (!TryRole(roleText, out var role)) return Layout.After(path, error: "Choose member, auditor or administrator.");
        if (user.Id == r.Person.User.Id && role != Role.Administrator)
            return Layout.After(path, error: "You cannot take the administrator role from the account you are signed in with.");
        if (role == user.Role) return Layout.After(path, done: $"'{user.Name}' is already {Layout.RoleName(role)}.");

        return await r.ChangeAsync(path, async change =>
        {
            await RoleChange.SetAsync(change.Identity, user.Id, role, r.Aborted);
            change.Record("user.role", user.Name, new { role = Layout.RoleName(user.Role) }, new { role = Layout.RoleName(role) });
            return $"'{user.Name}' is now {Layout.RoleName(role)}.";
        });
    }

    private static PasswordHasher Hasher(HttpContext http) =>
        http.RequestServices.GetService<PasswordHasher>() ?? new PasswordHasher();

    private static bool TryRole(string text, out Role role)
    {
        (var ok, role) = text switch
        {
            "member" => (true, Role.Member),
            "auditor" => (true, Role.Auditor),
            "administrator" => (true, Role.Administrator),
            _ => (false, Role.Member),
        };
        return ok;
    }
}

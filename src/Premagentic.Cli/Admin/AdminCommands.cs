using Premagentic.Core.Admin;
using Premagentic.Core.Identity;
using Premagentic.Core.Security;
using Premagentic.Core.Security.Acl;
using Premagentic.Core.Storage;

namespace Premagentic.Cli.Admin;

/// <summary>
/// The administration verbs: enough to run a deployment without a portal.
/// People, groups and agents are named by name here and stored by id, so a
/// rename never changes what an access rule means.
/// </summary>
internal static class AdminCommands
{
    public static readonly string[] Verbs = ["users", "groups", "agents", "tokens", "rules"];

    /// <summary>The source name <c>prem ingest</c> reads folders as.</summary>
    public const string FileSystemSource = "filesystem";

    public const string UsersUsage = """
        prem users add <sign-in-name> [--display "Name"] [--role administrator|auditor|member]
                       [--password | --password-file <file>]
        prem users disable|enable <sign-in-name>
        prem users set-password <sign-in-name> [--password-file <file>]
        prem users list

          The people who sign in. A disabled user, and every agent the user owns, reaches nothing.

          A password is never an argument: a script names a file, or pipes the password on standard
          input, and a person types it at a prompt that does not echo.

          --display "Name"       add: the name the portal shows (default: the sign-in name)
          --role r               add: administrator, auditor or member (default: member)
          --password             add: read a password from standard input or a prompt; without it or
                                 --password-file the user has no password until set-password gives one
          --password-file file   add, set-password: the password is the only line of this file, which
                                 is only read; standard input is not read and nothing is asked
        """;

    public const string GroupsUsage = """
        prem groups add <name>
        prem groups rename <name> <new-name>
        prem groups remove <name> [--force]
        prem groups members <name> [--add a,b] [--remove c,d]
        prem groups list

          Groups of people, which folder rules name by name and store by id, so a rename leaves every rule
          as it was.

          --force                remove: remove a group that rules name. A deny entry for it then matches
                                 nobody, which can widen who reads those folders
          --add a,b              members: add these users, by sign-in name
          --remove c,d           members: take these users out
        """;

    public const string AgentsUsage = """
        prem agents add <name> --owner <sign-in-name> --mode acts-for-user|service --model local|hosted
                        [--vendor "Name"] [--rate N] [--min-trust tier]
        prem agents set <name> --model local|hosted [--vendor "Name"]
        prem agents disable|enable <name>
        prem agents remove <name>
        prem agents grant|ungrant <agent> <group>
        prem agents list [--removed]

          Agents read with a token and never write. One that acts for its owner reads as the owner and
          never more; a service agent reads as itself and the groups it is granted.

          A removed agent's tokens are revoked, its grant ends, and it is gone from every list. Nothing
          is deleted: the change record, the audit and the usage still name it, and its name is free for
          a new agent.

          --owner name           add: the user responsible for the agent
          --mode m               add: acts-for-user or service
          --model where          add, set: local (the model runs inside your network) or hosted (it runs
                                 on someone else's). There is no default. An agent on a hosted model is
                                 in the reserved group of hosted-model agents, which a folder rule can deny
          --vendor "Name"        add, set: who runs a hosted model
          --rate N               add: requests per minute (default: 60)
          --min-trust tier       add: the lowest trust tier the agent is served (default: the deployment's)
          --removed              list: the removed agents instead, with when and by whom
        """;

    public const string TokensUsage = """
        prem tokens issue <agent> [--days N]
        prem tokens reissue <token-id> [--days N]
        prem tokens reissue --agent <agent> [--days N]
        prem tokens revoke <token-id>
        prem tokens list [<agent>]

          An agent's tokens. A token is printed once, when it is issued, and cannot be recovered; a
          revoked token reaches nothing on its next call.

          A reissue replaces a live token in one change: the new token is printed once and the old one
          is revoked, both or neither. It never brings a key back: a disabled agent or person, and a
          revoked, expired or ended token, are refused.

          --days N               issue: how long the token lasts, 1 to 3650 (default: 90); reissue: the
                                 same (default: the replaced token's own lifetime, counted from now)
          --agent name           reissue: the agent's one live token, when it has exactly one
        """;

    public const string RulesUsage = """
        prem rules set [--source s] [--prefix p] (--public | --principals a,b | --entry "allow group:Staff" ...)
        prem rules remove [--source s] [--prefix p]
        prem rules list

          A folder rule decides who may read the documents under its prefix, and the longest prefix wins.
          Its list is read in order and the first entry that names the caller decides; no match is a deny.

          --source s             the source the rule is for (default: filesystem, which prem ingest reads
                                 folders as)
          --prefix p             the path prefix the rule covers (default: the whole source)
          --public               set: allow everyone
          --principals a,b       set: allow these principals, by name: group:Staff, user:alice, everyone
          --entry "..."          set: one entry of the list, such as "deny group:Contractors"; give one
                                 --entry for each, in order
        """;

    public const string Usage = UsersUsage + "\n\n" + GroupsUsage + "\n\n" + AgentsUsage + "\n\n" + TokensUsage + "\n\n" + RulesUsage;

    /// <summary>What one verb needs: the database, the tenant, and the stores it reads through.</summary>
    private sealed record Context(PremagenticDatabase Db, Guid Tenant, IdentityStore Store, PrincipalNames Names, AclStore Rules);

    /// <summary>One verb, and whether it changes anything.</summary>
    private sealed record Verb(bool Writes, Func<string[], Context, Task<int>> Run);

    /// <summary>
    /// Every verb, and whether it writes. A verb that writes goes through
    /// <see cref="AdminChanges"/>, so the change and its row in the change
    /// record commit together, as they do in the portal; a test runs every
    /// verb marked as writing and fails on one that leaves no row, or that
    /// has no case in the test.
    /// </summary>
    private static readonly Dictionary<(string Noun, string Verb), Verb> Table = new()
    {
        [("users", "add")] = new(true, UsersAddAsync),
        [("users", "disable")] = new(true, (a, c) => UserDisabledAsync(a, c, disabled: true)),
        [("users", "enable")] = new(true, (a, c) => UserDisabledAsync(a, c, disabled: false)),
        [("users", "set-password")] = new(true, SetPasswordAsync),
        [("users", "list")] = new(false, (_, c) => UsersListAsync(c.Store)),
        [("groups", "add")] = new(true, GroupsAddAsync),
        [("groups", "rename")] = new(true, GroupsRenameAsync),
        [("groups", "remove")] = new(true, GroupsRemoveAsync),
        [("groups", "members")] = new(true, GroupsMembersAsync),
        [("groups", "list")] = new(false, (_, c) => GroupsListAsync(c.Store)),
        [("agents", "add")] = new(true, AgentsAddAsync),
        [("agents", "set")] = new(true, AgentsSetAsync),
        [("agents", "disable")] = new(true, (a, c) => AgentDisabledAsync(a, c, disabled: true)),
        [("agents", "enable")] = new(true, (a, c) => AgentDisabledAsync(a, c, disabled: false)),
        [("agents", "remove")] = new(true, AgentsRemoveAsync),
        [("agents", "grant")] = new(true, (a, c) => AgentGrantAsync(a, c, grant: true)),
        [("agents", "ungrant")] = new(true, (a, c) => AgentGrantAsync(a, c, grant: false)),
        [("agents", "list")] = new(false, (a, c) => AgentsListAsync(a, c.Store)),
        [("tokens", "issue")] = new(true, TokensIssueAsync),
        [("tokens", "reissue")] = new(true, TokensReissueAsync),
        [("tokens", "revoke")] = new(true, TokensRevokeAsync),
        [("tokens", "list")] = new(false, (a, c) => TokensListAsync(a, c.Store)),
        [("rules", "set")] = new(true, (a, c) => RulesSetAsync(a, c.Db, c.Tenant)),
        [("rules", "remove")] = new(true, (a, c) => RulesRemoveAsync(a, c.Db, c.Tenant)),
        [("rules", "list")] = new(false, async (_, c) => await RulesListAsync(c.Names, c.Rules, await HostedHolds.ListAsync(c.Db, c.Tenant))),
    };

    /// <summary>Every verb that changes something, as noun and verb.</summary>
    public static IReadOnlyList<(string Noun, string Verb)> WriteVerbs { get; } =
        [.. Table.Where(v => v.Value.Writes).Select(v => v.Key)];

    public static async Task<int> RunAsync(string[] args, PremagenticDatabase db, Guid tenantId)
    {
        var store = new IdentityStore(db, tenantId);
        var context = new Context(db, tenantId, store, new PrincipalNames(store), new AclStore(db, tenantId));
        try
        {
            return Table.TryGetValue((args[0], args.ElementAtOrDefault(1) ?? ""), out var verb)
                ? await verb.Run(args, context)
                : Fail("Usage:\n" + Usage);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return Fail(ex.Message);
        }
    }

    /// <summary>One change made at the command line and its rows in the change record, in one transaction.</summary>
    private static Task<T> Change<T>(Context c, Func<AdminChange, Task<T>> change) =>
        new AdminChanges(c.Db, c.Tenant).RunAsync(AdminActor.Cli(), change);

    /// <summary>
    /// The entries a <c>--public</c>, <c>--principals</c> or <c>--entry</c> flag
    /// asks for, by name; null when none is given. Shared by <c>rules set</c> and
    /// the <c>ingest</c> shorthand.
    /// </summary>
    public static IReadOnlyList<string>? RuleEntries(string[] args)
    {
        var isPublic = args.Contains("--public");
        var principals = CliArgs.List(args, "--principals");
        var entries = CliArgs.Values(args, "--entry");
        if (new[] { isPublic, principals.Length > 0, entries.Count > 0 }.Count(x => x) > 1)
            throw new ArgumentException("Choose one of --public, --principals and --entry.");
        if (isPublic) return ["allow everyone"];
        if (principals.Length > 0) return principals.Select(p => "allow " + p).ToArray();
        return entries.Count > 0 ? entries : null;
    }

    /// <summary>
    /// Sets a folder rule from names and says what it did. The rule and its row
    /// in the change record commit together, as a rule set in the portal does.
    /// </summary>
    public static async Task SetRuleAsync(
        PremagenticDatabase db, Guid tenantId, string source, string prefix, IReadOnlyList<string> entries)
    {
        var (described, moved) = await new AdminChanges(db, tenantId).RunAsync(AdminActor.Cli(), async change =>
        {
            var names = new PrincipalNames(change.Identity);
            var set = await names.ToAclSetAsync(entries);
            var before = await FindRuleAsync(change.Rules, source, prefix);
            var moved = await change.Rules.SetRuleAsync(new FolderRule(source, prefix, set));
            change.Record(RuleSetKind, RuleTarget(source, prefix),
                before is null ? null : new { entries = before.Rule.Acl.CanonicalText }, new { entries = set.CanonicalText });
            return (await names.DescribeAsync(set), moved);
        });
        Console.WriteLine($"Rule {Folder(source, prefix)}: {described}. {moved} indexed document(s) moved to it.");
    }

    /// <summary>The kinds the change record gives a rule set and a rule removed, whichever surface made them.</summary>
    public const string RuleSetKind = "rule.set";

    public const string RuleRemoveKind = "rule.remove";

    private static string RuleTarget(string source, string prefix) => $"{source}:{prefix}";

    private static async Task<StoredFolderRule?> FindRuleAsync(AclStore rules, string source, string prefix) =>
        (await rules.ListRulesAsync()).FirstOrDefault(r => r.Rule.Source == source && r.Rule.PathPrefix == prefix);

    /// <summary>True when a rule at <paramref name="rulePrefix"/> can decide a document under <paramref name="folder"/>.</summary>
    public static bool CanDecideUnder(string rulePrefix, string folder) =>
        rulePrefix.Length == 0 || folder.Length == 0 || rulePrefix == folder
        || rulePrefix.StartsWith(folder + "/", StringComparison.Ordinal)
        || folder.StartsWith(rulePrefix + "/", StringComparison.Ordinal);

    private static async Task<int> UsersAddAsync(string[] args, Context c)
    {
        var name = Single(args, "a sign-in name", "--display", "--role", PasswordFileFlag);
        var role = (CliArgs.Value(args, "--role") ?? "member") switch
        {
            "administrator" => Role.Administrator,
            "auditor" => Role.Auditor,
            "member" => Role.Member,
            var other => throw new ArgumentException($"Unknown role '{other}'. Use administrator, auditor or member."),
        };

        if (args.Contains("--password") && args.Contains(PasswordFileFlag))
            throw new ArgumentException("Give --password or --password-file, not both.");
        string? hash = null;
        if (args.Contains("--password") || args.Contains(PasswordFileFlag))
        {
            hash = HashNewPassword(args);
            if (hash is null) return 1;
        }

        var display = CliArgs.Value(args, "--display") ?? name;
        var user = await Change(c, async change =>
        {
            var user = await change.Identity.CreateUserAsync(name, display, role);
            if (hash is not null) await change.Identity.SetPasswordHashAsync(user.Id, hash);
            change.Record("user.add", user.Name, null,
                new { display_name = display, role = RoleName(user.Role), password_set = hash is not null });
            return user;
        });
        Console.WriteLine($"User '{user.Name}' added as {RoleName(user.Role)}{(hash is null ? ", with no password" : "")}.");
        return 0;
    }

    private static async Task<int> UserDisabledAsync(string[] args, Context c, bool disabled)
    {
        var user = await RequireUserAsync(c.Store, Single(args, "a sign-in name"));
        var changed = await Change(c, async change =>
        {
            if (user.Disabled == disabled || !await change.Identity.SetUserDisabledAsync(user.Id, disabled)) return false;
            change.Record(disabled ? "user.disable" : "user.enable", user.Name, new { disabled = user.Disabled }, new { disabled });
            return true;
        });
        Console.WriteLine(!changed
            ? $"User '{user.Name}' was already {(disabled ? "disabled" : "enabled")}. Nothing changed and nothing was recorded."
            : $"User '{user.Name}' {(disabled ? "disabled: it and every agent it owns now reach nothing" : "enabled")}.");
        return 0;
    }

    private static async Task<int> SetPasswordAsync(string[] args, Context c)
    {
        var user = await RequireUserAsync(c.Store, Single(args, "a sign-in name", PasswordFileFlag));
        var hash = HashNewPassword(args);
        if (hash is null) return 1;
        await Change(c, async change =>
        {
            await change.Identity.SetPasswordHashAsync(user.Id, hash);
            change.Record("user.password", user.Name, null, new { password_set = true });
            return 0;
        });
        Console.WriteLine($"Password set for '{user.Name}'.");
        return 0;
    }

    private static async Task<int> UsersListAsync(IdentityStore store)
    {
        foreach (var u in await store.ListUsersAsync())
            Console.WriteLine(
                $"{u.SignInName,-24} {RoleName(u.Role),-14} {(u.Disabled ? "disabled" : "enabled"),-9} " +
                $"{(u.HasPassword ? "password" : "no password"),-12} {u.DisplayName}");
        return 0;
    }

    private static async Task<int> GroupsAddAsync(string[] args, Context c)
    {
        var name = Single(args, "a group name");
        var group = await Change(c, async change =>
        {
            var group = await change.Identity.CreateGroupAsync(name);
            change.Record("group.add", group.Name, null, new { name = group.Name });
            return group;
        });
        Console.WriteLine($"Group '{group.Name}' added.");
        return 0;
    }

    private static async Task<int> GroupsRenameAsync(string[] args, Context c)
    {
        var positionals = CliArgs.Positionals(args, 2);
        if (positionals.Count != 2) throw new ArgumentException("Give the group's name and its new name.");
        var group = await RequireGroupAsync(c.Store, positionals[0]);
        await Change(c, async change =>
        {
            await change.Identity.RenameGroupAsync(group.Id, positionals[1]);
            change.Record("group.rename", positionals[1], new { name = group.Name }, new { name = positionals[1] });
            return 0;
        });
        Console.WriteLine($"Group '{group.Name}' renamed to '{positionals[1]}'. Rules that name it are unchanged.");
        return 0;
    }

    private static async Task<int> GroupsRemoveAsync(string[] args, Context c)
    {
        var group = await RequireGroupAsync(c.Store, Single(args, "a group name"));

        // A deny entry for a deleted group matches nobody, so its former members
        // fall through to whatever the list allows after it. Make that a choice.
        var naming = await c.Rules.RulesNamingAsync(Principal.Group(CallerResolver.IdText(group.Id)));
        if (naming.Count > 0 && !args.Contains("--force"))
        {
            Console.Error.WriteLine($"Group '{group.Name}' is named by {naming.Count} rule(s). Removing it could widen who reads them:");
            foreach (var r in naming)
                Console.Error.WriteLine($"  {Folder(r.Rule.Source, r.Rule.PathPrefix)}: {await c.Names.DescribeAsync(r.Rule.Acl)}");
            Console.Error.WriteLine("Change those rules first, or pass --force.");
            return 1;
        }

        await Change(c, async change =>
        {
            await change.Identity.DeleteGroupAsync(group.Id);
            change.Record("group.remove", group.Name, new { name = group.Name, rules_naming_it = naming.Count }, null);
            return 0;
        });
        Console.WriteLine(
            $"Group '{group.Name}' removed with its memberships and grants. Rules that name it now match nobody; " +
            "a new group with the same name will not inherit them.");
        return 0;
    }

    private static async Task<int> GroupsMembersAsync(string[] args, Context c)
    {
        var group = await RequireGroupAsync(c.Store, Single(args, "a group name", "--add", "--remove"));
        var adding = new List<User>();
        foreach (var name in CliArgs.List(args, "--add")) adding.Add(await RequireUserAsync(c.Store, name));
        var removing = new List<User>();
        foreach (var name in CliArgs.List(args, "--remove")) removing.Add(await RequireUserAsync(c.Store, name));

        // Every addition and removal in one change, each with its own row.
        var said = await Change(c, async change =>
        {
            var lines = new List<string>();
            foreach (var user in adding)
            {
                if (!await change.Identity.AddMemberAsync(group.Id, user.Id)) { lines.Add($"'{user.Name}' was already a member."); continue; }
                change.Record("group.member.add", group.Name, null, new { user = user.Name });
                lines.Add($"Added '{user.Name}'.");
            }
            foreach (var user in removing)
            {
                if (!await change.Identity.RemoveMemberAsync(group.Id, user.Id)) { lines.Add($"'{user.Name}' was not a member."); continue; }
                change.Record("group.member.remove", group.Name, null, new { user = user.Name });
                lines.Add($"Removed '{user.Name}'.");
            }
            return lines;
        });
        foreach (var line in said) Console.WriteLine(line);

        var members = await c.Store.ListMembersAsync(group.Id);
        Console.WriteLine($"Group '{group.Name}' has {members.Count} member(s){(members.Count == 0 ? "." : ":")}");
        foreach (var m in members) Console.WriteLine($"  {m.Name}{(m.Disabled ? " (disabled)" : "")}");
        return 0;
    }

    private static async Task<int> GroupsListAsync(IdentityStore store)
    {
        foreach (var g in await store.ListGroupsAsync())
        {
            // A system group holds agents, not people, so its member count is
            // the agents it holds. Saying which it is explains why rename and
            // remove refuse it.
            var members = g.IsSystem
                ? $"{(await store.AgentsInSystemGroupAsync(g.Id)).Count} agent(s), maintained by Premagentic"
                : $"{(await store.ListMembersAsync(g.Id)).Count} member(s)";
            Console.WriteLine($"{g.Name,-32} {members}");
        }
        return 0;
    }

    private static async Task<int> AgentsAddAsync(string[] args, Context c)
    {
        var store = c.Store;
        var name = Single(args, "an agent name", "--owner", "--mode", "--rate", "--min-trust", "--model", "--vendor");
        var ownerName = CliArgs.Value(args, "--owner") ?? throw new ArgumentException("--owner names the user responsible for the agent.");
        var owner = await RequireUserAsync(store, ownerName);
        var mode = CliArgs.Value(args, "--mode") switch
        {
            "acts-for-user" => AgentMode.ActsForUser,
            "service" => AgentMode.Service,
            _ => throw new ArgumentException("--mode is acts-for-user (reads as its owner, never more) or service (reads as itself and granted groups)."),
        };
        var rateText = CliArgs.Value(args, "--rate");
        var rate = 60;
        if (rateText is not null && (!int.TryParse(rateText, out rate) || rate <= 0))
            throw new ArgumentException("--rate is a positive number of requests per minute.");
        var model = RequiredModel(args);

        var tier = CliArgs.Value(args, "--min-trust");
        var actor = AdminActor.Cli();
        var agent = await Change(c, async change =>
        {
            var agent = await change.Identity.CreateAgentAsync(
                name, owner.Id, mode, rate, tier, model, CliArgs.Value(args, "--vendor"), AgentOrigin.Cli(actor.Account ?? "unknown"));
            change.Record("agent.add", agent.Name, null,
                new
                {
                    owner = owner.Name, mode = mode == AgentMode.ActsForUser ? "acts-for-user" : "service", requests_per_minute = rate,
                    minimum_trust_tier = tier, model_location = ModelLocations.Text(agent.ModelLocation), model_vendor = agent.ModelVendor,
                    created_by = agent.Origin?.Text,
                });
            return agent;
        });
        Console.WriteLine(
            $"Agent '{agent.Name}' added, owned by '{owner.Name}', {ModeText(agent.Mode)}, {ModelText(agent)}. " +
            $"Issue it a token with 'prem tokens issue {agent.Name}'.");
        return 0;
    }

    /// <summary>
    /// Moves an agent between a local and a hosted model. The membership of the
    /// reserved group follows, which is what a folder rule that denies it reads.
    /// </summary>
    private static async Task<int> AgentsSetAsync(string[] args, Context c)
    {
        var name = Single(args, "an agent name", "--model", "--vendor");
        var agent = await RequireAgentAsync(c.Store, name);
        var model = RequiredModel(args);
        var updated = await Change(c, async change =>
        {
            await change.Identity.SetAgentModelAsync(agent.Id, model, CliArgs.Value(args, "--vendor"));
            var updated = await change.Identity.FindAgentAsync(agent.Id) ?? agent;
            change.Record("agent.model", agent.Name,
                new { model_location = ModelLocations.Text(agent.ModelLocation), model_vendor = agent.ModelVendor },
                new { model_location = ModelLocations.Text(updated.ModelLocation), model_vendor = updated.ModelVendor });
            return updated;
        });
        Console.WriteLine($"Agent '{agent.Name}' now {ModelText(updated)}.");
        return 0;
    }

    /// <summary>
    /// Reads <c>--model</c>, which every command that writes one requires. There
    /// is no default: an agent registered without an answer would be guessed at,
    /// and the guess decides what may be served to it.
    /// </summary>
    private static ModelLocation RequiredModel(string[] args)
    {
        if (ModelLocations.TryParse(CliArgs.Value(args, "--model"), out var model)) return model;
        throw new ArgumentException(
            "--model is local (the model runs inside your network) or hosted (it runs on someone else's). " +
            "Name the vendor of a hosted model with --vendor.");
    }

    private static string ModelText(Agent agent) =>
        agent.ModelLocation == ModelLocation.Local
            ? "a local model"
            : $"a hosted model{(agent.ModelVendor is { } v ? $" from {v}" : "")}";

    private static async Task<int> AgentDisabledAsync(string[] args, Context c, bool disabled)
    {
        var agent = await RequireAgentAsync(c.Store, Single(args, "an agent name"));
        var changed = await Change(c, async change =>
        {
            if (agent.Disabled == disabled || !await change.Identity.SetAgentDisabledAsync(agent.Id, disabled)) return false;
            change.Record(disabled ? "agent.disable" : "agent.enable", agent.Name, new { disabled = agent.Disabled }, new { disabled });
            return true;
        });
        Console.WriteLine(!changed
            ? $"Agent '{agent.Name}' was already {(disabled ? "disabled" : "enabled")}. Nothing changed and nothing was recorded."
            : $"Agent '{agent.Name}' {(disabled ? "disabled" : "enabled")}.");
        return 0;
    }

    private static async Task<int> AgentGrantAsync(string[] args, Context c, bool grant)
    {
        var positionals = CliArgs.Positionals(args, 2);
        if (positionals.Count != 2) throw new ArgumentException("Give the agent's name and the group's name.");
        var agent = await RequireAgentAsync(c.Store, positionals[0]);
        var group = await RequireGroupAsync(c.Store, positionals[1]);
        var changed = await Change(c, async change =>
        {
            var changed = grant
                ? await change.Identity.GrantGroupAsync(agent.Id, group.Id)
                : await change.Identity.RevokeGroupAsync(agent.Id, group.Id);
            if (changed) change.Record(grant ? "agent.grant" : "agent.ungrant", agent.Name, null, new { group = group.Name });
            return changed;
        });
        Console.WriteLine((grant, changed) switch
        {
            (true, true) => $"Agent '{agent.Name}' now holds group '{group.Name}'.",
            (true, false) => $"Agent '{agent.Name}' already held group '{group.Name}'.",
            (false, true) => $"Agent '{agent.Name}' no longer holds group '{group.Name}'.",
            _ => $"Agent '{agent.Name}' did not hold group '{group.Name}'.",
        });
        return 0;
    }

    private static async Task<int> AgentsListAsync(string[] args, IdentityStore store)
    {
        if (args.Contains("--removed"))
        {
            foreach (var r in await store.ListRemovedAgentsAsync())
                Console.WriteLine($"{r.Name,-24} owner {r.OwnerSignInName,-16} removed {r.RemovedAt:u} by {r.RemovedBy}");
            return 0;
        }

        foreach (var a in await store.ListAgentsAsync())
        {
            var owner = await store.FindUserAsync(a.OwnerUserId);
            var granted = a.Mode == AgentMode.Service
                ? string.Join(", ", (await store.GroupsGrantedToAgentAsync(a.Id)).Select(g => g.Name))
                : "";
            Console.WriteLine(
                $"{a.Name,-24} {(a.Mode == AgentMode.ActsForUser ? "acts-for-user" : "service"),-14} owner {owner?.Name ?? "(gone)",-16} " +
                $"{(a.Disabled ? "disabled" : "enabled"),-9} {a.RequestsPerMinute}/min" +
                $"  model: {ModelLocations.Text(a.ModelLocation)}{(a.ModelVendor is { } vendor ? $" ({vendor})" : "")}" +
                (granted.Length > 0 ? $"  groups: {granted}" : ""));
        }
        return 0;
    }

    private static async Task<int> TokensIssueAsync(string[] args, Context c)
    {
        var agent = await RequireAgentAsync(c.Store, Single(args, "an agent name", "--days"));
        var days = Days(args) ?? 90;

        var issued = await Change(c, async change =>
        {
            var issued = await change.Identity.IssueTokenAsync(agent.Id, TimeSpan.FromDays(days));
            change.Record("token.issue", agent.Name, null, new { token_id = issued.Record.Id, expires_at = issued.Record.ExpiresAt });
            return issued;
        });
        Console.WriteLine($"Token {issued.Record.Id} for agent '{agent.Name}', expires {issued.Record.ExpiresAt:u}.");
        Console.WriteLine("It is shown this once and cannot be recovered; store it now:");
        Console.WriteLine(issued.PlainText);
        return 0;
    }

    /// <summary>The <c>--days</c> a token verb was given, 1 to 3650, or null when it was given none.</summary>
    private static int? Days(string[] args)
    {
        if (CliArgs.Value(args, "--days") is not { } text) return null;
        return int.TryParse(text, out var days) && days is > 0 and <= 3650
            ? days
            : throw new ArgumentException("--days is between 1 and 3650.");
    }

    private static async Task<int> TokensReissueAsync(string[] args, Context c)
    {
        var days = Days(args);
        string tokenId;
        if (CliArgs.Value(args, "--agent") is { } name)
        {
            if (CliArgs.Positionals(args, 2, "--agent", "--days").Count > 0)
                throw new ArgumentException("Give a token id or --agent, not both.");
            var agent = await RequireAgentAsync(c.Store, name);
            var now = DateTimeOffset.UtcNow;
            var live = (await c.Store.ListTokensAsync(agent.Id)).Where(t => t.IsUsableAt(now)).ToList();
            if (live.Count != 1)
                throw new ArgumentException($"Agent '{agent.Name}' has {live.Count} live tokens. Give the token id.");
            tokenId = live[0].Id;
        }
        else
        {
            tokenId = Single(args, "a token id, or --agent with an agent name", "--days");
        }

        var reissued = await Change(c, change =>
            AgentLifecycle.ReissueAsync(change, tokenId, days is { } d ? TimeSpan.FromDays(d) : null));
        var record = reissued.New.Record;
        Console.WriteLine(
            $"Token {record.Id} for agent '{reissued.AgentName}' replaces token {reissued.ReplacedTokenId}, now revoked; " +
            $"it expires {record.ExpiresAt:u}.");
        Console.WriteLine("It is shown this once and cannot be recovered; store it now:");
        Console.WriteLine(reissued.New.PlainText);
        return 0;
    }

    private static async Task<int> AgentsRemoveAsync(string[] args, Context c)
    {
        var agent = await RequireAgentAsync(c.Store, Single(args, "an agent name"));
        var removal = await Change(c, change => AgentLifecycle.RemoveAsync(change, agent.Id));
        Console.WriteLine(
            $"Agent '{removal.Name}' removed; {removal.RevokedTokenIds.Count} token(s) revoked. " +
            "Its history stays, and its name is free.");
        return 0;
    }

    private static async Task<int> TokensRevokeAsync(string[] args, Context c)
    {
        var id = Single(args, "a token id");
        var revoked = await Change(c, async change =>
        {
            if (!await change.Identity.RevokeTokenAsync(id)) return false;
            var token = await change.Identity.FindTokenAsync(id);
            var agent = token is null ? null : await change.Identity.FindAgentAsync(token.AgentId);
            change.Record("token.revoke", agent?.Name ?? id, null, new { token_id = id });
            return true;
        });
        return revoked
            ? Ok($"Token {id} revoked. The next call that presents it reaches nothing.")
            : Fail($"No unrevoked token has the id {id}.");
    }

    private static async Task<int> TokensListAsync(string[] args, IdentityStore store)
    {
        var positionals = CliArgs.Positionals(args, 2);
        Guid? agentId = positionals.Count > 0 ? (await RequireAgentAsync(store, positionals[0])).Id : null;
        var now = DateTimeOffset.UtcNow;
        foreach (var t in await store.ListTokensAsync(agentId))
        {
            var agent = await store.FindAgentAsync(t.AgentId);
            // Ended: a token a person made for themself, dead since its owner or
            // its agent was disabled or the owner's password changed.
            var state = t.RevokedAt is not null ? "revoked"
                : t.Superseded && now < t.ExpiresAt ? "ended"
                : t.IsUsableAt(now) ? "usable" : "expired";
            Console.WriteLine(
                $"{t.Id}  {agent?.Name ?? "(removed)",-24} {state,-8} expires {t.ExpiresAt:u}  " +
                $"last used {(t.LastUsedAt is { } used ? used.ToString("u") : "never")}");
        }
        return 0;
    }

    private static async Task<int> RulesSetAsync(string[] args, PremagenticDatabase db, Guid tenantId)
    {
        var entries = RuleEntries(args)
            ?? throw new ArgumentException("Say who may read: --public, --principals a,b, or one --entry per line of the list.");
        await SetRuleAsync(db, tenantId, CliArgs.Value(args, "--source") ?? FileSystemSource, CliArgs.Value(args, "--prefix") ?? "", entries);
        return 0;
    }

    private static async Task<int> RulesRemoveAsync(string[] args, PremagenticDatabase db, Guid tenantId)
    {
        var source = CliArgs.Value(args, "--source") ?? FileSystemSource;
        var prefix = CliArgs.Value(args, "--prefix") ?? "";
        // Removed and recorded together, or neither: a rule that is not there
        // records nothing.
        var (removed, moved) = await new AdminChanges(db, tenantId).RunAsync(AdminActor.Cli(), async change =>
        {
            var before = await FindRuleAsync(change.Rules, source, prefix);
            var (removed, moved) = await change.Rules.RemoveRuleAsync(source, prefix);
            if (removed)
                change.Record(RuleRemoveKind, RuleTarget(source, prefix), new { entries = before?.Rule.Acl.CanonicalText }, null);
            return (removed, moved);
        });
        return removed
            ? Ok($"Rule {Folder(source, prefix)} removed. {moved} indexed document(s) fell to the rule above it, or to nobody.")
            : Fail($"There is no rule {Folder(source, prefix)}.");
    }

    private static async Task<int> RulesListAsync(PrincipalNames names, AclStore rules, IReadOnlyList<HostedHold> holds)
    {
        foreach (var r in await rules.ListRulesAsync())
        {
            Console.WriteLine($"{Folder(r.Rule.Source, r.Rule.PathPrefix),-40} {await names.DescribeAsync(r.Rule.Acl)}");
            // The rule is not the whole story where a folder is held: the
            // switch keeps hosted-model agents out whatever the entries say.
            if (HostedHolds.Touching(holds, r.Rule) is { Count: > 0 } touching)
                Console.WriteLine($"{"",-40} {HostedHolds.Describe(touching)}");
        }
        return 0;
    }

    /// <summary>The flag that names a file holding a new password, for <c>users add</c> and <c>users set-password</c>.</summary>
    private const string PasswordFileFlag = "--password-file";

    /// <summary>
    /// The new password's hash. When <see cref="PasswordFileFlag"/> names a
    /// file, the file is the only source: standard input is not read and
    /// nothing is asked. Otherwise standard input when it is redirected, or a
    /// prompt. Null, said, when no password was given.
    /// </summary>
    /// <exception cref="ArgumentException">The file cannot be used; the message says why.</exception>
    private static string? HashNewPassword(string[] args)
    {
        var password = args.Contains(PasswordFileFlag)
            ? ConsoleSecrets.ReadFile(
                CliArgs.Value(args, PasswordFileFlag) ?? throw new ArgumentException("--password-file needs a file."),
                aloneOnItsLine: true)
            : ConsoleSecrets.Read("New password: ", confirm: true);
        if (string.IsNullOrEmpty(password))
        {
            Console.Error.WriteLine("No password was given.");
            return null;
        }
        return new PasswordHasher().Hash(password);
    }

    private static async Task<User> RequireUserAsync(IdentityStore store, string name) =>
        await store.FindUserByNameAsync(name) ?? throw new ArgumentException($"No user signs in as '{name}'.");

    private static async Task<Group> RequireGroupAsync(IdentityStore store, string name) =>
        await store.FindGroupByNameAsync(name) ?? throw new ArgumentException($"There is no group named '{name}'.");

    private static async Task<Agent> RequireAgentAsync(IdentityStore store, string name) =>
        await store.FindAgentByNameAsync(name) ?? throw new ArgumentException($"There is no agent named '{name}'.");

    private static string Single(string[] args, string what, params string[] flagsWithValues)
    {
        var positionals = CliArgs.Positionals(args, 2, flagsWithValues);
        return positionals.Count == 1 ? positionals[0] : throw new ArgumentException($"Give {what}.");
    }

    private static string RoleName(Role role) => role.ToString().ToLowerInvariant();

    private static string ModeText(AgentMode mode) => mode == AgentMode.ActsForUser ? "acts for its owner" : "service";

    private static string Folder(string source, string prefix) => $"{source}:{(prefix.Length == 0 ? "(whole source)" : prefix)}";

    private static int Ok(string message)
    {
        Console.WriteLine(message);
        return 0;
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine(message);
        return 1;
    }
}

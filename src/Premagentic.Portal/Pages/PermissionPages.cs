using Premagentic.Core;
using Premagentic.Core.Admin;
using Premagentic.Core.Identity;
using Premagentic.Core.Retrieval;
using Premagentic.Core.Security;
using Premagentic.Core.Security.Acl;
using Premagentic.Portal.Html;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Premagentic.Portal.Pages;

/// <summary>
/// The folder rules, and two tools: <b>view as</b>, for administrators only, a
/// search run as another user or agent with that caller's own rights and
/// policy, which shows the passages that caller would be served and is
/// recorded in the audit trail as the administrator viewing as them; and
/// <b>why</b>, for auditors too, for one document and one caller, the rule,
/// the entry that decided, and the trust, freshness and lifecycle outcomes,
/// with no text of the document.
/// </summary>
internal static class PermissionPages
{
    public const string DefaultSource = "filesystem";

    public static async Task<IResult> Rules(HttpContext http)
    {
        var r = PortalRequest.Of(http);
        var names = new PrincipalNames(r.Identity());
        var holds = await HostedHolds.ListAsync(r.Db, r.Tenant, r.Aborted);
        var rows = new List<Markup>();
        foreach (var rule in await new AclStore(r.Db, r.Tenant).ListRulesAsync(r.Aborted))
        {
            var described = await names.DescribeAsync(rule.Rule.Acl, r.Aborted);
            rows.Add(Layout.Row(rule.Rule.Source, Folder(rule.Rule.PathPrefix),
                // Where a folder is held, the entries are not the whole story.
                HostedHolds.Touching(holds, rule.Rule) is { Count: > 0 } touching
                    ? (object)M.H($"{described}<br><em class=\"held\">{HostedHolds.Describe(touching)}</em>")
                    : described,
                $"#{rule.AclSetId}",
                Layout.Form(r, "/portal/permissions/rules/remove",
                    M.H($"{Layout.Hidden("source", rule.Rule.Source)}{Layout.Hidden("prefix", rule.Rule.PathPrefix)}"), "Remove", danger: true)));
        }

        var set = Layout.Form(r, "/portal/permissions/rules", M.H($"""
            {Layout.Field("Source", "source", DefaultSource)}
            {Layout.Field("Folder prefix", "prefix")}
            <label>Entries, one per line, first match decides <textarea name="entries" required>allow group:Staff</textarea></label>
            """), "Set rule");

        return Layout.Page(r, "Permissions", M.H($"""
            <p class="note">A folder rule decides who may read the documents under its prefix; the longest prefix wins.
            Each list is read in order and the first entry that names the caller decides; no match is a deny.</p>
            {Layout.Table(["Source", "Folder", "Who may read, in order", "List", ""], rows)}
            {(r.IsAdministrator ? M.H($"<h2>Set a rule</h2>{set}") : Markup.Empty)}
            <h2>Tools</h2>
            <p>{(r.IsAdministrator ? M.H($"{Layout.Link("/portal/permissions/view-as", "View as")}: search as another user or agent, and see the passages they would be served.") : Markup.Empty)}
            {Layout.Link("/portal/permissions/why", "Why")}: for one document and one caller, what decided.</p>
            """));
    }

    public static async Task<IResult> SetRule(HttpContext http)
    {
        var r = PortalRequest.Of(http);
        var form = await r.FormAsync();
        var source = form["source"].ToString().Trim() is { Length: > 0 } s ? s : DefaultSource;
        var prefix = form["prefix"].ToString().Replace('\\', '/').Trim().Trim('/');
        var entries = form["entries"].ToString().ReplaceLineEndings("\n").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (entries.Length == 0) return Layout.After("/portal/permissions", error: "Give at least one entry, such as: allow group:Staff");

        return await r.ChangeAsync("/portal/permissions", async change =>
        {
            var names = new PrincipalNames(change.Identity);
            var acl = await names.ToAclSetAsync(entries, r.Aborted);
            var before = (await change.Rules.ListRulesAsync(r.Aborted)).FirstOrDefault(x => x.Rule.Source == source && x.Rule.PathPrefix == prefix);
            var moved = await change.Rules.SetRuleAsync(new FolderRule(source, prefix, acl), r.Aborted);
            change.Record("rule.set", $"{source}:{prefix}", before is null ? null : new { entries = before.Rule.Acl.CanonicalText }, new { entries = acl.CanonicalText });
            return $"Rule {source}:{Folder(prefix)} set: {await names.DescribeAsync(acl, r.Aborted)}. {moved} indexed document(s) moved to it.";
        });
    }

    public static async Task<IResult> RemoveRule(HttpContext http)
    {
        var r = PortalRequest.Of(http);
        var form = await r.FormAsync();
        var source = form["source"].ToString();
        var prefix = form["prefix"].ToString();

        return await r.ChangeAsync("/portal/permissions", async change =>
        {
            var before = (await change.Rules.ListRulesAsync(r.Aborted)).FirstOrDefault(x => x.Rule.Source == source && x.Rule.PathPrefix == prefix);
            var (removed, moved) = await change.Rules.RemoveRuleAsync(source, prefix, r.Aborted);
            if (!removed) throw new InvalidOperationException($"There is no rule {source}:{Folder(prefix)}.");
            change.Record("rule.remove", $"{source}:{prefix}", new { entries = before?.Rule.Acl.CanonicalText }, null);
            return $"Rule {source}:{Folder(prefix)} removed. {moved} indexed document(s) fell to the rule above it, or to nobody.";
        });
    }

    /// <summary>
    /// A search as another caller: that caller's rights and trust policy as they
    /// are now, and an audit row that names the administrator viewing as them.
    /// </summary>
    public static async Task<IResult> ViewAs(HttpContext http)
    {
        var r = PortalRequest.Of(http);
        var who = r.Query("who");
        var query = r.Query("q");
        var historical = r.Query("historical") == "on";

        var results = Markup.Empty;
        if (who is not null && query is not null)
        {
            try
            {
                var target = await TargetAsync(r, who);
                var policy = await CallerPolicy.TrustAsync(r.Db, r.Tenant, target, r.Aborted);
                var scope = ViewAsScope.For(target.Scope, r.Person.User.Id);
                var result = await http.RequestServices.GetRequiredService<HybridSearch>().SearchAsync(
                    r.Tenant, query, new SearchOptions(scope, SearchPages.TopK, historical, Trust: policy), r.Aborted);
                results = M.H($"""
                    <p class="note">As {who}, under that caller's policy: machine-written content from {TierName(policy)}, stale content {(policy.IncludeStale ? "shown, flagged" : "left out")}.
                    Recorded in the audit trail as you viewing as {who}.</p>
                    {SearchPages.Hits(result.Hits)}
                    """);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                results = M.H($"<p class=\"error\">{ex.Message}</p>");
            }
        }

        var callers = await CallersAsync(r);
        return Layout.Page(r, "View as", M.H($"""
            <form method="get" action="/portal/permissions/view-as" class="inline">
            {Layout.Select("As", "who", callers, who)}
            {Layout.Field("Question", "q", query ?? "", required: true)}
            {Layout.Check("Include superseded and archived", "historical", historical)}
            <button type="submit">Search as them</button>
            </form>
            {results}
            """));
    }

    /// <summary>For one document and one caller: the rule, the deciding entry, and each gate's outcome.</summary>
    public static async Task<IResult> Why(HttpContext http)
    {
        var r = PortalRequest.Of(http);
        var who = r.Query("who");
        var path = r.Query("path");
        var historical = r.Query("historical") == "on";

        var answer = Markup.Empty;
        if (who is not null && path is not null)
        {
            try
            {
                var document = await new DocumentCatalog(r.Db, r.Tenant).FindAsync(path, r.Aborted)
                    ?? throw new ArgumentException("That path is not in the index.");
                var target = await TargetAsync(r, who);
                var policy = await CallerPolicy.TrustAsync(r.Db, r.Tenant, target, r.Aborted);
                var rule = (await new AclStore(r.Db, r.Tenant).LoadMatcherAsync(r.Aborted)).Match(document.SourceName ?? "", document.Path);
                var why = AccessExplainer.Explain(document, rule, target.Scope, policy, r.Clock.GetUtcNow(), historical);
                answer = await RenderAsync(r, who, document, why);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                answer = M.H($"<p class=\"error\">{ex.Message}</p>");
            }
        }

        var callers = await CallersAsync(r);
        return Layout.Page(r, "Why", M.H($"""
            <form method="get" action="/portal/permissions/why" class="inline">
            {Layout.Select("Caller", "who", callers, who)}
            {Layout.Field("Document path", "path", path ?? "", required: true)}
            {Layout.Check("Historical access", "historical", historical)}
            <button type="submit">Explain</button>
            </form>
            {answer}
            """));
    }

    private static async Task<Markup> RenderAsync(PortalRequest r, string who, CatalogDocument document, AccessExplanation why)
    {
        var names = new PrincipalNames(r.Identity());
        var entries = new List<Markup>();
        if (why.Acl is { } acl)
            for (var i = 0; i < acl.Entries.Count; i++)
            {
                var entry = acl.Entries[i];
                var marker = i + 1 == why.DecidingIndex ? "decided" : i + 1 == why.NarrowingIndex ? "decided the agent check" : "";
                entries.Add(Layout.Row(i + 1, entry.Effect == AclEffect.Allow ? "allow" : "deny", await names.ToDisplayAsync(entry.Principal, r.Aborted), marker));
            }

        Markup Gate(string name, GateOutcome outcome) =>
            Layout.Row(name, M.H($"<span class=\"tag {(outcome.Passes ? "good" : "bad")}\">{(outcome.Passes ? "passes" : "holds it back")}</span>"), outcome.Reason);

        return M.H($"""
            <p class="{(why.Readable ? "done" : "error")}">{who} {(why.Readable ? "can" : "cannot")} read {document.Path} in a search now.</p>
            <h2>The rule</h2>
            <p>{(why.Rule is { } rule ? $"{rule.Source}:{Folder(rule.PathPrefix)} is the longest rule that covers this path." : "No folder rule covers this path.")}
            {(document.AclFromRule ? "The document's list came from a rule." : "The document's list came from its connector.")}</p>
            <h2>The list, in order</h2>
            {Layout.Table(["#", "Effect", "Who", ""], entries)}
            <h2>The gates</h2>
            {Layout.Table(["Gate", "Outcome", "Why"], [Gate("Access", why.Access), Gate("Trust", why.Trust), Gate("Freshness", why.Freshness), Gate("Lifecycle", why.Lifecycle)])}
            """);
    }

    /// <summary>The caller a view-as or a why is about: <c>user:name</c> or <c>agent:name</c>, resolved as it would be now.</summary>
    private static async Task<Caller> TargetAsync(PortalRequest r, string who)
    {
        var identity = r.Identity();
        if (who.StartsWith("user:", StringComparison.Ordinal))
        {
            var user = await identity.FindUserByNameAsync(who[5..], r.Aborted) ?? throw new ArgumentException($"No user signs in as '{who[5..]}'.");
            return await CallerAccess.ResolveUserAsync(identity, user.Id, r.Aborted);
        }
        if (who.StartsWith("agent:", StringComparison.Ordinal))
        {
            var agent = await identity.FindAgentByNameAsync(who[6..], r.Aborted) ?? throw new ArgumentException($"There is no agent named '{who[6..]}'.");
            return await AgentCaller.ResolveAsync(identity, agent.Id, r.Aborted);
        }
        throw new ArgumentException("Choose a user or an agent.");
    }

    private static async Task<IEnumerable<(string, string)>> CallersAsync(PortalRequest r)
    {
        var identity = r.Identity();
        var users = (await identity.ListUsersAsync(r.Aborted)).Select(u => ("user:" + u.SignInName, "user " + u.SignInName));
        var agents = (await identity.ListAgentsAsync(r.Aborted)).Select(a => ("agent:" + a.Name, "agent " + a.Name));
        return users.Concat(agents).ToArray();
    }

    private static string TierName(Core.Okf.TrustPolicy policy) =>
        Enum.IsDefined(policy.MinimumMachineTier) ? Core.Okf.TrustPolicy.TierKey(policy.MinimumMachineTier) : "human-reviewed";

    private static string Folder(string prefix) => prefix.Length == 0 ? "(whole source)" : prefix;
}

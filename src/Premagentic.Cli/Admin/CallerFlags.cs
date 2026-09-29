using Premagentic.Core.Identity;
using Premagentic.Core.Okf;
using Premagentic.Core.Security;
using Premagentic.Core.Storage;

namespace Premagentic.Cli.Admin;

/// <summary>
/// Who a <c>search</c> or <c>section</c> runs as, and whether that is a person or
/// an agent, which decides what trust policy it gets.
/// </summary>
/// <param name="AgentMinimumTrustTier">The agent's own minimum trust tier, for a token caller.</param>
internal sealed record CliCaller(AccessScope Scope, CallerKind Kind, string? AgentMinimumTrustTier = null);

/// <summary>
/// Who a <c>search</c> or <c>section</c> runs as. At most one of:
/// <list type="bullet">
/// <item><c>--user name</c>: as a Premagentic user, with that user's groups as
/// they are now.</item>
/// <item><c>--with-token</c>: as the agent whose token is read from standard
/// input or a prompt, never from an argument, with the agent's trust
/// policy.</item>
/// <item><c>--as a,b</c>: as a caller holding these principals, written by
/// name (<c>group:Staff</c>, <c>user:alice</c>).</item>
/// <item><c>--unrestricted reason</c>: past the access gate, recorded with the
/// reason.</item>
/// </list>
/// With none, only what <c>everyone</c> may read. Every choice but the token is
/// a person at the console, and gets a person's trust policy.
/// </summary>
internal static class CallerFlags
{
    public static async Task<(CliCaller? Caller, string? Error)> ResolveAsync(string[] args, PremagenticDatabase db, Guid tenantId)
    {
        var chosen = new[] { "--user", "--with-token", "--as", "--unrestricted" }.Count(args.Contains);
        if (chosen > 1) return (null, "Choose one of --user, --with-token, --as and --unrestricted.");

        var reason = CliArgs.Value(args, "--unrestricted");
        if (args.Contains("--unrestricted"))
            return reason is null ? (null, "--unrestricted needs a reason.") : (Person(AccessScope.UnrestrictedAudited(reason)), null);

        var store = new IdentityStore(db, tenantId);

        if (args.Contains("--user"))
        {
            var name = CliArgs.Value(args, "--user");
            if (name is null) return (null, "--user needs a sign-in name.");
            var user = await store.FindUserByNameAsync(name);
            if (user is null) return (null, $"No user signs in as '{name}'.");
            return (Person(await CallerAccess.ForUserAsync(store, user.Id)), null);
        }

        if (args.Contains("--with-token"))
        {
            var token = ConsoleSecrets.Read("Agent token: ")?.Trim();
            if (string.IsNullOrEmpty(token)) return (null, "No token was given.");
            var caller = await CallerAccess.ResolveAgentTokenAsync(store, token);
            return caller.IsResolved
                ? (new CliCaller(caller.Scope, caller.Kind, caller.Agent?.MinimumTrustTier), null)
                : (null, $"The token was refused ({caller.Scope.AuditLabel}).");
        }

        var names = CliArgs.List(args, "--as");
        if (names.Length > 0)
        {
            var translate = new PrincipalNames(store);
            var principals = new List<string>();
            foreach (var n in names)
                principals.Add((await translate.ToPrincipalAsync(n)).ToString());
            return (Person(AccessScope.ForPrincipals("cli", principals.ToArray())), null);
        }

        return (Person(AccessScope.PublicOnly), null);
    }

    private static CliCaller Person(AccessScope scope) => new(scope, CallerKind.Person);
}

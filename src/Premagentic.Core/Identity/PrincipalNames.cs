using Premagentic.Core.Security.Acl;

namespace Premagentic.Core.Identity;

/// <summary>
/// Translates between principals as people write them, by name
/// (<c>group:Staff</c>, <c>user:alice</c>, <c>agent:report-bot</c>), and as access
/// lists store them, by id. Names are looked up case-insensitively among live
/// rows; a name that matches nothing is an error, never a guess. <c>everyone</c>
/// stays as it is, and <c>sid:</c>, <c>uid:</c> and <c>gid:</c> pass through,
/// since they name a source system's own principals.
/// </summary>
public sealed class PrincipalNames(IdentityStore store)
{
    public async Task<Principal> ToPrincipalAsync(string text, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException("A principal cannot be empty.");
        text = text.Trim();
        if (text == "everyone") return Principal.Everyone;

        var colon = text.IndexOf(':');
        if (colon <= 0 || colon == text.Length - 1)
            throw new ArgumentException($"'{text}' is not kind:name, such as group:Staff, user:alice or agent:report-bot.");
        var kind = text[..colon];
        var name = text[(colon + 1)..];

        switch (kind)
        {
            case "user":
                var user = await store.FindUserByNameAsync(name, ct)
                    ?? throw new ArgumentException($"No user signs in as '{name}'.");
                return Principal.User(CallerResolver.IdText(user.Id));
            case "group":
                var group = await store.FindGroupByNameAsync(name, ct)
                    ?? throw new ArgumentException($"There is no group named '{name}'.");
                return Principal.Group(CallerResolver.IdText(group.Id));
            case "agent":
                var agent = await store.FindAgentByNameAsync(name, ct)
                    ?? throw new ArgumentException($"There is no agent named '{name}'.");
                return Principal.Agent(CallerResolver.IdText(agent.Id));
            case "sid" or "uid" or "gid":
                return Principal.TryParse(text, out var raw) ? raw : throw new ArgumentException($"'{text}' is not a valid principal.");
            default:
                throw new ArgumentException($"Unknown principal kind '{kind}'. Use user:, group:, agent:, sid:, uid:, gid: or everyone.");
        }
    }

    /// <summary>Entries such as <c>allow group:Staff</c> or <c>deny user:bob</c>, in the order given.</summary>
    public async Task<AclSet> ToAclSetAsync(IEnumerable<string> entries, CancellationToken ct = default)
    {
        var parsed = new List<AclEntry>();
        foreach (var raw in entries)
        {
            var entry = raw.Trim();
            var space = entry.IndexOf(' ');
            var effect = space < 0 ? entry : entry[..space];
            var who = space < 0 ? "" : entry[(space + 1)..];
            parsed.Add(effect switch
            {
                "allow" => AclEntry.Allow(await ToPrincipalAsync(who, ct)),
                "deny" => AclEntry.Deny(await ToPrincipalAsync(who, ct)),
                _ => throw new ArgumentException($"'{raw}' is not 'allow <principal>' or 'deny <principal>'."),
            });
        }
        return AclSet.Create(parsed);
    }

    /// <summary>A stored principal as a person reads it: names for ids, and a note for one that no longer exists.</summary>
    public async Task<string> ToDisplayAsync(Principal principal, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(principal);
        if (!Guid.TryParse(principal.Value, out var id)) return principal.ToString();

        switch (principal.Kind)
        {
            case PrincipalKind.User:
                return await store.FindUserAsync(id, ct) is { } user ? $"user:{user.Name}" : $"{principal} (no such user)";
            case PrincipalKind.Group:
                return await store.FindGroupAsync(id, ct) switch
                {
                    { Deleted: false } g => $"group:{g.Group.Name}",
                    { } g => $"{principal} (deleted group '{g.Group.Name}')",
                    null => $"{principal} (no such group)",
                };
            case PrincipalKind.Agent:
                return await store.FindAgentAsync(id, ct) is { } agent ? $"agent:{agent.Name}" : $"{principal} (no such agent)";
            default:
                return principal.ToString();
        }
    }

    /// <summary>An access list as a person reads it, one entry after another.</summary>
    public async Task<string> DescribeAsync(AclSet set, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(set);
        if (set.IsEmpty) return "(nobody)";
        var parts = new List<string>();
        foreach (var entry in set.Entries)
            parts.Add((entry.Effect == AclEffect.Allow ? "allow " : "deny ") + await ToDisplayAsync(entry.Principal, ct));
        return string.Join("; ", parts);
    }
}

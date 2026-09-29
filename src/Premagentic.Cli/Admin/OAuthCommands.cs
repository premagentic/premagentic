using System.Globalization;
using Premagentic.Core.Admin;
using Premagentic.Core.Identity;
using Premagentic.Core.Storage;

namespace Premagentic.Cli.Admin;

/// <summary>
/// <c>prem oauth</c>: the clients and the grants of the MCP authorization flow.
/// Every verb works whether the flow is on or off, so a client can be
/// registered before the flow is turned on and a grant can always be revoked.
/// A change goes through <see cref="AdminChanges"/> under the command line's
/// actor, with its row in the change record, as every administrator verb does.
/// </summary>
internal static class OAuthCommands
{
    public const string Verb = "oauth";

    public const string Usage = """
        prem oauth clients list
        prem oauth clients add <name> --redirect <address> [--redirect <address> ...] [--id <https-address>]
                               [--model local|hosted] [--vendor "Name"]
        prem oauth clients add --metadata-file <path> --id <https-address> [--model local|hosted] [--vendor "Name"]
        prem oauth clients replace --metadata-file <path> --id <https-address>
        prem oauth clients disable|enable|remove <client-id>
        prem oauth grants list [--user <sign-in-name>] [--all]
        prem oauth grants revoke <grant-id> | --user <sign-in-name> | --all

          The assistants that connect through the MCP authorization flow, and the grants people approved for
          them. Each grant reads through an agent that acts for the person who approved it, and never writes.
          Disabling or removing a client ends its grants and disables their agents; enabling it again brings
          none back. Replacing a client's metadata document, when its vendor has changed it, keeps every grant;
          its name and redirect addresses come from the new copy, and a code is exchanged only for an address
          the new copy lists. These commands work whether the flow is on or off.

          --redirect address     clients add: where the flow may send its answer: https, or http to 127.0.0.1,
                                 [::1] or localhost. Give one to five
          --id address           clients add: the https address an assistant names itself by, for one set
                                 up with it (default: a new prem_cli_ id, printed)
          --metadata-file path   clients add, replace: a copy of the metadata document that address names, saved
                                 by hand, at most 64 KB. Its client_id must be the --id address exactly; the name
                                 and redirect addresses come from it; nothing is fetched from the address
          --model where          clients add: local or hosted, where the assistant's model runs. The consent
                                 page then says so instead of asking each person
          --vendor "Name"        clients add: who runs a hosted model; required with --model hosted
          --user name            grants list, revoke: one person's grants
          --all                  grants list: ended grants too (default: live ones only); grants revoke: every
                                 grant not yet revoked
        """;

    /// <summary>Every verb that changes something, as noun and verb, for the test that proves each one leaves its row.</summary>
    public static IReadOnlyList<(string Noun, string Verb)> WriteVerbs { get; } =
        [("clients", "add"), ("clients", "replace"), ("clients", "disable"), ("clients", "enable"), ("clients", "remove"), ("grants", "revoke")];

    public static async Task<int> RunAsync(string[] args, PremagenticDatabase db, Guid tenantId)
    {
        // The stored public address judges each grant's audience, as the
        // running flow would; with none usable, no audience is judged.
        var stored = await OAuthStatus.StoredAddressAsync(db, tenantId);
        var clients = new OAuthClients(db, tenantId, stored);
        var grants = new OAuthGrants(db, tenantId, stored);
        var identity = new IdentityStore(db, tenantId);
        try
        {
            return (args.ElementAtOrDefault(1), args.ElementAtOrDefault(2)) switch
            {
                ("clients", "list") => await ClientsListAsync(clients),
                ("clients", "add") => await ClientsAddAsync(args, clients),
                ("clients", "replace") => await ClientsReplaceAsync(args, clients),
                ("clients", "disable") => await ClientChangeAsync(args, id => clients.DisableAsync(AdminActor.Cli(), id, default),
                    "is disabled. Every grant it had is ended and each one's agent is disabled; enabling it again brings none back."),
                ("clients", "enable") => await ClientChangeAsync(args, id => clients.EnableAsync(AdminActor.Cli(), id, default),
                    "is enabled. No grant it had comes back: each person approves it again."),
                ("clients", "remove") => await ClientChangeAsync(args, id => clients.RemoveAsync(AdminActor.Cli(), id, default),
                    "is removed. Every grant it had is ended and each one's agent is disabled; the assistant registers again to come back."),
                ("grants", "list") => await GrantsListAsync(args, identity, grants),
                ("grants", "revoke") => await GrantsRevokeAsync(args, identity, grants),
                _ => Fail("Usage:\n" + Usage),
            };
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return Fail(ex.Message);
        }
    }

    private static async Task<int> ClientsListAsync(OAuthClients clients)
    {
        foreach (var listing in await clients.ListAsync(default))
        {
            var client = listing.Client;
            var by = client.RegisteredBy == OAuthClientRegistration.Dynamic
                ? $"registered itself from {listing.RegisteredFrom}"
                : $"registered by {client.RegisteredByActor}";
            var model = client.StatedModelLocation is { } where
                ? $"  model: {ModelLocations.Text(where)}{(client.StatedModelVendor is { } vendor ? $" ({vendor})" : "")}"
                : "";
            Console.WriteLine(
                $"{client.Id}  {client.Name,-24} {(client.Disabled ? "disabled" : "enabled"),-9} {listing.LiveGrants} live grant(s)  " +
                $"{(listing.FirstApprovedAt is { } first ? $"first approved {Time(first)}" : "never approved")}  {by}{model}");
            Console.WriteLine($"    redirects: {string.Join(' ', client.RedirectUris)}");
            if (listing.Document is { } document)
                Console.WriteLine($"    from its metadata document, SHA-256 {document.Sha256}, stored {Time(document.StoredAt)}");
        }
        return 0;
    }

    private static async Task<int> ClientsAddAsync(string[] args, OAuthClients clients)
    {
        if (CliArgs.Value(args, "--metadata-file") is { } file) return await ClientsAddFromDocumentAsync(args, file, clients);

        var name = Single(args, "the assistant's name", "--redirect", "--id", "--model", "--vendor");
        var redirects = CliArgs.Values(args, "--redirect");
        if (redirects.Count == 0) throw new ArgumentException("Give at least one --redirect address.");
        var model = Model(args);

        var id = await clients.AddAsync(AdminActor.Cli(), CliArgs.Value(args, "--id"), name, redirects, model,
            CliArgs.Value(args, "--vendor"), default);
        Console.WriteLine($"Client '{name.Trim()}' registered. The assistant is set up with this client id:");
        Console.WriteLine(id);
        return 0;
    }

    /// <summary>
    /// An assistant that names itself by an https address, registered from a
    /// copy of its metadata document. The name and the redirect addresses come
    /// from the document, so neither may also be typed; nothing is fetched.
    /// </summary>
    private static async Task<int> ClientsAddFromDocumentAsync(string[] args, string file, OAuthClients clients)
    {
        if (CliArgs.Positionals(args, 3, "--metadata-file", "--redirect", "--id", "--model", "--vendor").Count > 0)
            throw new ArgumentException("The name comes from the metadata document; give no name beside --metadata-file.");
        if (CliArgs.Values(args, "--redirect").Count > 0)
            throw new ArgumentException("The redirect addresses come from the metadata document; give no --redirect beside --metadata-file.");
        var id = CliArgs.Value(args, "--id")
            ?? throw new ArgumentException("--metadata-file needs --id: the https address the assistant names itself by.");
        var model = Model(args);

        var document = await clients.AddFromDocumentAsync(AdminActor.Cli(), id, OAuthClientDocument.ReadBytes(file), model,
            CliArgs.Value(args, "--vendor"), default);
        Console.WriteLine($"Client '{document.Name}' registered from its metadata document, SHA-256 {document.Sha256}.");
        Console.WriteLine($"  redirect addresses: {string.Join(' ', document.RedirectUris)}");
        if (document.DroppedRedirects.Count > 0)
            Console.WriteLine($"  dropped, a private scheme: {string.Join(' ', document.DroppedRedirects)}");
        if (document.LeftOut.Count > 0)
            Console.WriteLine($"  left out, and never stored: {string.Join(", ", document.LeftOut)}");
        Console.WriteLine("Nothing was fetched from its address. The assistant is set up with this client id:");
        Console.WriteLine(document.ClientId);
        return 0;
    }

    /// <summary>
    /// A new copy of a stored metadata document, for the same address. Only
    /// the document changes, so nothing else may be given beside it.
    /// </summary>
    private static async Task<int> ClientsReplaceAsync(string[] args, OAuthClients clients)
    {
        if (CliArgs.Positionals(args, 3, "--metadata-file", "--redirect", "--id", "--model", "--vendor").Count > 0
            || CliArgs.Values(args, "--redirect").Count > 0 || CliArgs.Value(args, "--model") is not null || CliArgs.Value(args, "--vendor") is not null)
            throw new ArgumentException(
                "clients replace takes --metadata-file and --id only: the name and redirect addresses come from the document, " +
                "and where the model runs stays as the client was added.");
        var file = CliArgs.Value(args, "--metadata-file")
            ?? throw new ArgumentException("Give --metadata-file: the new copy of the assistant's metadata document.");
        var id = CliArgs.Value(args, "--id")
            ?? throw new ArgumentException("--metadata-file needs --id: the https address the assistant names itself by.");

        var replaced = await clients.ReplaceDocumentAsync(AdminActor.Cli(), id, OAuthClientDocument.ReadBytes(file), default);
        var document = replaced.Document;
        if (!replaced.Changed)
            return Ok($"The file is the document client {document.ClientId} is stored with already (SHA-256 {document.Sha256}); nothing changed.");
        Console.WriteLine($"Client {document.ClientId}'s metadata document replaced: SHA-256 {replaced.PreviousSha256} is now {document.Sha256}.");
        Console.WriteLine($"  name: {document.Name}");
        Console.WriteLine($"  redirect addresses: {string.Join(' ', document.RedirectUris)}");
        if (document.DroppedRedirects.Count > 0)
            Console.WriteLine($"  dropped, a private scheme: {string.Join(' ', document.DroppedRedirects)}");
        if (document.LeftOut.Count > 0)
            Console.WriteLine($"  left out, and never stored: {string.Join(", ", document.LeftOut)}");
        return Ok("Every grant stands. A code is exchanged only for a redirect address the new document lists. Nothing was fetched.");
    }

    private static ModelLocation? Model(string[] args) =>
        CliArgs.Value(args, "--model") is not { } where ? null
        : ModelLocations.TryParse(where, out var parsed) ? parsed
        : throw new ArgumentException($"--model is {ModelLocations.Local} or {ModelLocations.Hosted}.");

    private static async Task<int> ClientChangeAsync(string[] args, Func<string, Task> change, string said)
    {
        var id = Single(args, "a client id");
        await change(id);
        return Ok($"Client {id} {said}");
    }

    private static async Task<int> GrantsListAsync(string[] args, IdentityStore identity, OAuthGrants grants)
    {
        var user = CliArgs.Value(args, "--user") is { } name ? await RequireUserAsync(identity, name) : null;
        var filter = CliArgs.Has(args, "--all") ? OAuthGrantFilter.All : OAuthGrantFilter.Live;
        foreach (var g in (await grants.ListAllAsync(filter, default)).Where(g => user is null || g.UserId == user.Id))
        {
            Console.WriteLine(
                $"{g.GrantId}  {Status(g.Status),-40} {g.ClientName} ({g.ClientId})  for {g.UserName}  agent {g.AgentName}  " +
                $"approved {Time(g.CreatedAt)}  ends {Time(g.ExpiresAt)}  " +
                $"refreshed {g.RefreshCount} time(s), last {(g.LastRefreshedAt is { } last ? Time(last) : "never")}" +
                (g.RevokedReason is { } reason ? $"  reason: {reason}" : ""));
        }
        return 0;
    }

    private static async Task<int> GrantsRevokeAsync(string[] args, IdentityStore identity, OAuthGrants grants)
    {
        var ids = CliArgs.Positionals(args, 3, "--user");
        var name = CliArgs.Value(args, "--user");
        var all = CliArgs.Has(args, "--all");
        if (ids.Count + (name is null ? 0 : 1) + (all ? 1 : 0) != 1)
            throw new ArgumentException("Give one grant id, --user <sign-in-name>, or --all.");

        if (ids.Count == 1)
        {
            var id = ids[0];
            var grant = (await grants.ListAllAsync(OAuthGrantFilter.All, default)).FirstOrDefault(g => g.GrantId == id)
                        ?? throw new ArgumentException($"No grant has the id '{id}'.");
            if (grant.RevokedAt is not null) return Ok($"Grant {id} was revoked already ({grant.RevokedReason}); nothing changed.");
            await grants.RevokeAsync(AdminActor.Cli(), id, default);
            return Ok($"Grant {id} revoked. Its agent {grant.AgentName} is disabled, and its tokens reach nothing from their next call.");
        }

        var user = name is null ? null : await RequireUserAsync(identity, name);
        var revoked = await grants.RevokeManyAsync(AdminActor.Cli(), user?.Id, default);
        var whose = user is null ? "" : $" of {user.Name}";
        return Ok(revoked == 0
            ? $"No grant{whose} was left to revoke; nothing changed."
            : $"{revoked} grant(s){whose} revoked. Each one's agent is disabled, and its tokens reach nothing from their next call.");
    }

    /// <summary>What a grant's status is called here, as the portal's pages call it.</summary>
    private static string Status(OAuthGrantStatus status) => status switch
    {
        OAuthGrantStatus.Live => "live",
        OAuthGrantStatus.Pending => "pending",
        OAuthGrantStatus.Expired => "expired",
        OAuthGrantStatus.Revoked => "revoked",
        OAuthGrantStatus.EndedByDisable => "ended by a disable or a password change",
        OAuthGrantStatus.EndedByClient => "ended with its client",
        OAuthGrantStatus.EndedByAddressChange => "ended: the server's address changed",
        _ => status.ToString(),
    };

    private static string Time(DateTimeOffset at) => at.ToString("u", CultureInfo.InvariantCulture);

    private static async Task<User> RequireUserAsync(IdentityStore store, string name) =>
        await store.FindUserByNameAsync(name) ?? throw new ArgumentException($"No user signs in as '{name}'.");

    private static string Single(string[] args, string what, params string[] flagsWithValues)
    {
        var positionals = CliArgs.Positionals(args, 3, flagsWithValues);
        return positionals.Count == 1 ? positionals[0] : throw new ArgumentException($"Give {what}.");
    }

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

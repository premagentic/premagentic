using System.Net;
using System.Text.RegularExpressions;
using Premagentic.Core.Identity;
using Premagentic.Core.Storage;

namespace Premagentic.Tests;

/// <summary>
/// The portal as a browser uses it, in the API's own test host: the people,
/// groups, agents, rules and documents of <see cref="ApiWorld"/>, plus an
/// auditor, each signed in with a real session.
/// </summary>
internal sealed class PortalWorld : IAsyncDisposable
{
    public const string AuditorPassword = "audrey reads the long ledger";
    public const string Origin = "https://localhost";

    /// <summary>
    /// Also send the anti-forgery token in the header, which the host required
    /// before it read the form field. False once the host reads the field, so
    /// the tests exercise the field alone.
    /// </summary>
    public const bool AlsoSendHeaderToken = false;

    private PortalWorld(ApiWorld world, HttpClient client) => (World, Client) = (world, client);

    public ApiWorld World { get; }
    public HttpClient Client { get; }
    public ApiSession Admin { get; private set; } = null!;
    public ApiSession Auditor { get; private set; } = null!;
    public ApiSession Member { get; private set; } = null!;
    public User AuditorUser { get; private set; } = null!;

    public static async Task<PortalWorld> NewAsync(
        DatastoreTestDatabase server, bool okfBundle = true, ApiHostOptions? options = null,
        Func<PremagenticDatabase, Guid, Task>? beforeStart = null)
    {
        var world = await ApiWorld.NewAsync(server, options, okfBundle: okfBundle, beforeStart: beforeStart);
        var auditor = await world.Identity.CreateUserAsync("audrey", "Audrey", Role.Auditor);
        await world.Identity.SetPasswordHashAsync(auditor.Id, new PasswordHasher().Hash(AuditorPassword));
        // A handler of its own makes a client that does not follow redirects, so a
        // test sees the redirect a change answers with.
        var client = world.Host.Client(true, new PassThrough());
        return new PortalWorld(world, client)
        {
            Admin = await Api.SessionAsync(client, "carol", ApiWorld.CarolPassword),
            Auditor = await Api.SessionAsync(client, "audrey", AuditorPassword),
            Member = await Api.SessionAsync(client, "alice", ApiWorld.AlicePassword),
            AuditorUser = auditor,
        };
    }

    public Task<HttpResponseMessage> GetAsync(string path, ApiSession? session = null) =>
        Client.SendAsync(Api.Request(HttpMethod.Get, path, session: session));

    /// <summary>
    /// A form post the way a portal page sends it: form-encoded, the token as the
    /// hidden field, and the portal's own origin, each of which a test can drop.
    /// </summary>
    public Task<HttpResponseMessage> PostAsync(
        string path, ApiSession? session, IEnumerable<(string Name, string Value)>? fields = null,
        bool tokenField = true, bool headerToken = AlsoSendHeaderToken, string? origin = Origin)
    {
        var form = (fields ?? []).ToList();
        if (tokenField && session is not null) form.Add(("prem_antiforgery", session.AntiForgeryToken));
        var request = Api.Request(HttpMethod.Post, path, session: session, antiForgery: headerToken);
        request.Content = new FormUrlEncodedContent(form.Select(f => new KeyValuePair<string, string>(f.Name, f.Value)));
        if (origin is not null) request.Headers.Add("Origin", origin);
        return Client.SendAsync(request);
    }

    /// <summary>
    /// A form post that can carry a file, the way a page's upload form sends it:
    /// multipart/form-data, the token as the hidden field, and the portal's own
    /// origin, each of which a test can drop.
    /// </summary>
    public Task<HttpResponseMessage> PostMultipartAsync(
        string path, ApiSession? session, IEnumerable<(string Name, string Value)>? fields = null,
        IEnumerable<(string Name, string FileName, byte[] Bytes)>? files = null, bool tokenField = true, string? origin = Origin)
    {
        var content = new MultipartFormDataContent();
        foreach (var (name, value) in fields ?? []) content.Add(new StringContent(value), name);
        if (tokenField && session is not null) content.Add(new StringContent(session.AntiForgeryToken), "prem_antiforgery");
        foreach (var (name, fileName, bytes) in files ?? []) content.Add(new ByteArrayContent(bytes), name, fileName);
        var request = Api.Request(HttpMethod.Post, path, session: session, antiForgery: AlsoSendHeaderToken);
        request.Content = content;
        if (origin is not null) request.Headers.Add("Origin", origin);
        return Client.SendAsync(request);
    }

    public async Task<string> TextAsync(HttpResponseMessage response)
    {
        using (response) return await response.Content.ReadAsStringAsync();
    }

    public async Task<long> ScalarAsync(string sql)
    {
        await using var cmd = World.Db.DataSource.CreateCommand(sql);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>Every place a page points somewhere: src, href, action, and url() in a style.</summary>
    public static IEnumerable<string> References(string html) =>
        Regex.Matches(html, @"(?:src|href|action)\s*=\s*""([^""]*)""|url\(\s*['""]?([^'"")]*)", RegexOptions.IgnoreCase)
            .Select(m => m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value);

    /// <summary>A reference that stays on this origin: a path from the root, never a scheme or a protocol-relative host.</summary>
    public static bool StaysHere(string reference) =>
        reference.StartsWith('/') && !reference.StartsWith("//", StringComparison.Ordinal) && !reference.Contains(':');

    /// <summary>
    /// The one reference every page makes that leaves this origin: the link to
    /// this program's source, which a person follows and nothing loads.
    /// </summary>
    public static bool IsSourceLink(string reference) =>
        string.Equals(reference, Premagentic.Core.BuildVersion.SourceUrl, StringComparison.Ordinal);

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await World.DisposeAsync();
    }
}

/// <summary>Adds nothing; its presence gives a test client with no redirect following.</summary>
internal sealed class PassThrough : DelegatingHandler;

internal static class PortalRoutes
{
    /// <summary>Every page a reader may open, with any route values filled in from the test world.</summary>
    public static readonly string[] ReaderPages =
    [
        "/portal/users", "/portal/users/alice", "/portal/groups", "/portal/groups/Staff", "/portal/agents",
        "/portal/agents?view=removed",
        "/portal/agents/assistant", "/portal/sources", "/portal/documents", "/portal/documents/view?path=pub%2Fhandbook.md",
        "/portal/review",
        "/portal/permissions", "/portal/permissions/why", "/portal/settings",
        "/portal/tuning",
        "/portal/usage", "/portal/audit", "/portal/changes", "/portal/health", "/portal/export",
    ];

    /// <summary>
    /// Addresses only a reader may reach, which answer with the sentence that
    /// names PremAgentic for Teams: what used to be there came with it.
    /// </summary>
    public static readonly string[] AddOnAddresses = ["/portal/export/audit.jsonl"];

    /// <summary>
    /// Pages only an administrator may open: View as shows the passages another
    /// caller would be served, and an auditor sees documents by path, never
    /// their text.
    /// </summary>
    public static readonly string[] AdministratorPages = ["/portal/permissions/view-as"];

    /// <summary>Downloads a reader may take.</summary>
    public static readonly string[] ReaderDownloads =
        ["/portal/health/support-bundle.json", "/portal/export/config.json", "/portal/export/changes.jsonl"];

    /// <summary>Every route that changes something, each with fields that would make a real change.</summary>
    public static readonly (string Path, (string, string)[] Fields)[] Changes =
    [
        ("/portal/users", [("signInName", "zed"), ("role", "member")]),
        ("/portal/users/bob/enabled", [("enabled", "off")]),
        ("/portal/users/bob/password", [("password", "a long new password here")]),
        ("/portal/users/bob/role", [("role", "auditor")]),
        ("/portal/groups", [("name", "Visitors")]),
        ("/portal/groups/Staff/members", [("add", "dan")]),
        ("/portal/groups/Auditors/rename", [("newName", "Inspectors")]),
        ("/portal/groups/Contractors/remove", [("confirm", "yes")]),
        ("/portal/agents", [("name", "helper"), ("owner", "alice"), ("mode", "service"), ("rate", "30"), ("minTier", ""), ("model", "local")]),
        ("/portal/agents/report-bot/enabled", [("enabled", "off")]),
        ("/portal/agents/report-bot/limits", [("rate", "5"), ("minTier", "")]),
        ("/portal/agents/report-bot/grants", [("grant", "Staff")]),
        ("/portal/agents/report-bot/tokens", [("days", "7")]),
        ("/portal/sources", [("name", "desk"), ("folder", "."), ("prefix", "desk")]),
        ("/portal/permissions/rules", [("source", "test-datastore"), ("prefix", "extra"), ("entries", "allow everyone")]),
        ("/portal/permissions/rules/remove", [("source", "test-datastore"), ("prefix", "pub")]),
        ("/portal/settings", [("key", "trust.stale"), ("value", "hidden-from-everyone")]),
        ("/portal/tuning", [("key", "retrieval.rrf_k"), ("value", "40")]),
        ("/portal/tuning/unset", [("key", "retrieval.rrf_k")]),
        ("/portal/export/config.json", [("secrets", "yes")]),
        // Last: it takes report-bot out of the world.
        ("/portal/agents/report-bot/remove", [("confirm", "yes")]),
    ];

    public static async Task<bool> IsRefusedAsync(HttpResponseMessage response)
    {
        using (response)
            return response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized;
    }
}

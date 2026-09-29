using System.Net;
using System.Net.Mail;
using System.Text;
using System.Text.Json;
using Premagentic.Core.Extensions;
using Premagentic.Core.Reminders;

namespace Premagentic.Samples.Reminders;

/// <summary>
/// A reminder sink that sends each owner their summary by mail: one message
/// per owner, over SMTP, after the built-in sink has kept the run for the
/// portal. <c>prem reminders run --plan</c> delivers to no sink, so it sends
/// nothing.
/// <para>
/// This is the extension that calls out, which is why it is an extension and
/// not built in: PremAgentic sends nothing anywhere unless an administrator
/// installs and allows something that does, and the health page names it.
/// Its settings are <c>mail.json</c> beside its manifest. A password is never
/// in that file, in the manifest or in a PremAgentic setting: it is in a
/// credentials file of its own that <c>mail.json</c> names, readable only by
/// the account the service runs as.
/// </para>
/// </summary>
public sealed class MailRemindersExtension : IExtension
{
    public const string SettingsFile = "mail.json";

    public string Name => "mail-reminders";

    public void Register(ExtensionRegistrations registrations)
    {
        var folder = registrations.Folder
            ?? throw new InvalidOperationException("mail-reminders reads mail.json from its own folder, and the host did not say where that is.");
        registrations.AddReminderSink(new MailReminderSink(MailSettings.Read(Path.Combine(folder, SettingsFile))));
    }
}

/// <summary>What mail.json says: where to send from, through which server, and to whom.</summary>
/// <param name="Addresses">
/// An owner principal to the address its summary goes to, such as
/// <c>"user:alice": "alice@example.org"</c>, and <c>"administrators"</c> for the
/// summary of the documents nobody owns, which is required.
/// </param>
/// <param name="UserDomain">
/// For an owner <c>user:name</c> with no address of its own: <c>name@UserDomain</c>.
/// </param>
public sealed record MailSettings(string Host, int Port, bool Tls, string From, string? CredentialsFile,
    string? UserDomain, IReadOnlyDictionary<string, string> Addresses)
{
    /// <exception cref="InvalidOperationException">The file is missing or says something that cannot be used.</exception>
    public static MailSettings Read(string path)
    {
        if (!File.Exists(path)) throw new InvalidOperationException($"mail-reminders needs {path}, and it is not there.");
        using var json = JsonDocument.Parse(File.ReadAllText(path));
        var root = json.RootElement;
        if (root.TryGetProperty("password", out _))
            throw new InvalidOperationException(
                $"{path} holds a password. Put it in the credentials file that \"credentialsFile\" names, never in mail.json.");

        string Text(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            && v.GetString() is { Length: > 0 } s ? s : throw new InvalidOperationException($"{path} needs \"{name}\".");
        string? Optional(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        var port = root.TryGetProperty("port", out var p) && p.TryGetInt32(out var n) ? n : 587;
        if (port is < 1 or > 65535) throw new InvalidOperationException($"{path} gives port {port}, which is not a port.");
        var tls = !root.TryGetProperty("tls", out var t) || t.ValueKind != JsonValueKind.False;

        var addresses = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (root.TryGetProperty("addresses", out var a) && a.ValueKind == JsonValueKind.Object)
            foreach (var entry in a.EnumerateObject())
                if (entry.Value.GetString() is { Length: > 0 } address) addresses[entry.Name] = address;
        if (!addresses.ContainsKey(ReminderSummary.Administrators))
            throw new InvalidOperationException(
                $"{path} needs an address for \"{ReminderSummary.Administrators}\" under \"addresses\": the documents nobody owns go there.");

        return new MailSettings(Text("host"), port, tls, Text("from"), Optional("credentialsFile"), Optional("userDomain"), addresses);
    }

    /// <summary>Where <paramref name="owner"/>'s summary goes, or null when mail.json does not say.</summary>
    public string? AddressOf(string owner) =>
        Addresses.TryGetValue(owner, out var address) ? address
        : UserDomain is { Length: > 0 } domain && owner.StartsWith("user:", StringComparison.Ordinal) ? $"{owner[5..]}@{domain}"
        : null;
}

/// <summary>Sends each summary as one message to its owner's address.</summary>
public sealed class MailReminderSink(MailSettings settings) : IReminderSink
{
    public string Name => "mail";

    public async Task DeliverAsync(IReadOnlyList<ReminderSummary> summaries, DateTimeOffset computedAt, CancellationToken ct)
    {
        using var client = new SmtpClient(settings.Host, settings.Port) { EnableSsl = settings.Tls, Timeout = 30_000 };
        if (settings.CredentialsFile is { } file)
        {
            // Two lines: the user name, then the password. Read at each run and
            // never kept, logged or returned.
            var lines = File.ReadAllLines(file);
            if (lines.Length < 2) throw new InvalidOperationException($"{file} holds a user name and a password, one per line.");
            client.Credentials = new NetworkCredential(lines[0].Trim(), lines[1]);
        }

        var unaddressed = new List<string>();
        foreach (var summary in summaries)
        {
            if (settings.AddressOf(summary.OwnerPrincipal) is not { } to)
            {
                unaddressed.Add(summary.OwnerPrincipal);
                continue;
            }
            using var message = new MailMessage(settings.From, to, Subject(summary), Body(summary, computedAt));
            await client.SendMailAsync(message, ct);
        }
        // Every owner who has an address got their message first; the ones who
        // have none are the run's failure, which prem reminders run reports.
        if (unaddressed.Count > 0)
            throw new InvalidOperationException($"mail.json gives no address for {string.Join(", ", unaddressed)}, so they were not sent.");
    }

    private static string Subject(ReminderSummary summary) =>
        summary.OwnerPrincipal == ReminderSummary.Administrators
            ? $"PremAgentic reminders: {summary.Unowned.Count} document(s) nobody owns"
            : $"PremAgentic reminders: {summary.Stale.Count} stale, {summary.InReview.Count} waiting for review";

    private static string Body(ReminderSummary summary, DateTimeOffset computedAt)
    {
        var text = new StringBuilder($"What PremAgentic found for {summary.OwnerPrincipal} on {computedAt:u}.\n");
        void List(string heading, IReadOnlyList<ReminderItem> items)
        {
            if (items.Count == 0) return;
            text.Append('\n').Append(heading).Append('\n');
            foreach (var item in items) text.Append("  ").Append(item.Path).Append(item.Title is { } t ? $"  ({t})" : "").Append('\n');
        }
        List("Past their stale date:", summary.Stale);
        List("Waiting for review:", summary.InReview);
        List("From a source nobody owns:", summary.Unowned);
        return text.ToString();
    }
}

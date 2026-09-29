using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RedirectRules = Premagentic.Core.Identity.RedirectUris;

namespace Premagentic.Core.Identity;

/// <summary>
/// A client ID metadata document an administrator stores by hand, read and
/// checked, for an assistant that names itself by an https address.
/// <para>
/// Nothing in it is ever fetched: not the address the assistant names itself
/// by, not a key set, a logo or a home page. The fields the flow uses are
/// taken from it; every other field is left out, its name recorded and its
/// value never stored. The document must name itself by exactly the address
/// the administrator gives, compared by ordinal, as the specification's simple
/// string comparison requires.
/// </para>
/// </summary>
/// <param name="ClientId">The https address, exactly as the document and the administrator both give it.</param>
/// <param name="RedirectUris">The loopback and https addresses kept, in the document's order.</param>
/// <param name="DroppedRedirects">The scheme and host of each private-scheme address dropped.</param>
/// <param name="LeftOut">The names of the fields not used, in ordinal order. Their values are never kept.</param>
/// <param name="Sha256">The SHA-256 of the file's bytes, lowercase hex.</param>
public sealed record OAuthClientDocument(
    string ClientId, string Name, IReadOnlyList<string> RedirectUris, IReadOnlyList<string> DroppedRedirects,
    string? ApplicationType, string? SoftwareId, string? SoftwareVersion, IReadOnlyList<string> LeftOut, string Sha256)
{
    /// <summary>The largest document read. No client's document needs more.</summary>
    public const int MaxBytes = 64 * 1024;

    /// <summary>
    /// The refusal of a document over <see cref="MaxBytes"/>. A form that carries
    /// a document gives the same sentence when the form itself is past its bounds,
    /// so a person reads one sentence however large the document was.
    /// </summary>
    public static readonly string TooLarge = $"The metadata document is larger than {MaxBytes / 1024} KB, which no client's document needs.";

    /// <summary>The fields the flow takes from a document. Every other one is left out.</summary>
    public static readonly IReadOnlySet<string> Used = new HashSet<string>(StringComparer.Ordinal)
    {
        "client_id", "client_name", "redirect_uris", "grant_types", "response_types", "token_endpoint_auth_method",
        "application_type", "software_id", "software_version",
    };

    /// <summary>
    /// Reads the file once, never more than one byte past
    /// <see cref="MaxBytes"/>, so a file of any size costs no more than that,
    /// and checks what was read, which refuses a file over the bound.
    /// </summary>
    /// <exception cref="ArgumentException">The file cannot be read or the document will not do; the message says why.</exception>
    public static OAuthClientDocument Read(string path, string id) => Parse(ReadBytes(path), id);

    /// <summary>
    /// A file's bytes as a document is checked from them: never more than one
    /// byte past <see cref="MaxBytes"/> is read, so <see cref="Parse"/>
    /// refuses a file over the bound and a file of any size costs no more.
    /// </summary>
    /// <exception cref="ArgumentException">The file cannot be read; the message says why.</exception>
    public static byte[] ReadBytes(string path)
    {
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return ReadBytes(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ArgumentException($"The metadata document cannot be read: {ex.Message}");
        }
    }

    /// <summary>
    /// A stream's bytes, such as an upload's, read the same way: never more
    /// than one byte past <see cref="MaxBytes"/>, whatever the stream holds.
    /// </summary>
    public static byte[] ReadBytes(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var buffer = new byte[MaxBytes + 1];
        var read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
        return buffer.AsSpan(0, read).ToArray();
    }

    /// <summary>Checks a document's bytes against the address the administrator gives.</summary>
    /// <exception cref="ArgumentException">The document will not do; the message says why.</exception>
    public static OAuthClientDocument Parse(ReadOnlySpan<byte> bytes, string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        if (bytes.Length > MaxBytes)
            throw new ArgumentException(TooLarge);
        if (RedirectRules.Classify(id, OAuthSettings.AdministratorRedirectUriMaxLength, out var idProblem) != RedirectUriKind.Https)
            throw new ArgumentException(
                "The client id is the https address the assistant names itself by" + (idProblem is null ? "." : $": {idProblem}."));

        JsonElement root;
        try
        {
            using var parsed = JsonDocument.Parse(bytes.ToArray(), new JsonDocumentOptions { MaxDepth = 16 });
            root = parsed.RootElement.Clone();
        }
        catch (JsonException)
        {
            throw new ArgumentException("The metadata document is not JSON.");
        }
        if (root.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("The metadata document is a JSON object, and this file holds something else.");

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
            if (!names.Add(property.Name))
                throw new ArgumentException($"The metadata document names '{Clip(property.Name)}' twice.");

        // The one comparison the specification fixes: the document names
        // itself by exactly the address it is stored under.
        if (!root.TryGetProperty("client_id", out var documentId) || documentId.ValueKind != JsonValueKind.String)
            throw new ArgumentException("The metadata document has no client_id.");
        if (!string.Equals(documentId.GetString(), id, StringComparison.Ordinal))
            throw new ArgumentException(
                $"The document's client_id is '{Clip(documentId.GetString()!)}' and the client id given is '{Clip(id)}'. They must be the same address exactly.");

        if (root.TryGetProperty("token_endpoint_auth_method", out var method)
            && !(method.ValueKind == JsonValueKind.String && method.GetString() == "none"))
            throw new ArgumentException(
                $"The document asks to authenticate with {(method.ValueKind == JsonValueKind.String ? $"'{Clip(method.GetString()!)}'" : "a value that is not text")}. " +
                "This server issues no secret and holds no key, so it takes only an assistant that uses none.");

        var name = OAuthText.Check(Text(root, "client_name"), OAuthSettings.ClientNameMaxLength, "The document's client_name", out var nameProblem)
            ?? throw new ArgumentException(nameProblem + ".");

        if (!root.TryGetProperty("redirect_uris", out var uris) || uris.ValueKind != JsonValueKind.Array)
            throw new ArgumentException("The metadata document has no redirect_uris: a JSON array of addresses.");
        var submitted = uris.EnumerateArray().ToArray();
        if (submitted.Length > OAuthSettings.RedirectUrisSubmittedMax)
            throw new ArgumentException($"The metadata document lists more than {OAuthSettings.RedirectUrisSubmittedMax} redirect addresses.");
        var kept = new List<string>();
        var dropped = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in submitted)
        {
            if (entry.ValueKind != JsonValueKind.String) throw new ArgumentException("Every redirect address in the document is a string.");
            var uri = entry.GetString()!;
            if (!seen.Add(uri)) throw new ArgumentException("The document lists a redirect address twice.");
            switch (RedirectRules.Classify(uri, OAuthSettings.AdministratorRedirectUriMaxLength, out var problem))
            {
                case RedirectUriKind.Loopback or RedirectUriKind.Https:
                    kept.Add(uri);
                    break;
                case RedirectUriKind.PrivateUse:
                    dropped.Add(RedirectRules.SchemeAndHost(uri));
                    break;
                default:
                    throw new ArgumentException($"The document's redirect address '{Clip(uri)}' is refused: {problem}.");
            }
        }
        if (kept.Count is 0 or > 5)
            throw new ArgumentException(kept.Count == 0
                ? "The document lists no redirect address this server takes: https, or http to 127.0.0.1, [::1] or localhost."
                : "The document lists more than five redirect addresses this server takes; at most five are kept.");

        if (root.TryGetProperty("grant_types", out var grants)
            && (grants.ValueKind != JsonValueKind.Array
                || grants.EnumerateArray().Any(g => g.ValueKind != JsonValueKind.String || g.GetString() is not ("authorization_code" or "refresh_token"))))
            throw new ArgumentException("The document's grant_types may hold only authorization_code and refresh_token.");
        if (root.TryGetProperty("response_types", out var responses)
            && (responses.ValueKind != JsonValueKind.Array
                || responses.EnumerateArray().Any(r => r.ValueKind != JsonValueKind.String || r.GetString() != "code")))
            throw new ArgumentException("The document's response_types may hold only code.");
        string? softwareId = null, softwareVersion = null;
        if (Text(root, "software_id") is { } sid && (softwareId = OAuthText.Check(sid, 200, "The document's software_id", out var p1)) is null)
            throw new ArgumentException(p1 + ".");
        if (Text(root, "software_version") is { } sv && (softwareVersion = OAuthText.Check(sv, 200, "The document's software_version", out var p2)) is null)
            throw new ArgumentException(p2 + ".");
        var applicationType = Text(root, "application_type") is "native" or "web" ? Text(root, "application_type") : null;

        var leftOut = names.Where(n => !Used.Contains(n)).Select(Clip).Order(StringComparer.Ordinal).ToArray();
        return new OAuthClientDocument(
            id, name, kept, dropped, applicationType, softwareId, softwareVersion, leftOut,
            Convert.ToHexStringLower(SHA256.HashData(bytes)));
    }

    private static string? Text(JsonElement body, string name) =>
        body.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    /// <summary>
    /// A name or value from the file as a sentence or the record may carry it:
    /// printable ASCII only, each other character shown as '?', at most 64.
    /// </summary>
    private static string Clip(string text)
    {
        var shown = new StringBuilder(Math.Min(text.Length, 64));
        foreach (var c in text.Take(64)) shown.Append(c is >= ' ' and <= '~' ? c : '?');
        return text.Length > 64 ? shown + "..." : shown.ToString();
    }
}

/// <summary>A client's metadata document as stored: its hash and when it was stored. The document itself is not kept.</summary>
public sealed record OAuthStoredDocument(string Sha256, DateTimeOffset StoredAt);

/// <summary>What a replace of a client's stored document did.</summary>
/// <param name="Document">The new document, as read.</param>
/// <param name="PreviousSha256">The hash of the document it replaced; the same as the new one's when the file was the stored document already and nothing changed.</param>
public sealed record OAuthClientDocumentReplaced(OAuthClientDocument Document, string PreviousSha256)
{
    public bool Changed => !string.Equals(Document.Sha256, PreviousSha256, StringComparison.Ordinal);
}

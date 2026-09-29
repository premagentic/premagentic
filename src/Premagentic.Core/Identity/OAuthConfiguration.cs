using System.Globalization;
using System.Text.Json;

namespace Premagentic.Core.Identity;

/// <summary>
/// The one spelling of <see cref="OAuthSettings.PublicUrl"/>: lowercase
/// <c>https://</c> and a lowercase ASCII host, no user name or password, no
/// path (not even <c>/</c>), no query or fragment, and the port left out when
/// it is 443. Anything else is refused, naming the canonical spelling when
/// there is one, so a retype can never change the audience every access token
/// is bound to.
/// </summary>
public static class PublicUrls
{
    /// <summary>
    /// The canonical spelling of <paramref name="text"/>, or null with the
    /// reason when it cannot be made into one.
    /// </summary>
    public static string? Canonical(string? text, out string? problem)
    {
        problem = null;
        if (string.IsNullOrWhiteSpace(text) || text.Any(c => c is < '!' or > '~'))
        {
            problem = "it must be an https address in printable ASCII, such as https://prem.example.internal:8443";
            return null;
        }
        if (!text.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            problem = "it must be an https address; the flow is never served over plain HTTP";
            return null;
        }

        var rest = text["https://".Length..];
        var end = rest.IndexOfAny(['/', '?', '#']);
        var authority = end < 0 ? rest : rest[..end];
        if (authority.Contains('@'))
        {
            problem = "it must not carry a user name or password";
            return null;
        }

        var colon = authority.LastIndexOf(':');
        var host = (colon < 0 ? authority : authority[..colon]).ToLowerInvariant();
        if (host.Length == 0 || !host.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-' or '.'))
        {
            problem = "its host must be a name or an IPv4 address in ASCII letters, digits, hyphens and dots";
            return null;
        }

        var port = "";
        if (colon >= 0)
        {
            var digits = authority[(colon + 1)..];
            if (digits.Length == 0 || digits.Length > 5 || !digits.All(char.IsAsciiDigit)
                || !int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number is < 1 or > 65535)
            {
                problem = "its port must be a number from 1 to 65535";
                return null;
            }
            port = number == 443 ? "" : ":" + number.ToString(CultureInfo.InvariantCulture);
        }
        return "https://" + host + port;
    }

    /// <summary>The problem with a value to store, or null when it is already canonical.</summary>
    public static string? Problem(string? text)
    {
        var canonical = Canonical(text, out var problem);
        if (canonical is null) return $"{OAuthSettings.PublicUrl} is refused: {problem}.";
        return canonical == text
            ? null
            : $"{OAuthSettings.PublicUrl} is written in one spelling only, so a retype cannot change the address " +
              $"tokens are bound to: lowercase https and host, no path (not even /), no query or fragment, and no " +
              $":443. Set it to {canonical}.";
    }
}

/// <summary>What a redirect URI is, by the string-level rules.</summary>
public enum RedirectUriKind
{
    /// <summary><c>http</c> to <c>127.0.0.1</c>, <c>[::1]</c> or <c>localhost</c>: a program on the computer the browser runs on.</summary>
    Loopback = 1,

    /// <summary><c>https</c> to a name or an IPv4 address.</summary>
    Https = 2,

    /// <summary>
    /// A well-formed URI with a scheme other than http or https, such as
    /// <c>cursor:</c>. Never stored and never redirected to: dropped from a
    /// self-registration, refused at an administrator's.
    /// </summary>
    PrivateUse = 3,

    /// <summary>Anything else: refused, and at a self-registration the whole request is refused.</summary>
    Malformed = 4,
}

/// <summary>
/// Redirect URIs checked and matched as strings, never through a URI parser,
/// a loopback test or a name lookup: what the rules decide and what the
/// consent page shows are cut from the same text the browser will follow.
/// </summary>
public static class RedirectUris
{
    private static readonly string[] LoopbackHosts = ["127.0.0.1", "[::1]", "localhost"];

    /// <summary>What <paramref name="uri"/> is, and why when it is malformed.</summary>
    /// <param name="maxLength">
    /// The longest allowed: <see cref="OAuthSettings.DynamicRedirectUriMaxLength"/>
    /// for a self-registration, <see cref="OAuthSettings.AdministratorRedirectUriMaxLength"/>
    /// for an administrator's.
    /// </param>
    public static RedirectUriKind Classify(string? uri, int maxLength, out string? problem)
    {
        problem = null;
        if (string.IsNullOrEmpty(uri) || uri.Any(c => c is < '!' or > '~'))
            return Malformed(out problem, "it must be printable ASCII with no spaces");
        if (uri.Contains('#'))
            return Malformed(out problem, "it must not carry a fragment (#)");
        if (uri.Contains('\\'))
            return Malformed(out problem, "it must not contain a backslash");

        // The scheme by the grammar of RFC 3986: a letter, then letters,
        // digits, +, - or ., then a colon.
        var colon = uri.IndexOf(':');
        var scheme = colon > 0 ? uri[..colon] : "";
        var wellFormedScheme = scheme.Length > 0 && char.IsAsciiLetter(scheme[0])
            && scheme.All(c => char.IsAsciiLetterOrDigit(c) || c is '+' or '-' or '.');
        if (!wellFormedScheme)
            return Malformed(out problem, "it must be an absolute address that starts with https:// or http://");
        if (!scheme.Equals("http", StringComparison.OrdinalIgnoreCase) && !scheme.Equals("https", StringComparison.OrdinalIgnoreCase))
            return RedirectUriKind.PrivateUse;

        if (uri.Length > maxLength)
            return Malformed(out problem, $"it must be at most {maxLength} characters");

        var https = uri.StartsWith("https://", StringComparison.Ordinal);
        if (!https && !uri.StartsWith("http://", StringComparison.Ordinal))
            return Malformed(out problem, "it must start with https:// or http://, in lowercase");

        var authority = Authority(uri);
        if (authority.IndexOfAny(['@', '%', '*']) >= 0)
            return Malformed(out problem, "its host must not contain @, % or *");

        if (!https)
            return LoopbackAuthority(authority) is not null
                ? RedirectUriKind.Loopback
                : Malformed(out problem, "plain http is allowed only to 127.0.0.1, [::1] or localhost on this computer");

        var portAt = authority.LastIndexOf(':');
        var host = portAt < 0 ? authority : authority[..portAt];
        if (host.Length == 0 || !host.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '.'))
            return Malformed(out problem, "its host must be a name or an IPv4 address in ASCII letters, digits, hyphens and dots");
        if (portAt >= 0 && !IsPort(authority[(portAt + 1)..]))
            return Malformed(out problem, "its port must be a number from 1 to 65535 with no leading zero");
        return RedirectUriKind.Https;
    }

    /// <summary>
    /// Whether a requested redirect URI matches a registered one: equal, by
    /// ordinal comparison, or for a loopback address equal after the port
    /// right after the literal host is taken out of both. A port that is not a
    /// number from 1 to 65535 with no leading zero never matches.
    /// </summary>
    public static bool Matches(string registered, string requested)
    {
        if (string.Equals(registered, requested, StringComparison.Ordinal)) return true;
        return WithoutLoopbackPort(registered) is { } r && WithoutLoopbackPort(requested) is { } q
            && string.Equals(r, q, StringComparison.Ordinal);
    }

    /// <summary>The scheme, host and port the answer goes to, cut from the string itself.</summary>
    public static string Origin(string uri)
    {
        var start = uri.IndexOf("://", StringComparison.Ordinal) + 3;
        return uri[..start] + Authority(uri);
    }

    /// <summary>True for an http address to 127.0.0.1, [::1] or localhost, by the same rules as <see cref="Classify"/>.</summary>
    public static bool IsLoopback(string uri) =>
        uri.StartsWith("http://", StringComparison.Ordinal) && LoopbackAuthority(Authority(uri)) is not null;

    /// <summary>The scheme and host of a URI, for the change record when it is dropped: never the path or query.</summary>
    public static string SchemeAndHost(string uri)
    {
        var colon = uri.IndexOf(':');
        var scheme = colon > 0 ? uri[..colon] : "";
        var rest = colon > 0 ? uri[(colon + 1)..].TrimStart('/') : "";
        var end = rest.IndexOfAny(['/', '?']);
        var host = end < 0 ? rest : rest[..end];
        var text = scheme + "://" + host;
        return text.Length <= 80 ? text : text[..80];
    }

    private static string Authority(string uri)
    {
        var start = uri.IndexOf("://", StringComparison.Ordinal) + 3;
        var end = uri.IndexOfAny(['/', '?'], start);
        return end < 0 ? uri[start..] : uri[start..end];
    }

    /// <returns>The literal host when the authority is a loopback host with an optional well-formed port; otherwise null.</returns>
    private static string? LoopbackAuthority(string authority)
    {
        foreach (var host in LoopbackHosts)
        {
            if (authority == host) return host;
            if (authority.StartsWith(host + ":", StringComparison.Ordinal) && IsPort(authority[(host.Length + 1)..])) return host;
        }
        return null;
    }

    private static string? WithoutLoopbackPort(string uri)
    {
        if (!uri.StartsWith("http://", StringComparison.Ordinal)) return null;
        var authority = Authority(uri);
        var host = LoopbackAuthority(authority);
        if (host is null) return null;
        return "http://" + host + uri[("http://".Length + authority.Length)..];
    }

    private static bool IsPort(string text) =>
        text.Length is > 0 and <= 5 && text[0] != '0' && text.All(char.IsAsciiDigit)
        && int.Parse(text, NumberStyles.None, CultureInfo.InvariantCulture) <= 65535;

    private static RedirectUriKind Malformed(out string? problem, string why)
    {
        problem = why;
        return RedirectUriKind.Malformed;
    }
}

/// <summary>The checks each setting of the flow is stored with, one key at a time, as the settings catalog runs them.</summary>
public static class OAuthSettingRules
{
    public static string? BooleanProblem(string key, JsonElement value) =>
        value.ValueKind is JsonValueKind.True or JsonValueKind.False ? null : $"{key} takes true or false.";

    public static string? PublicUrlProblem(JsonElement value) =>
        value.ValueKind == JsonValueKind.String ? PublicUrls.Problem(value.GetString()) : $"{OAuthSettings.PublicUrl} takes an https address as text.";

    /// <summary>
    /// True when a value of <see cref="OAuthSettings.PublicUrl"/> is one the
    /// flow can start with. The one check that spans two of the flow's
    /// settings is that the flow is never on without such an address, judged
    /// by 'prem settings' one change at a time and by a profile on the whole
    /// of it, as it would stand once applied.
    /// </summary>
    public static bool AddressUsable(JsonElement? url) =>
        url is { ValueKind: JsonValueKind.String } u && PublicUrls.Problem(u.GetString()) is null;

    /// <summary>The refusal when the flow would be turned on with no usable address.</summary>
    public static string EnableNeedsAddress =>
        $"{OAuthSettings.Enabled} cannot be turned on until {OAuthSettings.PublicUrl} holds this server's https " +
        $"address as assistants reach it: 'prem settings set {OAuthSettings.PublicUrl} https://<host>:<port>'.";

    /// <summary>The refusal when the address would be unset while the flow is on.</summary>
    public static string AddressNeededWhileOn =>
        $"{OAuthSettings.PublicUrl} cannot be unset while {OAuthSettings.Enabled} is true: the flow would have no " +
        $"address. Set {OAuthSettings.Enabled} to false first.";

    public static string DynamicRedirectUrisAccepts =>
        $"a JSON array of at most {OAuthSettings.DynamicRedirectUrisMax} exact https redirect addresses of at most " +
        $"{OAuthSettings.DynamicRedirectUriMaxLength} characters each";

    public static string? DynamicRedirectUrisProblem(JsonElement value)
    {
        const string key = OAuthSettings.DynamicRedirectUris;
        if (value.ValueKind != JsonValueKind.Array) return $"{key} takes {DynamicRedirectUrisAccepts}.";
        var entries = value.EnumerateArray().ToArray();
        if (entries.Length > OAuthSettings.DynamicRedirectUrisMax) return $"{key} takes at most {OAuthSettings.DynamicRedirectUrisMax} addresses.";
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            if (entry.ValueKind != JsonValueKind.String) return $"{key} takes {DynamicRedirectUrisAccepts}.";
            var uri = entry.GetString()!;
            var kind = RedirectUris.Classify(uri, OAuthSettings.DynamicRedirectUriMaxLength, out var problem);
            if (kind != RedirectUriKind.Https)
                return $"{key} lists '{Clip(uri)}', which is not an https redirect address this server accepts" +
                       (problem is null ? "." : $": {problem}.");
            if (!seen.Add(uri)) return $"{key} lists '{Clip(uri)}' twice.";
        }
        return null;
    }

    /// <summary>A check for a whole number in a range, the way every count and lifetime of the flow is stored.</summary>
    public static Func<JsonElement, string?> Range(string key, int min, int max) => value =>
        value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var n) && n >= min && n <= max
            ? null
            : $"{key} takes a whole number from {min.ToString("N0", CultureInfo.InvariantCulture)} to {max.ToString("N0", CultureInfo.InvariantCulture)}.";

    /// <summary>The number in force: the stored one when it can be used, otherwise the default.</summary>
    public static int NumberOr(JsonElement? stored, Func<JsonElement, string?> problem, int fallback) =>
        stored is { } v && problem(v) is null ? v.GetInt32() : fallback;

    private static string Clip(string text) => text.Length <= 60 ? text : text[..57] + "...";
}

/// <summary>The flow at a glance, for the health page and the support bundle.</summary>
/// <param name="PublicUrl">The stored public address, as stored; null when it is not set.</param>
/// <param name="Clients">Registered clients, removed ones left out.</param>
/// <param name="PendingClients">What <see cref="OAuthSettings.MaxPendingClients"/> counts: self-registered clients never approved, not removed, within their pending time.</param>
/// <param name="PendingCap"><see cref="OAuthSettings.MaxPendingClients"/> as it applies.</param>
/// <param name="LiveGrants">Live grants, judged against the stored address when it is usable.</param>
public sealed record OAuthSummary(string? PublicUrl, int Clients, int PendingClients, int PendingCap, int LiveGrants);

/// <summary>
/// What the running flow noticed that its tables do not hold, for the health
/// page: when a self-registration was last refused at one of the operator's
/// limits, in this process. Resolves to null while the flow is off.
/// </summary>
public interface IOAuthHealth
{
    DateTimeOffset? LastRegistrationRefusal { get; }
}

/// <summary>What the command line, the health page and the support bundle say about the flow.</summary>
public static class OAuthStatus
{
    /// <summary>
    /// The flow at a glance while the stored flag turns it on. While it does
    /// not, null, having read only the settings: an install that never turned
    /// the flow on reads none of its tables here.
    /// </summary>
    public static async Task<OAuthSummary?> SummaryAsync(Storage.PremagenticDatabase db, Guid tenantId, DateTimeOffset now, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        var stored = await new SettingsStore(db, tenantId).GetManyAsync(
            [OAuthSettings.Enabled, OAuthSettings.PublicUrl, OAuthSettings.MaxPendingClients], ct);
        if (!stored.TryGetValue(OAuthSettings.Enabled, out var enabled) || enabled.ValueKind != JsonValueKind.True) return null;
        var url = stored.TryGetValue(OAuthSettings.PublicUrl, out var u) && u.ValueKind == JsonValueKind.String ? u.GetString() : null;
        var cap = OAuthSettingRules.NumberOr(stored.TryGetValue(OAuthSettings.MaxPendingClients, out var c) ? c : null,
            OAuthSettingRules.Range(OAuthSettings.MaxPendingClients, 1, 10_000), OAuthSettings.MaxPendingClientsDefault);

        await using var cmd = db.DataSource.CreateCommand("""
            SELECT count(*) FILTER (WHERE deleted_at IS NULL),
                   count(*) FILTER (WHERE deleted_at IS NULL AND registered_by = 'dynamic' AND first_approved_at IS NULL
                                     AND created_at > @since)
            FROM prem_config.oauth_client WHERE tenant_id = @tenant
            """);
        cmd.Parameters.AddWithValue("tenant", tenantId);
        cmd.Parameters.AddWithValue("since", now - TimeSpan.FromHours(OAuthSettings.PendingClientHours));
        int clients, pending;
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            await reader.ReadAsync(ct);
            (clients, pending) = (checked((int)reader.GetInt64(0)), checked((int)reader.GetInt64(1)));
        }
        return new OAuthSummary(url, clients, pending, cap, await LiveGrantsAsync(db, tenantId, now, ct));
    }

    /// <summary>
    /// How many grants are live now: by the liveness rules against the stored
    /// public address when it is usable, otherwise every activated, unrevoked,
    /// unexpired grant. Reading the flow's tables here is the flow's own
    /// administration, which works whether the flow is on or off.
    /// </summary>
    public static async Task<int> LiveGrantsAsync(Storage.PremagenticDatabase db, Guid tenantId, DateTimeOffset now, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        var stored = await StoredAddressAsync(db, tenantId, ct);
        await using var cmd = db.DataSource.CreateCommand(stored is not null
            ? $"SELECT count(*) FROM prem_config.oauth_grant g WHERE g.tenant_id = @tenant AND {CredentialSql.GrantLive("g")}"
            : "SELECT count(*) FROM prem_config.oauth_grant g WHERE g.tenant_id = @tenant AND g.activated_at IS NOT NULL AND g.revoked_at IS NULL AND g.expires_at > @now");
        cmd.Parameters.AddWithValue("tenant", tenantId);
        cmd.Parameters.AddWithValue("now", now);
        if (stored is not null) cmd.Parameters.AddWithValue("oauth_resource", stored.Resource);
        return checked((int)(long)(await cmd.ExecuteScalarAsync(ct))!);
    }

    /// <summary>
    /// The stored public address, as the flow would run with it, for judging
    /// each grant's audience where the flow itself is not running, as at the
    /// command line. Null when the address is not set or not usable; then no
    /// audience is judged.
    /// </summary>
    public static async Task<OAuthDeployment?> StoredAddressAsync(Storage.PremagenticDatabase db, Guid tenantId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        var stored = await new SettingsStore(db, tenantId).GetAsync(OAuthSettings.PublicUrl, ct);
        return stored is { ValueKind: JsonValueKind.String } s && PublicUrls.Problem(s.GetString()) is null
            ? new OAuthDeployment(s.GetString()!, DynamicRegistration: false, [])
            : null;
    }
}

/// <summary>Reads, at start, whether the flow runs, and never refuses to start over it.</summary>
public static class OAuthDeploymentLoader
{
    /// <summary>
    /// The flow as this process will run it, or null when it is off. When the
    /// flag is on but the public address is missing or unusable, or an entry
    /// of the redirect list is bad, the flow stays off and
    /// <paramref name="warn"/> is told once, naming both remedies.
    /// </summary>
    public static async Task<OAuthDeployment?> LoadAsync(SettingsStore settings, Action<string> warn, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(warn);
        var stored = await settings.GetManyAsync(
            [OAuthSettings.Enabled, OAuthSettings.PublicUrl, OAuthSettings.DynamicRegistration, OAuthSettings.DynamicRedirectUris], ct);

        if (!stored.TryGetValue(OAuthSettings.Enabled, out var enabled) || enabled.ValueKind != JsonValueKind.True) return null;

        string? why = null;
        if (!stored.TryGetValue(OAuthSettings.PublicUrl, out var url))
            why = $"{OAuthSettings.PublicUrl} is not set";
        else if (OAuthSettingRules.PublicUrlProblem(url) is { } urlProblem)
            why = urlProblem.TrimEnd('.');

        var redirects = Array.Empty<string>();
        if (why is null && stored.TryGetValue(OAuthSettings.DynamicRedirectUris, out var list))
        {
            if (OAuthSettingRules.DynamicRedirectUrisProblem(list) is { } listProblem) why = listProblem.TrimEnd('.');
            else redirects = list.EnumerateArray().Select(e => e.GetString()!).ToArray();
        }

        if (why is not null)
        {
            warn($"{OAuthSettings.Enabled} is true, but {why}. The MCP authorization flow is OFF until this is fixed: " +
                 $"set {OAuthSettings.PublicUrl} to this server's https address and fix the redirect list, or set " +
                 $"{OAuthSettings.Enabled} to false. Everything else runs as usual.");
            return null;
        }

        var dynamic = !stored.TryGetValue(OAuthSettings.DynamicRegistration, out var reg) || reg.ValueKind != JsonValueKind.False;
        return new OAuthDeployment(url.GetString()!, dynamic, redirects);
    }
}

using System.Text.Json;
using Npgsql;
using Premagentic.Core.Admin;
using Premagentic.Core.Storage;

namespace Premagentic.Core.Identity;

/// <summary>
/// What one of the flow's endpoints answers: a status, a JSON body, and for a
/// refusal the fixed reason logged for the operator (never a secret) and
/// whether it counts toward the address's failures.
/// </summary>
/// <param name="LogGrant">The grant a refusal concerns, when one was found, for the operator's log line.</param>
/// <param name="Warning">For a refusal at one of the operator's limits: the warning that names the setting and the count.</param>
public sealed record OAuthEndpointAnswer(
    int StatusCode, IReadOnlyDictionary<string, object?> Body, TimeSpan? RetryAfter = null,
    bool CountsAsFailure = false, string? LogReason = null, string? LogGrant = null, string? Warning = null)
{
    public bool Succeeded => StatusCode is >= 200 and < 300;

    public static OAuthEndpointAnswer Error(int status, string error, string? description = null, string? log = null,
        bool countsAsFailure = false, TimeSpan? retryAfter = null, string? grant = null, string? warning = null)
    {
        var body = new Dictionary<string, object?> { ["error"] = error };
        if (description is not null) body["error_description"] = description;
        return new OAuthEndpointAnswer(status, body, retryAfter, countsAsFailure, log ?? error, grant, warning);
    }
}

/// <summary>
/// Registered clients: dynamic registration (RFC 7591) as the flow serves it,
/// and an administrator's registration and management, which work whether the
/// flow is on or off.
/// </summary>
public sealed class OAuthClients(PremagenticDatabase db, Guid tenantId, OAuthDeployment? oauth = null, TimeProvider? time = null) : IOAuthClients
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    /// <summary>The one sentence a self-registration with no redirect this server takes is refused with.</summary>
    public const string LoopbackOnly =
        "A self-registered assistant may use only loopback redirect addresses on this server. Ask your administrator.";

    public const string PendingLimit =
        "This server has reached its limit of assistants waiting to be approved. Ask your administrator.";

    /// <summary>
    /// Registers a client that registered itself, with the redirect rules of
    /// the flow: loopback addresses kept, https ones kept only when an
    /// administrator listed them, private-use ones dropped, anything malformed
    /// refusing the whole request. Nothing the client says is fetched.
    /// </summary>
    /// <param name="sourceKey">The source address as the throttles count it: IPv4 exact, IPv6 by its /64.</param>
    public async Task<OAuthEndpointAnswer> RegisterAsync(JsonElement body, string sourceKey, CancellationToken ct)
    {
        if (oauth is not { DynamicRegistration: true })
            throw new InvalidOperationException("Dynamic registration is not served while the flow or registration is off.");
        if (body.ValueKind != JsonValueKind.Object)
            return Refuse("invalid_client_metadata", "The registration is a JSON object.", "register_not_object");

        // The redirect addresses first: they decide whether there is anything to register.
        if (!body.TryGetProperty("redirect_uris", out var uris) || uris.ValueKind != JsonValueKind.Array)
            return Refuse("invalid_redirect_uri", "redirect_uris is required: a JSON array of addresses.", "register_no_redirects");
        var submitted = uris.EnumerateArray().ToArray();
        if (submitted.Length > OAuthSettings.RedirectUrisSubmittedMax)
            return Refuse("invalid_redirect_uri", $"At most {OAuthSettings.RedirectUrisSubmittedMax} redirect addresses may be submitted.", "register_too_many_redirects");
        var kept = new List<string>();
        var dropped = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in submitted)
        {
            if (entry.ValueKind != JsonValueKind.String) return Refuse("invalid_redirect_uri", "Every redirect address is a string.", "register_redirect_not_string");
            var uri = entry.GetString()!;
            if (!seen.Add(uri)) return Refuse("invalid_redirect_uri", "A redirect address is listed twice.", "register_redirect_twice");
            switch (RedirectUris.Classify(uri, OAuthSettings.DynamicRedirectUriMaxLength, out var problem))
            {
                case RedirectUriKind.Loopback:
                    kept.Add(uri);
                    break;
                case RedirectUriKind.Https when oauth.DynamicRedirectUris.Contains(uri, StringComparer.Ordinal):
                    kept.Add(uri);
                    break;
                case RedirectUriKind.Https or RedirectUriKind.PrivateUse:
                    dropped.Add(RedirectUris.SchemeAndHost(uri));
                    break;
                default:
                    return Refuse("invalid_redirect_uri", $"A redirect address is refused: {problem}.", "register_redirect_malformed");
            }
        }
        if (kept.Count is 0 or > 5)
            return Refuse("invalid_redirect_uri", kept.Count == 0 ? LoopbackOnly : "At most five redirect addresses are kept.", "register_no_allowed_redirect");

        if (OAuthText.Check(Text(body, "client_name"), OAuthSettings.ClientNameMaxLength, "client_name", out var nameProblem) is not { } name)
            return Refuse("invalid_client_metadata", nameProblem + ".", "register_bad_name");
        if (body.TryGetProperty("grant_types", out var grants)
            && (grants.ValueKind != JsonValueKind.Array
                || grants.EnumerateArray().Any(g => g.ValueKind != JsonValueKind.String || g.GetString() is not ("authorization_code" or "refresh_token"))))
            return Refuse("invalid_client_metadata", "grant_types may hold only authorization_code and refresh_token.", "register_bad_grant_types");
        if (body.TryGetProperty("response_types", out var responses)
            && (responses.ValueKind != JsonValueKind.Array
                || responses.EnumerateArray().Any(r => r.ValueKind != JsonValueKind.String || r.GetString() != "code")))
            return Refuse("invalid_client_metadata", "response_types may hold only code.", "register_bad_response_types");
        string? softwareId = null, softwareVersion = null;
        if (Text(body, "software_id") is { } sid && (softwareId = OAuthText.Check(sid, 200, "software_id", out var p1)) is null)
            return Refuse("invalid_client_metadata", p1 + ".", "register_bad_software_id");
        if (Text(body, "software_version") is { } sv && (softwareVersion = OAuthText.Check(sv, 200, "software_version", out var p2)) is null)
            return Refuse("invalid_client_metadata", p2 + ".", "register_bad_software_version");
        var applicationType = Text(body, "application_type") is "native" or "web" ? Text(body, "application_type") : null;

        var id = OAuthSecrets.NewClientId();
        var now = _time.GetUtcNow();
        return await new AdminChanges(db, tenantId, _time).RunAsync(OAuthStore.FlowActor, async change =>
        {
            var tx = change.Transaction;
            await SweepClientsAsync(tx, now, ct);

            var settings = new SettingsStore(db, tenantId, tx);
            var stored = await settings.GetManyAsync([OAuthSettings.MaxPendingClients, OAuthSettings.PendingClientsPerAddress], ct);
            var cap = Number(stored, OAuthSettings.MaxPendingClients, 1, 10_000, OAuthSettings.MaxPendingClientsDefault);
            var perAddress = Number(stored, OAuthSettings.PendingClientsPerAddress, 1, 1_000, OAuthSettings.PendingClientsPerAddressDefault);
            var (pending, fromHere) = await PendingAsync(tx, sourceKey, now, ct);
            if (fromHere >= perAddress)
                return OAuthEndpointAnswer.Error(429, "temporarily_unavailable", PendingLimit, "register_address_pending_limit",
                    retryAfter: TimeSpan.FromHours(OAuthSettings.PendingClientHours),
                    warning: $"A self-registration from {sourceKey} was refused: {OAuthSettings.PendingClientsPerAddress} is {perAddress}, " +
                             $"and that address already holds {fromHere} assistants waiting to be approved.");
            if (pending >= cap)
                return OAuthEndpointAnswer.Error(503, "temporarily_unavailable", PendingLimit, "register_pending_cap",
                    retryAfter: TimeSpan.FromHours(1),
                    warning: $"A self-registration was refused: {OAuthSettings.MaxPendingClients} is {cap}, " +
                             $"and {pending} assistants are waiting to be approved.");

            await using (var insert = new NpgsqlCommand("""
                INSERT INTO prem_config.oauth_client(
                    id, tenant_id, name, redirect_uris, application_type, software_id, software_version,
                    registered_by, registered_from, created_at)
                VALUES(@id, @tenant, @name, @uris, @app, @sid, @sv, 'dynamic', @from, @now)
                """, tx.Connection, tx))
            {
                insert.Parameters.AddWithValue("id", id);
                insert.Parameters.AddWithValue("tenant", tenantId);
                insert.Parameters.AddWithValue("name", name);
                insert.Parameters.AddWithValue("uris", kept.ToArray());
                insert.Parameters.AddWithValue("app", (object?)applicationType ?? DBNull.Value);
                insert.Parameters.AddWithValue("sid", (object?)softwareId ?? DBNull.Value);
                insert.Parameters.AddWithValue("sv", (object?)softwareVersion ?? DBNull.Value);
                insert.Parameters.AddWithValue("from", sourceKey);
                insert.Parameters.AddWithValue("now", now);
                await insert.ExecuteNonQueryAsync(ct);
            }
            change.Record("oauth.client.register", id, null, new
            {
                name, redirect_uris = kept, dropped_redirects = dropped, application_type = applicationType,
                software_id = softwareId, software_version = softwareVersion, source = sourceKey,
            });

            var answer = new Dictionary<string, object?>
            {
                ["client_id"] = id,
                ["client_id_issued_at"] = OAuthSecrets.UnixSeconds(now),
                ["client_name"] = name,
                ["redirect_uris"] = kept,
                ["grant_types"] = new[] { "authorization_code", "refresh_token" },
                ["response_types"] = new[] { "code" },
                ["token_endpoint_auth_method"] = "none",
                ["scope"] = OAuthScopes.Read,
            };
            if (applicationType is not null) answer["application_type"] = applicationType;
            if (softwareId is not null) answer["software_id"] = softwareId;
            if (softwareVersion is not null) answer["software_version"] = softwareVersion;
            return new OAuthEndpointAnswer(201, answer);
        }, ct);
    }

    public async Task<IReadOnlyList<OAuthClientListing>> ListAsync(CancellationToken ct)
    {
        await using var conn = await db.DataSource.OpenConnectionAsync(ct);
        return (await OAuthStore.ListClientsAsync(conn, tenantId, _time.GetUtcNow(), oauth, ct))
            .Select(c => new OAuthClientListing(c.Row.ToClient(), c.Row.RegisteredFrom, c.Row.FirstApprovedAt, c.LiveGrants, c.Row.Document))
            .ToArray();
    }

    public async Task<string> AddAsync(AdminActor actor, string? id, string name, IReadOnlyList<string> redirectUris,
        ModelLocation? modelLocation, string? modelVendor, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(redirectUris);
        if (id is not null && RedirectUris.Classify(id, OAuthSettings.AdministratorRedirectUriMaxLength, out _) != RedirectUriKind.Https)
            throw new ArgumentException("A client id an administrator gives is an https address, such as the address a client uses as its id.");
        var clientName = OAuthText.Check(name, OAuthSettings.ClientNameMaxLength, "The name", out var nameProblem)
            ?? throw new ArgumentException(nameProblem + ".");
        if (redirectUris.Count is 0 or > 5) throw new ArgumentException("Give one to five redirect addresses.");
        if (redirectUris.Distinct(StringComparer.Ordinal).Count() != redirectUris.Count) throw new ArgumentException("A redirect address is given twice.");
        foreach (var uri in redirectUris)
        {
            switch (RedirectUris.Classify(uri, OAuthSettings.AdministratorRedirectUriMaxLength, out var problem))
            {
                case RedirectUriKind.Loopback or RedirectUriKind.Https:
                    break;
                case RedirectUriKind.PrivateUse:
                    throw new ArgumentException(
                        $"'{uri}' uses a private scheme. Every redirect address must be https, or http to 127.0.0.1, [::1] or localhost, as the MCP specification requires.");
                default:
                    throw new ArgumentException($"'{uri}' is refused: {problem}.");
            }
        }
        var vendor = CheckModel(modelLocation, modelVendor);
        var clientId = id ?? OAuthSecrets.NewClientId();
        await StoreAsync(actor, clientId, clientName, redirectUris, null, modelLocation, vendor, new
        {
            name = clientName, redirect_uris = redirectUris,
            model_location = modelLocation is { } l ? ModelLocations.Text(l) : null, model_vendor = vendor,
        }, ct);
        return clientId;
    }

    public async Task<OAuthClientDocument> AddFromDocumentAsync(AdminActor actor, string id, ReadOnlyMemory<byte> document,
        ModelLocation? modelLocation, string? modelVendor, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(id);
        var read = OAuthClientDocument.Parse(document.Span, id);
        await AddFromDocumentAsync(actor, read, modelLocation, modelVendor, ct);
        return read;
    }

    /// <summary>
    /// Registers an assistant that names itself by an https address, from a
    /// copy of its metadata document the administrator stored by hand. Its
    /// name and redirect addresses come from the document, which was checked
    /// against that address when it was read. Only the document's hash and
    /// when it was stored are kept, never the document; the change record
    /// names the fields left out and holds none of their values. Nothing is
    /// fetched.
    /// </summary>
    public async Task<string> AddFromDocumentAsync(
        AdminActor actor, OAuthClientDocument document, ModelLocation? modelLocation, string? modelVendor, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(document);
        var vendor = CheckModel(modelLocation, modelVendor);
        await StoreAsync(actor, document.ClientId, document.Name, document.RedirectUris, document, modelLocation, vendor, new
        {
            name = document.Name, redirect_uris = document.RedirectUris,
            model_location = modelLocation is { } l ? ModelLocations.Text(l) : null, model_vendor = vendor,
            document_sha256 = document.Sha256, dropped_redirects = document.DroppedRedirects,
            left_out = document.LeftOut, application_type = document.ApplicationType,
            software_id = document.SoftwareId, software_version = document.SoftwareVersion,
        }, ct);
        return document.ClientId;
    }

    public async Task<OAuthClientDocumentReplaced> ReplaceDocumentAsync(AdminActor actor, string id, ReadOnlyMemory<byte> document,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(id);
        var read = OAuthClientDocument.Parse(document.Span, id);
        return await new AdminChanges(db, tenantId, _time).RunAsync(actor, async change =>
        {
            var tx = change.Transaction;
            var client = await OAuthStore.FindClientAsync(tx.Connection!, tx, tenantId, read.ClientId, ct);
            if (client is null or { Removed: true }) throw new InvalidOperationException($"No client has the id '{read.ClientId}'.");
            if (client.DocumentSha256 is not { } previous)
                throw new InvalidOperationException(
                    $"The client '{read.ClientId}' was not registered from a metadata document, so there is none to replace. " +
                    "Remove it and add it again from the document.");
            if (string.Equals(previous, read.Sha256, StringComparison.Ordinal)) return new OAuthClientDocumentReplaced(read, previous);

            // Only the document's own fields move. The grants people gave stand;
            // the token endpoint checks a code's redirect address against the
            // list written here. The row must still hold the document this
            // replace read, so the record's old hash is the one replaced.
            await using (var write = new NpgsqlCommand("""
                UPDATE prem_config.oauth_client
                SET name = @name, redirect_uris = @uris, application_type = @app, software_id = @sid, software_version = @sv,
                    document_sha256 = @sha, document_stored_at = @now
                WHERE tenant_id = @tenant AND id = @id AND deleted_at IS NULL AND document_sha256 = @previous
                """, tx.Connection, tx))
            {
                write.Parameters.AddWithValue("tenant", tenantId);
                write.Parameters.AddWithValue("id", read.ClientId);
                write.Parameters.AddWithValue("name", read.Name);
                write.Parameters.AddWithValue("uris", read.RedirectUris.ToArray());
                write.Parameters.AddWithValue("app", (object?)read.ApplicationType ?? DBNull.Value);
                write.Parameters.AddWithValue("sid", (object?)read.SoftwareId ?? DBNull.Value);
                write.Parameters.AddWithValue("sv", (object?)read.SoftwareVersion ?? DBNull.Value);
                write.Parameters.AddWithValue("sha", read.Sha256);
                write.Parameters.AddWithValue("now", _time.GetUtcNow());
                write.Parameters.AddWithValue("previous", previous);
                if (await write.ExecuteNonQueryAsync(ct) != 1)
                    throw new InvalidOperationException($"The client '{read.ClientId}' changed while its document was being replaced. Try again.");
            }
            change.Record("oauth.client.replace", read.ClientId,
                new { name = client.Name, redirect_uris = client.RedirectUris, document_sha256 = previous },
                new
                {
                    name = read.Name, redirect_uris = read.RedirectUris, document_sha256 = read.Sha256,
                    dropped_redirects = read.DroppedRedirects, left_out = read.LeftOut, application_type = read.ApplicationType,
                    software_id = read.SoftwareId, software_version = read.SoftwareVersion,
                });
            return new OAuthClientDocumentReplaced(read, previous);
        }, ct);
    }

    private static string? CheckModel(ModelLocation? modelLocation, string? modelVendor)
    {
        var vendor = string.IsNullOrWhiteSpace(modelVendor) ? null : modelVendor.Trim();
        if (modelLocation == ModelLocation.Hosted && vendor is null)
            throw new ArgumentException("Name who runs the hosted model, so the record can say where passages went.");
        if (modelLocation == ModelLocation.Local && vendor is not null)
            throw new ArgumentException("A local model has no vendor. Name a vendor only for a hosted model.");
        if (modelLocation is null && vendor is not null)
            throw new ArgumentException("Name a vendor only with where the model runs: --model hosted.");
        return vendor;
    }

    /// <param name="document">The document the client came from, whose hash is stored with the time; null for one typed in.</param>
    /// <param name="recorded">What the change record's row holds as the new value.</param>
    private async Task StoreAsync(
        AdminActor actor, string clientId, string clientName, IReadOnlyList<string> redirectUris, OAuthClientDocument? document,
        ModelLocation? modelLocation, string? vendor, object recorded, CancellationToken ct)
    {
        var registeredBy = actor.Surface == "cli" ? $"cli:{actor.Account ?? "unknown"}"
            : actor.UserId is { } user ? $"portal:{CallerResolver.IdText(user)}"
            : throw new ArgumentException("An administrator's registration names who made it.");

        var now = _time.GetUtcNow();
        await new AdminChanges(db, tenantId, _time).RunAsync(actor, async change =>
        {
            var tx = change.Transaction;
            var existing = await OAuthStore.FindClientAsync(tx.Connection!, tx, tenantId, clientId, ct);
            if (existing is { Removed: false })
                throw new InvalidOperationException($"A client with the id '{clientId}' is registered already.");

            // A removed client registered again keeps its id and none of its
            // grants: they ended when it was removed.
            await using var write = new NpgsqlCommand(existing is null
                ? """
                  INSERT INTO prem_config.oauth_client(
                      id, tenant_id, name, redirect_uris, application_type, software_id, software_version, registered_by,
                      model_location, model_vendor, created_at, document_sha256, document_stored_at)
                  VALUES(@id, @tenant, @name, @uris, @app, @sid, @sv, @by, @model, @vendor, @now, @sha, @stored)
                  """
                : """
                  UPDATE prem_config.oauth_client
                  SET name = @name, redirect_uris = @uris, registered_by = @by, registered_from = NULL,
                      application_type = @app, software_id = @sid, software_version = @sv,
                      model_location = @model, model_vendor = @vendor, created_at = @now, first_approved_at = NULL,
                      disabled = false, deleted_at = NULL, document_sha256 = @sha, document_stored_at = @stored
                  WHERE tenant_id = @tenant AND id = @id
                  """, tx.Connection, tx);
            write.Parameters.AddWithValue("id", clientId);
            write.Parameters.AddWithValue("tenant", tenantId);
            write.Parameters.AddWithValue("name", clientName);
            write.Parameters.AddWithValue("uris", redirectUris.ToArray());
            write.Parameters.AddWithValue("app", (object?)document?.ApplicationType ?? DBNull.Value);
            write.Parameters.AddWithValue("sid", (object?)document?.SoftwareId ?? DBNull.Value);
            write.Parameters.AddWithValue("sv", (object?)document?.SoftwareVersion ?? DBNull.Value);
            write.Parameters.AddWithValue("by", registeredBy);
            write.Parameters.AddWithValue("model", modelLocation is { } m ? ModelLocations.Text(m) : DBNull.Value);
            write.Parameters.AddWithValue("vendor", (object?)vendor ?? DBNull.Value);
            write.Parameters.AddWithValue("now", now);
            write.Parameters.AddWithValue("sha", (object?)document?.Sha256 ?? DBNull.Value);
            write.Parameters.AddWithValue("stored", document is null ? DBNull.Value : (object)now);
            await write.ExecuteNonQueryAsync(ct);
            change.Record("oauth.client.add", clientId, null, recorded);
            return 0;
        }, ct);
    }

    public Task DisableAsync(AdminActor actor, string clientId, CancellationToken ct) => EndAsync(actor, clientId, remove: false, ct);

    public Task RemoveAsync(AdminActor actor, string clientId, CancellationToken ct) => EndAsync(actor, clientId, remove: true, ct);

    public async Task EnableAsync(AdminActor actor, string clientId, CancellationToken ct)
    {
        await new AdminChanges(db, tenantId, _time).RunAsync(actor, async change =>
        {
            var tx = change.Transaction;
            var client = await OAuthStore.FindClientAsync(tx.Connection!, tx, tenantId, clientId, ct);
            if (client is null or { Removed: true }) throw new InvalidOperationException($"No client has the id '{clientId}'.");
            if (!client.Disabled) return 0;
            await using var cmd = new NpgsqlCommand(
                "UPDATE prem_config.oauth_client SET disabled = false WHERE tenant_id = @tenant AND id = @id", tx.Connection, tx);
            cmd.Parameters.AddWithValue("tenant", tenantId);
            cmd.Parameters.AddWithValue("id", clientId);
            await cmd.ExecuteNonQueryAsync(ct);
            // Enabling brings no grant back: each ended when the client was disabled.
            change.Record("oauth.client.enable", clientId, new { disabled = true }, new { disabled = false });
            return 0;
        }, ct);
    }

    /// <summary>
    /// Disables or removes a client and, in the same change, ends every grant
    /// it has and disables each grant's agent.
    /// </summary>
    private async Task EndAsync(AdminActor actor, string clientId, bool remove, CancellationToken ct)
    {
        await new AdminChanges(db, tenantId, _time).RunAsync(actor, async change =>
        {
            var tx = change.Transaction;
            var now = _time.GetUtcNow();
            var client = await OAuthStore.FindClientAsync(tx.Connection!, tx, tenantId, clientId, ct);
            if (client is null or { Removed: true }) throw new InvalidOperationException($"No client has the id '{clientId}'.");
            if (!remove && client.Disabled) return 0;

            var reason = remove ? OAuthStore.ClientRemovedReason : OAuthStore.ClientDisabledReason;
            foreach (var grant in await OAuthStore.ListGrantsAsync(tx.Connection!, tx, tenantId,
                         "g.client_id = @client AND g.revoked_at IS NULL", now, ct, ("client", clientId)))
                await OAuthStore.RevokeAsync(change, tenantId, grant, reason, disableAgent: true, now, ct);

            await using var cmd = new NpgsqlCommand(remove
                ? "UPDATE prem_config.oauth_client SET deleted_at = @now WHERE tenant_id = @tenant AND id = @id"
                : "UPDATE prem_config.oauth_client SET disabled = true WHERE tenant_id = @tenant AND id = @id", tx.Connection, tx);
            cmd.Parameters.AddWithValue("tenant", tenantId);
            cmd.Parameters.AddWithValue("id", clientId);
            cmd.Parameters.AddWithValue("now", now);
            await cmd.ExecuteNonQueryAsync(ct);
            change.Record(remove ? "oauth.client.remove" : "oauth.client.disable", clientId,
                new { name = client.Name, disabled = client.Disabled }, remove ? null : new { disabled = true });
            return 0;
        }, ct);
    }

    /// <summary>
    /// Soft-deletes a self-registered client never approved after
    /// <see cref="OAuthSettings.PendingClientHours"/>, and one approved once
    /// that has had no grant standing for <c>grant_days</c> since its last one
    /// ended. An administrator's client is never swept.
    /// </summary>
    private async Task SweepClientsAsync(NpgsqlTransaction tx, DateTimeOffset now, CancellationToken ct)
    {
        var grantDays = Number(await new SettingsStore(db, tenantId, tx).GetManyAsync([OAuthSettings.GrantDays], ct),
            OAuthSettings.GrantDays, 1, 365, OAuthSettings.GrantDaysDefault);
        await using var cmd = new NpgsqlCommand("""
            UPDATE prem_config.oauth_client SET deleted_at = @now
            WHERE tenant_id = @tenant AND registered_by = 'dynamic' AND deleted_at IS NULL
              AND first_approved_at IS NULL AND created_at <= @pendingSince;
            UPDATE prem_config.oauth_client c SET deleted_at = @now
            WHERE c.tenant_id = @tenant AND c.registered_by = 'dynamic' AND c.deleted_at IS NULL
              AND c.first_approved_at IS NOT NULL
              AND NOT EXISTS (SELECT 1 FROM prem_config.oauth_grant g
                              WHERE g.tenant_id = c.tenant_id AND g.client_id = c.id
                                AND COALESCE(g.revoked_at, g.expires_at) > @endedSince);
            """, tx.Connection, tx);
        cmd.Parameters.AddWithValue("tenant", tenantId);
        cmd.Parameters.AddWithValue("now", now);
        cmd.Parameters.AddWithValue("pendingSince", now - TimeSpan.FromHours(OAuthSettings.PendingClientHours));
        cmd.Parameters.AddWithValue("endedSince", now - TimeSpan.FromDays(grantDays));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Pending self-registered clients, all and from this address, leaving out
    /// removed ones and those past their pending time, so the counts do not
    /// depend on when the sweep last ran.
    /// </summary>
    private async Task<(long All, long FromHere)> PendingAsync(NpgsqlTransaction tx, string sourceKey, DateTimeOffset now, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("""
            SELECT count(*), count(*) FILTER (WHERE registered_from = @from)
            FROM prem_config.oauth_client
            WHERE tenant_id = @tenant AND registered_by = 'dynamic' AND first_approved_at IS NULL
              AND deleted_at IS NULL AND created_at > @since
            """, tx.Connection, tx);
        cmd.Parameters.AddWithValue("tenant", tenantId);
        cmd.Parameters.AddWithValue("from", sourceKey);
        cmd.Parameters.AddWithValue("since", now - TimeSpan.FromHours(OAuthSettings.PendingClientHours));
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        return (reader.GetInt64(0), reader.GetInt64(1));
    }

    internal static int Number(IReadOnlyDictionary<string, JsonElement> stored, string key, int min, int max, int fallback) =>
        OAuthSettingRules.NumberOr(stored.TryGetValue(key, out var v) ? v : null, OAuthSettingRules.Range(key, min, max), fallback);

    private static string? Text(JsonElement body, string name) =>
        body.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static OAuthEndpointAnswer Refuse(string error, string description, string log) =>
        OAuthEndpointAnswer.Error(400, error, description, log);
}

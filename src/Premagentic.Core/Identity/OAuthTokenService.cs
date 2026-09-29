using Npgsql;
using Premagentic.Core.Admin;
using Premagentic.Core.Storage;

namespace Premagentic.Core.Identity;

/// <summary>An access token that passed every check at <c>/mcp</c>: the agent to resolve, and the credential that names it in the audit label.</summary>
public sealed record OAuthAccess(Guid AgentId, string GrantId, string TokenId)
{
    /// <summary>What the audit label carries after <c>token:</c>, so a call joins to its grant after its token is swept.</summary>
    public string CredentialId => $"oauth:{GrantId}:{TokenId}";
}

/// <summary>
/// The token and revocation endpoints of the flow, and the check an access
/// token gets at <c>/mcp</c>. Every client is public: none is issued a secret,
/// PKCE protects the code, and a refresh token rotates on every use.
/// </summary>
public sealed class OAuthTokenService(PremagenticDatabase db, Guid tenantId, OAuthDeployment oauth, TimeProvider? time = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    /// <summary>
    /// A token request, form parameters in hand (the host has already refused
    /// another media type, a repeated parameter, and an Authorization header).
    /// </summary>
    public async Task<OAuthEndpointAnswer> TokenAsync(IReadOnlyDictionary<string, string> form, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(form);
        form.TryGetValue("grant_type", out var grantType);
        if (grantType is not ("authorization_code" or "refresh_token"))
            return grantType is null
                ? OAuthEndpointAnswer.Error(400, "invalid_request", "grant_type is required.", "token_no_grant_type")
                : OAuthEndpointAnswer.Error(400, "unsupported_grant_type", null, "token_unsupported_grant_type");
        if (form.ContainsKey("client_secret"))
            return OAuthEndpointAnswer.Error(400, "invalid_client", "This server issues no client secret.", "token_client_secret");
        if (!form.TryGetValue("client_id", out var clientId))
            return OAuthEndpointAnswer.Error(400, "invalid_request", "client_id is required.", "token_no_client_id");

        await using var conn = await db.DataSource.OpenConnectionAsync(ct);
        var client = await OAuthStore.FindClientAsync(conn, null, tenantId, clientId, ct);
        if (client is null or { Removed: true } or { Disabled: true })
            return OAuthEndpointAnswer.Error(400, "invalid_client", UnknownClient(client, clientId), "token_unknown_client", countsAsFailure: client is null);

        return grantType == "authorization_code"
            ? await ExchangeCodeAsync(conn, form, client, ct)
            : await RefreshAsync(conn, form, client, ct);
    }

    private async Task<OAuthEndpointAnswer> ExchangeCodeAsync(NpgsqlConnection conn, IReadOnlyDictionary<string, string> form,
        OAuthClientRow client, CancellationToken ct)
    {
        var now = _time.GetUtcNow();
        if (OAuthSecrets.CodeHash(form.GetValueOrDefault("code")) is not { } hash) return Invalid("code_malformed", counts: true);
        if (!form.TryGetValue("code_verifier", out var verifier))
            return OAuthEndpointAnswer.Error(400, "invalid_request", "code_verifier is required.", "code_no_verifier");

        CodeRow? code;
        await using (var find = new NpgsqlCommand("""
            SELECT grant_id, client_id, redirect_uri, code_challenge, resource, expires_at, used_at
            FROM prem_config.oauth_code WHERE tenant_id = @tenant AND code_sha256 = @hash
            """, conn))
        {
            find.Parameters.AddWithValue("tenant", tenantId);
            find.Parameters.AddWithValue("hash", hash);
            await using var reader = await find.ExecuteReaderAsync(ct);
            code = await reader.ReadAsync(ct)
                ? new CodeRow(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4),
                    reader.GetFieldValue<DateTimeOffset>(5), reader.IsDBNull(6) ? null : reader.GetFieldValue<DateTimeOffset>(6))
                : null;
        }
        if (code is null) return Invalid("code_unknown", counts: true);

        // Nothing below consumes, revokes or counts a replay until the client,
        // the redirect address, the verifier and the grant have all passed.
        if (!string.Equals(code.ClientId, client.Id, StringComparison.Ordinal)) return Invalid("code_other_client", grant: code.GrantId);
        if (form.TryGetValue("redirect_uri", out var redirect) && !string.Equals(redirect, code.RedirectUri, StringComparison.Ordinal))
            return Invalid("code_redirect_differs", grant: code.GrantId);
        // The client's list as it stands now: an administrator may have
        // replaced its metadata document since the code was issued.
        if (!client.RedirectUris.Any(r => RedirectUris.Matches(r, code.RedirectUri)))
            return Invalid("code_redirect_not_listed", grant: code.GrantId);
        if (!OAuthSecrets.VerifierMatches(verifier, code.Challenge)) return Invalid("code_verifier_mismatch", counts: true, grant: code.GrantId);
        if (form.TryGetValue("resource", out var resource) && !OAuthConsent.SameResource(resource, code.Resource))
            return OAuthEndpointAnswer.Error(400, "invalid_target", null, "code_resource_differs");

        var grant = await OAuthStore.FindGrantAsync(conn, null, tenantId, code.GrantId, now, ct);
        if (grant is null || !await StandsAsync(conn, null, code.GrantId, now, ct))
        {
            if (grant is not null) await OAuthStore.MarkEndedAsync(db, tenantId, _time, grant, oauth.Resource, ct);
            return Invalid("code_grant_ended", grant: code.GrantId);
        }

        await using var tx = await conn.BeginTransactionAsync(ct);
        await using (var claim = new NpgsqlCommand("""
            UPDATE prem_config.oauth_code SET used_at = @now
            WHERE tenant_id = @tenant AND code_sha256 = @hash AND used_at IS NULL AND expires_at > @now
            """, conn, tx))
        {
            claim.Parameters.AddWithValue("tenant", tenantId);
            claim.Parameters.AddWithValue("hash", hash);
            claim.Parameters.AddWithValue("now", now);
            if (await claim.ExecuteNonQueryAsync(ct) != 1)
            {
                await tx.RollbackAsync(ct);
                // Only a code already used is a replay; an expired one is just expired.
                if (code.UsedAt is not null || await CodeUsedAsync(conn, hash, ct))
                {
                    await RevokeForAsync(grant, OAuthStore.CodeReplayed, ct);
                    return Invalid("code_replayed", grant: grant.Id);
                }
                return Invalid("code_expired", grant: grant.Id);
            }
        }

        await using (var activate = new NpgsqlCommand(
            "UPDATE prem_config.oauth_grant SET activated_at = COALESCE(activated_at, @now) WHERE tenant_id = @tenant AND id = @grant",
            conn, tx))
        {
            activate.Parameters.AddWithValue("tenant", tenantId);
            activate.Parameters.AddWithValue("grant", grant.Id);
            activate.Parameters.AddWithValue("now", now);
            await activate.ExecuteNonQueryAsync(ct);
        }
        var answer = await IssueAsync(conn, tx, grant, replacing: null, now, ct);
        await tx.CommitAsync(ct);
        return answer;
    }

    private async Task<OAuthEndpointAnswer> RefreshAsync(NpgsqlConnection conn, IReadOnlyDictionary<string, string> form,
        OAuthClientRow client, CancellationToken ct)
    {
        var now = _time.GetUtcNow();
        if (!OAuthSecrets.TryParse(form.GetValueOrDefault("refresh_token"), OAuthPrefixes.RefreshToken, out var presented))
            return Invalid("refresh_malformed", counts: true);

        RefreshRow? row;
        await using (var find = new NpgsqlCommand("""
            SELECT t.grant_id, t.secret_sha256, t.expires_at, t.used_at, g.client_id
            FROM prem_config.oauth_refresh_token t JOIN prem_config.oauth_grant g ON g.id = t.grant_id
            WHERE t.tenant_id = @tenant AND t.id = @id
            """, conn))
        {
            find.Parameters.AddWithValue("tenant", tenantId);
            find.Parameters.AddWithValue("id", presented.Id);
            await using var reader = await find.ExecuteReaderAsync(ct);
            row = await reader.ReadAsync(ct)
                ? new RefreshRow(reader.GetString(0), reader.GetFieldValue<byte[]>(1), reader.GetFieldValue<DateTimeOffset>(2),
                    reader.IsDBNull(3) ? null : reader.GetFieldValue<DateTimeOffset>(3), reader.GetString(4))
                : null;
        }
        if (row is null) return Invalid("refresh_unknown", counts: true);
        if (!OAuthSecrets.Matches(presented, row.SecretHash)) return Invalid("refresh_mismatch", counts: true, grant: row.GrantId);
        if (!string.Equals(row.ClientId, client.Id, StringComparison.Ordinal)) return Invalid("refresh_other_client", grant: row.GrantId);
        if (form.TryGetValue("resource", out var resource) && !OAuthConsent.SameResource(resource, oauth.Resource))
            return OAuthEndpointAnswer.Error(400, "invalid_target", null, "refresh_resource_differs");

        var grant = await OAuthStore.FindGrantAsync(conn, null, tenantId, row.GrantId, now, ct);
        if (grant is null || grant.ActivatedAt is null || !await StandsAsync(conn, null, row.GrantId, now, ct))
        {
            if (grant is not null) await OAuthStore.MarkEndedAsync(db, tenantId, _time, grant, oauth.Resource, ct);
            return Invalid("refresh_grant_ended", grant: row.GrantId);
        }
        if (row.ExpiresAt <= now && row.UsedAt is null) return Invalid("refresh_expired", grant: row.GrantId);

        await using var tx = await conn.BeginTransactionAsync(ct);
        await using (var claim = new NpgsqlCommand("""
            UPDATE prem_config.oauth_refresh_token SET used_at = @now
            WHERE tenant_id = @tenant AND id = @id AND used_at IS NULL AND expires_at > @now
            """, conn, tx))
        {
            claim.Parameters.AddWithValue("tenant", tenantId);
            claim.Parameters.AddWithValue("id", presented.Id);
            claim.Parameters.AddWithValue("now", now);
            if (await claim.ExecuteNonQueryAsync(ct) != 1)
            {
                await tx.RollbackAsync(ct);
                // A used refresh token that comes back, or the loser of a race,
                // is reuse: the whole grant ends, with no grace.
                if (row.UsedAt is not null || await RefreshUsedAsync(conn, presented.Id, ct))
                {
                    await RevokeForAsync(grant, OAuthStore.RefreshTokenReused, ct);
                    return Invalid("refresh_reused", grant: row.GrantId);
                }
                return Invalid("refresh_expired", grant: row.GrantId);
            }
        }

        await using (var count = new NpgsqlCommand("""
            UPDATE prem_config.oauth_grant SET last_refreshed_at = @now, refresh_count = refresh_count + 1
            WHERE tenant_id = @tenant AND id = @grant
            """, conn, tx))
        {
            count.Parameters.AddWithValue("tenant", tenantId);
            count.Parameters.AddWithValue("grant", grant.Id);
            count.Parameters.AddWithValue("now", now);
            await count.ExecuteNonQueryAsync(ct);
        }
        var answer = await IssueAsync(conn, tx, grant, replacing: presented.Id, now, ct);
        await tx.CommitAsync(ct);
        return answer;
    }

    /// <summary>
    /// Token revocation (RFC 7009). Anything that is not a live token of this
    /// client is answered 200 and revokes nothing, except a live token of
    /// another client, which is refused.
    /// </summary>
    /// <summary>
    /// The description an unknown client is refused with at the token and
    /// revocation endpoints: for an https address nobody stored, the route an
    /// administrator takes, since nothing is fetched to identify it; for any
    /// other, none.
    /// </summary>
    public const string NotRegistered =
        "This assistant is not registered on this server. An administrator registers it with 'prem oauth clients add --metadata-file'.";

    private static string? UnknownClient(OAuthClientRow? client, string clientId) =>
        client is null && clientId.StartsWith("https://", StringComparison.Ordinal) ? NotRegistered : null;

    public async Task<OAuthEndpointAnswer> RevokeAsync(IReadOnlyDictionary<string, string> form, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(form);
        if (form.ContainsKey("client_secret"))
            return OAuthEndpointAnswer.Error(400, "invalid_client", "This server issues no client secret.", "revoke_client_secret");
        if (!form.TryGetValue("client_id", out var clientId))
            return OAuthEndpointAnswer.Error(400, "invalid_request", "client_id is required.", "revoke_no_client_id");
        await using var conn = await db.DataSource.OpenConnectionAsync(ct);
        var client = await OAuthStore.FindClientAsync(conn, null, tenantId, clientId, ct);
        if (client is null or { Removed: true } or { Disabled: true })
            return OAuthEndpointAnswer.Error(400, "invalid_client", UnknownClient(client, clientId), "revoke_unknown_client", countsAsFailure: client is null);

        var now = _time.GetUtcNow();
        var token = form.GetValueOrDefault("token");
        // The hint is only a hint: both kinds are searched.
        string? grantId = null;
        if (OAuthSecrets.TryParse(token, OAuthPrefixes.RefreshToken, out var refresh))
            grantId = await LiveSecretGrantAsync(conn, "oauth_refresh_token", "AND used_at IS NULL", refresh, now, ct);
        else if (OAuthSecrets.TryParse(token, OAuthPrefixes.AccessToken, out var access))
            grantId = await LiveSecretGrantAsync(conn, "oauth_access_token", "", access, now, ct);
        if (grantId is null) return Ok();

        var grant = await OAuthStore.FindGrantAsync(conn, null, tenantId, grantId, now, ct);
        if (grant is null || grant.RevokedAt is not null) return Ok();
        if (!string.Equals(grant.ClientId, client.Id, StringComparison.Ordinal)) return Invalid("revoke_other_client", grant: grant.Id);
        await RevokeForAsync(grant, OAuthStore.ClientRevoke, ct);
        return Ok();
    }

    /// <summary>
    /// The check an access token gets at <c>/mcp</c>: its form, its secret, its
    /// expiry, its audience against the current resource, and its grant by the
    /// liveness rules. A grant met dead is marked with its reason. The agent's
    /// own resolution runs after this, as the final gate.
    /// </summary>
    /// <returns>The access, or null with the fixed reason for the operator's log.</returns>
    public async Task<(OAuthAccess? Access, string? Refusal)> CheckAccessAsync(string presentedToken, CancellationToken ct)
    {
        if (!OAuthSecrets.TryParse(presentedToken, OAuthPrefixes.AccessToken, out var presented)) return (null, "access_malformed");
        var now = _time.GetUtcNow();
        await using var conn = await db.DataSource.OpenConnectionAsync(ct);
        await using (var find = new NpgsqlCommand($"""
            SELECT t.grant_id, t.secret_sha256, t.audience, t.expires_at, g.agent_id, {CredentialSql.GrantLive("g")}
            FROM prem_config.oauth_access_token t JOIN prem_config.oauth_grant g ON g.id = t.grant_id
            WHERE t.tenant_id = @tenant AND t.id = @id
            """, conn))
        {
            find.Parameters.AddWithValue("tenant", tenantId);
            find.Parameters.AddWithValue("id", presented.Id);
            find.Parameters.AddWithValue("now", now);
            find.Parameters.AddWithValue("oauth_resource", oauth.Resource);
            string grantId;
            Guid agentId;
            bool live;
            await using (var reader = await find.ExecuteReaderAsync(ct))
            {
                if (!await reader.ReadAsync(ct)) return (null, "access_unknown");
                if (!OAuthSecrets.Matches(presented, reader.GetFieldValue<byte[]>(1))) return (null, "access_mismatch");
                if (!string.Equals(reader.GetString(2), oauth.Resource, StringComparison.Ordinal)) return (null, "access_other_audience");
                if (reader.GetFieldValue<DateTimeOffset>(3) <= now) return (null, "access_expired");
                grantId = reader.GetString(0);
                agentId = reader.GetGuid(4);
                live = reader.GetBoolean(5);
            }
            if (!live)
            {
                if (await OAuthStore.FindGrantAsync(conn, null, tenantId, grantId, now, ct) is { } grant)
                    await OAuthStore.MarkEndedAsync(db, tenantId, _time, grant, oauth.Resource, ct);
                return (null, "access_grant_ended");
            }
            return (new OAuthAccess(agentId, grantId, presented.Id), null);
        }
    }

    /// <summary>Issues an access token and a new refresh token for a grant, in the transaction given.</summary>
    private async Task<OAuthEndpointAnswer> IssueAsync(NpgsqlConnection conn, NpgsqlTransaction tx, OAuthGrantRow grant, string? replacing,
        DateTimeOffset now, CancellationToken ct)
    {
        var settings = await new SettingsStore(db, tenantId, tx).GetManyAsync([OAuthSettings.AccessTokenMinutes, OAuthSettings.RefreshTokenDays], ct);
        var accessLifetime = TimeSpan.FromMinutes(OAuthClients.Number(settings, OAuthSettings.AccessTokenMinutes, 5, 1_440, OAuthSettings.AccessTokenMinutesDefault));
        var refreshLifetime = TimeSpan.FromDays(OAuthClients.Number(settings, OAuthSettings.RefreshTokenDays, 1, 365, OAuthSettings.RefreshTokenDaysDefault));
        var access = OAuthSecrets.Issue(OAuthPrefixes.AccessToken);
        var refresh = OAuthSecrets.Issue(OAuthPrefixes.RefreshToken);
        // Neither outlives the grant.
        var accessEnds = Min(now + accessLifetime, grant.ExpiresAt);
        var refreshEnds = Min(now + refreshLifetime, grant.ExpiresAt);

        await OAuthStore.SweepAsync(conn, tx, tenantId, now, ct);
        await using var insert = new NpgsqlCommand("""
            INSERT INTO prem_config.oauth_access_token(id, tenant_id, grant_id, secret_sha256, audience, created_at, expires_at)
            VALUES(@aid, @tenant, @grant, @ahash, @audience, @now, @aends);
            INSERT INTO prem_config.oauth_refresh_token(id, tenant_id, grant_id, secret_sha256, created_at, expires_at)
            VALUES(@rid, @tenant, @grant, @rhash, @now, @rends);
            UPDATE prem_config.oauth_refresh_token SET replaced_by = @rid WHERE tenant_id = @tenant AND id = @replaced;
            """, conn, tx);
        insert.Parameters.AddWithValue("tenant", tenantId);
        insert.Parameters.AddWithValue("grant", grant.Id);
        insert.Parameters.AddWithValue("aid", access.Id);
        insert.Parameters.AddWithValue("ahash", access.SecretHash);
        insert.Parameters.AddWithValue("audience", grant.Resource);
        insert.Parameters.AddWithValue("rid", refresh.Id);
        insert.Parameters.AddWithValue("rhash", refresh.SecretHash);
        insert.Parameters.AddWithValue("now", now);
        insert.Parameters.AddWithValue("aends", accessEnds);
        insert.Parameters.AddWithValue("rends", refreshEnds);
        insert.Parameters.AddWithValue("replaced", NpgsqlTypes.NpgsqlDbType.Text, (object?)replacing ?? DBNull.Value);
        await insert.ExecuteNonQueryAsync(ct);

        return new OAuthEndpointAnswer(200, new Dictionary<string, object?>
        {
            ["access_token"] = access.PlainText,
            ["token_type"] = "Bearer",
            ["expires_in"] = (long)(accessEnds - now).TotalSeconds,
            ["refresh_token"] = refresh.PlainText,
            ["scope"] = OAuthScopes.Read,
        });
    }

    private async Task<bool> StandsAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, string grantId, DateTimeOffset now, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            $"SELECT {CredentialSql.GrantStands("g")} FROM prem_config.oauth_grant g WHERE g.tenant_id = @tenant AND g.id = @grant", conn, tx);
        cmd.Parameters.AddWithValue("tenant", tenantId);
        cmd.Parameters.AddWithValue("grant", grantId);
        cmd.Parameters.AddWithValue("now", now);
        cmd.Parameters.AddWithValue("oauth_resource", oauth.Resource);
        return await cmd.ExecuteScalarAsync(ct) is true;
    }

    private async Task<bool> CodeUsedAsync(NpgsqlConnection conn, byte[] hash, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            "SELECT used_at IS NOT NULL FROM prem_config.oauth_code WHERE tenant_id = @tenant AND code_sha256 = @hash", conn);
        cmd.Parameters.AddWithValue("tenant", tenantId);
        cmd.Parameters.AddWithValue("hash", hash);
        return await cmd.ExecuteScalarAsync(ct) is true;
    }

    private async Task<bool> RefreshUsedAsync(NpgsqlConnection conn, string id, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            "SELECT used_at IS NOT NULL FROM prem_config.oauth_refresh_token WHERE tenant_id = @tenant AND id = @id", conn);
        cmd.Parameters.AddWithValue("tenant", tenantId);
        cmd.Parameters.AddWithValue("id", id);
        return await cmd.ExecuteScalarAsync(ct) is true;
    }

    /// <summary>
    /// The grant of a live token of the given table whose secret matches, or
    /// null. A refresh token already rotated out is not live: revoking it
    /// revokes nothing.
    /// </summary>
    private async Task<string?> LiveSecretGrantAsync(NpgsqlConnection conn, string table, string alsoWhere, PresentedOAuthSecret presented,
        DateTimeOffset now, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            $"SELECT grant_id, secret_sha256 FROM prem_config.{table} WHERE tenant_id = @tenant AND id = @id AND expires_at > @now {alsoWhere}", conn);
        cmd.Parameters.AddWithValue("tenant", tenantId);
        cmd.Parameters.AddWithValue("id", presented.Id);
        cmd.Parameters.AddWithValue("now", now);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return OAuthSecrets.Matches(presented, reader.GetFieldValue<byte[]>(1)) ? reader.GetString(0) : null;
    }

    /// <summary>Ends a grant on the flow's own account, with its agent: a reuse, a replay, a client's revoke.</summary>
    private Task RevokeForAsync(OAuthGrantRow grant, string reason, CancellationToken ct) =>
        new AdminChanges(db, tenantId, _time).RunAsync(OAuthStore.FlowActor,
            change => OAuthStore.RevokeAsync(change, tenantId, grant, reason, disableAgent: true, _time.GetUtcNow(), ct), ct);

    private static OAuthEndpointAnswer Invalid(string log, bool counts = false, string? grant = null) =>
        OAuthEndpointAnswer.Error(400, "invalid_grant", null, log, countsAsFailure: counts, grant: grant);

    private static OAuthEndpointAnswer Ok() => new(200, new Dictionary<string, object?>());

    private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a < b ? a : b;

    private sealed record CodeRow(string GrantId, string ClientId, string RedirectUri, string Challenge, string Resource,
        DateTimeOffset ExpiresAt, DateTimeOffset? UsedAt);

    private sealed record RefreshRow(string GrantId, byte[] SecretHash, DateTimeOffset ExpiresAt, DateTimeOffset? UsedAt, string ClientId);
}

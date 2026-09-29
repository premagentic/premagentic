using Npgsql;
using Premagentic.Core.Admin;
using Premagentic.Core.Storage;

namespace Premagentic.Core.Identity;

/// <summary>A registered client as stored.</summary>
internal sealed record OAuthClientRow(
    string Id, string Name, string[] RedirectUris, string? ApplicationType, string? SoftwareId, string? SoftwareVersion,
    string RegisteredBy, string? RegisteredFrom, string? ModelLocation, string? ModelVendor,
    DateTimeOffset CreatedAt, DateTimeOffset? FirstApprovedAt, bool Disabled, DateTimeOffset? DeletedAt,
    string? DocumentSha256 = null, DateTimeOffset? DocumentStoredAt = null)
{
    public bool Removed => DeletedAt is not null;

    /// <summary>The metadata document the client was stored from, by its hash; null for any other client.</summary>
    public OAuthStoredDocument? Document => DocumentSha256 is { } sha && DocumentStoredAt is { } at ? new(sha, at) : null;

    public OAuthClient ToClient() => new(
        Id, Name, RedirectUris, ApplicationType,
        RegisteredBy == "dynamic" ? OAuthClientRegistration.Dynamic : OAuthClientRegistration.Administrator,
        RegisteredBy == "dynamic" ? null : RegisteredBy,
        ModelLocation is null ? null : ModelLocations.TryParse(ModelLocation, out var location) ? location : Identity.ModelLocation.Hosted,
        ModelVendor, CreatedAt, Disabled);
}

/// <summary>A grant as stored, with the current state of everything its liveness depends on.</summary>
internal sealed record OAuthGrantRow(
    string Id, string ClientId, Guid UserId, Guid AgentId, long UserGeneration, long AgentGeneration, string Resource,
    DateTimeOffset CreatedAt, DateTimeOffset? ActivatedAt, DateTimeOffset ExpiresAt, DateTimeOffset? RevokedAt, string? RevokedReason,
    DateTimeOffset? LastRefreshedAt, int RefreshCount,
    string ClientName, string ClientRegisteredBy, bool ClientDisabled, bool ClientRemoved,
    string UserName, bool OwnerGone, bool OwnerDisabled, long OwnerGeneration,
    string AgentName, bool AgentGone, bool AgentDisabled, long AgentGenerationNow,
    bool HasClaimableCode);

/// <summary>
/// The flow's rows, the one derivation of a grant's status and of the reason
/// it ended, and the one place a grant is revoked. Every command runs on the
/// connection and transaction it is given.
/// </summary>
internal static class OAuthStore
{
    public const string OAuthSurface = "oauth";

    /// <summary>The actor of every change the flow makes on its own: a reuse, a replay, a client's revoke, a grant met dead.</summary>
    public static AdminActor FlowActor { get; } = new(OAuthSurface, null);

    // The reasons a grant ends, in the words the change record keeps.
    public const string OwnerDisabled = "owner disabled";
    public const string AgentDisabled = "agent disabled";
    public const string OwnerDisabledOrPasswordChanged = "owner disabled or password changed";
    public const string ClientDisabledReason = "client disabled";
    public const string ClientRemovedReason = "client removed";
    public const string AddressChanged = "the server's address changed";
    public const string RefreshTokenReused = "refresh token reused";
    public const string CodeReplayed = "code replayed";
    public const string ClientRevoke = "client revoke";
    public const string ApprovedAgain = "approved again";
    public const string ApprovedAgainElsewhere = "approved again with a different model location";
    public const string RevokedByOwner = "revoked by its owner";
    public const string RevokedByAdministrator = "revoked by an administrator";

    private const string ClientColumns = """
        c.id, c.name, c.redirect_uris, c.application_type, c.software_id, c.software_version, c.registered_by,
        c.registered_from, c.model_location, c.model_vendor, c.created_at, c.first_approved_at, c.disabled, c.deleted_at,
        c.document_sha256, c.document_stored_at
        """;

    public static async Task<OAuthClientRow?> FindClientAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, Guid tenantId, string id, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand($"SELECT {ClientColumns} FROM prem_config.oauth_client c WHERE c.tenant_id = @tenant AND c.id = @id", conn, tx);
        cmd.Parameters.AddWithValue("tenant", tenantId);
        cmd.Parameters.AddWithValue("id", id);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadClient(reader) : null;
    }

    public static async Task<IReadOnlyList<(OAuthClientRow Row, int LiveGrants)>> ListClientsAsync(
        NpgsqlConnection conn, Guid tenantId, DateTimeOffset now, OAuthDeployment? oauth, CancellationToken ct)
    {
        var live = oauth is not null
            ? $"(SELECT count(*) FROM prem_config.oauth_grant g WHERE g.tenant_id = c.tenant_id AND g.client_id = c.id AND {CredentialSql.GrantLive("g")})"
            : "(SELECT count(*) FROM prem_config.oauth_grant g WHERE g.tenant_id = c.tenant_id AND g.client_id = c.id AND g.activated_at IS NOT NULL AND g.revoked_at IS NULL AND g.expires_at > @now)";
        await using var cmd = new NpgsqlCommand($"""
            SELECT {ClientColumns}, {live}
            FROM prem_config.oauth_client c
            WHERE c.tenant_id = @tenant AND c.deleted_at IS NULL
            ORDER BY c.created_at DESC, c.id
            """, conn);
        cmd.Parameters.AddWithValue("tenant", tenantId);
        cmd.Parameters.AddWithValue("now", now);
        if (oauth is not null) cmd.Parameters.AddWithValue("oauth_resource", oauth.Resource);
        var rows = new List<(OAuthClientRow, int)>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) rows.Add((ReadClient(reader), checked((int)reader.GetInt64(16))));
        return rows;
    }

    private const string GrantSelect = """
        SELECT g.id, g.client_id, g.user_id, g.agent_id, g.user_generation, g.agent_generation, g.resource,
               g.created_at, g.activated_at, g.expires_at, g.revoked_at, g.revoked_reason, g.last_refreshed_at, g.refresh_count,
               c.name, c.registered_by, c.disabled, c.deleted_at IS NOT NULL,
               u.sign_in_name, u.deleted_at IS NOT NULL, u.disabled, u.credential_generation,
               a.name, a.deleted_at IS NOT NULL, a.disabled, a.credential_generation,
               EXISTS (SELECT 1 FROM prem_config.oauth_code k WHERE k.grant_id = g.id AND k.used_at IS NULL AND k.expires_at > @now)
        FROM prem_config.oauth_grant g
        JOIN prem_config.oauth_client c ON c.tenant_id = g.tenant_id AND c.id = g.client_id
        JOIN prem_config.app_user u ON u.tenant_id = g.tenant_id AND u.id = g.user_id
        JOIN prem_config.agent a ON a.tenant_id = g.tenant_id AND a.id = g.agent_id
        """;

    public static async Task<OAuthGrantRow?> FindGrantAsync(
        NpgsqlConnection conn, NpgsqlTransaction? tx, Guid tenantId, string grantId, DateTimeOffset now, CancellationToken ct) =>
        (await ListGrantsAsync(conn, tx, tenantId, "g.id = @grant", now, ct, ("grant", grantId))).SingleOrDefault();

    public static async Task<IReadOnlyList<OAuthGrantRow>> ListGrantsAsync(
        NpgsqlConnection conn, NpgsqlTransaction? tx, Guid tenantId, string where, DateTimeOffset now, CancellationToken ct,
        params (string Name, object Value)[] parameters)
    {
        await using var cmd = new NpgsqlCommand($"{GrantSelect} WHERE g.tenant_id = @tenant AND {where} ORDER BY g.created_at DESC, g.id", conn, tx);
        cmd.Parameters.AddWithValue("tenant", tenantId);
        cmd.Parameters.AddWithValue("now", now);
        foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value);
        var rows = new List<OAuthGrantRow>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            rows.Add(new OAuthGrantRow(
                reader.GetString(0), reader.GetString(1), reader.GetGuid(2), reader.GetGuid(3), reader.GetInt64(4), reader.GetInt64(5),
                reader.GetString(6), reader.GetFieldValue<DateTimeOffset>(7), NullableTime(reader, 8), reader.GetFieldValue<DateTimeOffset>(9),
                NullableTime(reader, 10), reader.IsDBNull(11) ? null : reader.GetString(11), NullableTime(reader, 12), reader.GetInt32(13),
                reader.GetString(14), reader.GetString(15), reader.GetBoolean(16), reader.GetBoolean(17),
                reader.GetString(18), reader.GetBoolean(19), reader.GetBoolean(20), reader.GetInt64(21),
                reader.GetString(22), reader.GetBoolean(23), reader.GetBoolean(24), reader.GetInt64(25),
                reader.GetBoolean(26)));
        return rows;
    }

    /// <summary>
    /// Why a grant that has not been revoked no longer stands, in the words the
    /// change record keeps, or null when nothing but its own time is against it.
    /// The same conditions as <see cref="CredentialSql.GrantStands"/>, which is
    /// the guard; this names them.
    /// </summary>
    /// <param name="resource">The current resource, or null when the flow is off and the address cannot be judged.</param>
    public static string? EndReason(OAuthGrantRow g, string? resource)
    {
        if (g.OwnerGone || g.OwnerDisabled) return OwnerDisabled;
        if (g.AgentGone || g.AgentDisabled || g.AgentGenerationNow != g.AgentGeneration) return AgentDisabled;
        if (g.OwnerGeneration != g.UserGeneration) return OwnerDisabledOrPasswordChanged;
        if (g.ClientRemoved) return ClientRemovedReason;
        if (g.ClientDisabled) return ClientDisabledReason;
        if (resource is not null && !string.Equals(g.Resource, resource, StringComparison.Ordinal)) return AddressChanged;
        return null;
    }

    /// <summary>Where a grant stands, by the rules <see cref="OAuthGrantStatus"/> states.</summary>
    public static OAuthGrantStatus Status(OAuthGrantRow g, DateTimeOffset now, string? resource)
    {
        if (g.RevokedAt is not null) return StatusOfReason(g.RevokedReason);
        if (EndReason(g, resource) is { } reason) return StatusOfReason(reason);
        if (g.ActivatedAt is null) return g.HasClaimableCode && g.ExpiresAt > now ? OAuthGrantStatus.Pending : OAuthGrantStatus.Expired;
        return g.ExpiresAt > now ? OAuthGrantStatus.Live : OAuthGrantStatus.Expired;
    }

    private static OAuthGrantStatus StatusOfReason(string? reason) => reason switch
    {
        OwnerDisabled or AgentDisabled or OwnerDisabledOrPasswordChanged => OAuthGrantStatus.EndedByDisable,
        ClientDisabledReason or ClientRemovedReason => OAuthGrantStatus.EndedByClient,
        AddressChanged => OAuthGrantStatus.EndedByAddressChange,
        _ => OAuthGrantStatus.Revoked,
    };

    public static OAuthGrantView View(OAuthGrantRow g, DateTimeOffset now, string? resource) => new(
        g.Id, g.ClientId, g.ClientName,
        g.ClientRegisteredBy == "dynamic" ? OAuthClientRegistration.Dynamic : OAuthClientRegistration.Administrator,
        g.UserId, g.UserName, g.AgentId, g.AgentName, Status(g, now, resource), g.CreatedAt, g.ExpiresAt,
        g.LastRefreshedAt, g.RefreshCount, g.RevokedAt, g.RevokedReason);

    /// <summary>
    /// Revokes a grant with its reason, in the change's transaction, and writes
    /// its row. With <paramref name="disableAgent"/>, its agent is disabled
    /// too, with its own row. A grant already revoked is left as it is.
    /// </summary>
    /// <returns>True when this call revoked it.</returns>
    public static async Task<bool> RevokeAsync(AdminChange change, Guid tenantId, OAuthGrantRow grant, string reason, bool disableAgent,
        DateTimeOffset now, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("""
            UPDATE prem_config.oauth_grant SET revoked_at = @now, revoked_reason = @reason
            WHERE tenant_id = @tenant AND id = @id AND revoked_at IS NULL
            """, change.Transaction.Connection, change.Transaction);
        cmd.Parameters.AddWithValue("tenant", tenantId);
        cmd.Parameters.AddWithValue("id", grant.Id);
        cmd.Parameters.AddWithValue("now", now);
        cmd.Parameters.AddWithValue("reason", reason);
        if (await cmd.ExecuteNonQueryAsync(ct) != 1) return false;

        change.Record("oauth.grant.revoke", grant.ClientId, null,
            new { grant_id = grant.Id, user = grant.UserName, agent = grant.AgentName, reason });
        if (disableAgent && await change.Identity.SetAgentDisabledAsync(grant.AgentId, true, ct))
            change.Record("agent.disable", grant.AgentName, new { disabled = false }, new { disabled = true });
        return true;
    }

    /// <summary>
    /// The first request that meets a grant that no longer stands marks it
    /// revoked with the reason, as bookkeeping. The guard is the predicate; a
    /// grant that is merely past its time is left unmarked.
    /// </summary>
    public static async Task MarkEndedAsync(PremagenticDatabase db, Guid tenantId, TimeProvider time, OAuthGrantRow grant, string? resource,
        CancellationToken ct)
    {
        if (grant.RevokedAt is not null || EndReason(grant, resource) is not { } reason) return;
        await new AdminChanges(db, tenantId, time).RunAsync(FlowActor,
            change => RevokeAsync(change, tenantId, grant, reason, disableAgent: false, time.GetUtcNow(), ct), ct);
    }

    /// <summary>
    /// Removes what has expired: codes and tokens past their expires_at, never
    /// sooner, so a used refresh token and its replaced_by link outlive a
    /// thief's rotation. Grants are never removed.
    /// </summary>
    public static async Task SweepAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, Guid tenantId, DateTimeOffset now, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("""
            DELETE FROM prem_config.oauth_code WHERE tenant_id = @tenant AND expires_at <= @now;
            DELETE FROM prem_config.oauth_access_token WHERE tenant_id = @tenant AND expires_at <= @now;
            DELETE FROM prem_config.oauth_refresh_token WHERE tenant_id = @tenant AND expires_at <= @now;
            """, conn, tx);
        cmd.Parameters.AddWithValue("tenant", tenantId);
        cmd.Parameters.AddWithValue("now", now);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static OAuthClientRow ReadClient(NpgsqlDataReader r) => new(
        r.GetString(0), r.GetString(1), r.GetFieldValue<string[]>(2), r.IsDBNull(3) ? null : r.GetString(3),
        r.IsDBNull(4) ? null : r.GetString(4), r.IsDBNull(5) ? null : r.GetString(5), r.GetString(6),
        r.IsDBNull(7) ? null : r.GetString(7), r.IsDBNull(8) ? null : r.GetString(8), r.IsDBNull(9) ? null : r.GetString(9),
        r.GetFieldValue<DateTimeOffset>(10), NullableTime(r, 11), r.GetBoolean(12), NullableTime(r, 13),
        r.IsDBNull(14) ? null : r.GetString(14), NullableTime(r, 15));

    private static DateTimeOffset? NullableTime(NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetFieldValue<DateTimeOffset>(i);
}

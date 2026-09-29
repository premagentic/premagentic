using Npgsql;
using Premagentic.Core.Storage;

namespace Premagentic.Core.Identity;

/// <summary>
/// The rules that decide whether a person's assistant credential still stands,
/// as SQL, written once. The token read, the connect page's states, the bound
/// and the authorization flow all use these, so no two of them can disagree.
/// </summary>
internal static class CredentialSql
{
    /// <summary>
    /// A token that is still current: unstamped, or stamped with the owner's
    /// and the agent's credential generations as they are now. Over the token
    /// <paramref name="t"/>, its agent <paramref name="a"/> and the agent's
    /// owner <paramref name="u"/>.
    /// </summary>
    public static string TokenCurrent(string t, string a, string u) =>
        $"({t}.user_generation IS NULL OR ({t}.user_generation = {u}.credential_generation AND {t}.agent_generation = {a}.credential_generation))";

    /// <summary>
    /// A person's own agent <paramref name="a"/> holds a slot: it holds a token
    /// that is unrevoked, unexpired at <c>@now</c>, and current. The caller
    /// checks that the agent itself is live and enabled.
    /// </summary>
    public static string SelfAgentHoldsSlot(string a) => $"""
        EXISTS (SELECT 1 FROM prem_config.agent_token st
                JOIN prem_config.app_user su ON su.tenant_id = {a}.tenant_id AND su.id = {a}.owner_user_id
                WHERE st.tenant_id = {a}.tenant_id AND st.agent_id = {a}.id
                  AND st.revoked_at IS NULL AND st.expires_at > @now
                  AND {TokenCurrent("st", a, "su")})
        """;

    /// <summary>
    /// A person's own agent <paramref name="a"/> holds an unrevoked, unexpired
    /// token that is not current: ended by a disable or a password change.
    /// </summary>
    public static string SelfAgentHasEndedToken(string a) => $"""
        EXISTS (SELECT 1 FROM prem_config.agent_token et
                JOIN prem_config.app_user eu ON eu.tenant_id = {a}.tenant_id AND eu.id = {a}.owner_user_id
                WHERE et.tenant_id = {a}.tenant_id AND et.agent_id = {a}.id
                  AND et.revoked_at IS NULL AND et.expires_at > @now
                  AND NOT {TokenCurrent("et", a, "eu")})
        """;

    /// <summary>
    /// A grant <paramref name="g"/> still stands, whether or not its code has
    /// been exchanged: unrevoked, unexpired at <c>@now</c>, bound to the
    /// current resource <c>@oauth_resource</c>, its client neither disabled
    /// nor removed, its agent and owner live and enabled, and both captured
    /// generations equal to the current ones.
    /// </summary>
    public static string GrantStands(string g) => $"""
        ({g}.revoked_at IS NULL
         AND {g}.expires_at > @now
         AND {g}.resource = @oauth_resource
         AND EXISTS (SELECT 1 FROM prem_config.oauth_client gc
                     WHERE gc.tenant_id = {g}.tenant_id AND gc.id = {g}.client_id
                       AND NOT gc.disabled AND gc.deleted_at IS NULL)
         AND EXISTS (SELECT 1 FROM prem_config.agent ga
                     JOIN prem_config.app_user gu ON gu.tenant_id = ga.tenant_id AND gu.id = ga.owner_user_id
                     WHERE ga.tenant_id = {g}.tenant_id AND ga.id = {g}.agent_id AND gu.id = {g}.user_id
                       AND ga.deleted_at IS NULL AND NOT ga.disabled
                       AND gu.deleted_at IS NULL AND NOT gu.disabled
                       AND ga.credential_generation = {g}.agent_generation
                       AND gu.credential_generation = {g}.user_generation))
        """;

    /// <summary>A grant <paramref name="g"/> is live: it stands, and its first code was exchanged.</summary>
    public static string GrantLive(string g) => $"({GrantStands(g)} AND {g}.activated_at IS NOT NULL)";

    /// <summary>
    /// A grant <paramref name="g"/> holds a slot in the bound: it is live, or
    /// it stands pending with a code that can still be exchanged.
    /// </summary>
    public static string GrantHoldsSlot(string g) => $"""
        ({GrantStands(g)}
         AND ({g}.activated_at IS NOT NULL
              OR EXISTS (SELECT 1 FROM prem_config.oauth_code gk
                         WHERE gk.grant_id = {g}.id AND gk.used_at IS NULL AND gk.expires_at > @now)))
        """;
}

/// <summary>
/// How many assistants a person has connected, by the one count the connect
/// form, the approval and the consent view share: the person's own agents
/// (<c>self:</c>) that are enabled and hold a current, usable token, plus,
/// only while the authorization flow is on, the agents of the person's grants
/// that are live or pending with a code that can still be exchanged. While the
/// flow is off, no <c>oauth_*</c> table is read.
/// </summary>
internal static class ConnectedAssistants
{
    public static async Task<int> CountAsync(
        NpgsqlConnection conn, NpgsqlTransaction? tx, Guid tenantId, Guid ownerId, DateTimeOffset now,
        OAuthDeployment? oauth, CancellationToken ct)
    {
        var sql = $"""
            SELECT (SELECT count(*) FROM prem_config.agent a
                    WHERE a.tenant_id = @tenant AND a.owner_user_id = @owner AND a.created_by = @self
                      AND a.deleted_at IS NULL AND NOT a.disabled
                      AND {CredentialSql.SelfAgentHoldsSlot("a")})
            """;
        if (oauth is not null)
            sql += $"""

                 + (SELECT count(DISTINCT g.agent_id) FROM prem_config.oauth_grant g
                    WHERE g.tenant_id = @tenant AND g.user_id = @owner AND {CredentialSql.GrantHoldsSlot("g")})
                """;

        await using var cmd = new NpgsqlCommand(sql, conn, tx);
        cmd.Parameters.AddWithValue("tenant", tenantId);
        cmd.Parameters.AddWithValue("owner", ownerId);
        cmd.Parameters.AddWithValue("self", AgentOrigin.Self(ownerId).Text);
        cmd.Parameters.AddWithValue("now", now);
        if (oauth is not null) cmd.Parameters.AddWithValue("oauth_resource", oauth.Resource);
        return checked((int)(long)(await cmd.ExecuteScalarAsync(ct))!);
    }

    public static async Task<int> CountAsync(
        PremagenticDatabase db, Guid tenantId, Guid ownerId, DateTimeOffset now, OAuthDeployment? oauth, CancellationToken ct)
    {
        await using var conn = await db.DataSource.OpenConnectionAsync(ct);
        return await CountAsync(conn, null, tenantId, ownerId, now, oauth, ct);
    }
}

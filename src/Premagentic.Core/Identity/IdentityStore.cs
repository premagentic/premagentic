using Premagentic.Core.Storage;
using Npgsql;

namespace Premagentic.Core.Identity;

/// <summary>A user as an administrator lists them.</summary>
public sealed record UserAccount(
    Guid Id, string SignInName, string DisplayName, Role Role, bool Disabled, bool HasPassword, DateTimeOffset CreatedAt);

/// <summary>
/// Users, groups, agents and agent tokens for one tenant.
/// <para>
/// The lookups a <see cref="CallerResolver"/> makes read and never write, and
/// see live rows only, so a deleted user, group or agent resolves as unknown.
/// Ids come from the database once and are never reused: a delete keeps the row
/// with a tombstone. Names compare case-insensitively among live rows.
/// </para>
/// </summary>
/// <param name="transaction">
/// A transaction the caller owns, for a change that must commit together with
/// something else, such as its entry in the change record. Every command then
/// runs on its connection, inside it, and nothing here begins, commits or rolls
/// back. Null runs each call on its own, as before.
/// </param>
public sealed class IdentityStore(
    PremagenticDatabase db, Guid tenantId, TimeProvider? time = null, NpgsqlTransaction? transaction = null) : IIdentityDirectory
{
    private const string UniqueViolation = "23505";

    internal TimeProvider Time { get; } = time ?? TimeProvider.System;

    public Guid TenantId => tenantId;

    // --- The directory: reads only ---

    public async Task<User?> FindUserAsync(Guid userId, CancellationToken ct = default)
    {
        await using var cmd = Command("""
            SELECT id, sign_in_name, role, disabled FROM prem_config.app_user
            WHERE tenant_id = @tenant AND id = @id AND deleted_at IS NULL
            """);
        cmd.Parameters.AddWithValue("id", userId);
        return (await ReadAsync(cmd, ReadUser, ct)).SingleOrDefault();
    }

    public async Task<IReadOnlyList<Group>> GroupsOfUserAsync(Guid userId, CancellationToken ct = default)
    {
        await using var cmd = Command($"""
            SELECT {GroupColumns} FROM prem_config.group_membership m
            JOIN prem_config.app_group g ON g.id = m.group_id
            WHERE m.tenant_id = @tenant AND m.user_id = @user AND g.deleted_at IS NULL
            ORDER BY g.name
            """);
        cmd.Parameters.AddWithValue("user", userId);
        return await ReadAsync(cmd, ReadGroup, ct);
    }

    public async Task<Agent?> FindAgentAsync(Guid agentId, CancellationToken ct = default)
    {
        await using var cmd = Command($"""
            SELECT {AgentColumns} FROM prem_config.agent
            WHERE tenant_id = @tenant AND id = @id AND deleted_at IS NULL
            """);
        cmd.Parameters.AddWithValue("id", agentId);
        return (await ReadAsync(cmd, ReadAgent, ct)).SingleOrDefault();
    }

    /// <summary>
    /// The groups an administrator granted, which a service agent holds. System
    /// groups are left out on purpose: they are not something an agent holds,
    /// they are something weighed against it, and
    /// <see cref="SystemGroupsOfAgentAsync"/> reads those.
    /// </summary>
    public async Task<IReadOnlyList<Group>> GroupsGrantedToAgentAsync(Guid agentId, CancellationToken ct = default)
    {
        await using var cmd = Command($"""
            SELECT {GroupColumns} FROM prem_config.agent_group_grant a
            JOIN prem_config.app_group g ON g.id = a.group_id
            WHERE a.tenant_id = @tenant AND a.agent_id = @agent AND g.deleted_at IS NULL
              AND g.system_key IS NULL
            ORDER BY g.name
            """);
        cmd.Parameters.AddWithValue("agent", agentId);
        return await ReadAsync(cmd, ReadGroup, ct);
    }

    /// <summary>
    /// The system groups this agent is in, read from the membership rows rather
    /// than from the agent's attributes, so that what the gate weighs is the
    /// same thing an administrator sees listed under the group.
    /// </summary>
    public async Task<IReadOnlyList<Group>> SystemGroupsOfAgentAsync(Guid agentId, CancellationToken ct = default)
    {
        await using var cmd = Command($"""
            SELECT {GroupColumns} FROM prem_config.agent_group_grant a
            JOIN prem_config.app_group g ON g.id = a.group_id
            WHERE a.tenant_id = @tenant AND a.agent_id = @agent AND g.deleted_at IS NULL
              AND g.system_key IS NOT NULL
            ORDER BY g.name
            """);
        cmd.Parameters.AddWithValue("agent", agentId);
        return await ReadAsync(cmd, ReadGroup, ct);
    }

    /// <summary>
    /// The live agents a system group holds, which is what its membership means:
    /// a system group holds agents, never people. For the portal and
    /// <c>prem groups list</c>.
    /// </summary>
    public async Task<IReadOnlyList<Agent>> AgentsInSystemGroupAsync(Guid groupId, CancellationToken ct = default)
    {
        await using var cmd = Command($"""
            SELECT {AgentColumns} FROM prem_config.agent a
            JOIN prem_config.agent_group_grant m ON m.agent_id = a.id
            WHERE a.tenant_id = @tenant AND m.tenant_id = @tenant AND m.group_id = @group AND a.deleted_at IS NULL
            ORDER BY lower(a.name)
            """);
        cmd.Parameters.AddWithValue("group", groupId);
        return await ReadAsync(cmd, ReadAgent, ct);
    }

    /// <summary>
    /// A token by its id, with whether it is superseded: see
    /// <see cref="AgentTokenRecord.Superseded"/>. Read and never written.
    /// </summary>
    public async Task<AgentTokenRecord?> FindTokenAsync(string tokenId, CancellationToken ct = default)
    {
        await using var cmd = Command($"{TokenSelect} WHERE t.tenant_id = @tenant AND t.id = @id");
        cmd.Parameters.AddWithValue("id", tokenId);
        return (await ReadAsync(cmd, ReadToken, ct)).SingleOrDefault();
    }

    // --- Users ---

    public async Task<User> CreateUserAsync(string signInName, string displayName, Role role, CancellationToken ct = default)
    {
        signInName = IdentityNames.Require(signInName, "sign-in name");
        displayName = IdentityNames.Require(displayName, "display name");
        RequireDefined(role);

        await using var cmd = Command("""
            INSERT INTO prem_config.app_user(tenant_id, sign_in_name, display_name, role)
            VALUES(@tenant, @name, @display, @role)
            RETURNING id, sign_in_name, role, disabled
            """);
        cmd.Parameters.AddWithValue("name", signInName);
        cmd.Parameters.AddWithValue("display", displayName);
        cmd.Parameters.AddWithValue("role", RoleText(role));
        return await UniqueAsync(() => ReadOneAsync(cmd, ReadUser, ct), $"A user signs in as '{signInName}' already.");
    }

    /// <summary>The live user with this sign-in name, compared case-insensitively.</summary>
    public async Task<User?> FindUserByNameAsync(string signInName, CancellationToken ct = default)
    {
        await using var cmd = Command("""
            SELECT id, sign_in_name, role, disabled FROM prem_config.app_user
            WHERE tenant_id = @tenant AND lower(sign_in_name) = lower(@name) AND deleted_at IS NULL
            """);
        cmd.Parameters.AddWithValue("name", signInName);
        return (await ReadAsync(cmd, ReadUser, ct)).SingleOrDefault();
    }

    public async Task<IReadOnlyList<UserAccount>> ListUsersAsync(CancellationToken ct = default)
    {
        await using var cmd = Command("""
            SELECT id, sign_in_name, display_name, role, disabled, password_hash IS NOT NULL, created_at
            FROM prem_config.app_user WHERE tenant_id = @tenant AND deleted_at IS NULL
            ORDER BY lower(sign_in_name)
            """);
        return await ReadAsync(cmd, r => new UserAccount(
            r.GetGuid(0), r.GetString(1), r.GetString(2), ParseRole(r.GetString(3)), r.GetBoolean(4), r.GetBoolean(5),
            r.GetFieldValue<DateTimeOffset>(6)), ct);
    }

    /// <summary>
    /// Disables or enables a user. Disabling also ends every session the user
    /// holds, in the same statement, so enabling the user again later does not
    /// bring an old session back. For the same reason a disable moves the
    /// user's credential generation, which ends every assistant credential the
    /// person made for themself: their connect-page agents' tokens and their
    /// authorization grants stay dead after a re-enable.
    /// </summary>
    /// <returns>False when there is no live user with this id.</returns>
    public async Task<bool> SetUserDisabledAsync(Guid userId, bool disabled, CancellationToken ct = default)
    {
        await using var cmd = Command("""
            WITH changed AS (
                UPDATE prem_config.app_user
                SET disabled = @value,
                    credential_generation = credential_generation + CASE WHEN @value THEN 1 ELSE 0 END
                WHERE tenant_id = @tenant AND id = @id AND deleted_at IS NULL
                RETURNING id),
            ended AS (
                DELETE FROM prem_config.user_session s USING changed
                WHERE @value AND s.tenant_id = @tenant AND s.user_id = changed.id)
            SELECT count(*) FROM changed
            """);
        cmd.Parameters.AddWithValue("id", userId);
        cmd.Parameters.AddWithValue("value", disabled);
        return (long)(await cmd.ExecuteScalarAsync(ct))! > 0;
    }

    /// <summary>
    /// Stores a hash made by <see cref="PasswordHasher"/>. The password itself
    /// never reaches this class. A new password ends every session the user
    /// holds, in the same statement, and moves the user's credential
    /// generation, which ends every assistant credential the person made for
    /// themself. A first password does the same, which ends the credentials
    /// of a person who signed in another way until now. The sign-in rehash
    /// (<see cref="RehashPasswordAsync"/>) is not a new password and ends
    /// nothing.
    /// </summary>
    /// <returns>False when there is no live user with this id.</returns>
    public async Task<bool> SetPasswordHashAsync(Guid userId, string passwordHash, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(passwordHash) || !passwordHash.StartsWith("$pbkdf2-", StringComparison.Ordinal))
            throw new ArgumentException("Expected an encoded hash from PasswordHasher.", nameof(passwordHash));

        await using var cmd = Command("""
            WITH changed AS (
                UPDATE prem_config.app_user
                SET password_hash = @value, credential_generation = credential_generation + 1
                WHERE tenant_id = @tenant AND id = @id AND deleted_at IS NULL
                RETURNING id),
            ended AS (
                DELETE FROM prem_config.user_session s USING changed
                WHERE s.tenant_id = @tenant AND s.user_id = changed.id)
            SELECT count(*) FROM changed
            """);
        cmd.Parameters.AddWithValue("id", userId);
        cmd.Parameters.AddWithValue("value", passwordHash);
        return (long)(await cmd.ExecuteScalarAsync(ct))! > 0;
    }

    // --- Sign-in ---

    /// <summary>The live account a sign-in name belongs to, with what sign-in needs to judge an attempt.</summary>
    internal async Task<SignInAccount?> FindSignInAccountAsync(string signInName, CancellationToken ct = default)
    {
        await using var cmd = Command("""
            SELECT id, sign_in_name, role, disabled, password_hash, failed_sign_ins, locked_until
            FROM prem_config.app_user
            WHERE tenant_id = @tenant AND lower(sign_in_name) = lower(@name) AND deleted_at IS NULL
            """);
        cmd.Parameters.AddWithValue("name", signInName);
        return (await ReadAsync(cmd, r => new SignInAccount(
            ReadUser(r),
            r.IsDBNull(4) ? null : r.GetString(4),
            r.GetInt32(5),
            r.IsDBNull(6) ? null : r.GetFieldValue<DateTimeOffset>(6)), ct)).SingleOrDefault();
    }

    /// <summary>
    /// Counts one wrong password. From the <paramref name="lockout"/>'s count on,
    /// every further failure locks the account until its lock time has passed.
    /// </summary>
    internal async Task RecordFailedSignInAsync(Guid userId, LockoutPolicy lockout, CancellationToken ct = default)
    {
        await using var cmd = Command("""
            UPDATE prem_config.app_user
            SET failed_sign_ins = LEAST(failed_sign_ins + 1, 1000000),
                locked_until = CASE WHEN failed_sign_ins + 1 >= @lockAfter THEN @until ELSE locked_until END
            WHERE tenant_id = @tenant AND id = @id AND deleted_at IS NULL
            """);
        cmd.Parameters.AddWithValue("id", userId);
        cmd.Parameters.AddWithValue("lockAfter", lockout.LockAfterFailures);
        cmd.Parameters.AddWithValue("until", Time.GetUtcNow() + lockout.LockFor);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>A successful sign-in clears the failure count and any lock.</summary>
    internal async Task RecordSignInAsync(Guid userId, CancellationToken ct = default)
    {
        await using var cmd = Command("""
            UPDATE prem_config.app_user SET failed_sign_ins = 0, locked_until = NULL
            WHERE tenant_id = @tenant AND id = @id AND deleted_at IS NULL
            """);
        cmd.Parameters.AddWithValue("id", userId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Replaces a hash with a stronger one made from the same password, only if
    /// the stored hash is still the one that was verified. Not a new password, so
    /// no session ends.
    /// </summary>
    internal async Task<bool> RehashPasswordAsync(Guid userId, string verifiedHash, string newHash, CancellationToken ct = default)
    {
        await using var cmd = Command("""
            UPDATE prem_config.app_user SET password_hash = @new
            WHERE tenant_id = @tenant AND id = @id AND deleted_at IS NULL AND password_hash = @old
            """);
        cmd.Parameters.AddWithValue("id", userId);
        cmd.Parameters.AddWithValue("old", verifiedHash);
        cmd.Parameters.AddWithValue("new", newHash);
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    /// <summary>
    /// Changes a user's role. Refuses to take the administrator role from the
    /// last live, enabled administrator, which would leave nobody able to
    /// administer the deployment.
    /// </summary>
    /// <returns>False when there is no live user with this id.</returns>
    /// <exception cref="InvalidOperationException">The change would leave no administrator.</exception>
    public async Task<bool> SetUserRoleAsync(Guid userId, Role role, CancellationToken ct = default)
    {
        RequireDefined(role);
        // The administrators are locked first, so two demotions at once cannot
        // each count the other and leave none.
        await using var cmd = Command("""
            WITH admins AS (
                SELECT id FROM prem_config.app_user
                WHERE tenant_id = @tenant AND role = 'administrator' AND NOT disabled AND deleted_at IS NULL
                FOR UPDATE),
            changed AS (
                UPDATE prem_config.app_user u SET role = @role
                WHERE u.tenant_id = @tenant AND u.id = @id AND u.deleted_at IS NULL
                  AND (@role = 'administrator' OR u.role <> 'administrator' OR u.disabled
                       OR EXISTS (SELECT 1 FROM admins a WHERE a.id <> @id))
                RETURNING u.id)
            SELECT (SELECT count(*) FROM changed),
                   EXISTS (SELECT 1 FROM prem_config.app_user WHERE tenant_id = @tenant AND id = @id AND deleted_at IS NULL)
            """);
        cmd.Parameters.AddWithValue("id", userId);
        cmd.Parameters.AddWithValue("role", RoleText(role));
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        if (reader.GetInt64(0) > 0) return true;
        if (!reader.GetBoolean(1)) return false;
        throw new InvalidOperationException("That would leave no enabled administrator. Make another user an administrator first.");
    }

    // --- Groups ---

    public async Task<Group> CreateGroupAsync(string name, CancellationToken ct = default)
    {
        name = IdentityNames.Require(name, "group name");
        await using var cmd = Command(
            "INSERT INTO prem_config.app_group(tenant_id, name) VALUES(@tenant, @name) RETURNING id, name, system_key");
        cmd.Parameters.AddWithValue("name", name);
        return await UniqueAsync(() => ReadOneAsync(cmd, ReadGroup, ct), $"A group named '{name}' exists already.");
    }

    public async Task<Group?> FindGroupByNameAsync(string name, CancellationToken ct = default)
    {
        await using var cmd = Command($"""
            SELECT {GroupColumns} FROM prem_config.app_group g
            WHERE g.tenant_id = @tenant AND lower(g.name) = lower(@name) AND g.deleted_at IS NULL
            """);
        cmd.Parameters.AddWithValue("name", name);
        return (await ReadAsync(cmd, ReadGroup, ct)).SingleOrDefault();
    }

    /// <summary>
    /// A group the product maintains, by its key: see <see cref="SystemGroups"/>.
    /// The id is per tenant, so this lookup is how anything else reaches one.
    /// </summary>
    public async Task<Group?> FindSystemGroupAsync(string systemKey, CancellationToken ct = default)
    {
        await using var cmd = Command($"""
            SELECT {GroupColumns} FROM prem_config.app_group g
            WHERE g.tenant_id = @tenant AND g.system_key = @key AND g.deleted_at IS NULL
            """);
        cmd.Parameters.AddWithValue("key", systemKey);
        return (await ReadAsync(cmd, ReadGroup, ct)).SingleOrDefault();
    }

    /// <summary>A group by id, live or, when asked, deleted: a rule may still name a deleted group.</summary>
    public async Task<(Group Group, bool Deleted)?> FindGroupAsync(Guid groupId, CancellationToken ct = default)
    {
        await using var cmd = Command(
            $"SELECT {GroupColumns}, g.deleted_at IS NOT NULL FROM prem_config.app_group g WHERE g.tenant_id = @tenant AND g.id = @id");
        cmd.Parameters.AddWithValue("id", groupId);
        var rows = await ReadAsync(cmd, r => (ReadGroup(r), r.GetBoolean(3)), ct);
        return rows.Count == 0 ? null : rows[0];
    }

    public async Task<IReadOnlyList<Group>> ListGroupsAsync(CancellationToken ct = default)
    {
        await using var cmd = Command(
            $"SELECT {GroupColumns} FROM prem_config.app_group g WHERE g.tenant_id = @tenant AND g.deleted_at IS NULL ORDER BY lower(g.name)");
        return await ReadAsync(cmd, ReadGroup, ct);
    }

    /// <summary>Renames a group. Rules name the group's id, so what they mean does not change.</summary>
    /// <returns>False when there is no live group with this id.</returns>
    /// <exception cref="InvalidOperationException">The group is one the product maintains.</exception>
    public async Task<bool> RenameGroupAsync(Guid groupId, string newName, CancellationToken ct = default)
    {
        newName = IdentityNames.Require(newName, "group name");
        await RefuseSystemGroupAsync(groupId, "renamed", ct);
        return await UniqueAsync(
            () => UpdateLiveAsync("app_group", "name = @value", groupId, newName, ct),
            $"A group named '{newName}' exists already.");
    }

    /// <summary>
    /// Deletes a group: the row stays with a tombstone so its id is never reused,
    /// and its memberships and agent grants go. A rule that names it then matches
    /// nobody, and a new group that takes its name gets a new id.
    /// </summary>
    /// <returns>False when there is no live group with this id.</returns>
    /// <exception cref="InvalidOperationException">The group is one the product maintains.</exception>
    public async Task<bool> DeleteGroupAsync(Guid groupId, CancellationToken ct = default)
    {
        await RefuseSystemGroupAsync(groupId, "deleted", ct);
        if (transaction is not null) return await DeleteGroupAsync(transaction.Connection!, transaction, groupId, ct);

        await using var conn = await db.DataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var deleted = await DeleteGroupAsync(conn, tx, groupId, ct);
        if (deleted) await tx.CommitAsync(ct);
        return deleted;
    }

    private async Task<bool> DeleteGroupAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid groupId, CancellationToken ct)
    {
        await using (var tomb = new NpgsqlCommand(
            "UPDATE prem_config.app_group SET deleted_at = now() WHERE tenant_id = @tenant AND id = @id AND deleted_at IS NULL", conn, tx))
        {
            tomb.Parameters.AddWithValue("tenant", tenantId);
            tomb.Parameters.AddWithValue("id", groupId);
            if (await tomb.ExecuteNonQueryAsync(ct) == 0) return false;
        }

        // Every way into the group goes with it: the people in it, the agents
        // granted it, and what an outside directory's principals meant by it.
        // A rule that names the group then matches nobody, which is what a
        // deleted group has always meant here.
        foreach (var table in new[] { "group_membership", "agent_group_grant", "identity_mapping" })
        {
            await using var drop = new NpgsqlCommand(
                $"DELETE FROM prem_config.{table} WHERE tenant_id = @tenant AND group_id = @id", conn, tx);
            drop.Parameters.AddWithValue("tenant", tenantId);
            drop.Parameters.AddWithValue("id", groupId);
            await drop.ExecuteNonQueryAsync(ct);
        }
        return true;
    }

    /// <returns>True when the membership was added; false when it existed or either side is not live.</returns>
    /// <exception cref="InvalidOperationException">The group is one the product maintains.</exception>
    public async Task<bool> AddMemberAsync(Guid groupId, Guid userId, CancellationToken ct = default)
    {
        await RefuseSystemGroupAsync(groupId, "added to", ct);
        // The statement refuses it too, so a race between the check and the
        // write cannot put a person in a group the gate reads as an agent's.
        await using var cmd = Command("""
            INSERT INTO prem_config.group_membership(tenant_id, group_id, user_id)
            SELECT @tenant, g.id, u.id
            FROM prem_config.app_group g, prem_config.app_user u
            WHERE g.tenant_id = @tenant AND g.id = @group AND g.deleted_at IS NULL AND g.system_key IS NULL
              AND u.tenant_id = @tenant AND u.id = @user AND u.deleted_at IS NULL
            ON CONFLICT DO NOTHING
            """);
        cmd.Parameters.AddWithValue("group", groupId);
        cmd.Parameters.AddWithValue("user", userId);
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    public async Task<bool> RemoveMemberAsync(Guid groupId, Guid userId, CancellationToken ct = default)
    {
        await using var cmd = Command(
            "DELETE FROM prem_config.group_membership WHERE tenant_id = @tenant AND group_id = @group AND user_id = @user");
        cmd.Parameters.AddWithValue("group", groupId);
        cmd.Parameters.AddWithValue("user", userId);
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    public async Task<IReadOnlyList<User>> ListMembersAsync(Guid groupId, CancellationToken ct = default)
    {
        await using var cmd = Command("""
            SELECT u.id, u.sign_in_name, u.role, u.disabled FROM prem_config.group_membership m
            JOIN prem_config.app_user u ON u.id = m.user_id
            WHERE m.tenant_id = @tenant AND m.group_id = @group AND u.deleted_at IS NULL
            ORDER BY lower(u.sign_in_name)
            """);
        cmd.Parameters.AddWithValue("group", groupId);
        return await ReadAsync(cmd, ReadUser, ct);
    }

    // --- Agents ---

    /// <summary>
    /// Registers an agent owned by a live user. A hosted-model agent joins
    /// <see cref="SystemGroups.HostedModelAgents"/> in the same statement as the
    /// agent row, so the two can never be written apart.
    /// </summary>
    /// <param name="minimumTrustTier">Null follows the deployment's trust policy.</param>
    /// <param name="modelLocation">Where the model this agent speaks for runs. There is no default; see <see cref="Agent.ModelLocation"/>.</param>
    /// <param name="modelVendor">Who runs a hosted model. Refused for a local one, which has no vendor to name.</param>
    /// <param name="origin">Who is making the agent, stored as <c>created_by</c>; null stores <c>unknown</c>.</param>
    public async Task<Agent> CreateAgentAsync(
        string name, Guid ownerUserId, AgentMode mode, int requestsPerMinute, string? minimumTrustTier,
        ModelLocation modelLocation, string? modelVendor = null, AgentOrigin? origin = null, CancellationToken ct = default)
    {
        name = IdentityNames.Require(name, "agent name");
        RequireDefined(mode);
        if (requestsPerMinute <= 0)
            throw new ArgumentOutOfRangeException(nameof(requestsPerMinute), "A rate limit must be positive.");
        modelVendor = RequireModel(modelLocation, modelVendor);

        // One statement, so the agent row and the membership that follows from
        // it commit together or not at all.
        //
        // The group is read as a scalar, not joined. A join would write no
        // membership row at all if the group were somehow missing, and a hosted
        // agent in no group is exactly the agent a never-leaves rule would fail
        // to hold back. Read this way, a missing group is a null group_id, which
        // the column refuses, and the registration fails instead of half
        // happening.
        await using var cmd = Command($"""
            WITH created AS (
                INSERT INTO prem_config.agent(
                    tenant_id, name, owner_user_id, mode, requests_per_minute, minimum_trust_tier,
                    model_location, model_vendor, created_by)
                SELECT @tenant, @name, u.id, @mode, @rate, @tier, @model, @vendor, @createdBy
                FROM prem_config.app_user u
                WHERE u.tenant_id = @tenant AND u.id = @owner AND u.deleted_at IS NULL
                RETURNING {AgentColumns}),
            joined AS (
                INSERT INTO prem_config.agent_group_grant(tenant_id, agent_id, group_id)
                SELECT @tenant, c.id, {ReservedGroupId}
                FROM created c WHERE @model = 'hosted')
            SELECT {AgentColumns} FROM created
            """);
        cmd.Parameters.AddWithValue("name", name);
        cmd.Parameters.AddWithValue("owner", ownerUserId);
        cmd.Parameters.AddWithValue("mode", ModeText(mode));
        cmd.Parameters.AddWithValue("rate", requestsPerMinute);
        cmd.Parameters.AddWithValue("tier", (object?)minimumTrustTier ?? DBNull.Value);
        cmd.Parameters.AddWithValue("createdBy", (origin ?? AgentOrigin.Unknown).Text);
        AddModelParameters(cmd, modelLocation, modelVendor);
        var created = await UniqueAsync(() => ReadAsync(cmd, ReadAgent, ct), $"An agent named '{name}' exists already.");
        return created.SingleOrDefault()
            ?? throw new InvalidOperationException("The owner is not a live user of this tenant.");
    }

    /// <summary>
    /// Moves an agent between a local and a hosted model, and with it in or out
    /// of <see cref="SystemGroups.HostedModelAgents"/>, in one statement. The
    /// membership follows the attribute both ways; nothing else writes it.
    /// </summary>
    /// <returns>False when there is no live agent with this id.</returns>
    public async Task<bool> SetAgentModelAsync(
        Guid agentId, ModelLocation modelLocation, string? modelVendor = null, CancellationToken ct = default)
    {
        modelVendor = RequireModel(modelLocation, modelVendor);

        // The group is read as a scalar here too, so that moving an agent to a
        // hosted model fails rather than leaving it hosted and in no group. The
        // way out needs no such care: a delete that matches nothing has already
        // left the agent where it should be.
        await using var cmd = Command($"""
            WITH changed AS (
                UPDATE prem_config.agent SET model_location = @model, model_vendor = @vendor
                WHERE tenant_id = @tenant AND id = @id AND deleted_at IS NULL
                RETURNING id),
            joined AS (
                INSERT INTO prem_config.agent_group_grant(tenant_id, agent_id, group_id)
                SELECT @tenant, c.id, {ReservedGroupId}
                FROM changed c WHERE @model = 'hosted'
                ON CONFLICT DO NOTHING),
            parted AS (
                DELETE FROM prem_config.agent_group_grant a USING changed c, prem_config.app_group g
                WHERE @model <> 'hosted' AND a.tenant_id = @tenant AND a.agent_id = c.id AND a.group_id = g.id
                  AND g.tenant_id = @tenant AND g.system_key = @systemKey AND g.deleted_at IS NULL)
            SELECT count(*) FROM changed
            """);
        cmd.Parameters.AddWithValue("id", agentId);
        AddModelParameters(cmd, modelLocation, modelVendor);
        return (long)(await cmd.ExecuteScalarAsync(ct))! > 0;
    }

    public async Task<Agent?> FindAgentByNameAsync(string name, CancellationToken ct = default)
    {
        await using var cmd = Command($"""
            SELECT {AgentColumns} FROM prem_config.agent
            WHERE tenant_id = @tenant AND lower(name) = lower(@name) AND deleted_at IS NULL
            """);
        cmd.Parameters.AddWithValue("name", name);
        return (await ReadAsync(cmd, ReadAgent, ct)).SingleOrDefault();
    }

    public async Task<IReadOnlyList<Agent>> ListAgentsAsync(CancellationToken ct = default)
    {
        await using var cmd = Command(
            $"SELECT {AgentColumns} FROM prem_config.agent WHERE tenant_id = @tenant AND deleted_at IS NULL ORDER BY lower(name)");
        return await ReadAsync(cmd, ReadAgent, ct);
    }

    /// <summary>
    /// Disables or enables an agent. An agent already in that state is not
    /// written, so a caller that records a change on true records nothing for
    /// a change that changed nothing. A disable moves the agent's credential
    /// generation, so a token a person made for themself, and a grant, stay
    /// dead after the agent is enabled again.
    /// </summary>
    /// <returns>False when there is no live agent with this id, or it is already in that state.</returns>
    public async Task<bool> SetAgentDisabledAsync(Guid agentId, bool disabled, CancellationToken ct = default)
    {
        await using var cmd = Command("""
            UPDATE prem_config.agent
            SET disabled = @value,
                credential_generation = credential_generation + CASE WHEN @value THEN 1 ELSE 0 END
            WHERE tenant_id = @tenant AND id = @id AND deleted_at IS NULL AND disabled <> @value
            """);
        cmd.Parameters.AddWithValue("id", agentId);
        cmd.Parameters.AddWithValue("value", disabled);
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    /// <summary>
    /// Grants a group to a service agent. An agent that acts for a user holds
    /// that user's groups and nothing granted, so a grant to one is refused
    /// rather than stored with no effect.
    /// </summary>
    /// <returns>True when the grant was added; false when it existed.</returns>
    /// <exception cref="InvalidOperationException">The group is one the product maintains.</exception>
    public async Task<bool> GrantGroupAsync(Guid agentId, Guid groupId, CancellationToken ct = default)
    {
        var agent = await FindAgentAsync(agentId, ct) ?? throw new InvalidOperationException("No live agent has this id.");
        if (agent.Mode != AgentMode.Service)
            throw new InvalidOperationException($"Agent '{agent.Name}' acts for a user and holds that user's groups; it cannot be granted groups.");
        await RefuseSystemGroupAsync(groupId, "granted by hand", ct);

        await using var cmd = Command("""
            INSERT INTO prem_config.agent_group_grant(tenant_id, agent_id, group_id)
            SELECT @tenant, @agent, g.id FROM prem_config.app_group g
            WHERE g.tenant_id = @tenant AND g.id = @group AND g.deleted_at IS NULL AND g.system_key IS NULL
            ON CONFLICT DO NOTHING
            """);
        cmd.Parameters.AddWithValue("agent", agentId);
        cmd.Parameters.AddWithValue("group", groupId);
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    /// <exception cref="InvalidOperationException">The group is one the product maintains.</exception>
    public async Task<bool> RevokeGroupAsync(Guid agentId, Guid groupId, CancellationToken ct = default)
    {
        await RefuseSystemGroupAsync(groupId, "revoked by hand", ct);
        await using var cmd = Command("""
            DELETE FROM prem_config.agent_group_grant a USING prem_config.app_group g
            WHERE a.tenant_id = @tenant AND a.agent_id = @agent AND a.group_id = @group
              AND g.tenant_id = @tenant AND g.id = a.group_id AND g.system_key IS NULL
            """);
        cmd.Parameters.AddWithValue("agent", agentId);
        cmd.Parameters.AddWithValue("group", groupId);
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    /// <summary>
    /// Changes an agent's rate limit and its own minimum trust tier. The tier is
    /// written as the spec names it; null or blank follows the deployment's
    /// policy.
    /// </summary>
    /// <returns>False when there is no live agent with this id.</returns>
    /// <exception cref="ArgumentException">The rate is not positive, or the tier is not one the trust policy reads.</exception>
    public async Task<bool> SetAgentLimitsAsync(
        Guid agentId, int requestsPerMinute, string? minimumTrustTier, CancellationToken ct = default)
    {
        if (requestsPerMinute <= 0)
            throw new ArgumentOutOfRangeException(nameof(requestsPerMinute), "A rate limit must be positive.");
        string? tier = null;
        if (!string.IsNullOrWhiteSpace(minimumTrustTier))
        {
            if (!Okf.TrustPolicy.TryParseTier(minimumTrustTier, out var parsed))
                throw new ArgumentException(
                    $"'{minimumTrustTier}' is not a trust tier. Use unverified, machine-confirmed or human-reviewed.",
                    nameof(minimumTrustTier));
            tier = Okf.TrustPolicy.TierKey(parsed);
        }

        await using var cmd = Command("""
            UPDATE prem_config.agent SET requests_per_minute = @rate, minimum_trust_tier = @tier
            WHERE tenant_id = @tenant AND id = @id AND deleted_at IS NULL
            """);
        cmd.Parameters.AddWithValue("id", agentId);
        cmd.Parameters.AddWithValue("rate", requestsPerMinute);
        cmd.Parameters.AddWithValue("tier", NpgsqlTypes.NpgsqlDbType.Text, (object?)tier ?? DBNull.Value);
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    // --- Tokens ---

    /// <summary>
    /// Issues a token for a live agent and stores only the hash of its secret.
    /// The plain token is in the result and nowhere else.
    /// <para>
    /// A token of an agent its person made (<c>self:</c>) is stamped with the
    /// owner's and the agent's credential generations as they are now, in the
    /// same statement, so a later disable or password change ends it for good;
    /// every other agent's token is left unstamped. An agent made through the
    /// authorization flow gets no token here: its credentials belong to its
    /// grant.
    /// </para>
    /// </summary>
    /// <exception cref="InvalidOperationException">No live agent has this id, or it was made through the authorization flow.</exception>
    public async Task<IssuedAgentToken> IssueTokenAsync(Guid agentId, TimeSpan lifetime, CancellationToken ct = default)
    {
        if (lifetime <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(lifetime), "A token must have a positive lifetime.");
        var agent = await FindAgentAsync(agentId, ct) ?? throw new InvalidOperationException("No live agent has this id.");
        if (agent.Origin?.Kind == AgentOrigin.OAuthKind)
            throw new InvalidOperationException(OAuthAgentTokenRefusal);

        var now = Time.GetUtcNow();
        var issued = AgentTokens.Issue(agentId, now, now + lifetime);
        var record = issued.Record;

        await using var cmd = Command("""
            INSERT INTO prem_config.agent_token(
                id, tenant_id, agent_id, secret_sha256, created_at, expires_at, user_generation, agent_generation)
            SELECT @id, @tenant, a.id, @hash, @created, @expires,
                   CASE WHEN a.created_by LIKE 'self:%' THEN u.credential_generation END,
                   CASE WHEN a.created_by LIKE 'self:%' THEN a.credential_generation END
            FROM prem_config.agent a
            JOIN prem_config.app_user u ON u.tenant_id = a.tenant_id AND u.id = a.owner_user_id
            WHERE a.tenant_id = @tenant AND a.id = @agent AND a.deleted_at IS NULL
              AND a.created_by NOT LIKE 'oauth:%'
            """);
        cmd.Parameters.AddWithValue("id", record.Id);
        cmd.Parameters.AddWithValue("agent", record.AgentId);
        cmd.Parameters.AddWithValue("hash", record.SecretHash);
        cmd.Parameters.AddWithValue("created", record.CreatedAt);
        cmd.Parameters.AddWithValue("expires", record.ExpiresAt);
        if (await cmd.ExecuteNonQueryAsync(ct) != 1)
            throw new InvalidOperationException("No live agent has this id.");
        return issued;
    }

    /// <summary>Why an agent made through the authorization flow is issued no token.</summary>
    public const string OAuthAgentTokenRefusal = "This assistant's credentials belong to its grant.";

    /// <returns>False when the token does not exist or was already revoked.</returns>
    public async Task<bool> RevokeTokenAsync(string tokenId, CancellationToken ct = default)
    {
        await using var cmd = Command(
            "UPDATE prem_config.agent_token SET revoked_at = @now WHERE tenant_id = @tenant AND id = @id AND revoked_at IS NULL");
        cmd.Parameters.AddWithValue("id", tokenId);
        cmd.Parameters.AddWithValue("now", Time.GetUtcNow());
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    /// <summary>
    /// Token metadata, for one agent or all, each with whether it is
    /// superseded, read the way <see cref="FindTokenAsync"/> reads it. Never a
    /// secret: none is stored.
    /// </summary>
    public async Task<IReadOnlyList<AgentTokenRecord>> ListTokensAsync(Guid? agentId = null, CancellationToken ct = default)
    {
        await using var cmd = Command($"""
            {TokenSelect}
            WHERE t.tenant_id = @tenant AND (@agent::uuid IS NULL OR t.agent_id = @agent)
            ORDER BY t.created_at
            """);
        cmd.Parameters.AddWithValue("agent", NpgsqlTypes.NpgsqlDbType.Uuid, (object?)agentId ?? DBNull.Value);
        return await ReadAsync(cmd, ReadToken, ct);
    }

    /// <summary>
    /// Records a successful use. Called by whoever served the call, after the
    /// resolver accepted the token; resolving never writes.
    /// </summary>
    public async Task RecordTokenUseAsync(string tokenId, CancellationToken ct = default)
    {
        await using var cmd = Command("UPDATE prem_config.agent_token SET last_used_at = @now WHERE tenant_id = @tenant AND id = @id");
        cmd.Parameters.AddWithValue("id", tokenId);
        cmd.Parameters.AddWithValue("now", Time.GetUtcNow());
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // --- Reissue and removal, inside an administrator change ---

    /// <summary>
    /// A token, its agent and its person, with the token's and the agent's rows
    /// locked until the change ends, so what a reissue judges cannot move under
    /// it: a revoke, a disable or a removal waits for the change, or the change
    /// waits for it. The agent is null when it was removed.
    /// </summary>
    /// <returns>Null when there is no token with this id.</returns>
    public async Task<TokenStanding?> LockTokenAsync(string tokenId, CancellationToken ct = default)
    {
        RequireTransaction();
        await using var cmd = Command($"""
            SELECT t.id, t.agent_id, t.secret_sha256, t.created_at, t.expires_at, t.revoked_at, t.last_used_at, NOT {CredentialSql.TokenCurrent("t", "a", "u")},
                   a.id, a.name, a.owner_user_id, a.mode, a.disabled, a.requests_per_minute, a.minimum_trust_tier,
                   a.model_location, a.model_vendor, a.created_by,
                   a.deleted_at IS NOT NULL, NOT u.disabled AND u.deleted_at IS NULL
            FROM prem_config.agent_token t
            JOIN prem_config.agent a ON a.tenant_id = t.tenant_id AND a.id = t.agent_id
            JOIN prem_config.app_user u ON u.tenant_id = a.tenant_id AND u.id = a.owner_user_id
            WHERE t.tenant_id = @tenant AND t.id = @id
            FOR UPDATE OF t, a
            """);
        cmd.Parameters.AddWithValue("id", tokenId);
        return (await ReadAsync(cmd, r => new TokenStanding(
            ReadToken(r), r.GetBoolean(18) ? null : ReadAgentAt(r, 8), r.GetBoolean(19)), ct)).SingleOrDefault();
    }

    /// <summary>
    /// Removes an agent: every token it holds that is not revoked is revoked,
    /// its credential generation moves, so a grant or a stamped token of it
    /// ends as a disable would end it, and it is marked removed by
    /// <paramref name="removedBy"/>. Nothing is deleted: the row, its tokens
    /// and every audit and usage row that points at it stay, and its name is
    /// free for a new agent.
    /// </summary>
    /// <returns>Null when there is no live agent with this id.</returns>
    public async Task<AgentRemoval?> RemoveAgentAsync(Guid agentId, string removedBy, CancellationToken ct = default)
    {
        RequireTransaction();
        ArgumentException.ThrowIfNullOrWhiteSpace(removedBy);
        await using var find = Command("""
            SELECT name, disabled FROM prem_config.agent
            WHERE tenant_id = @tenant AND id = @id AND deleted_at IS NULL
            FOR UPDATE
            """);
        find.Parameters.AddWithValue("id", agentId);
        var found = (await ReadAsync(find, r => (Name: r.GetString(0), Disabled: r.GetBoolean(1)), ct)).SingleOrDefault();
        if (found.Name is null) return null;

        var now = Time.GetUtcNow();
        await using var revoke = Command("""
            UPDATE prem_config.agent_token SET revoked_at = @now
            WHERE tenant_id = @tenant AND agent_id = @id AND revoked_at IS NULL
            RETURNING id
            """);
        revoke.Parameters.AddWithValue("id", agentId);
        revoke.Parameters.AddWithValue("now", now);
        var revoked = (await ReadAsync(revoke, r => r.GetString(0), ct)).Order(StringComparer.Ordinal).ToList();

        await using var remove = Command("""
            UPDATE prem_config.agent
            SET credential_generation = credential_generation + 1, deleted_at = @now, deleted_by = @by
            WHERE tenant_id = @tenant AND id = @id AND deleted_at IS NULL
            """);
        remove.Parameters.AddWithValue("id", agentId);
        remove.Parameters.AddWithValue("now", now);
        remove.Parameters.AddWithValue("by", removedBy);
        if (await remove.ExecuteNonQueryAsync(ct) != 1)
            throw new InvalidOperationException("The agent could not be marked removed.");
        return new AgentRemoval(found.Name, found.Disabled, revoked);
    }

    /// <summary>The removed agents, newest removal first, with their person and who removed them.</summary>
    public async Task<IReadOnlyList<RemovedAgent>> ListRemovedAgentsAsync(CancellationToken ct = default)
    {
        await using var cmd = Command("""
            SELECT a.id, a.name, a.owner_user_id, u.sign_in_name, a.deleted_at, coalesce(a.deleted_by, 'unknown')
            FROM prem_config.agent a
            JOIN prem_config.app_user u ON u.tenant_id = a.tenant_id AND u.id = a.owner_user_id
            WHERE a.tenant_id = @tenant AND a.deleted_at IS NOT NULL
            ORDER BY a.deleted_at DESC, lower(a.name), a.id
            """);
        return await ReadAsync(cmd, r => new RemovedAgent(
            r.GetGuid(0), r.GetString(1), r.GetGuid(2), r.GetString(3), r.GetFieldValue<DateTimeOffset>(4), r.GetString(5)), ct);
    }

    private void RequireTransaction()
    {
        if (transaction is null)
            throw new InvalidOperationException("This runs only inside an administrator change, which holds its transaction.");
    }

    // --- Plumbing ---

    private const string AgentColumns =
        "id, name, owner_user_id, mode, disabled, requests_per_minute, minimum_trust_tier, model_location, model_vendor, created_by";

    /// <summary>Read through the alias <c>g</c>, since most group reads are joins.</summary>
    private const string GroupColumns = "g.id, g.name, g.system_key";

    /// <summary>
    /// The reserved group's id as a scalar, which is null when the group is not
    /// there. Written into a NOT NULL column on purpose: a membership that
    /// cannot be written must stop the write it belongs to, not be skipped.
    /// </summary>
    private const string ReservedGroupId = """
        (SELECT g.id FROM prem_config.app_group g
         WHERE g.tenant_id = @tenant AND g.system_key = @systemKey AND g.deleted_at IS NULL)
        """;

    /// <summary>
    /// The one read of a token, used by the lookup and the list alike. A token
    /// with no stamps is never superseded; a stamped one is once either of its
    /// generations differs from the current one of its owner or its agent.
    /// </summary>
    private static readonly string TokenSelect = $"""
        SELECT t.id, t.agent_id, t.secret_sha256, t.created_at, t.expires_at, t.revoked_at, t.last_used_at,
               NOT {CredentialSql.TokenCurrent("t", "a", "u")}
        FROM prem_config.agent_token t
        JOIN prem_config.agent a ON a.tenant_id = t.tenant_id AND a.id = t.agent_id
        JOIN prem_config.app_user u ON u.tenant_id = a.tenant_id AND u.id = a.owner_user_id
        """;

    private NpgsqlCommand Command(string sql)
    {
        var cmd = transaction is null
            ? db.DataSource.CreateCommand(sql)
            : new NpgsqlCommand(sql, transaction.Connection, transaction);
        cmd.Parameters.AddWithValue("tenant", tenantId);
        return cmd;
    }

    private async Task<bool> UpdateLiveAsync<T>(string table, string assignment, Guid id, T value, CancellationToken ct)
    {
        await using var cmd = Command(
            $"UPDATE prem_config.{table} SET {assignment} WHERE tenant_id = @tenant AND id = @id AND deleted_at IS NULL");
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("value", value!);
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    private static async Task<T> UniqueAsync<T>(Func<Task<T>> write, string conflictMessage)
    {
        try
        {
            return await write();
        }
        catch (PostgresException ex) when (ex.SqlState == UniqueViolation)
        {
            throw new InvalidOperationException(conflictMessage, ex);
        }
    }

    private static async Task<List<T>> ReadAsync<T>(NpgsqlCommand cmd, Func<NpgsqlDataReader, T> read, CancellationToken ct)
    {
        var rows = new List<T>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) rows.Add(read(reader));
        return rows;
    }

    private static async Task<T> ReadOneAsync<T>(NpgsqlCommand cmd, Func<NpgsqlDataReader, T> read, CancellationToken ct) =>
        (await ReadAsync(cmd, read, ct)).Single();

    internal static User ReadUser(NpgsqlDataReader r) =>
        new(r.GetGuid(0), r.GetString(1), ParseRole(r.GetString(2)), r.GetBoolean(3));

    private static Group ReadGroup(NpgsqlDataReader r) =>
        new(r.GetGuid(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2));

    private static Agent ReadAgent(NpgsqlDataReader r) => ReadAgentAt(r, 0);

    /// <summary>An agent read from <see cref="AgentColumns"/> starting at column <paramref name="o"/>.</summary>
    private static Agent ReadAgentAt(NpgsqlDataReader r, int o) =>
        new(r.GetGuid(o), r.GetString(o + 1), r.GetGuid(o + 2), ParseMode(r.GetString(o + 3)), r.GetBoolean(o + 4), r.GetInt32(o + 5),
            r.IsDBNull(o + 6) ? null : r.GetString(o + 6),
            ParseModelLocation(r.GetString(o + 7)), r.IsDBNull(o + 8) ? null : r.GetString(o + 8), AgentOrigin.Parse(r.GetString(o + 9)));

    /// <summary>
    /// Stops a command that would change a group the product maintains. The
    /// database refuses these changes too; this is here so an administrator is
    /// told what happened rather than shown a trigger's message.
    /// </summary>
    private async Task RefuseSystemGroupAsync(Guid groupId, string what, CancellationToken ct)
    {
        await using var cmd = Command(
            "SELECT g.name FROM prem_config.app_group g WHERE g.tenant_id = @tenant AND g.id = @id AND g.system_key IS NOT NULL");
        cmd.Parameters.AddWithValue("id", groupId);
        if (await cmd.ExecuteScalarAsync(ct) is string name)
            throw new InvalidOperationException(
                $"'{name}' is a group Premagentic maintains itself, and cannot be {what}. Its membership follows what the agents are set to.");
    }

    /// <summary>
    /// Checks a model location and the vendor that goes with it. A local model
    /// has no vendor to name: storing one would put a company's name on a row
    /// that says the answer went to an agent registered as local.
    /// </summary>
    private static string? RequireModel(ModelLocation location, string? vendor)
    {
        RequireDefined(location);
        if (string.IsNullOrWhiteSpace(vendor)) return null;
        if (location == ModelLocation.Local)
            throw new ArgumentException("A local model has no vendor. Name a vendor only for a hosted model.", nameof(vendor));
        return IdentityNames.Require(vendor, "model vendor");
    }

    private static void AddModelParameters(NpgsqlCommand cmd, ModelLocation location, string? vendor)
    {
        cmd.Parameters.AddWithValue("model", NpgsqlTypes.NpgsqlDbType.Text, ModelLocations.Text(location));
        cmd.Parameters.AddWithValue("vendor", NpgsqlTypes.NpgsqlDbType.Text, (object?)vendor ?? DBNull.Value);
        cmd.Parameters.AddWithValue("systemKey", NpgsqlTypes.NpgsqlDbType.Text, SystemGroups.HostedModelAgents);
    }

    /// <summary>
    /// A location the database does not have a name for reads as hosted, the
    /// assumption that keeps an agent away from a folder marked never-leaves.
    /// The column's constraint means this cannot happen; if it ever does, it
    /// fails the safe way.
    /// </summary>
    private static ModelLocation ParseModelLocation(string text) =>
        ModelLocations.TryParse(text, out var location) ? location : ModelLocation.Hosted;

    private static AgentTokenRecord ReadToken(NpgsqlDataReader r) =>
        new(r.GetString(0), r.GetGuid(1), r.GetFieldValue<byte[]>(2), r.GetFieldValue<DateTimeOffset>(3),
            r.GetFieldValue<DateTimeOffset>(4),
            r.IsDBNull(5) ? null : r.GetFieldValue<DateTimeOffset>(5),
            r.IsDBNull(6) ? null : r.GetFieldValue<DateTimeOffset>(6),
            Superseded: r.GetBoolean(7));

    internal static string RoleText(Role role) => role switch
    {
        Role.Administrator => "administrator",
        Role.Auditor => "auditor",
        Role.Member => "member",
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, "Unknown role."),
    };

    internal static Role ParseRole(string text) => text switch
    {
        "administrator" => Role.Administrator,
        "auditor" => Role.Auditor,
        "member" => Role.Member,
        _ => throw new InvalidOperationException($"Unknown role '{text}' in the database."),
    };

    internal static string ModeText(AgentMode mode) => mode switch
    {
        AgentMode.ActsForUser => "acts_for_user",
        AgentMode.Service => "service",
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown agent mode."),
    };

    // An unknown mode reads as undefined, which the resolver refuses.
    private static AgentMode ParseMode(string text) => text switch
    {
        "acts_for_user" => AgentMode.ActsForUser,
        "service" => AgentMode.Service,
        _ => 0,
    };

    private static void RequireDefined<TEnum>(TEnum value) where TEnum : struct, Enum
    {
        if (!Enum.IsDefined(value)) throw new ArgumentOutOfRangeException(nameof(value), value, $"Unknown {typeof(TEnum).Name}.");
    }
}

/// <summary>Names people type: trimmed, non-empty, printable, and not absurdly long.</summary>
internal static class IdentityNames
{
    public const int MaxLength = 200;

    public static string Require(string? value, string what)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException($"A {what} cannot be empty.");
        if (value != value.Trim())
            throw new ArgumentException($"A {what} cannot start or end with white space.");
        if (value.Length > MaxLength)
            throw new ArgumentException($"A {what} is at most {MaxLength} characters.");
        if (value.Any(char.IsControl))
            throw new ArgumentException($"A {what} cannot contain control characters.");
        return value;
    }
}

using System.Text.Json;
using Npgsql;
using Premagentic.Core.Admin;
using Premagentic.Core.Storage;

namespace Premagentic.Core.Identity;

/// <summary>The settings that bound what a member may do for themself.</summary>
public static class AgentSettings
{
    /// <summary>
    /// How many live agents one person may create for themself, 0 to 10,
    /// default 2. Zero turns the connect form off.
    /// </summary>
    public const string SelfServiceMax = "agents.self_service_max";

    /// <summary>How many a person may make when nothing is set.</summary>
    public const int SelfServiceDefault = 2;

    /// <summary>The most <see cref="SelfServiceMax"/> may be set to.</summary>
    public const int SelfServiceCeiling = 10;

    /// <summary>What <see cref="SelfServiceMax"/> accepts, as a phrase that completes "takes ...".</summary>
    public static string SelfServiceAccepts =>
        $"a whole number from 0 to {SelfServiceCeiling}; 0 turns the connect form off";

    /// <summary>The problem with a stored value, or null when it can be used.</summary>
    public static string? SelfServiceProblem(JsonElement value) =>
        value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var n) && n is >= 0 and <= SelfServiceCeiling
            ? null
            : $"{SelfServiceMax} takes {SelfServiceAccepts}, not {value.GetRawText()}.";

    /// <summary>
    /// The bound in force. A stored value that cannot be used reads as 0, so a
    /// typo turns the connect form off rather than lifting the bound.
    /// </summary>
    public static async Task<int> SelfServiceMaxAsync(SettingsStore settings, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (await settings.GetAsync(SelfServiceMax, ct) is not { } value) return SelfServiceDefault;
        return SelfServiceProblem(value) is null ? value.GetInt32() : 0;
    }
}

/// <summary>
/// What a member asks for when they connect an assistant. There is no field
/// for a group or a rate, which is the point: an agent a member makes acts as
/// that member and nothing more, so there is nothing else for them to ask for.
/// </summary>
/// <param name="ModelLocation"><c>local</c> or <c>hosted</c>, required.</param>
/// <param name="ModelVendor">Who runs a hosted model; required for a hosted one, refused for a local one.</param>
/// <param name="AssistantKind">Free text naming the kind of assistant, stored on the agent.</param>
public sealed record SelfServeAgentRequest(string Name, string ModelLocation, string? ModelVendor, string AssistantKind);

/// <summary>
/// A new agent and its first token. The token is returned once to the caller
/// and is never stored in clear or logged.
/// </summary>
public sealed record CreatedAgent(Guid AgentId, string Token)
{
    /// <summary>Leaves the token out, so the record can be logged or printed by accident without harm.</summary>
    public override string ToString() => $"CreatedAgent {{ AgentId = {AgentId} }}";
}

/// <summary>One of a person's own agents, as the connect page lists it.</summary>
/// <param name="Revoked">True once the agent is revoked; its tokens then reach nothing.</param>
/// <param name="State">
/// Where the agent stands, which the connect page shows. Only
/// <see cref="OwnAgentState.Live"/> counts toward <c>agents.self_service_max</c>.
/// <see cref="SelfServeAgents.ListAsync"/> is its one source; the default
/// exists only so a record built by hand compiles.
/// </param>
public sealed record OwnAgent(Guid AgentId, string Name, string ModelLocation, string? ModelVendor, int RatePerMinute,
    string AssistantKind, DateTimeOffset CreatedAt, DateTimeOffset? LastUsedAt, bool Revoked,
    OwnAgentState State = OwnAgentState.Live);

/// <summary>Where one of a person's own agents stands.</summary>
public enum OwnAgentState
{
    /// <summary>Enabled, with a token that is unrevoked, unexpired and current. It holds a slot.</summary>
    Live = 1,

    /// <summary>Revoked: the agent is disabled and every token it holds reaches nothing.</summary>
    Revoked = 2,

    /// <summary>
    /// Ended by a disable or a password change: its tokens were issued before
    /// its owner or the agent was disabled, or before the owner's password
    /// changed, so they reach nothing and a re-enable does not bring them back.
    /// It holds no slot; connect the assistant again.
    /// </summary>
    Ended = 3,

    /// <summary>Its tokens have expired or were revoked one by one. It holds no slot.</summary>
    Expired = 4,
}

/// <summary>
/// The agents a member makes for themself: each acts for that member, holds
/// exactly the member's access, runs at the default rate, and is bounded in
/// number by <see cref="AgentSettings.SelfServiceMax"/>. Every creation and
/// revocation is in the change record with the person as the actor.
/// <para>
/// An agent made here is told apart from one an administrator registered by
/// who made it: <c>created_by</c> is <c>self:&lt;the person&gt;</c>, which the
/// database requires every path to state. A person lists, counts and revokes
/// only those: an agent an administrator registered in their name is the
/// administrator's to change.
/// </para>
/// </summary>
public sealed class SelfServeAgents
{
    /// <summary>The rate every self-made agent runs at, the default the command line gives a new agent.</summary>
    public const int RatePerMinute = 60;

    /// <summary>How long the token issued at creation lasts, the default the command line gives a token.</summary>
    public static readonly TimeSpan TokenLifetime = TimeSpan.FromDays(90);

    /// <summary>The longest assistant kind that is stored.</summary>
    public const int AssistantKindMaxLength = 64;

    private readonly PremagenticDatabase _db;
    private readonly Guid _tenantId;
    private readonly OAuthDeployment? _oauth;
    private readonly TimeProvider _time;

    /// <summary>With the authorization flow off: the bound counts only the person's own agents.</summary>
    public SelfServeAgents(PremagenticDatabase db, Guid tenantId)
        : this(db, tenantId, oauth: null)
    {
    }

    /// <param name="oauth">
    /// The flow as this process runs it, from the host's services, or null
    /// when it is off. With it, the bound also counts the person's grants;
    /// without it, no <c>oauth_*</c> table is read.
    /// </param>
    /// <param name="time">The clock a token's expiry is judged by; the system clock when null.</param>
    public SelfServeAgents(PremagenticDatabase db, Guid tenantId, OAuthDeployment? oauth, TimeProvider? time = null)
    {
        _db = db;
        _tenantId = tenantId;
        _oauth = oauth;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>
    /// How many assistants the person has connected, by the one count the
    /// connect form and the approval use: what the connect page shows beside
    /// the bound.
    /// </summary>
    public Task<int> ConnectedCountAsync(Guid userId, CancellationToken ct) =>
        ConnectedAssistants.CountAsync(_db, _tenantId, userId, _time.GetUtcNow(), _oauth, ct);

    /// <summary>Creates an agent that acts as <paramref name="actingUserId"/> and issues its token.</summary>
    /// <exception cref="ArgumentException">The request is incomplete or holds something that cannot be stored.</exception>
    /// <exception cref="InvalidOperationException">
    /// The person is not a live, enabled account, self-service is off, or the
    /// person is at the bound. Nothing is written.
    /// </exception>
    public async Task<CreatedAgent> CreateAsync(Guid actingUserId, SelfServeAgentRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!ModelLocations.TryParse(request.ModelLocation, out var location))
            throw new ArgumentException(
                $"Say where the assistant's model runs: {ModelLocations.Local} for inside your network, " +
                $"{ModelLocations.Hosted} for outside it.");
        var vendor = string.IsNullOrWhiteSpace(request.ModelVendor) ? null : request.ModelVendor.Trim();
        if (location == ModelLocation.Hosted && vendor is null)
            throw new ArgumentException("Name who runs the hosted model, so the record can say where passages went.");
        if (location == ModelLocation.Local && vendor is not null)
            throw new ArgumentException("A local model has no vendor. Name a vendor only for a hosted model.");
        var kind = RequireKind(request.AssistantKind);

        return await new AdminChanges(_db, _tenantId, _time).RunAsync(Actor(actingUserId), async change =>
        {
            var person = await change.Identity.FindUserAsync(actingUserId, ct);
            if (person is null || person.Disabled)
                throw new InvalidOperationException("Only a live, enabled account can connect an assistant.");

            // Read under the change lock, so two requests at once cannot both
            // see room for one more.
            var bound = await AgentSettings.SelfServiceMaxAsync(new SettingsStore(_db, _tenantId, change.Transaction), ct);
            if (bound == 0)
                throw new InvalidOperationException(SelfServiceOff);
            var live = await ConnectedAssistants.CountAsync(
                change.Transaction.Connection!, change.Transaction, _tenantId, actingUserId, _time.GetUtcNow(), _oauth, ct);
            if (live >= bound)
                throw new InvalidOperationException(
                    $"You have {live} connected assistants, and this server allows {bound} each. Revoke one to connect another.");

            // Acts for the person, never as a service: it holds exactly the
            // person's principals on every call and cannot be granted a group.
            var agent = await change.Identity.CreateAgentAsync(
                request.Name, actingUserId, AgentMode.ActsForUser, RatePerMinute, null, location, vendor,
                AgentOrigin.Self(actingUserId), ct);
            await SetKindAsync(change.Transaction, agent.Id, kind, ct);
            var issued = await change.Identity.IssueTokenAsync(agent.Id, TokenLifetime, ct);

            change.Record("agent.add", agent.Name, null,
                new
                {
                    owner = person.Name, mode = "acts-for-user", requests_per_minute = RatePerMinute,
                    model_location = ModelLocations.Text(location), model_vendor = vendor, assistant_kind = kind,
                    self_service = true,
                });
            change.Record("token.issue", agent.Name, null,
                new { token_id = issued.Record.Id, expires_at = issued.Record.ExpiresAt });
            return new CreatedAgent(agent.Id, issued.PlainText);
        }, ct);
    }

    /// <summary>
    /// The acting person's own agents, revoked ones included, newest first,
    /// each with its state, read by the same rules as the bound: an agent is
    /// <see cref="OwnAgentState.Live"/> exactly when the count counts it.
    /// </summary>
    public async Task<IReadOnlyList<OwnAgent>> ListAsync(Guid actingUserId, CancellationToken ct)
    {
        await using var cmd = _db.DataSource.CreateCommand($"""
            SELECT a.id, a.name, a.model_location, a.model_vendor, a.requests_per_minute, a.assistant_kind,
                   a.created_at, (SELECT max(t.last_used_at) FROM prem_config.agent_token t WHERE t.agent_id = a.id),
                   a.disabled,
                   CASE WHEN a.disabled THEN {(int)OwnAgentState.Revoked}
                        WHEN {CredentialSql.SelfAgentHoldsSlot("a")} THEN {(int)OwnAgentState.Live}
                        WHEN {CredentialSql.SelfAgentHasEndedToken("a")} THEN {(int)OwnAgentState.Ended}
                        ELSE {(int)OwnAgentState.Expired} END
            FROM prem_config.agent a
            WHERE a.tenant_id = @tenant AND a.owner_user_id = @owner
              AND a.created_by = @self AND a.deleted_at IS NULL
            ORDER BY a.created_at DESC, a.id
            """);
        cmd.Parameters.AddWithValue("tenant", _tenantId);
        cmd.Parameters.AddWithValue("owner", actingUserId);
        cmd.Parameters.AddWithValue("self", AgentOrigin.Self(actingUserId).Text);
        cmd.Parameters.AddWithValue("now", _time.GetUtcNow());

        var own = new List<OwnAgent>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            own.Add(new OwnAgent(
                reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.GetInt32(4), reader.GetString(5), reader.GetFieldValue<DateTimeOffset>(6),
                reader.IsDBNull(7) ? null : reader.GetFieldValue<DateTimeOffset>(7), reader.GetBoolean(8),
                (OwnAgentState)reader.GetInt32(9)));
        return own;
    }

    /// <summary>
    /// Revokes one of the acting person's own agents: the agent is disabled and
    /// every token it holds is revoked, so none reaches anything from the next
    /// call. Revoking one already revoked changes nothing and records nothing.
    /// </summary>
    /// <exception cref="InvalidOperationException">The agent is not one the acting person made.</exception>
    public async Task RevokeAsync(Guid actingUserId, Guid agentId, CancellationToken ct)
    {
        await new AdminChanges(_db, _tenantId, _time).RunAsync(Actor(actingUserId), async change =>
        {
            // One answer for an agent that does not exist and one that is not
            // theirs, so the question cannot be used to learn who has which.
            if (await FindOwnAsync(change.Transaction, actingUserId, agentId, ct) is not { } agent)
                throw new InvalidOperationException(NotYours);

            foreach (var token in await change.Identity.ListTokensAsync(agentId, ct))
                if (token.RevokedAt is null && await change.Identity.RevokeTokenAsync(token.Id, ct))
                    change.Record("token.revoke", agent.Name, null, new { token_id = token.Id });
            if (!agent.Disabled && await change.Identity.SetAgentDisabledAsync(agentId, true, ct))
                change.Record("agent.disable", agent.Name, new { disabled = false }, new { disabled = true });
            return 0;
        }, ct);
    }

    /// <summary>
    /// Gives one of the acting person's own assistants a new key in place of
    /// its one live key, through <see cref="Admin.AgentLifecycle.ReissueAsync"/>,
    /// with the replaced key's own lifetime. The new key is shown once.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The agent is not one the acting person made, it has no live key or more
    /// than one, or the reissue is refused; the message says which.
    /// </exception>
    public async Task<Admin.ReissuedAgentToken> ReissueAsync(Guid actingUserId, Guid agentId, CancellationToken ct) =>
        await new AdminChanges(_db, _tenantId, _time).RunAsync(Actor(actingUserId), async change =>
        {
            if (await FindOwnAsync(change.Transaction, actingUserId, agentId, ct) is null)
                throw new InvalidOperationException(NotYours);
            var now = change.Identity.Time.GetUtcNow();
            var live = (await change.Identity.ListTokensAsync(agentId, ct)).Where(t => t.IsUsableAt(now)).ToList();
            if (live.Count == 0) throw new InvalidOperationException(NoLiveKey);
            if (live.Count > 1) throw new InvalidOperationException(MoreThanOneKey);
            return await Admin.AgentLifecycle.ReissueAsync(change, live[0].Id, null, ct);
        }, ct);

    /// <summary>
    /// Removes one of the acting person's own assistants, in any state, through
    /// <see cref="Admin.AgentLifecycle.RemoveAsync"/>: gone from their page and
    /// from every list, its history kept.
    /// </summary>
    /// <exception cref="InvalidOperationException">The agent is not one the acting person made.</exception>
    public async Task<AgentRemoval> RemoveAsync(Guid actingUserId, Guid agentId, CancellationToken ct) =>
        await new AdminChanges(_db, _tenantId, _time).RunAsync(Actor(actingUserId), async change =>
        {
            if (await FindOwnAsync(change.Transaction, actingUserId, agentId, ct) is null)
                throw new InvalidOperationException(NotYours);
            return await Admin.AgentLifecycle.RemoveAsync(change, agentId, ct);
        }, ct);

    /// <summary>The one answer for an assistant that does not exist and one that is somebody else's.</summary>
    public const string NotYours = "You have no connected assistant with this id.";

    public const string NoLiveKey = "This assistant has no live key to replace. Connect it again.";

    public const string MoreThanOneKey = "This assistant has more than one live key. An administrator replaces them.";

    /// <summary>The refusal when <see cref="AgentSettings.SelfServiceMax"/> is 0, shared with the approval of an assistant.</summary>
    public const string SelfServiceOff = "Connecting an assistant yourself is turned off on this server. Ask an administrator.";

    private static AdminActor Actor(Guid actingUserId) => new("portal", null, actingUserId);

    private static string RequireKind(string? kind)
    {
        if (string.IsNullOrWhiteSpace(kind)) throw new ArgumentException("Say which kind of assistant you are connecting.");
        kind = kind.Trim();
        if (kind.Length > AssistantKindMaxLength)
            throw new ArgumentException($"An assistant kind is at most {AssistantKindMaxLength} characters.");
        if (kind.Any(char.IsControl)) throw new ArgumentException("An assistant kind cannot hold control characters.");
        return kind;
    }

    private async Task SetKindAsync(NpgsqlTransaction tx, Guid agentId, string kind, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            "UPDATE prem_config.agent SET assistant_kind = @kind WHERE tenant_id = @tenant AND id = @id", tx.Connection, tx);
        cmd.Parameters.AddWithValue("tenant", _tenantId);
        cmd.Parameters.AddWithValue("id", agentId);
        cmd.Parameters.AddWithValue("kind", kind);
        if (await cmd.ExecuteNonQueryAsync(ct) != 1)
            throw new InvalidOperationException("The new agent could not be marked as connected by its owner.");
    }

    /// <returns>The agent's name and state when it is a live agent the person made, otherwise null.</returns>
    private async Task<(string Name, bool Disabled)?> FindOwnAsync(NpgsqlTransaction tx, Guid ownerId, Guid agentId, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("""
            SELECT name, disabled FROM prem_config.agent
            WHERE tenant_id = @tenant AND id = @id AND owner_user_id = @owner
              AND created_by = @self AND deleted_at IS NULL
            """, tx.Connection, tx);
        cmd.Parameters.AddWithValue("tenant", _tenantId);
        cmd.Parameters.AddWithValue("id", agentId);
        cmd.Parameters.AddWithValue("owner", ownerId);
        cmd.Parameters.AddWithValue("self", AgentOrigin.Self(ownerId).Text);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? (reader.GetString(0), reader.GetBoolean(1)) : null;
    }
}

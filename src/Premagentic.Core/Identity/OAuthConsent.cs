using Npgsql;
using Premagentic.Core.Admin;
using Premagentic.Core.Storage;

namespace Premagentic.Core.Identity;

/// <summary>
/// The consent step of the flow: the checks on an authorization request, what
/// the page shows, and what Approve and Deny do. Only served while the flow is
/// on, so it always has the deployment.
/// </summary>
public sealed class OAuthConsent(PremagenticDatabase db, Guid tenantId, OAuthDeployment oauth, TimeProvider? time = null) : IOAuthConsent
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public const string AlreadyAnswered = "This request was already answered. Go back to your assistant.";
    public const string RegistrationEnded =
        "This assistant's registration on this server has expired or was removed. Remove this server from the assistant and add it again.";
    /// <summary>For an assistant that names itself by an https address no administrator stored: nothing is fetched to identify it.</summary>
    public const string NotRegistered =
        "This assistant names itself by a web address, and PremAgentic fetches nothing from the internet to identify an assistant. " +
        "Ask your administrator to register it from its metadata document with 'prem oauth clients add --metadata-file'.";
    public const string NewAgentName = "a new connected assistant";

    private static readonly string[] Recognized =
        ["response_type", "client_id", "redirect_uri", "code_challenge", "code_challenge_method", "resource", "scope", "state"];

    public async Task<AuthorizationCheck> CheckAsync(IReadOnlyDictionary<string, IReadOnlyList<string>> parameters, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        await using var conn = await db.DataSource.OpenConnectionAsync(ct);
        return await CheckAsync(conn, null, parameters, ct);
    }

    public async Task<ConsentView> ViewAsync(Guid userId, OAuthAuthorizationRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var conn = await db.DataSource.OpenConnectionAsync(ct);
        var now = _time.GetUtcNow();
        var client = request.Client;
        var loopback = RedirectUris.IsLoopback(request.RedirectUri);
        var identity = new IdentityStore(db, tenantId, _time);

        // Whether approving again keeps the agent, judged with the location the
        // page shows: exact for a client with a stated one, and the preselected
        // hosted otherwise.
        var shown = client.StatedModelLocation ?? ModelLocation.Hosted;
        var previous = (await OAuthStore.ListGrantsAsync(conn, null, tenantId, "g.user_id = @user AND g.client_id = @client", now, ct,
            ("user", userId), ("client", client.Id))).FirstOrDefault();
        var agent = previous is null ? null : await identity.FindAgentAsync(previous.AgentId, ct);
        var kept = agent is { Disabled: false } && agent.ModelLocation == shown
                   && (client.StatedModelLocation is null || agent.ModelVendor == client.StatedModelVendor);

        return new ConsentView(
            request, RedirectUris.Origin(request.RedirectUri), loopback,
            LocalAllowed: loopback || client.StatedModelLocation == ModelLocation.Local,
            ReplacesGrant: kept, AgentName: kept ? agent!.Name : NewAgentName,
            KeptAgentModelLocation: kept ? agent!.ModelLocation : null, KeptAgentModelVendor: kept ? agent!.ModelVendor : null,
            ConnectedAssistants: await ConnectedAssistants.CountAsync(conn, null, tenantId, userId, now, oauth, ct),
            Bound: await AgentSettings.SelfServiceMaxAsync(new SettingsStore(db, tenantId), ct));
    }

    public async Task<string> ApproveAsync(Guid userId, IReadOnlyDictionary<string, IReadOnlyList<string>> parameters,
        ModelLocation modelLocation, string? modelVendor, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        var chosenVendor = string.IsNullOrWhiteSpace(modelVendor) ? null : modelVendor.Trim();
        try
        {
            return await new AdminChanges(db, tenantId, _time).RunAsync(new AdminActor("portal", null, userId), async change =>
            {
                var tx = change.Transaction;
                var conn = tx.Connection!;
                var now = _time.GetUtcNow();
                var check = await CheckAsync(conn, tx, parameters, ct);
                if (check is not { Outcome: AuthorizationCheckOutcome.Valid, Request: { } request })
                    throw new InvalidOperationException(check.Message ?? "This request can no longer be approved.");
                var client = request.Client;

                // Where the model runs: an administrator's statement wins;
                // otherwise the person's answer, which may be local only when
                // the answer goes to a program on this computer.
                var loopback = RedirectUris.IsLoopback(request.RedirectUri);
                var location = client.StatedModelLocation ?? modelLocation;
                var vendor = client.StatedModelLocation is not null ? client.StatedModelVendor : chosenVendor;
                if (location == ModelLocation.Local && !(loopback || client.StatedModelLocation == ModelLocation.Local))
                    throw new ArgumentException(
                        "This assistant receives answers outside this computer, so what it is served leaves the network. " +
                        "It cannot be marked as using a local model unless an administrator registers it that way.");
                if (client.StatedModelLocation is null)
                {
                    if (location == ModelLocation.Local && vendor is not null)
                        throw new ArgumentException("A local model has no vendor. Name a vendor only for a hosted model.");
                    if (location == ModelLocation.Hosted && vendor is null)
                        throw new ArgumentException("Name who runs the hosted model, so the record can say where passages went.");
                }

                var person = await change.Identity.FindUserAsync(userId, ct);
                if (person is null || person.Disabled) throw new InvalidOperationException("Only a live, enabled account can connect an assistant.");
                var bound = await AgentSettings.SelfServiceMaxAsync(new SettingsStore(db, tenantId, tx), ct);
                if (bound == 0) throw new InvalidOperationException(SelfServeAgents.SelfServiceOff);
                if (await ChallengeUsedAsync(tx, client.Id, request.CodeChallenge, ct)) throw new InvalidOperationException(AlreadyAnswered);

                // Approving the same assistant again: the person's latest grant for
                // this client, and its agent.
                var previous = (await OAuthStore.ListGrantsAsync(conn, tx, tenantId, "g.user_id = @user AND g.client_id = @client", now, ct,
                    ("user", userId), ("client", client.Id))).FirstOrDefault();
                var previousHeldSlot = previous is not null && await HoldsSlotAsync(tx, previous.Id, now, ct);
                var old = previous is null ? null : await change.Identity.FindAgentAsync(previous.AgentId, ct);
                var keep = old is { Disabled: false } && old.ModelLocation == location && old.ModelVendor == vendor;
                var elsewhere = old is { Disabled: false } && !keep;

                if (previous is { RevokedAt: null })
                    await OAuthStore.RevokeAsync(change, tenantId, previous,
                        elsewhere ? OAuthStore.ApprovedAgainElsewhere : OAuthStore.ApprovedAgain, disableAgent: false, now, ct);
                if (elsewhere && await change.Identity.SetAgentDisabledAsync(old!.Id, true, ct))
                    change.Record("agent.disable", old.Name, new { disabled = false }, new { disabled = true });

                // A kept agent over a grant that held its slot keeps it; anything else takes one.
                if (!(keep && previousHeldSlot))
                {
                    var connected = await ConnectedAssistants.CountAsync(conn, tx, tenantId, userId, now, oauth, ct);
                    if (connected >= bound)
                        throw new InvalidOperationException(
                            $"You have {connected} connected assistants, and this server allows {bound} each. Revoke one to connect another.");
                }

                var grantId = OAuthSecrets.NewId();
                var agent = keep ? old! : await CreateAgentAsync(change, grantId, userId, person.Name, client, location, vendor, ct);
                var (userGeneration, agentGeneration) = await GenerationsAsync(tx, userId, agent.Id, ct);
                var settings = await new SettingsStore(db, tenantId, tx).GetManyAsync([OAuthSettings.GrantDays, OAuthSettings.CodeSeconds], ct);
                var grantDays = OAuthClients.Number(settings, OAuthSettings.GrantDays, 1, 365, OAuthSettings.GrantDaysDefault);
                var codeSeconds = OAuthClients.Number(settings, OAuthSettings.CodeSeconds, 10, 600, OAuthSettings.CodeSecondsDefault);

                await OAuthStore.SweepAsync(conn, tx, tenantId, now, ct);
                var (code, codeHash) = OAuthSecrets.NewCode();
                await using (var insert = new NpgsqlCommand("""
                    INSERT INTO prem_config.oauth_grant(
                        id, tenant_id, client_id, user_id, agent_id, user_generation, agent_generation, scope, resource,
                        created_at, expires_at)
                    VALUES(@grant, @tenant, @client, @user, @agent, @ugen, @agen, 'read', @resource, @now, @grantEnds);
                    INSERT INTO prem_config.oauth_code(
                        code_sha256, tenant_id, grant_id, client_id, redirect_uri, code_challenge, resource, created_at, expires_at)
                    VALUES(@code, @tenant, @grant, @client, @redirect, @challenge, @resource, @now, @codeEnds);
                    UPDATE prem_config.oauth_client SET first_approved_at = @now
                    WHERE tenant_id = @tenant AND id = @client AND first_approved_at IS NULL;
                    """, conn, tx))
                {
                    insert.Parameters.AddWithValue("grant", grantId);
                    insert.Parameters.AddWithValue("tenant", tenantId);
                    insert.Parameters.AddWithValue("client", client.Id);
                    insert.Parameters.AddWithValue("user", userId);
                    insert.Parameters.AddWithValue("agent", agent.Id);
                    insert.Parameters.AddWithValue("ugen", userGeneration);
                    insert.Parameters.AddWithValue("agen", agentGeneration);
                    insert.Parameters.AddWithValue("resource", oauth.Resource);
                    insert.Parameters.AddWithValue("now", now);
                    insert.Parameters.AddWithValue("grantEnds", now + TimeSpan.FromDays(grantDays));
                    insert.Parameters.AddWithValue("code", codeHash);
                    insert.Parameters.AddWithValue("redirect", request.RedirectUri);
                    insert.Parameters.AddWithValue("challenge", request.CodeChallenge);
                    insert.Parameters.AddWithValue("codeEnds", now + TimeSpan.FromSeconds(codeSeconds));
                    await insert.ExecuteNonQueryAsync(ct);
                }
                change.Record("oauth.approve", client.Id, null, new
                {
                    grant_id = grantId, user = person.Name, agent = agent.Name, redirect_uri = request.RedirectUri,
                    model_location = ModelLocations.Text(location), model_vendor = vendor, kept_agent = keep,
                });
                return AnswerUrl(request.RedirectUri, ("code", code), ("state", request.State), ("iss", oauth.Issuer));
            }, ct);
        }
        catch (PostgresException ex) when (ex.SqlState == "23505" && ex.ConstraintName == "oauth_code_challenge_once")
        {
            throw new InvalidOperationException(AlreadyAnswered, ex);
        }
    }

    public async Task<string> DenyAsync(Guid userId, IReadOnlyDictionary<string, IReadOnlyList<string>> parameters, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        return await new AdminChanges(db, tenantId, _time).RunAsync(new AdminActor("portal", null, userId), async change =>
        {
            var check = await CheckAsync(change.Transaction.Connection!, change.Transaction, parameters, ct);
            if (check is not { Outcome: AuthorizationCheckOutcome.Valid, Request: { } request })
                throw new InvalidOperationException(check.Message ?? "This request can no longer be answered.");
            change.Record("oauth.deny", request.Client.Id, null, new { redirect_uri = request.RedirectUri });
            return AnswerUrl(request.RedirectUri, ("error", "access_denied"), ("state", request.State), ("iss", oauth.Issuer));
        }, ct);
    }

    /// <summary>
    /// The checks, in the order the specification sets: the client and the
    /// redirect address first, whose failures are shown and never sent
    /// anywhere; then everything else, whose failures go back to the client
    /// only when the person follows the link.
    /// </summary>
    private async Task<AuthorizationCheck> CheckAsync(NpgsqlConnection conn, NpgsqlTransaction? tx,
        IReadOnlyDictionary<string, IReadOnlyList<string>> parameters, CancellationToken ct)
    {
        // A parameter sent with no value counts as not sent.
        IReadOnlyList<string> Values(string name) =>
            parameters.TryGetValue(name, out var v) ? v.Where(s => s.Length > 0).ToArray() : [];

        var clientIds = Values("client_id");
        if (clientIds.Count != 1)
            return Show("invalid_request", clientIds.Count == 0 ? "The request names no assistant." : "The request names its assistant twice.");
        var clientId = clientIds[0];
        var row = await OAuthStore.FindClientAsync(conn, tx, tenantId, clientId, ct);
        if (row is null)
            return Show("invalid_request", clientId.StartsWith(OAuthPrefixes.ClientId, StringComparison.Ordinal)
                ? RegistrationEnded
                : clientId.StartsWith("https://", StringComparison.Ordinal) ? NotRegistered : "This assistant is not registered on this server.");
        if (row.Removed) return Show("invalid_request", RegistrationEnded);
        if (row.Disabled) return Show("invalid_request", "This assistant has been turned off on this server. Ask your administrator.");
        var client = row.ToClient();

        var redirects = Values("redirect_uri");
        if (redirects.Count > 1) return Show("invalid_request", "The request names its answer address twice.");
        string redirect;
        if (redirects.Count == 1)
        {
            redirect = redirects[0];
            if (RedirectUris.Classify(redirect, OAuthSettings.AdministratorRedirectUriMaxLength, out _) is not (RedirectUriKind.Loopback or RedirectUriKind.Https)
                || !client.RedirectUris.Any(r => RedirectUris.Matches(r, redirect)))
                return Show("invalid_request", "The address this request would send the answer to is not one this assistant registered.");
        }
        else if (client.RedirectUris.Count == 1)
            redirect = client.RedirectUris[0];
        else
            return Show("invalid_request", "This assistant registered more than one answer address, so the request must name one.");

        // A self-registered client keeps an https address only while an
        // administrator lists it.
        if (client.RegisteredBy == OAuthClientRegistration.Dynamic && RedirectUris.Classify(redirect, OAuthSettings.DynamicRedirectUriMaxLength, out _) == RedirectUriKind.Https
            && !oauth.DynamicRedirectUris.Contains(redirect, StringComparer.Ordinal))
            return Show("invalid_request", "The address this assistant answers at is no longer allowed for a self-registered assistant. Ask your administrator.");

        var states = Values("state");
        if (states.Count > 1) return Show("invalid_request", "The request carries its state twice.");
        var state = states.Count == 1 ? states[0] : null;
        if (state is { Length: > 2048 }) return Show("invalid_request", "The request's state is too long.");

        AuthorizationCheck Return(string error, string message) => new(AuthorizationCheckOutcome.ReturnError, null, error, message,
            AnswerUrl(redirect, ("error", error), ("error_description", message), ("state", state), ("iss", oauth.Issuer)));

        foreach (var name in Recognized)
            if (Values(name).Count > 1) return Return("invalid_request", $"The request carries {name} twice.");
        var responseType = Values("response_type").FirstOrDefault();
        if (responseType is null) return Return("invalid_request", "The request names no response_type.");
        if (responseType != "code") return Return("unsupported_response_type", "Only the authorization code flow is served.");
        if (Values("code_challenge_method").FirstOrDefault() != "S256")
            return Return("invalid_request", "PKCE with the S256 method is required.");
        var challenge = Values("code_challenge").FirstOrDefault();
        if (!OAuthSecrets.IsChallenge(challenge)) return Return("invalid_request", "The code_challenge must be an S256 challenge.");
        var resource = Values("resource").FirstOrDefault();
        if (resource is null || !SameResource(resource, oauth.Resource))
            return Return("invalid_target", $"The resource must be this server's MCP address, {oauth.Resource}.");

        return new AuthorizationCheck(AuthorizationCheckOutcome.Valid,
            new OAuthAuthorizationRequest(client, redirect, challenge!, OAuthScopes.Read, oauth.Resource, state), null, null, null);
    }

    /// <summary>
    /// The requested resource names this server: equal to the canonical one,
    /// or equal to it with the scheme and host in other case.
    /// </summary>
    internal static bool SameResource(string requested, string canonical)
    {
        if (string.Equals(requested, canonical, StringComparison.Ordinal)) return true;
        if (!requested.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return false;
        var slash = requested.IndexOf('/', "https://".Length);
        if (slash < 0) return false;
        return string.Equals(requested[..slash].ToLowerInvariant() + requested[slash..], canonical, StringComparison.Ordinal);
    }

    /// <summary>The redirect address with the answer's parameters added, each escaped, keeping any query it has.</summary>
    internal static string AnswerUrl(string redirect, params (string Name, string? Value)[] parameters)
    {
        var query = string.Join("&", parameters.Where(p => p.Value is not null)
            .Select(p => Uri.EscapeDataString(p.Name) + "=" + Uri.EscapeDataString(p.Value!)));
        return redirect + (redirect.Contains('?') ? "&" : "?") + query;
    }

    private static AuthorizationCheck Show(string error, string message) =>
        new(AuthorizationCheckOutcome.ShowError, null, error, message, null);

    private async Task<Agent> CreateAgentAsync(AdminChange change, string grantId, Guid userId, string owner, OAuthClient client,
        ModelLocation location, string? vendor, CancellationToken ct)
    {
        var agent = await change.Identity.CreateAgentAsync(
            "oauth-" + grantId, userId, AgentMode.ActsForUser, SelfServeAgents.RatePerMinute, null, location, vendor,
            AgentOrigin.OAuth(client.Id), ct);
        await using (var kind = new NpgsqlCommand(
            "UPDATE prem_config.agent SET assistant_kind = @kind WHERE tenant_id = @tenant AND id = @id",
            change.Transaction.Connection, change.Transaction))
        {
            kind.Parameters.AddWithValue("tenant", tenantId);
            kind.Parameters.AddWithValue("id", agent.Id);
            kind.Parameters.AddWithValue("kind", client.Name);
            await kind.ExecuteNonQueryAsync(ct);
        }
        change.Record("agent.add", agent.Name, null, new
        {
            owner, mode = "acts-for-user", requests_per_minute = SelfServeAgents.RatePerMinute,
            model_location = ModelLocations.Text(location), model_vendor = vendor, assistant_kind = client.Name, oauth_client = client.Id,
        });
        return agent;
    }

    private async Task<bool> ChallengeUsedAsync(NpgsqlTransaction tx, string clientId, string challenge, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            "SELECT EXISTS (SELECT 1 FROM prem_config.oauth_code WHERE tenant_id = @tenant AND client_id = @client AND code_challenge = @challenge)",
            tx.Connection, tx);
        cmd.Parameters.AddWithValue("tenant", tenantId);
        cmd.Parameters.AddWithValue("client", clientId);
        cmd.Parameters.AddWithValue("challenge", challenge);
        return (bool)(await cmd.ExecuteScalarAsync(ct))!;
    }

    private async Task<bool> HoldsSlotAsync(NpgsqlTransaction tx, string grantId, DateTimeOffset now, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            $"SELECT {CredentialSql.GrantHoldsSlot("g")} FROM prem_config.oauth_grant g WHERE g.tenant_id = @tenant AND g.id = @grant",
            tx.Connection, tx);
        cmd.Parameters.AddWithValue("tenant", tenantId);
        cmd.Parameters.AddWithValue("grant", grantId);
        cmd.Parameters.AddWithValue("now", now);
        cmd.Parameters.AddWithValue("oauth_resource", oauth.Resource);
        return await cmd.ExecuteScalarAsync(ct) is true;
    }

    private async Task<(long User, long Agent)> GenerationsAsync(NpgsqlTransaction tx, Guid userId, Guid agentId, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("""
            SELECT u.credential_generation, a.credential_generation
            FROM prem_config.app_user u JOIN prem_config.agent a ON a.tenant_id = u.tenant_id AND a.id = @agent
            WHERE u.tenant_id = @tenant AND u.id = @user
            """, tx.Connection, tx);
        cmd.Parameters.AddWithValue("tenant", tenantId);
        cmd.Parameters.AddWithValue("user", userId);
        cmd.Parameters.AddWithValue("agent", agentId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) throw new InvalidOperationException("The person or the agent is gone.");
        return (reader.GetInt64(0), reader.GetInt64(1));
    }
}

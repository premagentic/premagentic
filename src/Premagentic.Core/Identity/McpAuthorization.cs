namespace Premagentic.Core.Identity;

// The MCP authorization flow (OAuth 2.1 with PKCE), built to MCP revision
// 2026-07-28 and OAuth 2.1 draft 16. Off unless an administrator turns it on.
// The authorization server and the resource server are this process: nothing
// in the flow makes a network request of its own, so a client is registered
// by dynamic registration (RFC 7591) or by an administrator, and no client
// document, key set or logo is ever fetched.
//
// What a client receives is bound to an agent that acts for the person who
// approved it, so every gate, the audit row, the rate limit and the rule that
// agents read and never write apply unchanged.
//
// Migration 0080, prem_config only:
//
//   app_user.credential_generation  BIGINT NOT NULL DEFAULT 0, and the same on
//   agent.credential_generation     agent. Bumped in the UPDATE that writes a
//                                   disable (SetUserDisabledAsync,
//                                   SetAgentDisabledAsync) and, on app_user,
//                                   in the one that sets a new password
//                                   (SetPasswordHashAsync; never the sign-in
//                                   rehash, RehashPasswordAsync). Never
//                                   decremented. This is how a disable or a
//                                   password change ends a person's assistant
//                                   credentials for good, whether the flow is
//                                   on or off. The migration sets it to 1 on
//                                   every user and agent that is disabled when
//                                   it runs, so a disable in force at the
//                                   upgrade is final too.
//   agent_token.user_generation     BIGINT, and agent_generation: stamped at
//                                   issue for a token of an agent the person
//                                   made (created_by self:), NULL for every
//                                   other agent's token, which a disable or a
//                                   password change leaves as today. The
//                                   migration stamps existing self: tokens
//                                   (0, 0), so one whose owner or agent was
//                                   disabled at the upgrade is superseded and
//                                   every other stays current. A token whose
//                                   stamps differ from the current values is
//                                   refused (refused:TokenSuperseded). No
//                                   token is issued to an oauth: agent: its
//                                   credentials belong to its grant.
//   oauth_client          id TEXT PK (prem_cli_<24 hex>, or an https URL an
//                         administrator typed), tenant_id, name (1 to 64
//                         after trimming), redirect_uris TEXT[] (1 to 5),
//                         application_type (kept only when exactly native or
//                         web, else NULL), software_id, software_version,
//                         registered_by (dynamic | cli:<account> |
//                         portal:<user id>), registered_from (the source
//                         address, IPv4 exact or IPv6 by /64, dynamic only),
//                         model_location, model_vendor (an administrator's
//                         statement only), created_at, first_approved_at (set
//                         by the first approval), disabled, deleted_at.
//   oauth_grant           id TEXT PK (24 hex), tenant_id, client_id, user_id,
//                         agent_id, user_generation, agent_generation (both
//                         captured at approval, under the change lock), scope
//                         (always read), resource (the canonical resource),
//                         created_at, activated_at (the first code exchange;
//                         NULL while pending), expires_at, revoked_at,
//                         revoked_reason, last_refreshed_at, refresh_count.
//   oauth_code            code_sha256 BYTEA PK, tenant_id, grant_id, client_id,
//                         redirect_uri (exactly as the request used it),
//                         code_challenge, resource, created_at, expires_at,
//                         used_at; unique (client_id, code_challenge).
//   oauth_refresh_token   id TEXT PK, tenant_id, grant_id, secret_sha256,
//                         created_at, expires_at, used_at, replaced_by.
//   oauth_access_token    id TEXT PK, tenant_id, grant_id, secret_sha256,
//                         audience, created_at, expires_at.
//
// agent.created_by gains the origin oauth:<client id>, with a partial index as
// 0068 has for self:.
//
// A grant is PENDING from approval until its first code exchange, which sets
// activated_at; it ends with its code if the code is never exchanged. One
// liveness predicate, in SQL, decides whether a grant is live wherever it
// matters (at /mcp, at refresh, in the grant lists and in the bound):
// activated_at IS NOT NULL, revoked_at IS NULL, expires_at later than @now
// (the application's TimeProvider, never the database clock), resource equal
// to the current canonical resource, the client neither disabled nor removed,
// the agent and its owner live and enabled, and both captured generations
// equal to the current credential_generation of the owner and the agent. The
// code exchange applies the same predicate less activated_at, and sets it.
// The first request that meets a grant failing it marks the grant revoked with
// its reason, as bookkeeping; the predicate is the guard. The reasons: owner
// disabled (the owner is disabled now); agent disabled (the agent is disabled
// now, or its generation moved); owner disabled or password changed (the
// owner's generation moved and the owner is enabled now, since one counter
// cannot tell the two apart); client disabled; client removed; the server's
// address changed.
//
// No path deletes a code or token row before its expires_at: ending a grant
// marks the grant, and its tokens die by the predicate. Expired rows are
// swept by expires_at only, never by used_at, so a used refresh token and its
// replaced_by link outlive a thief's rotation. The oauth_* tables stay out of
// the configuration export: codes and tokens are live credentials, and every
// client registration is in the change record.

/// <summary>The settings of the authorization flow. Every one is in the settings catalog.</summary>
public static class OAuthSettings
{
    /// <summary>
    /// True turns the flow on. Default false. Read when the server starts.
    /// With it true and <see cref="PublicUrl"/> missing or unusable, or a bad
    /// entry in <see cref="DynamicRedirectUris"/>, the server starts with the
    /// flow off and logs one warning naming both remedies. It never refuses to
    /// start over these settings.
    /// </summary>
    public const string Enabled = "mcp.oauth.enabled";

    /// <summary>
    /// The server's address as clients reach it, in its one canonical
    /// spelling: lowercase <c>https://</c> and a lowercase ASCII host, no
    /// userinfo, no path (not even <c>/</c>), no query or fragment, and the
    /// port left out when it is 443 (otherwise 1 to 65535, no leading zero).
    /// Anything else is refused with the canonical spelling named, so a retype
    /// cannot change the audience. The issuer and the resource are made from
    /// it, never from a request's Host header. Read when the server starts.
    /// </summary>
    public const string PublicUrl = "mcp.oauth.public_url";

    /// <summary>True lets a client register itself at the registration endpoint. Default true. Read when the server starts.</summary>
    public const string DynamicRegistration = "mcp.oauth.dynamic_registration";

    /// <summary>
    /// The https redirect URIs a self-registered client may use, each exact,
    /// empty by default, at most <see cref="DynamicRedirectUrisMax"/>. Each
    /// entry is an https URI that passes the redirect rules and is at most
    /// <see cref="DynamicRedirectUriMaxLength"/> characters, checked when it is
    /// set and again at start, where a bad entry turns the flow off. With it
    /// empty a self-registered client may send its answer only to a loopback
    /// address, that is a program on the computer the browser runs on. Read
    /// when the server starts.
    /// </summary>
    public const string DynamicRedirectUris = "mcp.oauth.dynamic_redirect_uris";

    /// <summary>
    /// The most self-registered clients that have never had a grant approved.
    /// Administrator-registered and once-approved clients never count. Default
    /// 100, 1 to 10,000.
    /// </summary>
    public const string MaxPendingClients = "mcp.oauth.max_pending_clients";

    /// <summary>
    /// The most never-approved self-registered clients one source address may
    /// hold (IPv4 exact, IPv6 by /64), counted in the database. Removed
    /// clients are not counted. Default 10, 1 to 1,000.
    /// </summary>
    public const string PendingClientsPerAddress = "mcp.oauth.pending_clients_per_address";

    /// <summary>Dynamic registrations one source address may make in an hour, counted in memory. Default 10, 1 to 1,000.</summary>
    public const string RegistrationsPerHour = "mcp.oauth.registrations_per_hour";

    /// <summary>How long an access token lasts, in minutes. Default 60, 5 to 1,440.</summary>
    public const string AccessTokenMinutes = "mcp.oauth.access_token_minutes";

    /// <summary>How long an unused refresh token lasts, in days. Default 30, 1 to 365.</summary>
    public const string RefreshTokenDays = "mcp.oauth.refresh_token_days";

    /// <summary>How long a grant lasts however it is used, in days, before the person approves again. Default 90, 1 to 365.</summary>
    public const string GrantDays = "mcp.oauth.grant_days";

    /// <summary>How long an authorization code lasts, in seconds. Default 60, 10 to 600. A code is used once.</summary>
    public const string CodeSeconds = "mcp.oauth.code_seconds";

    /// <summary>The settings read when the server starts, so a change to one applies after a restart.</summary>
    public static readonly IReadOnlySet<string> ReadAtStart =
        new HashSet<string>(StringComparer.Ordinal) { Enabled, PublicUrl, DynamicRegistration, DynamicRedirectUris };

    public const int MaxPendingClientsDefault = 100;
    public const int PendingClientsPerAddressDefault = 10;
    public const int RegistrationsPerHourDefault = 10;
    public const int AccessTokenMinutesDefault = 60;
    public const int RefreshTokenDaysDefault = 30;
    public const int GrantDaysDefault = 90;
    public const int CodeSecondsDefault = 60;

    /// <summary>The most entries <see cref="DynamicRedirectUris"/> may hold.</summary>
    public const int DynamicRedirectUrisMax = 20;

    /// <summary>The longest redirect URI a self-registered client may give, which bounds its row in the change record.</summary>
    public const int DynamicRedirectUriMaxLength = 512;

    /// <summary>The longest redirect URI an administrator may register.</summary>
    public const int AdministratorRedirectUriMaxLength = 2_000;

    /// <summary>The most redirect URIs a registration may submit; at most five are stored.</summary>
    public const int RedirectUrisSubmittedMax = 10;

    /// <summary>The longest client name, after trimming. It is never cut.</summary>
    public const int ClientNameMaxLength = 64;

    /// <summary>How long a self-registered client that was never approved lives.</summary>
    public const int PendingClientHours = 24;
}

/// <summary>
/// The paths of the flow. None is mapped while the flow is off, so each one
/// answers exactly what an unmapped path answers for the same caller and
/// method: to an anonymous caller or a bearer that does not resolve, a 401
/// with <c>WWW-Authenticate: Bearer</c> and the body
/// <c>{"error":"Sign in, or present an agent token."}</c>; to a valid agent
/// token or a signed-in GET, an empty 404; to a signed-in POST without the
/// session's anti-forgery token, today's 403 JSON, and with it, a 404. The
/// design report's section 2 table is the full statement.
/// </summary>
public static class OAuthPaths
{
    /// <summary>Protected resource metadata (RFC 9728) for the resource <c>/mcp</c>, at its path-inserted well-known address.</summary>
    public const string ProtectedResourceMetadata = "/.well-known/oauth-protected-resource/mcp";

    /// <summary>The same document, byte for byte, at the root well-known address, which a client tries second.</summary>
    public const string ProtectedResourceMetadataRoot = "/.well-known/oauth-protected-resource";

    /// <summary>Authorization server metadata (RFC 8414). The issuer has no path.</summary>
    public const string AuthorizationServerMetadata = "/.well-known/oauth-authorization-server";

    /// <summary>Dynamic client registration (RFC 7591), POST. Not mapped when dynamic registration is off.</summary>
    public const string Register = "/oauth/register";

    /// <summary>
    /// The authorization endpoint, GET. It serves no page: it answers 303 to
    /// <see cref="Consent"/> with the query rebuilt from every parsed
    /// parameter, each name and value escaped again, repeats kept. A request
    /// whose forward would be too long gets a plain-text 400.
    /// </summary>
    public const string Authorize = "/oauth/authorize";

    /// <summary>
    /// The consent page, GET and POST, for a signed-in person only. The
    /// portal's. The POST answers with a page holding a link, never a redirect.
    /// </summary>
    public const string Consent = "/portal/oauth/consent";

    /// <summary>The token endpoint, POST: the authorization code and refresh token grants only.</summary>
    public const string Token = "/oauth/token";

    /// <summary>Token revocation (RFC 7009), POST, for a client ending its own grant.</summary>
    public const string Revoke = "/oauth/revoke";

    /// <summary>Every grant, for an administrator. The portal's. A person's own are on the connect page.</summary>
    public const string Grants = "/portal/oauth/grants";

    /// <summary>Every registered client, for an administrator. The portal's.</summary>
    public const string Clients = "/portal/oauth/clients";
}

/// <summary>
/// The one scope: search and fetch, read-only. An omitted scope means
/// <see cref="Read"/>; every other value, <see cref="OfflineAccess"/> and
/// <c>openid</c> included, is ignored and never refused. Every grant stores
/// <see cref="Read"/>, and every token response says <c>scope: read</c>.
/// </summary>
public static class OAuthScopes
{
    public const string Read = "read";

    /// <summary>Ignored: a refresh token is issued whether or not it is asked for. Not advertised.</summary>
    public const string OfflineAccess = "offline_access";
}

/// <summary>
/// The prefixes that tell the flow's values apart from an agent token
/// (<c>prem_agt_</c>) and from each other, so none is accepted where another
/// belongs. Tokens have the agent token's shape: a 24 hex id and a 32 byte
/// secret, of which only the SHA-256 is stored.
/// </summary>
public static class OAuthPrefixes
{
    public const string AccessToken = "prem_oat_";
    public const string RefreshToken = "prem_ort_";
    public const string ClientId = "prem_cli_";
}

/// <summary>
/// The flow as this process runs it, read once at start. Registered only while
/// the flow is on and its settings are usable; its absence is how every host
/// knows the flow is off.
/// </summary>
/// <param name="PublicUrl">The value of <see cref="OAuthSettings.PublicUrl"/>, already canonical.</param>
/// <param name="DynamicRedirectUris">The value of <see cref="OAuthSettings.DynamicRedirectUris"/>, each entry already checked.</param>
public sealed record OAuthDeployment(string PublicUrl, bool DynamicRegistration, IReadOnlyList<string> DynamicRedirectUris)
{
    /// <summary>The issuer identifier: the public address itself.</summary>
    public string Issuer => PublicUrl;

    /// <summary>The canonical address of the resource, the audience every access token is bound to.</summary>
    public string Resource => PublicUrl + "/mcp";

    /// <summary>What the 401 from <c>/mcp</c> points a client at.</summary>
    public string ResourceMetadataUrl => PublicUrl + OAuthPaths.ProtectedResourceMetadata;
}

/// <summary>How a client came to be registered.</summary>
public enum OAuthClientRegistration
{
    /// <summary>It registered itself, with no one signed in.</summary>
    Dynamic = 1,

    /// <summary>An administrator registered it, at the command line or in the portal.</summary>
    Administrator = 2,
}

/// <summary>A registered client, as the consent page shows it.</summary>
/// <param name="Name">What the client calls itself: self-asserted, shown as text and never as a link.</param>
/// <param name="RegisteredByActor">For an administrator's registration, who: <c>cli:&lt;account&gt;</c> or <c>portal:&lt;user id&gt;</c>.</param>
/// <param name="StatedModelLocation">
/// Where the client's model runs, as an administrator stated it; null for a
/// self-registered client and for an administrator who stated nothing. When
/// set, the agent takes it and the consent page shows it instead of asking.
/// </param>
/// <param name="StatedModelVendor">Who runs a hosted model, as an administrator stated it.</param>
public sealed record OAuthClient(
    string Id,
    string Name,
    IReadOnlyList<string> RedirectUris,
    string? ApplicationType,
    OAuthClientRegistration RegisteredBy,
    string? RegisteredByActor,
    ModelLocation? StatedModelLocation,
    string? StatedModelVendor,
    DateTimeOffset CreatedAt,
    bool Disabled);

/// <summary>One registered client, as the administrator's list shows it.</summary>
/// <param name="RegisteredFrom">The source address of a self-registration; null for an administrator's.</param>
/// <param name="LiveGrants">Grants that are live by the liveness predicate.</param>
/// <param name="Document">The metadata document an administrator stored it from, by its hash; null for any other client.</param>
public sealed record OAuthClientListing(
    OAuthClient Client, string? RegisteredFrom, DateTimeOffset? FirstApprovedAt, int LiveGrants, OAuthStoredDocument? Document = null);

/// <summary>An authorization request that has passed every check.</summary>
/// <param name="RedirectUri">
/// Where the answer goes, exactly as the request named it (a loopback URI may
/// carry another port than the registered one), or the one registered URI
/// when the request named none.
/// </param>
/// <param name="Scope">Always <see cref="OAuthScopes.Read"/>.</param>
/// <param name="Resource">The canonical resource the request named.</param>
/// <param name="State">Passed back untouched, or null when the client sent none.</param>
public sealed record OAuthAuthorizationRequest(
    OAuthClient Client,
    string RedirectUri,
    string CodeChallenge,
    string Scope,
    string Resource,
    string? State);

/// <summary>What to do with an authorization request.</summary>
public enum AuthorizationCheckOutcome
{
    /// <summary>Show the consent page.</summary>
    Valid = 1,

    /// <summary>
    /// The client or the redirect URI is wrong, or the client is disabled,
    /// removed or unknown: show the error on the page, with no link, and
    /// never send the browser anywhere.
    /// </summary>
    ShowError = 2,

    /// <summary>
    /// Anything else is wrong: show the error with a link that returns the
    /// person to the client. Never sent automatically.
    /// </summary>
    ReturnError = 3,
}

/// <param name="Request">Set for <see cref="AuthorizationCheckOutcome.Valid"/>.</param>
/// <param name="Error">The OAuth error code, for the two error outcomes.</param>
/// <param name="Message">One sentence for the person, for the two error outcomes.</param>
/// <param name="ReturnUrl">For <see cref="AuthorizationCheckOutcome.ReturnError"/>: the redirect URI with error, state and iss, for the page's link.</param>
public sealed record AuthorizationCheck(
    AuthorizationCheckOutcome Outcome,
    OAuthAuthorizationRequest? Request,
    string? Error,
    string? Message,
    string? ReturnUrl);

/// <summary>What the consent page shows about one request, for one person.</summary>
/// <param name="RedirectOrigin">The scheme, host and port the answer goes to, cut from the matched request string, shown large.</param>
/// <param name="LoopbackRedirect">
/// True when the answer goes to a loopback address: to whatever program is
/// listening there, which on a shared computer can be another person's. The
/// page says so in words.
/// </param>
/// <param name="LocalAllowed">
/// Whether the person may say the model runs inside the network: only for a
/// loopback redirect, or for a client an administrator stated local. The page
/// preselects hosted in every case.
/// </param>
/// <param name="ReplacesGrant">
/// True when approving would keep the person's existing agent for this client
/// (it is live and enabled and its model location and vendor match), judged
/// with the location the page shows: exact for a client with a stated
/// location, and for the preselected answer otherwise. The page says that a
/// different answer makes a new assistant and ends the old one.
/// </param>
/// <param name="AgentName">The kept agent's name, or "a new connected assistant" when approving creates one.</param>
/// <param name="KeptAgentModelLocation">The kept agent's model location, shown beside the question; null for a new agent.</param>
/// <param name="KeptAgentModelVendor">The kept agent's vendor; null for a new agent or a local one.</param>
/// <param name="ConnectedAssistants">
/// The person's connected assistants that hold a slot, by the one count the
/// connect form and the approval also use.
/// </param>
/// <param name="Bound">
/// What <c>agents.self_service_max</c> allows. Approve is refused only when it
/// would take a slot past the bound: a new agent, or a kept agent whose
/// previous grant has ended. A kept agent over a live grant already holds its
/// slot, so a person at the bound can always approve such a client again.
/// </param>
public sealed record ConsentView(
    OAuthAuthorizationRequest Request,
    string RedirectOrigin,
    bool LoopbackRedirect,
    bool LocalAllowed,
    bool ReplacesGrant,
    string AgentName,
    ModelLocation? KeptAgentModelLocation,
    string? KeptAgentModelVendor,
    int ConnectedAssistants,
    int Bound);

/// <summary>
/// The consent step, for the portal's page. The page reads the query, asks
/// <see cref="CheckAsync"/>, shows <see cref="ViewAsync"/>, and on a post from
/// a signed-in person (with the anti-forgery token when there is a session)
/// calls <see cref="ApproveAsync"/> or <see cref="DenyAsync"/> and answers
/// with a page holding a link to the address returned, which the person
/// follows. It never answers the post with a redirect.
/// </summary>
public interface IOAuthConsent
{
    /// <summary>
    /// Checks a request's parameters, the same checks on the page and on the
    /// post. A parameter given twice is an error.
    /// </summary>
    Task<AuthorizationCheck> CheckAsync(IReadOnlyDictionary<string, IReadOnlyList<string>> parameters, CancellationToken ct);

    Task<ConsentView> ViewAsync(Guid userId, OAuthAuthorizationRequest request, CancellationToken ct);

    /// <summary>
    /// Checks the parameters again against the stored client, then keeps or
    /// creates the agent, starts the grant and records it, all in one change,
    /// and returns the redirect URI with the code, the state and the issuer.
    /// Never enables an agent and never changes an existing agent's model
    /// location.
    /// </summary>
    /// <param name="modelLocation">Where the assistant's model runs, which the person states unless the client has a stated location.</param>
    /// <param name="modelVendor">Who runs a hosted model; required for hosted, refused for local.</param>
    /// <exception cref="ArgumentException">
    /// Local was chosen where it is not allowed, or the vendor does not fit the
    /// location. Nothing is written.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// The request no longer checks (the client was disabled or removed), the
    /// request was already answered, the person is not a live, enabled
    /// account, self-service is off, or approving would take a slot past the
    /// bound. Nothing is written.
    /// </exception>
    Task<string> ApproveAsync(Guid userId, IReadOnlyDictionary<string, IReadOnlyList<string>> parameters,
        ModelLocation modelLocation, string? modelVendor, CancellationToken ct);

    /// <summary>
    /// Checks the parameters again, records the refusal, and returns the
    /// redirect URI with access_denied, the state and the issuer.
    /// </summary>
    /// <exception cref="InvalidOperationException">The request no longer checks. Nothing is written.</exception>
    Task<string> DenyAsync(Guid userId, IReadOnlyDictionary<string, IReadOnlyList<string>> parameters, CancellationToken ct);
}

/// <summary>
/// Where a grant stands, from the liveness predicate. A revoked grant shows
/// <see cref="EndedByDisable"/> when its reason is owner disabled, agent
/// disabled, or owner disabled or password changed; <see cref="EndedByClient"/>
/// for client disabled or client removed; <see cref="EndedByAddressChange"/>
/// for the server's address changed; and <see cref="Revoked"/> for any other
/// reason. An unrevoked grant that fails the predicate shows the matching
/// Ended status, in that order, then <see cref="Expired"/>; one never
/// exchanged shows <see cref="Pending"/> while its code can still be
/// exchanged and <see cref="Expired"/> after.
/// </summary>
public enum OAuthGrantStatus
{
    Live = 1,

    /// <summary>Past its end, or approved and never completed by its assistant before the code ran out.</summary>
    Expired = 2,

    /// <summary>Revoked by a person, an administrator, the client, or reuse; the reason says which.</summary>
    Revoked = 3,

    /// <summary>
    /// Its owner or its agent was disabled, or its owner's password was
    /// changed, after it was approved. Final: re-enabling does not bring it back.
    /// </summary>
    EndedByDisable = 4,

    /// <summary>Its client was disabled or removed.</summary>
    EndedByClient = 5,

    /// <summary>The server's public address changed, so its audience is no longer this server's.</summary>
    EndedByAddressChange = 6,

    /// <summary>
    /// Approved, and waiting for its assistant to exchange the code (at most
    /// <c>mcp.oauth.code_seconds</c>). It holds a slot in the bound only while
    /// that code can still be exchanged.
    /// </summary>
    Pending = 7,
}

/// <summary>One grant, as the connect page, the grants page and <c>prem oauth grants list</c> show it.</summary>
/// <param name="RefreshCount">How many times it was refreshed; a refresh writes no row in the change record.</param>
public sealed record OAuthGrantView(
    string GrantId,
    string ClientId,
    string ClientName,
    OAuthClientRegistration ClientRegistration,
    Guid UserId,
    string UserName,
    Guid AgentId,
    string AgentName,
    OAuthGrantStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    DateTimeOffset? LastRefreshedAt,
    int RefreshCount,
    DateTimeOffset? RevokedAt,
    string? RevokedReason);

/// <summary>Which grants a list shows.</summary>
public enum OAuthGrantFilter
{
    /// <summary>Live grants only, the default.</summary>
    Live = 1,

    All = 2,
}

/// <summary>
/// Listing and revoking grants. Revoking ends the grant, disables its agent,
/// and makes every token of it reach nothing from the next call; revoking one
/// already revoked changes nothing and records nothing. Works whether the flow
/// is on or off, so a revocation is never refused.
/// </summary>
public interface IOAuthGrants
{
    /// <summary>The person's own grants, ended ones included, newest first.</summary>
    Task<IReadOnlyList<OAuthGrantView>> ListOwnAsync(Guid userId, CancellationToken ct);

    /// <exception cref="InvalidOperationException">The grant is not the person's. The same answer as for no such grant.</exception>
    Task RevokeOwnAsync(Guid userId, string grantId, CancellationToken ct);

    /// <summary>Every grant, for an administrator; live ones unless <paramref name="filter"/> says all.</summary>
    Task<IReadOnlyList<OAuthGrantView>> ListAllAsync(OAuthGrantFilter filter, CancellationToken ct);

    /// <summary>Revokes any grant, recorded under <paramref name="actor"/>.</summary>
    Task RevokeAsync(Admin.AdminActor actor, string grantId, CancellationToken ct);
}

/// <summary>
/// Registering and managing clients, for an administrator. Works whether the
/// flow is on or off, so a client can be registered before the flow is turned
/// on. Disabling or removing a client ends every grant it has and disables
/// each grant's agent, in the same change; enabling it again brings none back.
/// Removing is a soft delete. None of this blocks the software itself, whose
/// name and software id are self-asserted: it ends that one registration.
/// </summary>
public interface IOAuthClients
{
    /// <summary>Every client not removed.</summary>
    Task<IReadOnlyList<OAuthClientListing>> ListAsync(CancellationToken ct);

    /// <summary>Registers a client and returns its id.</summary>
    /// <param name="id">
    /// An https URL the client uses as its id, for a client configured with
    /// one; null to have the server make a <c>prem_cli_</c> id.
    /// </param>
    /// <param name="redirectUris">One to five, https or loopback, never a private-use scheme.</param>
    /// <param name="modelLocation">Where the client's model runs, when the administrator can say; null leaves it to each person.</param>
    /// <param name="modelVendor">Who runs a hosted model; required for hosted, refused for local.</param>
    /// <exception cref="ArgumentException">Something given cannot be registered. Nothing is written.</exception>
    Task<string> AddAsync(Admin.AdminActor actor, string? id, string name, IReadOnlyList<string> redirectUris,
        ModelLocation? modelLocation, string? modelVendor, CancellationToken ct);

    /// <summary>
    /// Registers an assistant that names itself by an https address, from a
    /// copy of its metadata document an administrator saved by hand, and
    /// returns the document as read. The document is checked by
    /// <see cref="OAuthClientDocument.Parse"/>, as the command line checks a
    /// file, so both refuse the same things with the same sentences; its name
    /// and redirect addresses come from it; only its hash and when it was
    /// stored are kept; nothing is fetched.
    /// </summary>
    /// <param name="id">The https address the assistant names itself by. The document's client_id must be exactly this.</param>
    /// <param name="document">
    /// The document's bytes. More than <see cref="OAuthClientDocument.MaxBytes"/>
    /// is refused before it is parsed; <see cref="OAuthClientDocument.ReadBytes(Stream)"/>
    /// reads an upload without taking more than one byte past that.
    /// </param>
    /// <exception cref="ArgumentException">The document will not do, or the model statement is incomplete; the message says why. Nothing is written.</exception>
    /// <exception cref="InvalidOperationException">A client with that id is registered already. Nothing is written.</exception>
    Task<OAuthClientDocument> AddFromDocumentAsync(Admin.AdminActor actor, string id, ReadOnlyMemory<byte> document,
        ModelLocation? modelLocation, string? modelVendor, CancellationToken ct);

    /// <summary>
    /// Replaces, in place, the stored document of a client registered from
    /// one, when its vendor has changed it: the same https id, checked the
    /// same way as an add. The name and the redirect addresses come from the
    /// new document, and its hash and the time it was stored move together;
    /// the change record's row holds the old hash and the new. Every grant a
    /// person gave under the old document stands, and a code is exchanged
    /// only for a redirect address the new document lists. The model
    /// statement and who registered the client stay as they were. A file
    /// that is the stored document already changes nothing and records
    /// nothing.
    /// </summary>
    /// <exception cref="ArgumentException">The document will not do, its client_id among it; the message says why. Nothing is written.</exception>
    /// <exception cref="InvalidOperationException">No client has that id, or it was not registered from a metadata document. Nothing is written.</exception>
    Task<OAuthClientDocumentReplaced> ReplaceDocumentAsync(Admin.AdminActor actor, string id, ReadOnlyMemory<byte> document,
        CancellationToken ct);

    Task DisableAsync(Admin.AdminActor actor, string clientId, CancellationToken ct);

    Task EnableAsync(Admin.AdminActor actor, string clientId, CancellationToken ct);

    Task RemoveAsync(Admin.AdminActor actor, string clientId, CancellationToken ct);
}

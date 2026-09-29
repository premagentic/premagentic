using System.Security.Cryptography;
using System.Text;
using Premagentic.Core.Storage;
using NpgsqlTypes;

namespace Premagentic.Core.Identity;

/// <summary>How long a person's sign-in lasts.</summary>
/// <param name="IdleTimeout">A session with no request for this long has ended.</param>
/// <param name="AbsoluteLifetime">A session ends this long after sign-in, however busy it is.</param>
public sealed record SessionPolicy(TimeSpan IdleTimeout, TimeSpan AbsoluteLifetime)
{
    /// <summary>Thirty minutes idle, eight hours at most.</summary>
    public static SessionPolicy Default { get; } = new(TimeSpan.FromMinutes(30), TimeSpan.FromHours(8));
}

/// <summary>
/// A session just started. <see cref="Value"/> is the session id the browser
/// keeps in its cookie; it is here and in that cookie, and nowhere else.
/// </summary>
public sealed class IssuedSession
{
    internal IssuedSession(string value, DateTimeOffset expiresAt)
    {
        Value = value;
        ExpiresAt = expiresAt;
    }

    public string Value { get; }

    /// <summary>The absolute expiry. The session can end sooner, by idling, sign-out or a change to the user.</summary>
    public DateTimeOffset ExpiresAt { get; }

    /// <summary>Never includes the session id, so it is safe in a log line.</summary>
    public override string ToString() => $"session expiring {ExpiresAt:u}";
}

/// <summary>
/// People's sign-in sessions for one tenant.
/// <para>
/// A session id is 32 random bytes in unpadded base64url, issued fresh at every
/// sign-in. Only its SHA-256 is stored. A session is honored only while it is
/// within both expiries and its user is live and enabled, and disabling a user
/// or setting a new password deletes the user's sessions
/// (<see cref="IdentityStore.SetUserDisabledAsync"/>,
/// <see cref="IdentityStore.SetPasswordHashAsync"/>).
/// </para>
/// </summary>
public sealed class SessionStore(PremagenticDatabase db, Guid tenantId, TimeProvider? time = null, SessionPolicy? policy = null)
{
    /// <summary>Random bytes in a session id.</summary>
    public const int SecretBytes = 32;

    // A request refreshes last_seen_at only when it is older than this, so a
    // busy session does not write on every request. It is also how late the
    // idle timeout can be, at most.
    private static readonly TimeSpan TouchInterval = TimeSpan.FromMinutes(1);

    // What the anti-forgery token is derived for. Changing it invalidates every
    // token in use, which is the way to rotate them.
    private static readonly byte[] AntiForgeryLabel = Encoding.ASCII.GetBytes("premagentic.antiforgery.v1");

    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public SessionPolicy Policy { get; } = policy ?? SessionPolicy.Default;

    /// <summary>Starts a new session for a user. Expired sessions of the tenant are swept on the way.</summary>
    public async Task<IssuedSession> StartAsync(Guid userId, CancellationToken ct = default)
    {
        var now = _time.GetUtcNow();
        var secret = RandomNumberGenerator.GetBytes(SecretBytes);
        try
        {
            var expires = now + Policy.AbsoluteLifetime;
            await using var cmd = db.DataSource.CreateCommand("""
                WITH swept AS (
                    DELETE FROM prem_config.user_session
                    WHERE tenant_id = @tenant AND (expires_at <= @now OR last_seen_at <= @idleCutoff))
                INSERT INTO prem_config.user_session(id_sha256, tenant_id, user_id, created_at, last_seen_at, expires_at)
                VALUES(@hash, @tenant, @user, @now, @now, @expires)
                """);
            cmd.Parameters.AddWithValue("hash", SHA256.HashData(secret));
            cmd.Parameters.AddWithValue("tenant", tenantId);
            cmd.Parameters.AddWithValue("user", userId);
            cmd.Parameters.AddWithValue("now", now);
            cmd.Parameters.AddWithValue("idleCutoff", now - Policy.IdleTimeout);
            cmd.Parameters.AddWithValue("expires", expires);
            await cmd.ExecuteNonQueryAsync(ct);
            return new IssuedSession(StrictBase64Url.Encode(secret), expires);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
        }
    }

    /// <summary>
    /// The user a presented session id belongs to, or null when it is malformed,
    /// unknown, expired, idle too long, or its user is gone or disabled.
    /// </summary>
    public async Task<User?> FindUserAsync(string? presented, CancellationToken ct = default)
    {
        if (!TryHash(presented, out var hash)) return null;
        var now = _time.GetUtcNow();

        User? user = null;
        DateTimeOffset lastSeen = default;
        await using (var cmd = db.DataSource.CreateCommand("""
            SELECT u.id, u.sign_in_name, u.role, u.disabled, s.last_seen_at
            FROM prem_config.user_session s
            JOIN prem_config.app_user u ON u.tenant_id = s.tenant_id AND u.id = s.user_id
            WHERE s.tenant_id = @tenant AND s.id_sha256 = @hash
              AND s.expires_at > @now AND s.last_seen_at > @idleCutoff
              AND u.deleted_at IS NULL AND NOT u.disabled
            """))
        {
            cmd.Parameters.AddWithValue("tenant", tenantId);
            cmd.Parameters.AddWithValue("hash", hash);
            cmd.Parameters.AddWithValue("now", now);
            cmd.Parameters.AddWithValue("idleCutoff", now - Policy.IdleTimeout);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                user = IdentityStore.ReadUser(reader);
                lastSeen = reader.GetFieldValue<DateTimeOffset>(4);
            }
        }

        if (user is not null && now - lastSeen >= TouchInterval)
        {
            await using var touch = db.DataSource.CreateCommand(
                "UPDATE prem_config.user_session SET last_seen_at = @now WHERE tenant_id = @tenant AND id_sha256 = @hash");
            touch.Parameters.AddWithValue("tenant", tenantId);
            touch.Parameters.AddWithValue("hash", hash);
            touch.Parameters.AddWithValue("now", now);
            await touch.ExecuteNonQueryAsync(ct);
        }
        return user;
    }

    /// <summary>Ends one session: sign-out.</summary>
    /// <returns>False when there was no such session.</returns>
    public async Task<bool> EndAsync(string? presented, CancellationToken ct = default)
    {
        if (!TryHash(presented, out var hash)) return false;
        await using var cmd = db.DataSource.CreateCommand(
            "DELETE FROM prem_config.user_session WHERE tenant_id = @tenant AND id_sha256 = @hash");
        cmd.Parameters.AddWithValue("tenant", tenantId);
        cmd.Parameters.Add(new Npgsql.NpgsqlParameter("hash", NpgsqlDbType.Bytea) { Value = hash });
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    /// <summary>
    /// The anti-forgery token for a session: an HMAC keyed by the session id, so
    /// only a holder of the session id can compute it, and a page on another
    /// site, which can make the browser send the cookie but cannot read it,
    /// cannot. Nothing about it is stored.
    /// </summary>
    public static string AntiForgeryToken(string sessionValue)
    {
        if (!StrictBase64Url.TryDecode(sessionValue, out var secret) || secret.Length != SecretBytes)
            throw new ArgumentException("Not a session id.", nameof(sessionValue));
        try
        {
            return StrictBase64Url.Encode(HMACSHA256.HashData(secret, AntiForgeryLabel));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
        }
    }

    /// <summary>True only when <paramref name="presentedToken"/> is the anti-forgery token of this session. Never throws.</summary>
    public static bool AntiForgeryMatches(string? sessionValue, string? presentedToken)
    {
        if (string.IsNullOrEmpty(presentedToken) || !StrictBase64Url.TryDecode(sessionValue, out var secret)
            || secret.Length != SecretBytes)
            return false;
        try
        {
            var expected = HMACSHA256.HashData(secret, AntiForgeryLabel);
            return StrictBase64Url.TryDecode(presentedToken, out var presented)
                && CryptographicOperations.FixedTimeEquals(expected, presented);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
        }
    }

    private static bool TryHash(string? presented, out byte[] hash)
    {
        hash = [];
        if (!StrictBase64Url.TryDecode(presented, out var secret) || secret.Length != SecretBytes) return false;
        hash = SHA256.HashData(secret);
        CryptographicOperations.ZeroMemory(secret);
        return true;
    }
}

using System.Security.Cryptography;

namespace Premagentic.Core.Identity;

/// <summary>When repeated wrong passwords lock an account.</summary>
/// <param name="LockAfterFailures">The failure that locks the account, and every one after it until a success.</param>
/// <param name="LockFor">How long each lock lasts.</param>
public sealed record LockoutPolicy(int LockAfterFailures, TimeSpan LockFor)
{
    /// <summary>Five wrong passwords lock for fifteen minutes; after that, one guess per fifteen minutes.</summary>
    public static LockoutPolicy Default { get; } = new(5, TimeSpan.FromMinutes(15));
}

/// <summary>What sign-in needs to know about one account. The hash never leaves the identity layer.</summary>
internal sealed record SignInAccount(User User, string? PasswordHash, int FailedSignIns, DateTimeOffset? LockedUntil);

/// <summary>
/// The outcome of one sign-in attempt. A failure carries nothing, whatever the
/// reason, so no caller can tell a wrong password from an unknown name, a
/// disabled account or a locked one.
/// </summary>
public sealed class SignInResult
{
    private SignInResult(User? user, IssuedSession? session)
    {
        User = user;
        Session = session;
    }

    internal static SignInResult Failed { get; } = new(null, null);

    internal static SignInResult Succeeded(User user, IssuedSession session) => new(user, session);

    public bool IsSuccess => Session is not null;

    public User? User { get; }

    public IssuedSession? Session { get; }

    /// <summary>Never includes the session id, so it is safe in a log line.</summary>
    public override string ToString() => IsSuccess ? $"signed in as {User!.Name}" : "sign-in failed";
}

/// <summary>
/// Signs people in with a sign-in name and a password.
/// <para>
/// Every attempt costs exactly one full password verification, whatever
/// happens: an unknown name, or an account with no password, is checked
/// against a fixed dummy hash made with the same work factor, and a disabled
/// or locked account is still checked. The time an attempt takes therefore
/// does not say which names exist or why an attempt failed. A success clears
/// the failure count, rehashes a password stored with less work than this
/// hasher uses, and starts a fresh session.
/// </para>
/// </summary>
public sealed class SignInService
{
    private readonly PasswordHasher _hasher;
    private readonly LockoutPolicy _lockout;
    private readonly string _dummyHash;

    public SignInService(PasswordHasher? hasher = null, LockoutPolicy? lockout = null)
    {
        _hasher = hasher ?? new PasswordHasher();
        _lockout = lockout ?? LockoutPolicy.Default;
        if (_lockout.LockAfterFailures < 1 || _lockout.LockFor <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(lockout), "A lockout needs at least one failure and a positive time.");

        // The password behind it is random and thrown away, so nothing verifies against it.
        _dummyHash = _hasher.Hash(Convert.ToHexString(RandomNumberGenerator.GetBytes(32)));
    }

    public async Task<SignInResult> SignInAsync(
        IdentityStore identity, SessionStore sessions, string? signInName, string? password, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(sessions);

        var account = string.IsNullOrWhiteSpace(signInName) || signInName.Length > IdentityNames.MaxLength
            ? null
            : await identity.FindSignInAccountAsync(signInName, ct);

        var passwordMatches = _hasher.Verify(password, account?.PasswordHash ?? _dummyHash);

        if (account?.PasswordHash is not { } storedHash) return SignInResult.Failed;
        var user = account.User;
        if (user.Disabled) return SignInResult.Failed;
        if (account.LockedUntil is { } lockedUntil && lockedUntil > identity.Time.GetUtcNow()) return SignInResult.Failed;

        if (!passwordMatches)
        {
            await identity.RecordFailedSignInAsync(user.Id, _lockout, ct);
            return SignInResult.Failed;
        }

        await identity.RecordSignInAsync(user.Id, ct);
        if (_hasher.NeedsRehash(storedHash))
            await identity.RehashPasswordAsync(user.Id, storedHash, _hasher.Hash(password!), ct);

        return SignInResult.Succeeded(user, await sessions.StartAsync(user.Id, ct));
    }
}

using System.Diagnostics;
using Premagentic.Core.Identity;
using Premagentic.Core.Storage;
using Xunit.Abstractions;

namespace Premagentic.Tests;

/// <summary>
/// Sign-in and sessions below HTTP: what an attempt costs, rehashing, and the
/// anti-forgery token's binding to its session.
/// Requires a running Docker daemon.
/// </summary>
public sealed class IdentitySignInTests(DatastoreTestDatabase server, ITestOutputHelper output) : IClassFixture<DatastoreTestDatabase>
{
    private const string Password = "a lantern in the long hall";

    private async Task<(PremagenticDatabase Db, IdentityStore Identity, SessionStore Sessions, TestClock Clock)> NewAsync()
    {
        var db = new PremagenticDatabase(await server.CreateDatabaseAsync());
        await db.InitializeAsync();
        var tenant = await db.EnsureTenantAsync("t", "T");
        var clock = new TestClock(new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.Zero));
        return (db, new IdentityStore(db, tenant, clock), new SessionStore(db, tenant, clock), clock);
    }

    [Fact]
    public async Task An_unknown_name_costs_as_much_as_a_wrong_password()
    {
        var (db, identity, sessions, _) = await NewAsync();
        await using var _ = db;

        // A work factor far above the default, so one verification dwarfs
        // everything else an attempt does and the timing is not noise. The
        // lockout is out of reach, so every wrong password below takes the
        // wrong-password path and none becomes a locked account.
        var hasher = new PasswordHasher(2_000_000);
        var signIn = new SignInService(hasher, new LockoutPolicy(100, TimeSpan.FromMinutes(15)));
        var user = await identity.CreateUserAsync("frankie", "Frankie", Role.Member);
        await identity.SetPasswordHashAsync(user.Id, hasher.Hash(Password));
        var disabled = await identity.CreateUserAsync("gale", "Gale", Role.Member);
        await identity.SetPasswordHashAsync(disabled.Id, hasher.Hash(Password));
        await identity.SetUserDisabledAsync(disabled.Id, true);

        async Task<TimeSpan> Time(string name, string password)
        {
            var sw = Stopwatch.StartNew();
            var result = await signIn.SignInAsync(identity, sessions, name, password);
            sw.Stop();
            Assert.False(result.IsSuccess);
            return sw.Elapsed;
        }

        (string Name, string SignInName, string Password)[] paths =
        [
            ("wrong password", "frankie", "not the password"),
            ("unknown name", "nobody-at-all", Password),
            ("disabled account", "gale", Password),
        ];
        var times = paths.ToDictionary(p => p.Name, _ => new List<TimeSpan>());

        // Each path once unmeasured, then five measured rounds, each round in a
        // different order so no path always goes first or last. Medians, so one
        // attempt slowed by a busy machine does not decide the comparison.
        foreach (var p in paths)
            await Time(p.SignInName, p.Password);
        const int Rounds = 5;
        for (var round = 0; round < Rounds; round++)
            for (var i = 0; i < paths.Length; i++)
            {
                var p = paths[(i + round) % paths.Length];
                times[p.Name].Add(await Time(p.SignInName, p.Password));
            }

        TimeSpan Median(string name) => times[name].Order().ElementAt(Rounds / 2);
        string All(string name) => string.Join(", ", times[name].Select(t => $"{t.TotalMilliseconds:F0}"));
        var wrongPassword = Median("wrong password");
        var unknownName = Median("unknown name");
        var disabledAccount = Median("disabled account");
        foreach (var p in paths)
            output.WriteLine($"{p.Name}: median {Median(p.Name).TotalMilliseconds:F0} ms of {All(p.Name)}");

        Assert.True(wrongPassword > TimeSpan.FromMilliseconds(150),
            $"A verification took only {wrongPassword.TotalMilliseconds} ms (median of {All("wrong password")}).");
        Assert.True(unknownName > wrongPassword * 0.5,
            $"Unknown name {unknownName.TotalMilliseconds} ms (median of {All("unknown name")}) against wrong password " +
            $"{wrongPassword.TotalMilliseconds} ms (median of {All("wrong password")}).");
        Assert.True(disabledAccount > wrongPassword * 0.5,
            $"Disabled account {disabledAccount.TotalMilliseconds} ms (median of {All("disabled account")}) against wrong password " +
            $"{wrongPassword.TotalMilliseconds} ms (median of {All("wrong password")}).");
    }

    [Fact]
    public async Task A_success_rehashes_a_password_stored_with_less_work()
    {
        var (db, identity, sessions, _) = await NewAsync();
        await using var _ = db;
        var user = await identity.CreateUserAsync("frankie", "Frankie", Role.Member);
        var weak = new PasswordHasher().Hash(Password);
        await identity.SetPasswordHashAsync(user.Id, weak);

        var stronger = new PasswordHasher(PasswordHasher.DefaultIterations + 1);
        var result = await new SignInService(stronger).SignInAsync(identity, sessions, "frankie", Password);
        Assert.True(result.IsSuccess);

        var stored = (await identity.FindSignInAccountAsync("frankie"))!.PasswordHash!;
        Assert.NotEqual(weak, stored);
        Assert.False(stronger.NeedsRehash(stored));
        Assert.True(stronger.Verify(Password, stored));

        // A rehash is not a new password: the session it came with still works.
        Assert.NotNull(await sessions.FindUserAsync(result.Session!.Value));
    }

    [Fact]
    public async Task A_failed_attempt_never_starts_a_session_and_a_success_clears_the_count()
    {
        var (db, identity, sessions, _) = await NewAsync();
        await using var _ = db;
        var signIn = new SignInService();
        var user = await identity.CreateUserAsync("frankie", "Frankie", Role.Member);
        await identity.SetPasswordHashAsync(user.Id, new PasswordHasher().Hash(Password));

        for (var i = 0; i < 3; i++)
            Assert.Null((await signIn.SignInAsync(identity, sessions, "frankie", "wrong")).Session);
        Assert.Equal(3, (await identity.FindSignInAccountAsync("frankie"))!.FailedSignIns);

        Assert.True((await signIn.SignInAsync(identity, sessions, "FRANKIE", Password)).IsSuccess);
        var account = (await identity.FindSignInAccountAsync("frankie"))!;
        Assert.Equal(0, account.FailedSignIns);
        Assert.Null(account.LockedUntil);

        await using var count = db.DataSource.CreateCommand("SELECT count(*) FROM prem_config.user_session");
        Assert.Equal(1L, await count.ExecuteScalarAsync());
    }

    [Fact]
    public async Task The_anti_forgery_token_belongs_to_its_own_session_only()
    {
        var (db, identity, sessions, _) = await NewAsync();
        await using var _ = db;
        var user = await identity.CreateUserAsync("frankie", "Frankie", Role.Member);
        var one = await sessions.StartAsync(user.Id);
        var two = await sessions.StartAsync(user.Id);

        var token = SessionStore.AntiForgeryToken(one.Value);
        Assert.True(SessionStore.AntiForgeryMatches(one.Value, token));
        Assert.False(SessionStore.AntiForgeryMatches(two.Value, token));
        Assert.False(SessionStore.AntiForgeryMatches(one.Value, SessionStore.AntiForgeryToken(two.Value)));
        Assert.False(SessionStore.AntiForgeryMatches(one.Value, null));
        Assert.False(SessionStore.AntiForgeryMatches(one.Value, ""));
        Assert.False(SessionStore.AntiForgeryMatches(one.Value, one.Value));
        Assert.DoesNotContain(one.Value, one.ToString());
    }
}

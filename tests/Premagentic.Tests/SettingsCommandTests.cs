using System.Text;
using Premagentic.Cli.Admin;
using Premagentic.Core.Okf;
using Premagentic.Core.Storage;

namespace Premagentic.Tests;

/// <summary>
/// Tests that read what a command prints. The console is one per process, so
/// they run on their own, after every other test and never beside one.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ConsoleCollection
{
    public const string Name = "console";
}

/// <summary>Runs a command with the console captured, and hands each printed line to an optional probe as it is printed.</summary>
internal static class ConsoleCapture
{
    public static async Task<(int Exit, string Out, string Err)> RunAsync(Func<Task<int>> command, Action<string>? onLine = null)
    {
        var (oldOut, oldErr) = (Console.Out, Console.Error);
        var (output, error) = (new ProbeWriter(onLine), new ProbeWriter(null));
        Console.SetOut(output);
        Console.SetError(error);
        try
        {
            return (await command(), output.ToString(), error.ToString());
        }
        finally
        {
            Console.SetOut(oldOut);
            Console.SetError(oldErr);
        }
    }

    private sealed class ProbeWriter(Action<string>? onLine) : StringWriter
    {
        public override void WriteLine(string? value)
        {
            onLine?.Invoke(value ?? "");
            base.WriteLine(value);
        }
    }
}

/// <summary>
/// <c>prem settings</c> as an administrator uses it. Requires a running
/// Docker daemon.
/// </summary>
[Collection(ConsoleCollection.Name)]
public sealed class SettingsCommandTests(DatastoreTestDatabase server) : IClassFixture<DatastoreTestDatabase>
{
    private const string Agents = TrustSettingsStore.AgentsMinimumTier;

    private async Task<(PremagenticDatabase Db, Guid Tenant)> EmptyAsync()
    {
        var db = new PremagenticDatabase(await server.CreateDatabaseAsync());
        await db.InitializeAsync();
        return (db, await db.EnsureTenantAsync("t", "T"));
    }

    private static Task<(int Exit, string Out, string Err)> Settings(
        PremagenticDatabase db, Guid tenant, Action<string>? onLine, params string[] args) =>
        ConsoleCapture.RunAsync(() => SettingsCommands.RunAsync(["settings", .. args], db, tenant), onLine);

    // Read with the synchronous API, from inside a console write.
    private static string? StoredNow(PremagenticDatabase db, string key)
    {
        using var cmd = db.DataSource.CreateCommand("SELECT value::text FROM prem_config.setting WHERE key = @key");
        cmd.Parameters.AddWithValue("key", key);
        return cmd.ExecuteScalar() as string;
    }

    [Theory]
    [InlineData(Agents, "machine-confirmed")]
    [InlineData(Agents, "unverified")]
    [InlineData(TrustSettingsStore.Stale, "shown-to-everyone")]
    [InlineData(TrustSettingsStore.PeopleMinimumTier, "unverified")]
    public async Task A_looser_value_is_warned_about_before_it_is_saved(string key, string looser)
    {
        var (db, tenant) = await EmptyAsync();
        await using var _ = db;
        // People start at unverified, so give that key something to loosen from.
        if (key == TrustSettingsStore.PeopleMinimumTier)
            await Settings(db, tenant, null, "set", key, "human-reviewed");

        string? storedAtWarning = "(no warning was printed)";
        var run = await Settings(db, tenant, line =>
        {
            if (line.StartsWith("WARNING:", StringComparison.Ordinal)) storedAtWarning = StoredNow(db, key);
        }, "set", key, looser);

        Assert.Equal(0, run.Exit);
        // At the moment the warning was printed, the looser value was not yet stored.
        Assert.NotEqual($"\"{looser}\"", storedAtWarning);
        Assert.NotEqual("(no warning was printed)", storedAtWarning);
        Assert.Equal($"\"{looser}\"", StoredNow(db, key));
        Assert.Contains($"is now {looser}", run.Out);
    }

    [Theory]
    [InlineData(Agents, "human-reviewed")]
    [InlineData(TrustSettingsStore.PeopleMinimumTier, "machine-confirmed")]
    [InlineData(TrustSettingsStore.Stale, "hidden-from-everyone")]
    public async Task A_value_as_strict_or_stricter_is_saved_with_no_warning(string key, string value)
    {
        var (db, tenant) = await EmptyAsync();
        await using var _ = db;

        var run = await Settings(db, tenant, null, "set", key, value);

        Assert.Equal(0, run.Exit);
        Assert.DoesNotContain("WARNING", run.Out);
        Assert.Equal($"\"{value}\"", StoredNow(db, key));
    }

    [Theory]
    [InlineData(Agents, "2", "Allowed: unverified, machine-confirmed, human-reviewed")]
    [InlineData("trust.default_tier", "unverified", "The settings are: trust.agents_minimum_tier, trust.people_minimum_tier, trust.stale")]
    public async Task A_refused_value_names_what_is_allowed_and_saves_nothing(string key, string value, string allowed)
    {
        var (db, tenant) = await EmptyAsync();
        await using var _ = db;

        var run = await Settings(db, tenant, null, "set", key, value);

        Assert.Equal(1, run.Exit);
        Assert.Contains(allowed, run.Err);
        Assert.Null(StoredNow(db, key));
        Assert.Contains("The change record is empty.", (await Settings(db, tenant, null, "history")).Out);
    }

    [Fact]
    public async Task The_list_says_where_each_value_came_from_and_history_says_who_changed_it()
    {
        var (db, tenant) = await EmptyAsync();
        await using var _ = db;
        await Settings(db, tenant, null, "set", Agents, "machine-confirmed");
        await using (var raw = db.DataSource.CreateCommand(
            "INSERT INTO prem_config.setting(tenant_id, key, value) VALUES(@t, 'trust.stale', '42'::jsonb)"))
        {
            raw.Parameters.AddWithValue("t", tenant);
            await raw.ExecuteNonQueryAsync();
        }

        var list = (await Settings(db, tenant, null, "list")).Out.Split(Environment.NewLine);
        Assert.Contains(list, l => l.StartsWith(Agents) && l.Contains("machine-confirmed") && l.TrimEnd().EndsWith("set"));
        Assert.Contains(list, l => l.StartsWith(TrustSettingsStore.PeopleMinimumTier) && l.Contains("unverified") && l.TrimEnd().EndsWith("default"));
        Assert.Contains(list, l => l.StartsWith(TrustSettingsStore.Stale) && l.Contains("hidden-from-everyone") && l.Contains("UNREADABLE"));

        var history = (await Settings(db, tenant, null, "history")).Out;
        Assert.Contains("setting.set", history);
        Assert.Contains("(none) -> \"machine-confirmed\"", history);
        Assert.Contains($"by cli ({Core.Admin.AdminActor.Cli().Account})", history);
    }
}

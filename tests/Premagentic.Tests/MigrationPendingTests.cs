using System.Security.Cryptography;
using Premagentic.Cli.Setup;
using Premagentic.Core.Storage;
using Npgsql;

namespace Premagentic.Tests;

/// <summary>
/// What the runner reports as pending, beside the sections: a text match body
/// in the database that is not the one this build generates, which migrate
/// would install.
/// </summary>
public sealed class MigrationPendingTests(DatastoreTestDatabase server) : IClassFixture<DatastoreTestDatabase>, IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "premagentic-pending-tests", Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
    }

    /// <summary>Puts back a body other than this build's, as an older build would have left it, keeping the signature.</summary>
    private static async Task MakeTheBodyStaleAsync(string ownerConnection)
    {
        await using var source = NpgsqlDataSource.Create(ownerConnection);
        await using var read = source.CreateCommand("""
            SELECT pg_get_function_arguments(p.oid), pg_get_function_result(p.oid)
            FROM pg_proc p WHERE p.pronamespace = 'prem_index'::regnamespace AND p.proname = 'text_matches'
            """);
        string arguments, result;
        await using (var reader = await read.ExecuteReaderAsync())
        {
            Assert.True(await reader.ReadAsync(), "the text match function is not installed");
            (arguments, result) = (reader.GetString(0), reader.GetString(1));
        }
        await using var replace = source.CreateCommand(
            $"CREATE OR REPLACE FUNCTION prem_index.text_matches({arguments}) RETURNS {result} LANGUAGE plpgsql STABLE SECURITY DEFINER " +
            "SET search_path = pg_catalog, pg_temp AS $body$BEGIN RAISE EXCEPTION 'an older body'; END$body$");
        await replace.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task A_stale_text_match_body_is_pending_until_migrate_installs_this_builds()
    {
        var connection = await server.CreateDatabaseAsync();
        await using var db = new PremagenticDatabase(connection);
        await db.MigrateAsync();
        var runner = new MigrationRunner(db.DataSource);
        Assert.Empty(await runner.PendingAsync());

        await MakeTheBodyStaleAsync(connection);

        var pending = await runner.PendingAsync();
        var body = Assert.Single(pending);
        Assert.Equal(MigrationWork.TextMatchBody, body.Kind);
        Assert.Equal("the generated body of prem_index.text_matches (prem_index)", body.Label);

        // Migrate applies no section and installs the body, after which nothing is pending.
        Assert.Empty(await db.MigrateAsync());
        Assert.Empty(await runner.PendingAsync());
    }

    [Fact]
    public async Task Setup_plan_shows_a_stale_body_and_still_checks_what_follows()
    {
        var suffix = Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant();
        var install = new SetupOptions(
            AdminConnectionString: server.AdminConnectionString,
            CredentialsDirectory: Path.Combine(_folder, suffix),
            EmbedderFactory: _ => new SeededEmbeddingProvider(),
            RequiredModelDirectory: null,
            DatabaseName: "prem_" + suffix,
            OwnerRole: "owner_" + suffix,
            AppRole: "app_" + suffix,
            SearchRole: "search_" + suffix);
        var first = new StringWriter();
        Assert.False((await new SetupEngine(install, first).RunAsync()).Failed, first.ToString());
        await MakeTheBodyStaleAsync(CredentialsFile.ReadConnectionString(install.OwnerCredentialsPath));

        var output = new StringWriter();
        var report = await new SetupEngine(install with { Plan = true }, output).RunAsync();

        Assert.False(report.Failed, output.ToString());
        var migrations = report.Find("migrations")!;
        Assert.Equal(StepOutcome.WouldApply, migrations.Outcome);
        Assert.Equal($"would apply as {install.OwnerRole}: the generated body of prem_index.text_matches (prem_index)", migrations.Detail);
        // A body changes no grant, so the grants are still looked at, not assumed;
        // what needs the application role to start waits for the body.
        Assert.Equal(StepOutcome.Done, report.Find("grants.search")!.Outcome);
        Assert.Equal(StepOutcome.Warning, report.Find("administrator")!.Outcome);
    }
}

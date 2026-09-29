using Premagentic.Cli.Admin;
using Premagentic.Core.Embeddings;
using Premagentic.Core.Storage;

namespace Premagentic.Tests;

/// <summary>
/// A folder rule set or removed at the command line is in the change record,
/// as one set in the portal or by a profile is, and in the same shape. Requires
/// a running Docker daemon.
/// </summary>
[Collection(ConsoleCollection.Name)]
public sealed class RulesCommandRecordTests(DatastoreTestDatabase server) : IClassFixture<DatastoreTestDatabase>
{
    private async Task<(PremagenticDatabase Db, Guid Tenant)> EmptyAsync()
    {
        var db = new PremagenticDatabase(await server.CreateDatabaseAsync());
        await db.InitializeAsync();
        return (db, await db.EnsureTenantAsync("t", "T"));
    }

    private static Task<(int Exit, string Out, string Err)> Cli(PremagenticDatabase db, Guid tenant, params string[] args) =>
        ConsoleCapture.RunAsync(() => args[0] == "ingest"
            ? SourcesCommands.IngestAsync(args, db, tenant, headingPrefix: true, new HashEmbeddingProvider(), null)
            : AdminCommands.RunAsync(args, db, tenant));

    private static async Task<List<(string Kind, string Target, string? Old, string? New, string Surface)>> RecordAsync(PremagenticDatabase db)
    {
        await using var cmd = db.DataSource.CreateCommand(
            "SELECT kind, target, old_value::text, new_value::text, actor_surface FROM prem_config.admin_event ORDER BY id");
        var rows = new List<(string, string, string?, string?, string)>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            rows.Add((reader.GetString(0), reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetString(4)));
        return rows;
    }

    [Fact]
    public async Task A_rule_set_changed_and_removed_at_the_command_line_is_recorded_each_time()
    {
        var (db, tenant) = await EmptyAsync();
        await using var _ = db;
        Assert.Equal(0, (await Cli(db, tenant, "groups", "add", "Staff")).Exit);

        Assert.Equal(0, (await Cli(db, tenant, "rules", "set", "--prefix", "handbook", "--public")).Exit);
        Assert.Equal(0, (await Cli(db, tenant, "rules", "set", "--prefix", "handbook", "--principals", "group:Staff")).Exit);
        Assert.Equal(0, (await Cli(db, tenant, "rules", "remove", "--prefix", "handbook")).Exit);

        var rules = (await RecordAsync(db)).Where(r => r.Kind.StartsWith("rule.", StringComparison.Ordinal)).ToList();
        Assert.Equal(
            [
                (AdminCommands.RuleSetKind, "filesystem:handbook", false, true),
                (AdminCommands.RuleSetKind, "filesystem:handbook", true, true),
                (AdminCommands.RuleRemoveKind, "filesystem:handbook", true, false),
            ],
            rules.Select(r => (r.Kind, r.Target, r.Old is not null, r.New is not null)));
        Assert.All(rules, r => Assert.Equal("cli", r.Surface));
        Assert.Contains("everyone", rules[0].New);
    }

    [Fact]
    public async Task Removing_a_rule_that_is_not_there_records_nothing()
    {
        var (db, tenant) = await EmptyAsync();
        await using var _ = db;

        var run = await Cli(db, tenant, "rules", "remove", "--prefix", "nowhere");

        Assert.Equal(1, run.Exit);
        Assert.Empty(await RecordAsync(db));
    }

    [Fact]
    public async Task The_ingest_shorthand_records_the_rule_it_sets()
    {
        var (db, tenant) = await EmptyAsync();
        await using var _ = db;
        var folder = SourcesTests.BundleCopy();

        var run = await Cli(db, tenant, "ingest", folder, "--public", "--prefix", "greenhouse");

        Assert.Equal(0, run.Exit);
        var rule = Assert.Single(await RecordAsync(db), r => r.Kind == AdminCommands.RuleSetKind);
        Assert.Equal("filesystem:greenhouse", rule.Target);
    }
}

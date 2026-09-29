using Premagentic.Core;
using Premagentic.Core.Ingestion;
using Premagentic.Core.Retrieval;
using Premagentic.Core.Security;
using Premagentic.Core.Storage;

namespace Premagentic.Tests;

/// <summary>
/// The index is disposable and the config is not. The supported rebuild empties
/// every <c>prem_index</c> table and re-ingests; the structure stays, because it
/// belongs to the migrations, and <c>prem_config</c> is left exactly as it was.
/// </summary>
public sealed class DatastoreRebuildTests(DatastoreTestDatabase server) : IClassFixture<DatastoreTestDatabase>
{
    [Fact]
    public async Task Emptying_prem_index_and_reingesting_works_and_leaves_prem_config_rows_alone()
    {
        await using var db = new PremagenticDatabase(await server.CreateDatabaseAsync());
        await db.InitializeAsync();
        var tenantId = await db.EnsureTenantAsync("t", "T");
        var embedder = new SeededEmbeddingProvider();
        var search = new HybridSearch(db, embedder);
        var source = new DatastoreSource("r", [
            DatastoreSource.Doc("r/one.md", "## One\na sprocket policy", DocumentAccess.Everyone),
            DatastoreSource.Doc("r/two.md", "## Two\na widget policy", DocumentAccess.For("group:hr")),
        ]);

        await new IngestPipeline(db, embedder).RunAsync(tenantId, source);
        await search.WarmUpAsync();
        Assert.Contains((await search.SearchAsync(tenantId, "sprocket policy", new SearchOptions(AccessScope.PublicOnly))).Hits,
            h => h.Path == "r/one.md");

        var configBefore = await ConfigSnapshotAsync(db);
        Assert.Contains("tenant", configBefore);
        Assert.Contains("retrieval_event", configBefore);

        var (documents, chunks) = await db.ClearIndexAsync();
        Assert.Equal((2L, 2L), (documents, chunks));
        Assert.Empty(await db.MigrateAsync());
        Assert.Equal(configBefore, await ConfigSnapshotAsync(db));
        Assert.Empty((await search.SearchAsync(tenantId, "sprocket policy", new SearchOptions(AccessScope.PublicOnly))).Hits);
        configBefore = await ConfigSnapshotAsync(db);

        var summary = await new IngestPipeline(db, embedder).RunAsync(tenantId, source);
        Assert.Equal(2, summary.Ingested);

        // The same search instance, with no restart: every chunk id it holds is
        // gone, and it loads the new ones on demand.
        var hits = (await search.SearchAsync(tenantId, "sprocket policy", new SearchOptions(AccessScope.PublicOnly))).Hits;
        var hit = Assert.Single(hits, h => h.Path == "r/one.md");
        Assert.NotNull(hit.VectorRank);
        Assert.DoesNotContain(hits, h => h.Path == "r/two.md");

        // Config rows are the same rows, plus the one event the search just logged.
        var configAfter = await ConfigSnapshotAsync(db);
        Assert.Equal(configBefore["tenant"], configAfter["tenant"]);
        Assert.Equal(configBefore["schema_migration"], configAfter["schema_migration"]);
        Assert.Equal(configBefore["retrieval_event"].Length + 1, configAfter["retrieval_event"].Length);
        Assert.Equal(configBefore["retrieval_event"], configAfter["retrieval_event"][..configBefore["retrieval_event"].Length]);
    }

    [Fact]
    public async Task Emptying_the_index_refuses_rather_than_reach_into_config()
    {
        await using var db = new PremagenticDatabase(await server.CreateDatabaseAsync());
        await db.InitializeAsync();
        var tenantId = await db.EnsureTenantAsync("t", "T");
        await new IngestPipeline(db, new SeededEmbeddingProvider()).RunAsync(tenantId, new DatastoreSource("x", [
            DatastoreSource.Doc("x/a.md", "## A\na passage", DocumentAccess.Everyone)]));

        // The rule a future migration could break: config referencing the index.
        await using (var bad = db.DataSource.CreateCommand("""
            CREATE TABLE prem_config.bad_reference(document_id UUID REFERENCES prem_index.document(id));
            INSERT INTO prem_config.bad_reference SELECT id FROM prem_index.document;
            """))
            await bad.ExecuteNonQueryAsync();

        await Assert.ThrowsAsync<Npgsql.PostgresException>(() => db.ClearIndexAsync());

        await using var count = db.DataSource.CreateCommand(
            "SELECT (SELECT count(*) FROM prem_config.bad_reference) + (SELECT count(*) FROM prem_index.document)");
        Assert.Equal(2L, (long)(await count.ExecuteScalarAsync())!);
    }

    /// <summary>Every row of every prem_config table, as text, in a stable order.</summary>
    private static async Task<Dictionary<string, string[]>> ConfigSnapshotAsync(PremagenticDatabase db)
    {
        var tables = new List<string>();
        await using (var list = db.DataSource.CreateCommand(
            "SELECT table_name FROM information_schema.tables WHERE table_schema = 'prem_config' ORDER BY 1"))
        await using (var reader = await list.ExecuteReaderAsync())
            while (await reader.ReadAsync()) tables.Add(reader.GetString(0));

        var snapshot = new Dictionary<string, string[]>();
        foreach (var table in tables)
        {
            var order = table == "retrieval_event" ? "t.created_at, t.id::text" : "t::text";
            await using var rows = db.DataSource.CreateCommand($"SELECT t::text FROM prem_config.{table} t ORDER BY {order}");
            var values = new List<string>();
            await using var reader = await rows.ExecuteReaderAsync();
            while (await reader.ReadAsync()) values.Add(reader.GetString(0));
            snapshot[table] = values.ToArray();
        }
        return snapshot;
    }
}

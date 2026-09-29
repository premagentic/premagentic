using Premagentic.Core;
using Premagentic.Core.Ingestion;
using Premagentic.Core.Retrieval;
using Premagentic.Core.Security;
using Premagentic.Core.Storage;

namespace Premagentic.Tests;

/// <summary>
/// The text leg returns passages that tie on rank in a fixed order, path then
/// sequence, so a rebuilt index answers the same question the same way.
/// </summary>
public sealed class DatastoreTextLegTests(DatastoreTestDatabase server) : IClassFixture<DatastoreTestDatabase>
{
    [Fact]
    public async Task Passages_that_tie_on_rank_come_back_in_path_order_whatever_order_they_were_stored_in()
    {
        // On eight rows the planner walks the (tenant_id, path) index, so rows
        // already arrive in path order and a missing tie-breaker is invisible. A
        // real corpus is read through the text index instead, in physical order.
        // Turning off index scans and nested loops gives this small table the plan
        // a large one gets, so the test fails when the tie-breaker is missing.
        var connection = new Npgsql.NpgsqlConnectionStringBuilder(await server.CreateDatabaseAsync())
        {
            Options = "-c enable_indexscan=off -c enable_indexonlyscan=off -c enable_nestloop=off",
        }.ConnectionString;
        await using var db = new PremagenticDatabase(connection);
        await db.InitializeAsync();
        var tenantId = await db.EnsureTenantAsync("t", "T");
        var embedder = new SeededEmbeddingProvider();

        // Eight documents with the same title, heading and text, so every one
        // ranks the same. Stored one at a time in a shuffled order that is neither
        // path order nor its reverse, so physical order cannot pass for path order.
        string[] letters = ["e", "b", "h", "a", "f", "c", "g", "d"];
        var pipeline = new IngestPipeline(db, embedder);
        var stored = new List<SourceDocument>();
        foreach (var letter in letters)
        {
            stored.Add(DatastoreSource.Doc($"tie/{letter}.md", "## Same\nkumquat lattice ordering", DocumentAccess.Everyone, title: "Same"));
            await pipeline.RunAsync(tenantId, new DatastoreSource("tie", stored.ToArray()));
        }

        var hits = (await new HybridSearch(db, embedder).SearchAsync(
            tenantId, "kumquat lattice", new SearchOptions(AccessScope.PublicOnly, TopK: 10))).Hits;

        var byLexicalRank = hits.Where(h => h.LexicalRank is not null).OrderBy(h => h.LexicalRank).Select(h => h.Path).ToArray();
        Assert.Equal(letters.Order().Select(l => $"tie/{l}.md").ToArray(), byLexicalRank);
    }
}

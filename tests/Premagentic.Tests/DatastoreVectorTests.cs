using Premagentic.Core;
using Premagentic.Core.Ingestion;
using Premagentic.Core.Retrieval;
using Premagentic.Core.Retrieval.Gates;
using Premagentic.Core.Security;
using Premagentic.Core.Storage;

namespace Premagentic.Tests;

/// <summary>
/// The in-process vector leg against stock PostgreSQL: that it ranks exactly as
/// cosine similarity does, that it follows the database with no restart, and
/// that it never scores a chunk the gates did not return.
/// </summary>
public sealed class DatastoreVectorTests(DatastoreTestDatabase server) : IClassFixture<DatastoreTestDatabase>
{
    private sealed record Setup(PremagenticDatabase Db, Guid TenantId, SeededEmbeddingProvider Embedder, IngestPipeline Pipeline, HybridSearch Search);

    private async Task<Setup> NewSetupAsync(int dimensions = 64)
    {
        var db = new PremagenticDatabase(await server.CreateDatabaseAsync());
        await db.InitializeAsync();
        var tenantId = await db.EnsureTenantAsync("t", "T");
        var embedder = new SeededEmbeddingProvider(dimensions);
        return new Setup(db, tenantId, embedder, new IngestPipeline(db, embedder), new HybridSearch(db, embedder));
    }

    private static async Task<GateContext> As(Setup s, AccessScope scope) =>
        new(new SearchOptions(await PermittedSetReader.ResolveAsync(s.Db, s.TenantId, scope)));

    private static async Task<Guid> ChunkIdAsync(PremagenticDatabase db, string path)
    {
        await using var cmd = db.DataSource.CreateCommand(
            "SELECT c.id FROM prem_index.chunk c JOIN prem_index.document d ON d.id = c.document_id WHERE d.path = @p");
        cmd.Parameters.AddWithValue("p", path);
        return (Guid)(await cmd.ExecuteScalarAsync())!;
    }

    private static bool Holds(HybridSearch search, Guid chunkId) =>
        search.Vectors.Spaces.Any(s => s.HoldsChunk(chunkId));

    [Fact]
    public async Task Top_ten_equals_a_naive_double_precision_cosine_ranking_of_the_stored_vectors()
    {
        var s = await NewSetupAsync(dimensions: 96);
        await using var _ = s.Db;

        // 120 documents of 10 sections: 1,200 chunks, each with its own seeded vector.
        var docs = Enumerable.Range(0, 120).Select(d => DatastoreSource.Doc(
            $"corpus/doc-{d:D3}.md",
            string.Join("\n\n", Enumerable.Range(0, 10).Select(i => $"## Section {i}\npassage {d}-{i} of the seeded corpus")),
            DocumentAccess.Everyone)).ToArray();
        await s.Pipeline.RunAsync(s.TenantId, new DatastoreSource("corpus", docs));

        // Every stored vector, read back as bytes, the way the index reads them.
        var stored = new List<(Guid Id, float[] Vector)>();
        await using (var cmd = s.Db.DataSource.CreateCommand("SELECT id, embedding FROM prem_index.chunk"))
        await using (var reader = await cmd.ExecuteReaderAsync())
            while (await reader.ReadAsync())
                stored.Add((reader.GetGuid(0), VectorCodec.Decode(reader.GetFieldValue<byte[]>(1))));
        Assert.Equal(1200, stored.Count);

        // Ingest normalized them: the provider's own vectors are far from unit length.
        Assert.All(stored, v => Assert.InRange(Math.Sqrt(v.Vector.Sum(x => (double)x * x)), 1 - 1e-5, 1 + 1e-5));
        Assert.True(Math.Sqrt(s.Embedder.Vector("anything").Sum(x => (double)x * x)) > 5);

        await s.Search.WarmUpAsync();
        for (var q = 0; q < 25; q++)
        {
            var query = s.Embedder.Vector($"query number {q}");

            var naive = stored
                .Select(v => (v.Id, Cosine: Cosine(query, v.Vector)))
                .OrderByDescending(v => v.Cosine)
                .Take(10)
                .ToArray();
            var leg = await s.Search.Vectors.SearchAsync(
                s.TenantId, s.Embedder.Name, query, await As(s, AccessScope.PublicOnly), limit: 10);

            Assert.Equal(naive.Select(n => n.Id).ToArray(), leg.Select(c => c.ChunkId).ToArray());
            for (var i = 0; i < 10; i++)
                Assert.InRange(Math.Abs(1 - naive[i].Cosine - leg[i].Distance), 0, 1e-5);
        }
    }

    private static double Cosine(float[] a, float[] b)
    {
        double dot = 0, na = 0, nb = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += (double)a[i] * b[i];
            na += (double)a[i] * a[i];
            nb += (double)b[i] * b[i];
        }
        return dot / Math.Sqrt(na * nb);
    }

    [Fact]
    public async Task A_new_document_is_found_and_a_deleted_one_is_gone_with_no_restart()
    {
        var s = await NewSetupAsync();
        await using var _ = s.Db;
        var keep = DatastoreSource.Doc("live/keep.md", "## Keep\nan ordinary kept passage", DocumentAccess.Everyone);
        await s.Pipeline.RunAsync(s.TenantId, new DatastoreSource("live", [keep]));
        Assert.Equal(1, await s.Search.WarmUpAsync());

        // Another pipeline, as a separate ingest process would be: nothing tells
        // the search instance that anything changed.
        const string body = "zebra quantum lattice";
        var added = DatastoreSource.Doc("live/added.md", "## Added\n" + body, DocumentAccess.Everyone);
        await new IngestPipeline(s.Db, s.Embedder).RunAsync(s.TenantId, new DatastoreSource("live", [keep, added]));
        var addedId = await ChunkIdAsync(s.Db, "live/added.md");

        var found = await s.Search.Vectors.SearchAsync(s.TenantId, s.Embedder.Name, s.Embedder.Vector(body), await As(s, AccessScope.PublicOnly), 5);
        Assert.Equal(addedId, found[0].ChunkId);
        Assert.InRange(found[0].Distance, 0, 1e-6);
        var hit = Assert.Single((await s.Search.SearchAsync(s.TenantId, body, new SearchOptions(AccessScope.PublicOnly))).Hits,
            h => h.Path == "live/added.md");
        Assert.Equal(1, hit.VectorRank);

        // Deleted at the source; reconciliation removes it from the database.
        await new IngestPipeline(s.Db, s.Embedder).RunAsync(s.TenantId, new DatastoreSource("live", [keep]));

        var after = await s.Search.Vectors.SearchAsync(s.TenantId, s.Embedder.Name, s.Embedder.Vector(body), await As(s, AccessScope.PublicOnly), 5);
        Assert.DoesNotContain(after, c => c.ChunkId == addedId);
        Assert.DoesNotContain((await s.Search.SearchAsync(s.TenantId, body, new SearchOptions(AccessScope.PublicOnly))).Hits,
            h => h.Path == "live/added.md");

        // Still held in memory, and still never scored: the database stopped
        // returning it, and only what the database returns is scored. The sweep
        // then reclaims it.
        Assert.True(Holds(s.Search, addedId));
        Assert.Equal(1, await s.Search.Vectors.SweepAsync());
        Assert.False(Holds(s.Search, addedId));
    }

    [Fact]
    public async Task A_chunk_the_gate_hides_is_never_scored()
    {
        var s = await NewSetupAsync();
        await using var _ = s.Db;
        const string secret = "band four salary kettle zeppelin";
        await s.Pipeline.RunAsync(s.TenantId, new DatastoreSource("g", [
            DatastoreSource.Doc("g/open.md", "## Open\nan open handbook passage", DocumentAccess.Everyone),
            DatastoreSource.Doc("g/other.md", "## Other\nanother open passage entirely", DocumentAccess.Everyone),
            DatastoreSource.Doc("g/hr.md", "## Band four\n" + secret, DocumentAccess.For("group:hr")),
        ]));
        var hiddenId = await ChunkIdAsync(s.Db, "g/hr.md");

        // Warm-up loads EVERY vector, the hidden one included, so the only thing
        // between it and a score is the permitted-id read.
        await s.Search.WarmUpAsync();
        Assert.True(Holds(s.Search, hiddenId));

        // The query IS the hidden passage's vector: scored at all, it would come
        // first with distance 0, so leaving it out cannot be an accident of rank.
        var query = s.Embedder.Vector(secret);
        foreach (var scope in new[] { AccessScope.PublicOnly, AccessScope.ForPrincipals("t", "group:engineering") })
        {
            var scored = await s.Search.Vectors.SearchAsync(s.TenantId, s.Embedder.Name, query, await As(s, scope), limit: 10);
            Assert.NotEmpty(scored);
            Assert.DoesNotContain(scored, c => c.ChunkId == hiddenId);
        }

        // The control: for a caller who may read it, it is exactly first.
        var hr = await s.Search.Vectors.SearchAsync(
            s.TenantId, s.Embedder.Name, query, await As(s, AccessScope.ForPrincipals("t", "group:hr")), limit: 10);
        Assert.Equal(hiddenId, hr[0].ChunkId);
        Assert.InRange(hr[0].Distance, 0, 1e-6);
    }

    [Fact]
    public async Task The_final_passage_read_refuses_a_chunk_the_caller_may_not_read()
    {
        var s = await NewSetupAsync();
        await using var _ = s.Db;
        await s.Pipeline.RunAsync(s.TenantId, new DatastoreSource("f", [
            DatastoreSource.Doc("f/open.md", "## Open\nan open passage", DocumentAccess.Everyone),
            DatastoreSource.Doc("f/hr.md", "## HR\na restricted passage", DocumentAccess.For("group:hr")),
        ]));
        var open = await ChunkIdAsync(s.Db, "f/open.md");
        var hidden = await ChunkIdAsync(s.Db, "f/hr.md");

        // Hand the read an id no gated read would have produced, as a stale or
        // buggy vector leg could.
        var fused = new Dictionary<Guid, HybridSearch.FusedCandidate>
        {
            [open] = new(null, false, 2, 0.5, 0.01),
            [hidden] = new(null, false, 1, 0.1, 0.02),
        };
        var hits = await s.Search.LoadHitsAsync(s.TenantId, [hidden, open], fused, await As(s, AccessScope.PublicOnly));
        Assert.Equal(["f/open.md"], hits.Select(h => h.Path).ToArray());

        // The control: the same call for a caller who may read it returns both.
        var hr = await s.Search.LoadHitsAsync(
            s.TenantId, [hidden, open], fused, await As(s, AccessScope.ForPrincipals("t", "group:hr")));
        Assert.Equal(["f/hr.md", "f/open.md"], hr.Select(h => h.Path).ToArray());

        // Another tenant's read never returns this tenant's passage, even unrestricted.
        var other = await s.Db.EnsureTenantAsync("other", "Other");
        Assert.Empty(await s.Search.LoadHitsAsync(
            other, [open], fused, await As(s, AccessScope.UnrestrictedAudited("test"))));
    }

    [Fact]
    public async Task Every_hit_carries_its_content_hash_and_the_audit_trail_records_path_heading_and_hash()
    {
        var s = await NewSetupAsync();
        await using var _ = s.Db;
        var doc = DatastoreSource.Doc("a/policy.md", "# Policy\n\n## Claims\nsubmit a gizmo claim within thirty days", DocumentAccess.Everyone);
        await s.Pipeline.RunAsync(s.TenantId, new DatastoreSource("a", [doc]));

        var hit = Assert.Single((await s.Search.SearchAsync(s.TenantId, "gizmo claim", new SearchOptions(AccessScope.PublicOnly))).Hits);
        Assert.Equal(doc.ContentHash, hit.ContentHash);

        await using var cmd = s.Db.DataSource.CreateCommand(
            "SELECT passages::text FROM prem_config.retrieval_event ORDER BY created_at DESC LIMIT 1");
        var passages = System.Text.Json.JsonDocument.Parse((string)(await cmd.ExecuteScalarAsync())!).RootElement;
        var passage = Assert.Single(passages.EnumerateArray());
        Assert.Equal("a/policy.md", passage.GetProperty("path").GetString());
        Assert.Equal("Policy > Claims", passage.GetProperty("heading_path").GetString());
        Assert.Equal(doc.ContentHash, passage.GetProperty("content_hash").GetString());
    }

    [Fact]
    public async Task A_document_rewritten_in_place_is_reloaded_with_no_restart()
    {
        // The case the per-document read has to get right: the same path, so the
        // same document id, with new text and so new chunk ids.
        var s = await NewSetupAsync();
        await using var _ = s.Db;
        const string before = "an original walrus passage";
        const string after = "a rewritten narwhal passage";
        await s.Pipeline.RunAsync(s.TenantId, new DatastoreSource("rw", [
            DatastoreSource.Doc("rw/doc.md", "## Body\n" + before, DocumentAccess.Everyone)]));
        await s.Search.WarmUpAsync();
        var oldId = await ChunkIdAsync(s.Db, "rw/doc.md");
        Assert.True(Holds(s.Search, oldId));

        await new IngestPipeline(s.Db, s.Embedder).RunAsync(s.TenantId, new DatastoreSource("rw", [
            DatastoreSource.Doc("rw/doc.md", "## Body\n" + after, DocumentAccess.Everyone)]));
        var newId = await ChunkIdAsync(s.Db, "rw/doc.md");
        Assert.NotEqual(oldId, newId);

        var found = await s.Search.Vectors.SearchAsync(
            s.TenantId, s.Embedder.Name, s.Embedder.Vector(after), await As(s, AccessScope.PublicOnly), 5);
        Assert.Equal(newId, found[0].ChunkId);
        Assert.InRange(found[0].Distance, 0, 1e-6);

        // The old version is gone from memory at once, not left for the sweep,
        // and asking for its exact text no longer reaches it.
        Assert.False(Holds(s.Search, oldId));
        var old = await s.Search.Vectors.SearchAsync(
            s.TenantId, s.Embedder.Name, s.Embedder.Vector(before), await As(s, AccessScope.PublicOnly), 5);
        Assert.DoesNotContain(old, c => c.ChunkId == oldId);
    }

    [Fact]
    public async Task A_passage_that_embeds_to_nothing_is_stored_and_never_scored()
    {
        var s = await NewSetupAsync();
        await using var _ = s.Db;
        await s.Pipeline.RunAsync(s.TenantId, new DatastoreSource("z", [
            DatastoreSource.Doc("z/a.md", "## A\nordinary words here", DocumentAccess.Everyone),
        ]));
        // A vector of zeros, as a real provider can return for a passage with no
        // tokens it knows. Written directly, because ingest never produces one
        // from the seeded provider.
        await using (var cmd = s.Db.DataSource.CreateCommand("""
            INSERT INTO prem_index.chunk(document_id, seq, content, embedding, embedding_model, embedding_dims)
            SELECT d.id, 99, 'zero', @e, @m, 64 FROM prem_index.document d WHERE d.path = 'z/a.md'
            """))
        {
            cmd.Parameters.AddWithValue("e", VectorCodec.Encode(new float[64]));
            cmd.Parameters.AddWithValue("m", s.Embedder.Name);
            await cmd.ExecuteNonQueryAsync();
        }

        // One chunk held: the zero vector is stored in the database but never
        // given a place in memory, so it cannot be scored.
        Assert.Equal(1, await s.Search.WarmUpAsync());
        var scored = await s.Search.Vectors.SearchAsync(
            s.TenantId, s.Embedder.Name, s.Embedder.Vector("x"), await As(s, AccessScope.PublicOnly), limit: 10);
        Assert.Single(scored);

        // A zero query has no direction either, and returns nothing rather than noise.
        Assert.Empty(await s.Search.Vectors.SearchAsync(
            s.TenantId, s.Embedder.Name, new float[64], await As(s, AccessScope.PublicOnly), limit: 10));
    }
}

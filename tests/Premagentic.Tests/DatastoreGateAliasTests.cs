using System.Text.RegularExpressions;
using Premagentic.Core.Retrieval.Gates;

namespace Premagentic.Tests;

/// <summary>
/// Gates are written over the document alias <c>d</c> only. The vector leg asks
/// the database which documents a caller may read in a query that has no chunk
/// in it, so a gate that mentions the chunk alias <c>c</c> would fail there at
/// run time, or worse, be silently dropped by whoever "fixed" that query.
/// </summary>
public class DatastoreGateAliasTests
{
    // "c." as an alias: a c that is not the end of a longer name, then a dot.
    private static readonly Regex ChunkAlias =
        new(@"(?<![\w.""])c\s*\.", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static bool RefersToChunk(string sql) => ChunkAlias.IsMatch(sql);

    [Fact]
    public void The_check_finds_a_chunk_reference_and_ignores_look_alikes()
    {
        Assert.True(RefersToChunk("(c.embedding_model = @model)"));
        Assert.True(RefersToChunk("(d.is_public AND C.seq > 0)"));
        Assert.True(RefersToChunk("(@x OR c .heading_path = '')"));

        Assert.False(RefersToChunk("(@historical OR d.lifecycle_status = 'active')"));
        Assert.False(RefersToChunk("(d.doc_class = 'abc.def' OR d.source_name = 'etc.')"));
    }

    [Fact]
    public void No_gate_in_the_default_set_refers_to_the_chunk_alias()
    {
        Assert.NotEmpty(GateSet.Default.Names);
        Assert.False(RefersToChunk(GateSet.Default.Sql),
            "A gate in GateSet.Default refers to the chunk alias c. Gates must be written over d alone:\n" +
            GateSet.Default.Sql);
    }
}

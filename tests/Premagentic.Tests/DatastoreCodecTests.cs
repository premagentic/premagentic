using Premagentic.Core.Retrieval.Gates;
using Premagentic.Core.Storage;

namespace Premagentic.Tests;

public class DatastoreCodecTests
{
    [Fact]
    public void Encoding_normalizes_and_writes_four_little_endian_bytes_per_dimension()
    {
        var bytes = VectorCodec.Encode([3f, 4f]);
        Assert.Equal(8, bytes.Length);
        Assert.Equal([0.6f, 0.8f], VectorCodec.Decode(bytes));
        // 0.6f is 0x3F19999A; little-endian puts the low byte first.
        Assert.Equal(new byte[] { 0x9A, 0x99, 0x19, 0x3F }, bytes[..4]);
    }

    [Fact]
    public void A_vector_with_no_direction_is_stored_as_zeros_and_normalizes_to_null()
    {
        Assert.Null(VectorCodec.Normalize(new float[5]));
        var bytes = VectorCodec.Encode(new float[5]);
        Assert.Equal(20, bytes.Length);
        Assert.True(VectorCodec.IsZero(VectorCodec.Decode(bytes)));
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    public void A_non_finite_component_is_refused(float bad)
    {
        Assert.Throws<ArgumentException>(() => VectorCodec.Encode([1f, bad]));
    }

    [Fact]
    public void Normalizing_keeps_precision_for_a_long_vector_of_tiny_components()
    {
        var tiny = Enumerable.Repeat(1e-20f, 384).ToArray();
        var unit = VectorCodec.Normalize(tiny)!;
        Assert.InRange(Math.Sqrt(unit.Sum(x => (double)x * x)), 1 - 1e-6, 1 + 1e-6);
    }

    [Fact]
    public void Decoding_refuses_bytes_that_are_not_whole_floats_or_the_wrong_length()
    {
        Assert.Throws<ArgumentException>(() => VectorCodec.Decode(new byte[7]));
        Assert.Throws<ArgumentException>(() => VectorCodec.Decode(new byte[8], new float[3]));
    }

    [Fact]
    public void The_default_gate_set_joins_access_and_lifecycle_and_can_leave_one_out()
    {
        var gates = GateSet.Default;
        Assert.Equal([AccessGate.GateName, LifecycleGate.GateName, TrustGate.GateName, FreshnessGate.GateName], gates.Names);
        Assert.Contains("d.acl_set_id = ANY(@permitted)", gates.Sql);
        Assert.Contains("d.lifecycle_status = 'active'", gates.Sql);

        var lookup = gates.Without(LifecycleGate.GateName);
        Assert.Equal([AccessGate.GateName, TrustGate.GateName, FreshnessGate.GateName], lookup.Names);
        Assert.DoesNotContain("lifecycle_status", lookup.Sql);

        Assert.Equal("TRUE", new GateSet().Sql);
        Assert.Throws<ArgumentException>(() => gates.Without("no-such-gate"));
    }
}

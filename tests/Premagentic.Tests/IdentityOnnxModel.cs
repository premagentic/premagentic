using System.Text;

namespace Premagentic.Tests;

/// <summary>
/// The smallest model ONNX Runtime loads: one Identity node from a float[1]
/// input to a float[1] output, written as ONNX's protobuf by hand, so a test
/// can get past the model file to what is read after it without the real
/// model. It embeds nothing.
/// </summary>
internal static class IdentityOnnxModel
{
    public static byte[] Bytes()
    {
        // TypeProto { tensor_type = 1 { elem_type = 1: FLOAT, shape = 2 { dim = 1 { dim_value = 1: 1 } } } }
        var floatOfOne = Message(1, Number(1, 1), Message(2, Message(1, Number(1, 1))));
        var graph = Concat(
            Message(1, Text(1, "x"), Text(2, "y"), Text(4, "Identity")), // NodeProto: input, output, op_type
            Text(2, "identity"),                                         // GraphProto.name
            Message(11, Text(1, "x"), Message(2, floatOfOne)),           // GraphProto.input: ValueInfoProto
            Message(12, Text(1, "y"), Message(2, floatOfOne)));          // GraphProto.output
        return Concat(
            Number(1, 8),              // ModelProto.ir_version
            Message(8, Number(2, 13)), // ModelProto.opset_import: OperatorSetIdProto.version, the default domain
            Message(7, graph));        // ModelProto.graph
    }

    // Field numbers and lengths here stay under 16 and 128, so each tag and
    // each length is one byte.
    private static byte[] Number(int field, byte value) => [(byte)(field << 3), value];

    private static byte[] Text(int field, string value) => Message(field, Encoding.ASCII.GetBytes(value));

    private static byte[] Message(int field, params byte[][] parts)
    {
        var body = Concat(parts);
        return [(byte)(field << 3 | 2), (byte)body.Length, .. body];
    }

    private static byte[] Concat(params byte[][] parts) => parts.SelectMany(part => part).ToArray();
}

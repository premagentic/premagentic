using System.Runtime.InteropServices;

namespace Premagentic.Core.Storage;

/// <summary>
/// The one place an embedding changes shape between memory and the database.
/// <para>
/// Stored form: <c>chunk.embedding BYTEA</c>, float32, little-endian, 4 bytes per
/// dimension, L2-normalized at ingest. Normalizing once at write time means a
/// dot product is the cosine similarity, so the search path never divides and
/// the reported distance (1 minus the dot product) keeps the meaning cosine
/// distance had when the database computed it.
/// </para>
/// <para>
/// The bytes are the in-memory layout of a <c>float[]</c> on a little-endian
/// host, which is what makes loading a large index a copy rather than a parse.
/// A big-endian host would read every stored vector as noise, so it is refused
/// at startup instead of returning confident wrong answers.
/// </para>
/// </summary>
public static class VectorCodec
{
    /// <summary>Throws on a big-endian host. Called before any database work.</summary>
    public static void EnsureSupportedHost()
    {
        if (!BitConverter.IsLittleEndian)
            throw new PlatformNotSupportedException(
                "Premagentic stores embeddings as little-endian float32 and does not run on a big-endian host.");
    }

    /// <summary>
    /// The unit vector pointing the same way as <paramref name="vector"/>, or null
    /// when it has no direction (every component zero). The norm is accumulated in
    /// double precision so a long vector of small components does not lose it.
    /// </summary>
    /// <exception cref="ArgumentException">A component is NaN or infinite.</exception>
    public static float[]? Normalize(ReadOnlySpan<float> vector)
    {
        double sumOfSquares = 0;
        foreach (var x in vector)
        {
            if (!float.IsFinite(x))
                throw new ArgumentException("An embedding holds a NaN or infinite component.", nameof(vector));
            sumOfSquares += (double)x * x;
        }
        if (sumOfSquares == 0) return null;

        var norm = Math.Sqrt(sumOfSquares);
        var unit = new float[vector.Length];
        for (var i = 0; i < vector.Length; i++)
            unit[i] = (float)(vector[i] / norm);
        return unit;
    }

    /// <summary>
    /// Normalizes and encodes an embedding for storage.
    /// <para>
    /// A vector with no direction is stored as zeros rather than refused. It has no
    /// cosine distance to anything, so the vector leg never scores it, and the
    /// chunk stays reachable through the text leg. Refusing it would fail a whole
    /// document over one passage that happens to embed to nothing.
    /// </para>
    /// </summary>
    public static byte[] Encode(ReadOnlySpan<float> vector)
    {
        var unit = Normalize(vector);
        return unit is null
            ? new byte[vector.Length * sizeof(float)]
            : MemoryMarshal.AsBytes(unit.AsSpan()).ToArray();
    }

    /// <summary>Copies stored bytes into <paramref name="destination"/>, which must hold exactly one vector.</summary>
    public static void Decode(ReadOnlySpan<byte> stored, Span<float> destination)
    {
        if (stored.Length != destination.Length * sizeof(float))
            throw new ArgumentException(
                $"A stored vector of {stored.Length} bytes does not fill {destination.Length} dimensions.", nameof(stored));
        MemoryMarshal.Cast<byte, float>(stored).CopyTo(destination);
    }

    public static float[] Decode(ReadOnlySpan<byte> stored)
    {
        if (stored.Length % sizeof(float) != 0)
            throw new ArgumentException($"A stored vector of {stored.Length} bytes is not whole float32 values.", nameof(stored));
        var vector = new float[stored.Length / sizeof(float)];
        Decode(stored, vector);
        return vector;
    }

    /// <summary>True when every component is zero, which is how a vector with no direction is stored.</summary>
    public static bool IsZero(ReadOnlySpan<float> vector)
    {
        foreach (var x in vector)
            if (x != 0f) return false;
        return true;
    }
}

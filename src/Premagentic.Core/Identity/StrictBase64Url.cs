using System.Buffers.Text;

namespace Premagentic.Core.Identity;

/// <summary>
/// Unpadded base64url with exactly one accepted text per value: the alphabet
/// only, no padding, no white space, and no stray bits in the last character.
/// </summary>
internal static class StrictBase64Url
{
    public static string Encode(ReadOnlySpan<byte> bytes) => Base64Url.EncodeToString(bytes);

    public static bool TryDecode(string? text, out byte[] bytes)
    {
        bytes = [];
        if (string.IsNullOrEmpty(text)) return false;
        foreach (var c in text)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c == '-' || c == '_')) return false;
        }

        byte[] decoded;
        try
        {
            decoded = Base64Url.DecodeFromChars(text);
        }
        catch (FormatException)
        {
            return false;
        }

        // Re-encoding must give the same text back, so two texts can never
        // decode to one value. The runtime decoder already refuses stray bits in
        // the last character; this keeps that a property of this class.
        if (!string.Equals(Base64Url.EncodeToString(decoded), text, StringComparison.Ordinal)) return false;

        bytes = decoded;
        return true;
    }
}

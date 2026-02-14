namespace Qubic.Toolkit.Helpers;

public static class AssetNameHelper
{
    /// <summary>
    /// Encode an asset name string (up to 7 ASCII chars) to a ulong.
    /// </summary>
    public static ulong ToUlong(string name)
    {
        ulong result = 0;
        for (int i = 0; i < name.Length && i < 7; i++)
            result |= (ulong)name[i] << (i * 8);
        return result;
    }

    /// <summary>
    /// Decode a ulong asset name back to a string, or null if not printable ASCII.
    /// </summary>
    public static string? FromUlong(ulong value)
    {
        if (value == 0) return null;
        Span<byte> bytes = stackalloc byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(bytes, value);
        int len = 8;
        while (len > 0 && bytes[len - 1] == 0) len--;
        if (len == 0) return null;
        for (int i = 0; i < len; i++)
            if (bytes[i] < 0x20 || bytes[i] > 0x7E) return null;
        return System.Text.Encoding.ASCII.GetString(bytes[..len]);
    }
}

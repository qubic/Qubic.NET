using System.Buffers.Binary;

namespace Qubic.Core.Contracts;

/// <summary>
/// Represents a Qubic asset reference (issuer public key + asset name).
/// Wire format: 32 bytes (issuer) + 8 bytes (assetName) = 40 bytes.
/// Maps to the QPI <c>Asset</c> type.
/// </summary>
public readonly struct QubicAsset
{
    public const int SerializedSize = 40;

    /// <summary>The 32-byte issuer public key.</summary>
    public byte[] Issuer { get; init; }

    /// <summary>The 8-byte asset name identifier.</summary>
    public ulong AssetName { get; init; }

    public void WriteTo(Span<byte> destination)
    {
        Issuer.AsSpan(0, 32).CopyTo(destination);
        BinaryPrimitives.WriteUInt64LittleEndian(destination[32..], AssetName);
    }

    public static QubicAsset ReadFrom(ReadOnlySpan<byte> source)
    {
        return new QubicAsset
        {
            Issuer = source[..32].ToArray(),
            AssetName = BinaryPrimitives.ReadUInt64LittleEndian(source[32..])
        };
    }
}

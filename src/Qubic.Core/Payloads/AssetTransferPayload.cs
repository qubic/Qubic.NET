using Qubic.Core.Entities;

namespace Qubic.Core.Payloads;

/// <summary>
/// Payload for QX TransferShareOwnershipAndPossession - transfers asset ownership to another identity.
///
/// This is a QX contract call (contract index 1), not a base protocol feature.
/// The transaction destination must be set to the QX contract address.
/// The transaction amount should be 0 (or the transfer fee if required).
/// </summary>
[Obsolete("Use Qubic.Core.Contracts.Qx.TransferShareOwnershipAndPossessionPayload instead.")]
public sealed class AssetTransferPayload : ITransactionPayload
{
    /// <summary>
    /// QX contract index.
    /// </summary>
    public const int QxContractIndex = 1;

    /// <summary>
    /// TransferShareOwnershipAndPossession input type.
    /// </summary>
    public const ushort TransferShareOwnershipAndPossessionType = 2;

    // Payload structure:
    // - issuer (32 bytes)
    // - newOwnerAndPossessor (32 bytes)
    // - assetName (8 bytes) - 7 chars + null terminator or 8 char asset name
    // - numberOfShares (8 bytes)
    private const int PayloadSize = 80;

    public ushort InputType => TransferShareOwnershipAndPossessionType;
    public ushort InputSize => PayloadSize;

    /// <summary>
    /// The asset issuer identity.
    /// </summary>
    public required QubicIdentity Issuer { get; init; }

    /// <summary>
    /// The new owner identity to receive the asset.
    /// </summary>
    public required QubicIdentity NewOwner { get; init; }

    /// <summary>
    /// The asset name (up to 7 characters, will be padded/truncated).
    /// </summary>
    public required string AssetName { get; init; }

    /// <summary>
    /// The number of shares to transfer.
    /// </summary>
    public required long NumberOfShares { get; init; }

    /// <summary>
    /// Gets the QX contract destination public key.
    /// Contract addresses are encoded as: [contractIndex, 0, 0, 0] in the first 8 bytes, rest zeros.
    /// </summary>
    public static byte[] GetQxContractPublicKey()
    {
        var pubKey = new byte[32];
        pubKey[0] = QxContractIndex;
        return pubKey;
    }

    public byte[] GetPayloadBytes()
    {
        var bytes = new byte[PayloadSize];
        var offset = 0;

        // Issuer public key (32 bytes)
        Array.Copy(Issuer.PublicKey, 0, bytes, offset, 32);
        offset += 32;

        // New owner public key (32 bytes)
        Array.Copy(NewOwner.PublicKey, 0, bytes, offset, 32);
        offset += 32;

        // Asset name (8 bytes) - convert to bytes, pad with zeros
        var nameBytes = System.Text.Encoding.ASCII.GetBytes(AssetName);
        var copyLen = Math.Min(nameBytes.Length, 8);
        Array.Copy(nameBytes, 0, bytes, offset, copyLen);
        offset += 8;

        // Number of shares (8 bytes, little-endian)
        BitConverter.TryWriteBytes(bytes.AsSpan(offset, 8), NumberOfShares);

        return bytes;
    }
}

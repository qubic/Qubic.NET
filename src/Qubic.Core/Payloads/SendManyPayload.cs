using Qubic.Core.Entities;

namespace Qubic.Core.Payloads;

/// <summary>
/// Payload for QUTIL SendToManyV1 - sends QU to multiple destinations in a single transaction.
/// Maximum 25 transfers per transaction.
///
/// This is a QUTIL contract call (contract index 4), not a base protocol feature.
/// The transaction destination must be set to the QUTIL contract address.
/// </summary>
[Obsolete("Use Qubic.Core.Contracts.Qutil.SendToManyV1Payload instead.")]
public sealed class SendManyPayload : ITransactionPayload
{
    /// <summary>
    /// Maximum number of transfers in a single SendToManyV1 call.
    /// </summary>
    public const int MaxTransfers = 25;

    /// <summary>
    /// QUTIL contract index.
    /// </summary>
    public const int QutilContractIndex = 4;

    /// <summary>
    /// Fixed payload size: 25 addresses (25*32) + 25 amounts (25*8) = 1000 bytes.
    /// </summary>
    public const int PayloadSize = MaxTransfers * 32 + MaxTransfers * 8; // 1000 bytes

    private readonly List<(QubicIdentity Destination, long Amount)> _transfers = [];

    /// <summary>
    /// QUTIL SendToManyV1 procedure ID.
    /// </summary>
    public ushort InputType => 1;

    /// <summary>
    /// Fixed size payload for SendToManyV1.
    /// </summary>
    public ushort InputSize => PayloadSize;

    /// <summary>
    /// The list of transfers in this payload.
    /// </summary>
    public IReadOnlyList<(QubicIdentity Destination, long Amount)> Transfers => _transfers;

    /// <summary>
    /// Adds a transfer to this payload.
    /// </summary>
    public SendManyPayload AddTransfer(QubicIdentity destination, long amount)
    {
        if (_transfers.Count >= MaxTransfers)
            throw new InvalidOperationException($"Cannot add more than {MaxTransfers} transfers.");

        if (amount <= 0)
            throw new ArgumentException("Amount must be positive.", nameof(amount));

        _transfers.Add((destination, amount));
        return this;
    }

    /// <summary>
    /// Gets the total amount being transferred (excluding fee).
    /// </summary>
    public long TotalAmount => _transfers.Sum(t => t.Amount);

    /// <summary>
    /// Gets the QUTIL contract destination public key.
    /// Contract addresses are encoded as: [contractIndex, 0, 0, 0] in the first 8 bytes, rest zeros.
    /// </summary>
    public static byte[] GetQutilContractPublicKey()
    {
        var pubKey = new byte[32];
        pubKey[0] = QutilContractIndex;
        return pubKey;
    }

    public byte[] GetPayloadBytes()
    {
        // SendToManyV1_input structure:
        // - uint8_t addresses[25][32] = 800 bytes (25 public keys)
        // - int64_t amounts[25] = 200 bytes (25 amounts)
        // Total: 1000 bytes

        var bytes = new byte[PayloadSize];

        // Write addresses (first 800 bytes)
        for (int i = 0; i < _transfers.Count; i++)
        {
            Array.Copy(_transfers[i].Destination.PublicKey, 0, bytes, i * 32, 32);
        }
        // Remaining address slots are zero-filled (default)

        // Write amounts (bytes 800-999)
        var amountsOffset = MaxTransfers * 32; // 800
        for (int i = 0; i < _transfers.Count; i++)
        {
            BitConverter.TryWriteBytes(bytes.AsSpan(amountsOffset + i * 8, 8), _transfers[i].Amount);
        }
        // Remaining amount slots are zero (default)

        return bytes;
    }
}

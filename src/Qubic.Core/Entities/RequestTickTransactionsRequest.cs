namespace Qubic.Core.Entities;

/// <summary>
/// Parsed payload of an incoming <c>RequestTickTransactions</c> packet.
/// Use this on the responder side to decide which transaction slots the requester wants.
/// </summary>
/// <remarks>
/// Wire layout: 4-byte tick + (<c>MaxTransactionsPerTick</c>/8) bytes of transaction
/// flags. A clear bit (0) at index <c>i</c> means "I do NOT have slot <c>i</c>, please
/// send it." A set bit (1) means "I already have it, skip." Flag array is 128 bytes
/// for legacy epochs (1024 tx/tick) and 512 bytes for V2+ (4096 tx/tick).
/// </remarks>
public sealed class RequestTickTransactionsRequest
{
    /// <summary>The tick the requester wants transactions for.</summary>
    public required uint Tick { get; init; }

    /// <summary>The raw transaction-flag bitmap. Length = 128 (legacy) or 512 (V2+).</summary>
    public required byte[] TransactionFlags { get; init; }

    /// <summary>Total slot count this flag array can address (<c>TransactionFlags.Length * 8</c>).</summary>
    public int SlotCount => TransactionFlags.Length * 8;

    /// <summary>
    /// True when slot <paramref name="slotIndex"/> is requested (bit is 0).
    /// </summary>
    public bool IsRequested(int slotIndex)
    {
        if ((uint)slotIndex >= (uint)SlotCount)
            throw new ArgumentOutOfRangeException(nameof(slotIndex));
        return (TransactionFlags[slotIndex >> 3] & (1 << (slotIndex & 7))) == 0;
    }

    /// <summary>
    /// Enumerates slot indices the requester needs (bit = 0), in ascending order.
    /// </summary>
    public IEnumerable<int> RequestedSlotIndices()
    {
        for (var byteIdx = 0; byteIdx < TransactionFlags.Length; byteIdx++)
        {
            var b = TransactionFlags[byteIdx];
            if (b == 0xFF) continue; // every slot in this byte is already had
            for (var bit = 0; bit < 8; bit++)
            {
                if ((b & (1 << bit)) == 0)
                    yield return byteIdx * 8 + bit;
            }
        }
    }

    /// <summary>Count of slots the requester needs.</summary>
    public int RequestedSlotCount()
    {
        var count = 0;
        foreach (var b in TransactionFlags)
            count += 8 - System.Numerics.BitOperations.PopCount(b);
        return count;
    }
}

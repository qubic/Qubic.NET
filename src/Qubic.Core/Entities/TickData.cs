namespace Qubic.Core.Entities;

/// <summary>
/// Tick data broadcast for future ticks (contains transaction digests).
/// </summary>
public sealed class TickData
{
    /// <summary>
    /// Index of the computor that created this tick data.
    /// </summary>
    public required ushort ComputorIndex { get; init; }

    /// <summary>
    /// The epoch.
    /// </summary>
    public required ushort Epoch { get; init; }

    /// <summary>
    /// The tick number.
    /// </summary>
    public required uint TickNumber { get; init; }

    /// <summary>
    /// Timestamp components.
    /// </summary>
    public required DateTime Timestamp { get; init; }

    /// <summary>
    /// Timelock value (32 bytes).
    /// </summary>
    public required byte[] Timelock { get; init; }

    /// <summary>
    /// Transaction digests for all transactions in this tick.
    /// Length is up to 1024 for epoch &lt; <c>TransactionsPerTickV2Epoch</c> (214),
    /// up to 4096 from epoch 214 onward (qubic/core PR #879).
    /// </summary>
    public required byte[][] TransactionDigests { get; init; }

    /// <summary>
    /// Contract fees for this tick (up to 1024 contracts, unchanged by the epoch-214 fork).
    /// </summary>
    public required long[] ContractFees { get; init; }

    /// <summary>
    /// Signature (64 bytes).
    /// </summary>
    public required byte[] Signature { get; init; }
}

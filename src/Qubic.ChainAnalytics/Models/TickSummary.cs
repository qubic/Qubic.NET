using Qubic.Core.Entities;

namespace Qubic.ChainAnalytics.Models;

/// <summary>
/// Aggregated view of a single tick on the live network: the tick data
/// header (chain metadata), the parsed transactions, and a verification
/// result comparing the locally-computed transaction digests against the
/// digest list embedded in the tick data.
/// </summary>
public sealed class TickSummary
{
    /// <summary>The tick number this summary describes.</summary>
    public required uint TickNumber { get; init; }

    /// <summary>The epoch this tick belongs to (from tick data).</summary>
    public required ushort Epoch { get; init; }

    /// <summary>
    /// True if the node reported tick data for this tick (computor signed and broadcast
    /// the slot). When false, only an empty <see cref="Transactions"/> list is meaningful —
    /// the tick may be empty, still pending, or already evicted from the node's storage.
    /// </summary>
    public required bool TickDataAvailable { get; init; }

    /// <summary>Raw tick data record as returned by the node (null if unavailable).</summary>
    public TickData? TickData { get; init; }

    /// <summary>
    /// Digest slots from the tick data, in tick-data order. Each entry is either a
    /// 32-byte non-zero K12 digest of an included tx, or 32 zero bytes for an empty slot.
    /// Empty list when <see cref="TickDataAvailable"/> is false.
    /// </summary>
    public required IReadOnlyList<byte[]> TickDataDigests { get; init; }

    /// <summary>Parsed transactions for this tick, in the order returned by the node.</summary>
    public required IReadOnlyList<TickTransaction> Transactions { get; init; }

    /// <summary>
    /// True when every parsed transaction's locally-computed digest is present in the
    /// tick data digest slots, and the count of non-empty slots matches the transaction
    /// count. False when tick data is unavailable or any mismatch is detected.
    /// </summary>
    public required bool DigestsVerified { get; init; }

    /// <summary>Per-step wall-clock timings collected while building this summary.</summary>
    public required TickFetchTimings Timings { get; init; }

    /// <summary>
    /// Transaction hashes (in tx order) — convenience accessor for the chain info.
    /// </summary>
    public IEnumerable<string> TransactionHashes => Transactions.Select(tx => tx.Hash);

    /// <summary>Total amount transferred across all parsed transactions, in QU.</summary>
    public long TotalAmount => Transactions.Sum(tx => tx.Amount);

    /// <summary>Count of calls to deployed contracts (contract-shaped destination, index ≥ 1). Mutually exclusive with the other counters.</summary>
    public int ContractCallCount => Transactions.Count(tx => tx.Kind == TickTransactionKind.ContractCall);

    /// <summary>Count of transactions whose destination is the zero address — system messages. Mutually exclusive with the other counters.</summary>
    public int SystemMessageCount => Transactions.Count(tx => tx.Kind == TickTransactionKind.SystemMessage);

    /// <summary>Count of plain user-to-user QU transfers (non-contract, non-system destinations). Mutually exclusive with the other counters.</summary>
    public int UserTransferCount => Transactions.Count(tx => tx.Kind == TickTransactionKind.UserTransfer);
}

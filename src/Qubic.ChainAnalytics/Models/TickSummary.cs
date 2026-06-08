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
    /// Raw <c>BroadcastFutureTickData</c> wire-bytes payload, exactly as the node sent
    /// it. Null when tick data is unavailable. Persist this to capture the canonical
    /// signed form of the tick — independent of how the C# model evolves.
    /// </summary>
    public byte[]? TickDataRawBytes { get; init; }

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

    /// <summary>
    /// Number of non-empty digest slots in the signed tick data — the number of
    /// transactions the issuing computor committed to. Compare against
    /// <see cref="Transactions"/>.Count: if smaller, the queried peer is missing tx
    /// bodies for some slots.
    /// </summary>
    public int UsedTickDataSlotCount =>
        TickDataDigests.Count(d => d.Any(b => b != 0));

    /// <summary>
    /// Slot indices whose digest is present in tick data but absent from
    /// <see cref="Transactions"/> — i.e. transactions the peer didn't return. Empty
    /// when verified, populated when the peer's archive is incomplete for this tick.
    /// </summary>
    public IReadOnlyList<int> MissingSlotIndices
    {
        get
        {
            if (Transactions.Count == 0 && TickDataDigests.Count == 0) return [];
            var receivedDigests = new HashSet<string>(
                Transactions.Select(tx => Convert.ToHexString(tx.Digest)),
                StringComparer.Ordinal);
            var missing = new List<int>();
            for (var slot = 0; slot < TickDataDigests.Count; slot++)
            {
                var d = TickDataDigests[slot];
                if (!d.Any(b => b != 0)) continue; // empty slot
                if (!receivedDigests.Contains(Convert.ToHexString(d)))
                    missing.Add(slot);
            }
            return missing;
        }
    }

    /// <summary>
    /// SchnorrQ verification of the computor's signature over the tick-data bytes
    /// (everything except the trailing 64-byte signature). Null when the analyzer
    /// wasn't given a <c>Computors</c> set, or when the supplied set's epoch doesn't
    /// match this tick's epoch (no public key to check against).
    /// </summary>
    public bool? SignatureVerified { get; init; }

    /// <summary>
    /// Diagnostic when <see cref="SignatureVerified"/> is null — e.g.
    /// "no computors supplied" or "computor set epoch 214 ≠ tick epoch 215".
    /// </summary>
    public string? SignatureSkipReason { get; init; }

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

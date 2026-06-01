namespace Qubic.Core.Entities;

/// <summary>
/// One parsed log entry as stored in the node's qLogger ring buffer.
/// </summary>
/// <remarks>
/// Wire layout (26-byte header + body):
///   u16 epoch | u32 tick | u24 messageSize | u8 messageType | u64 logId | u64 logDigest | byte[messageSize] body
/// See <c>qubic-core/src/logging/logging.h</c> (<c>LOG_HEADER_SIZE</c> = 26).
/// </remarks>
public sealed class LogEntry
{
    public required ushort Epoch { get; init; }
    public required uint Tick { get; init; }
    public required byte MessageType { get; init; }
    public required uint MessageSize { get; init; }
    public required ulong LogId { get; init; }
    /// <summary>First 8 bytes of K12(<see cref="MessageBody"/>) computed by the node.</summary>
    public required ulong LogDigest { get; init; }
    public required byte[] MessageBody { get; init; }

    /// <summary>Human label for <see cref="MessageType"/>.</summary>
    public string MessageTypeName => MessageType switch
    {
        0 => "QU_TRANSFER",
        1 => "ASSET_ISSUANCE",
        2 => "ASSET_OWNERSHIP_CHANGE",
        3 => "ASSET_POSSESSION_CHANGE",
        4 => "CONTRACT_ERROR_MESSAGE",
        5 => "CONTRACT_WARNING_MESSAGE",
        6 => "CONTRACT_INFORMATION_MESSAGE",
        7 => "CONTRACT_DEBUG_MESSAGE",
        8 => "BURNING",
        9 => "DUST_BURNING",
        10 => "SPECTRUM_STATS",
        11 => "ASSET_OWNERSHIP_MANAGING_CONTRACT_CHANGE",
        12 => "ASSET_POSSESSION_MANAGING_CONTRACT_CHANGE",
        13 => "CONTRACT_RESERVE_DEDUCTION",
        14 => "ORACLE_QUERY_STATUS_CHANGE",
        15 => "ORACLE_SUBSCRIBER_MESSAGE",
        255 => "CUSTOM_MESSAGE",
        _ => $"UNKNOWN({MessageType})",
    };
}

/// <summary>
/// Result of <c>REQUEST_LOG_ID_RANGE_FROM_TX</c>: the contiguous log-ID range that one
/// transaction produced.
/// </summary>
/// <param name="FromLogId">First log-ID for the tx, or -3 when the tick hasn't logged yet, -1 when no logs.</param>
/// <param name="Length">Number of log entries the tx produced.</param>
public sealed record LogIdRange(long FromLogId, long Length)
{
    public bool TickNotYetLogged => FromLogId == -3;
    public bool NoLogs => FromLogId == -1 || Length == 0;
}

/// <summary>
/// Result of <c>REQUEST_ALL_LOG_ID_RANGES_FROM_TX</c>: per-tx-slot log ranges for the
/// whole tick. Indexed by tx slot (0..NUMBER_OF_TRANSACTIONS_PER_TICK-1) followed by 6
/// special-event slots.
/// </summary>
public sealed class TickLogIdRanges
{
    public required uint Tick { get; init; }
    public required IReadOnlyList<LogIdRange> Ranges { get; init; }
    public int SpecialEventCount => 6;
    public int TotalSlots => Ranges.Count;
}

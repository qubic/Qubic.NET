using System.Buffers.Binary;
using Qubic.Core;
using Qubic.Core.Entities;

namespace Qubic.Serialization.Readers;

/// <summary>
/// Reads Qubic protocol packets from byte arrays.
/// </summary>
public sealed class QubicPacketReader
{
    /// <summary>
    /// Reads the packet header from a byte span.
    /// </summary>
    public QubicPacketHeader ReadHeader(ReadOnlySpan<byte> data)
    {
        if (data.Length < QubicPacketHeader.Size)
            throw new ArgumentException($"Data too short for header. Expected at least {QubicPacketHeader.Size} bytes.");

        var sizeAndType = BinaryPrimitives.ReadUInt32LittleEndian(data);
        var dejavu = BinaryPrimitives.ReadUInt32LittleEndian(data[4..]);

        return new QubicPacketHeader
        {
            Dejavu = dejavu,
            Type = (byte)(sizeAndType >> 24),
            PacketSize = (int)(sizeAndType & 0x00FFFFFF)
        };
    }

    /// <summary>
    /// Reads current tick info response.
    /// </summary>
    public CurrentTickInfo ReadCurrentTickInfo(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 16)
            throw new ArgumentException("Payload too short for CurrentTickInfo.");

        return new CurrentTickInfo
        {
            TickDuration = BinaryPrimitives.ReadUInt16LittleEndian(payload),
            Epoch = BinaryPrimitives.ReadUInt16LittleEndian(payload[2..]),
            Tick = BinaryPrimitives.ReadUInt32LittleEndian(payload[4..]),
            NumberOfAlignedVotes = BinaryPrimitives.ReadUInt16LittleEndian(payload[8..]),
            NumberOfMisalignedVotes = BinaryPrimitives.ReadUInt16LittleEndian(payload[10..]),
            InitialTick = BinaryPrimitives.ReadUInt32LittleEndian(payload[12..])
        };
    }

    /// <summary>
    /// Reads entity (balance) response.
    /// </summary>
    public QubicBalance ReadEntityResponse(ReadOnlySpan<byte> payload, QubicIdentity identity)
    {
        if (payload.Length < 56)
            throw new ArgumentException("Payload too short for entity response.");

        // Skip first 32 bytes (public key echo)
        var offset = 32;

        var incomingAmount = BinaryPrimitives.ReadInt64LittleEndian(payload[offset..]);
        offset += 8;

        var outgoingAmount = BinaryPrimitives.ReadInt64LittleEndian(payload[offset..]);
        offset += 8;

        var numberOfIncomingTransfers = BinaryPrimitives.ReadUInt32LittleEndian(payload[offset..]);
        offset += 4;

        var numberOfOutgoingTransfers = BinaryPrimitives.ReadUInt32LittleEndian(payload[offset..]);

        return new QubicBalance
        {
            Identity = identity,
            Amount = incomingAmount - outgoingAmount,
            IncomingCount = numberOfIncomingTransfers,
            OutgoingCount = numberOfOutgoingTransfers
        };
    }

    /// <summary>
    /// Reads an ExchangePublicPeers response (4 IPv4 addresses).
    /// </summary>
    public string[] ReadExchangePublicPeers(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 16)
            throw new ArgumentException("Payload too short for ExchangePublicPeers.");

        var peers = new List<string>();
        for (int i = 0; i < 4; i++)
        {
            var offset = i * 4;
            var ip = $"{payload[offset]}.{payload[offset + 1]}.{payload[offset + 2]}.{payload[offset + 3]}";
            if (ip != "0.0.0.0")
                peers.Add(ip);
        }
        return peers.ToArray();
    }

    /// <summary>
    /// Reads a ContractIPO response (676 public keys + 676 prices).
    /// </summary>
    public ContractIpo ReadContractIpoResponse(ReadOnlySpan<byte> payload)
    {
        // Payload: contractIndex (4) + tick (4) + 676 * 32 pubkeys + 676 * 8 prices = 27,048 bytes
        const int numSlots = 676;
        const int headerSize = 8; // contractIndex (4) + tick (4)
        var expectedSize = headerSize + numSlots * 32 + numSlots * 8;
        if (payload.Length < expectedSize)
            throw new ArgumentException($"Payload too short for ContractIPO. Expected {expectedSize}, got {payload.Length}.");

        var publicKeys = new byte[numSlots][];
        var offset = headerSize; // skip contractIndex + tick
        for (int i = 0; i < numSlots; i++)
        {
            publicKeys[i] = payload.Slice(offset, 32).ToArray();
            offset += 32;
        }

        var prices = new long[numSlots];
        for (int i = 0; i < numSlots; i++)
        {
            prices[i] = BinaryPrimitives.ReadInt64LittleEndian(payload[offset..]);
            offset += 8;
        }

        return new ContractIpo
        {
            PublicKeys = publicKeys,
            Prices = prices
        };
    }

    /// <summary>
    /// Reads a <c>BroadcastComputors</c> payload (packed struct, 21,698 bytes) into a
    /// <see cref="Computors"/>. Layout: <c>u16 epoch | 676 * 32 pubkeys | 64 signature</c>.
    /// </summary>
    public Computors ReadComputors(ReadOnlySpan<byte> payload)
    {
        const int Expected = 2 + 676 * 32 + 64; // 21,698
        if (payload.Length != Expected)
            throw new ArgumentException($"BroadcastComputors payload must be {Expected} bytes, got {payload.Length}.");

        var epoch = BinaryPrimitives.ReadUInt16LittleEndian(payload);
        var keys = new byte[676][];
        var offset = 2;
        for (var i = 0; i < 676; i++)
        {
            keys[i] = payload.Slice(offset, 32).ToArray();
            offset += 32;
        }
        var signature = payload.Slice(offset, 64).ToArray();

        return new Computors { Epoch = epoch, PublicKeys = keys, Signature = signature };
    }

    /// <summary>
    /// Reads a <c>BroadcastFutureTickData</c> payload into a <see cref="TickData"/>.
    /// Slot count is inferred from payload size — 4096 transaction digests for V2-era
    /// payloads, 1024 for legacy. Throws if the size matches neither layout.
    /// </summary>
    public TickData ReadTickData(ReadOnlySpan<byte> payload)
    {
        // Wire layout: 8 (computor/epoch/tick) + 8 (timestamp) + 32 (timelock) + N*32 (digests)
        //              + 1024*8 (contractFees) + 64 (signature). N is 4096 (V2) or 1024 (legacy).
        const int ContractFeesSize = 1024 * 8;
        const int FixedSize = 8 + 8 + 32 + ContractFeesSize + QubicConstants.SignatureSize;
        const int V2Slots = 4096;
        const int LegacySlots = 1024;

        var perSlotSection = payload.Length - FixedSize;
        int slots;
        if (perSlotSection == V2Slots * 32)
            slots = V2Slots;
        else if (perSlotSection == LegacySlots * 32)
            slots = LegacySlots;
        else
            throw new ArgumentException(
                $"Unrecognised TickData payload size {payload.Length} — expected " +
                $"{FixedSize + V2Slots * 32} (V2) or {FixedSize + LegacySlots * 32} (legacy).");

        var offset = 0;
        var computorIndex = BinaryPrimitives.ReadUInt16LittleEndian(payload[offset..]); offset += 2;
        var epoch = BinaryPrimitives.ReadUInt16LittleEndian(payload[offset..]); offset += 2;
        var tickNumber = BinaryPrimitives.ReadUInt32LittleEndian(payload[offset..]); offset += 4;

        var millisecond = BinaryPrimitives.ReadUInt16LittleEndian(payload[offset..]); offset += 2;
        var second = payload[offset++];
        var minute = payload[offset++];
        var hour = payload[offset++];
        var day = payload[offset++];
        var month = payload[offset++];
        var year = payload[offset++];

        var timelock = payload.Slice(offset, 32).ToArray();
        offset += 32;

        var digests = new byte[slots][];
        for (var i = 0; i < slots; i++)
        {
            digests[i] = payload.Slice(offset, 32).ToArray();
            offset += 32;
        }

        var fees = new long[1024];
        for (var i = 0; i < 1024; i++)
        {
            fees[i] = BinaryPrimitives.ReadInt64LittleEndian(payload[offset..]);
            offset += 8;
        }

        var signature = payload.Slice(offset, QubicConstants.SignatureSize).ToArray();

        var timestamp = TryBuildTimestamp(year, month, day, hour, minute, second, millisecond);

        return new TickData
        {
            ComputorIndex = computorIndex,
            Epoch = epoch,
            TickNumber = tickNumber,
            Timestamp = timestamp,
            Timelock = timelock,
            TransactionDigests = digests,
            ContractFees = fees,
            Signature = signature,
        };
    }

    private static DateTime TryBuildTimestamp(byte year, byte month, byte day, byte hour, byte minute, byte second, ushort millisecond)
    {
        // Empty TickData slots zero the timestamp; fall back to MinValue rather than throw.
        if (year == 0 && month == 0 && day == 0)
            return DateTime.MinValue;

        try
        {
            return new DateTime(2000 + year, month, day, hour, minute, second, millisecond, DateTimeKind.Utc);
        }
        catch
        {
            return DateTime.MinValue;
        }
    }

    /// <summary>
    /// Reads a <c>BroadcastTick</c> payload (a single computor's vote for a tick) into
    /// a <see cref="Tick"/>. Payload is exactly 344 bytes per the protocol's
    /// static_assert on <c>sizeof(Tick)</c>.
    /// </summary>
    public Tick ReadTick(ReadOnlySpan<byte> payload)
    {
        const int TickSize = 8 + 8 + 2 * 4 + 2 * 4 + 6 * 32 + 2 * 32 + QubicConstants.SignatureSize; // 344
        if (payload.Length != TickSize)
            throw new ArgumentException($"Tick payload must be {TickSize} bytes, got {payload.Length}.");

        var offset = 0;
        var computorIndex = BinaryPrimitives.ReadUInt16LittleEndian(payload[offset..]); offset += 2;
        var epoch = BinaryPrimitives.ReadUInt16LittleEndian(payload[offset..]); offset += 2;
        var tickNumber = BinaryPrimitives.ReadUInt32LittleEndian(payload[offset..]); offset += 4;

        var millisecond = BinaryPrimitives.ReadUInt16LittleEndian(payload[offset..]); offset += 2;
        var second = payload[offset++];
        var minute = payload[offset++];
        var hour = payload[offset++];
        var day = payload[offset++];
        var month = payload[offset++];
        var year = payload[offset++];

        var prevResourceTestingDigest = BinaryPrimitives.ReadUInt32LittleEndian(payload[offset..]); offset += 4;
        var saltedResourceTestingDigest = BinaryPrimitives.ReadUInt32LittleEndian(payload[offset..]); offset += 4;
        var prevTransactionBodyDigest = BinaryPrimitives.ReadUInt32LittleEndian(payload[offset..]); offset += 4;
        var saltedTransactionBodyDigest = BinaryPrimitives.ReadUInt32LittleEndian(payload[offset..]); offset += 4;

        var prevSpectrumDigest = payload.Slice(offset, 32).ToArray(); offset += 32;
        var prevUniverseDigest = payload.Slice(offset, 32).ToArray(); offset += 32;
        var prevComputerDigest = payload.Slice(offset, 32).ToArray(); offset += 32;
        var saltedSpectrumDigest = payload.Slice(offset, 32).ToArray(); offset += 32;
        var saltedUniverseDigest = payload.Slice(offset, 32).ToArray(); offset += 32;
        var saltedComputerDigest = payload.Slice(offset, 32).ToArray(); offset += 32;

        var transactionDigest = payload.Slice(offset, 32).ToArray(); offset += 32;
        var expectedNextTickTransactionDigest = payload.Slice(offset, 32).ToArray(); offset += 32;

        var signature = payload.Slice(offset, QubicConstants.SignatureSize).ToArray();

        return new Tick
        {
            ComputorIndex = computorIndex,
            Epoch = epoch,
            TickNumber = tickNumber,
            Millisecond = millisecond,
            Second = second,
            Minute = minute,
            Hour = hour,
            Day = day,
            Month = month,
            Year = year,
            PrevResourceTestingDigest = prevResourceTestingDigest,
            SaltedResourceTestingDigest = saltedResourceTestingDigest,
            PrevTransactionBodyDigest = prevTransactionBodyDigest,
            SaltedTransactionBodyDigest = saltedTransactionBodyDigest,
            PrevSpectrumDigest = prevSpectrumDigest,
            PrevUniverseDigest = prevUniverseDigest,
            PrevComputerDigest = prevComputerDigest,
            SaltedSpectrumDigest = saltedSpectrumDigest,
            SaltedUniverseDigest = saltedUniverseDigest,
            SaltedComputerDigest = saltedComputerDigest,
            TransactionDigest = transactionDigest,
            ExpectedNextTickTransactionDigest = expectedNextTickTransactionDigest,
            Signature = signature,
        };
    }

    /// <summary>
    /// Parses a concatenated stream of log entries (as returned by REQUEST_LOG) into
    /// a list of <see cref="LogEntry"/>. Stops cleanly at the buffer end.
    /// </summary>
    public List<LogEntry> ReadLogEntries(ReadOnlySpan<byte> payload)
    {
        const int HeaderSize = 26; // LOG_HEADER_SIZE
        var entries = new List<LogEntry>();
        var offset = 0;
        while (offset + HeaderSize <= payload.Length)
        {
            var epoch = BinaryPrimitives.ReadUInt16LittleEndian(payload[offset..]);
            var tick = BinaryPrimitives.ReadUInt32LittleEndian(payload[(offset + 2)..]);
            var sizeAndType = BinaryPrimitives.ReadUInt32LittleEndian(payload[(offset + 6)..]);
            var messageSize = sizeAndType & 0x00FFFFFF;
            var messageType = (byte)(sizeAndType >> 24);
            var logId = BinaryPrimitives.ReadUInt64LittleEndian(payload[(offset + 10)..]);
            var logDigest = BinaryPrimitives.ReadUInt64LittleEndian(payload[(offset + 18)..]);

            var bodyStart = offset + HeaderSize;
            if (bodyStart + messageSize > payload.Length)
                break; // truncated trailing entry — caller can request the next range

            var body = payload.Slice(bodyStart, (int)messageSize).ToArray();
            entries.Add(new LogEntry
            {
                Epoch = epoch,
                Tick = tick,
                MessageType = messageType,
                MessageSize = messageSize,
                LogId = logId,
                LogDigest = logDigest,
                MessageBody = body,
            });
            offset = bodyStart + (int)messageSize;
        }
        return entries;
    }

    /// <summary>
    /// Reads a <c>RespondLogIdRangeFromTx</c> (16-byte packed: <c>fromLogId</c>, <c>length</c>).
    /// </summary>
    public LogIdRange ReadLogIdRange(ReadOnlySpan<byte> payload)
    {
        if (payload.Length != 16)
            throw new ArgumentException($"RespondLogIdRangeFromTx must be 16 bytes, got {payload.Length}.");
        var from = BinaryPrimitives.ReadInt64LittleEndian(payload);
        var len = BinaryPrimitives.ReadInt64LittleEndian(payload[8..]);
        return new LogIdRange(from, len);
    }

    /// <summary>
    /// Reads a <c>RespondAllLogIdRangesFromTick</c> (two long[] of LOG_TX_PER_TICK each
    /// = 4102 entries). Returns one <see cref="LogIdRange"/> per tx slot.
    /// </summary>
    public TickLogIdRanges ReadAllLogIdRanges(uint tick, ReadOnlySpan<byte> payload)
    {
        // LOG_TX_PER_TICK = NUMBER_OF_TRANSACTIONS_PER_TICK + 6 = 4096 + 6 = 4102
        const int LogTxPerTick = 4102;
        const int Expected = LogTxPerTick * 16;
        if (payload.Length != Expected)
            throw new ArgumentException($"RespondAllLogIdRangesFromTick must be {Expected} bytes, got {payload.Length}.");

        var ranges = new LogIdRange[LogTxPerTick];
        var fromOffset = 0;
        var lenOffset = LogTxPerTick * 8;
        for (var i = 0; i < LogTxPerTick; i++)
        {
            var from = BinaryPrimitives.ReadInt64LittleEndian(payload[fromOffset..]);
            var len = BinaryPrimitives.ReadInt64LittleEndian(payload[lenOffset..]);
            ranges[i] = new LogIdRange(from, len);
            fromOffset += 8;
            lenOffset += 8;
        }
        return new TickLogIdRanges { Tick = tick, Ranges = ranges };
    }

    /// <summary>
    /// Reads a <c>RequestTickTransactions</c> incoming-request payload. Layout:
    /// 4-byte tick + N bytes of transaction flags where N is 128 (legacy, 1024 tx/tick)
    /// or 512 (V2+, 4096 tx/tick). The flag-array size is inferred from the payload length.
    /// </summary>
    public RequestTickTransactionsRequest ReadRequestTickTransactions(ReadOnlySpan<byte> payload)
    {
        const int LegacyFlagsSize = 1024 / 8;  // 128
        const int V2FlagsSize = 4096 / 8;      // 512
        var flagsLen = payload.Length - 4;
        if (flagsLen != LegacyFlagsSize && flagsLen != V2FlagsSize)
            throw new ArgumentException(
                $"RequestTickTransactions payload size {payload.Length} unexpected — " +
                $"expected {4 + LegacyFlagsSize} (legacy) or {4 + V2FlagsSize} (V2).");

        var tick = BinaryPrimitives.ReadUInt32LittleEndian(payload);
        var flags = payload.Slice(4, flagsLen).ToArray();
        return new RequestTickTransactionsRequest
        {
            Tick = tick,
            TransactionFlags = flags,
        };
    }

    /// <summary>
    /// Reads a full entity response with Merkle siblings.
    /// </summary>
    public EntityResponse ReadFullEntityResponse(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 64 + 4 + 4) // EntityRecord + tick + spectrumIndex
            throw new ArgumentException("Payload too short for full entity response.");

        var offset = 0;

        // Read entity record (64 bytes)
        var publicKey = payload.Slice(offset, 32).ToArray();
        offset += 32;

        var incomingAmount = BinaryPrimitives.ReadInt64LittleEndian(payload[offset..]);
        offset += 8;

        var outgoingAmount = BinaryPrimitives.ReadInt64LittleEndian(payload[offset..]);
        offset += 8;

        var numberOfIncomingTransfers = BinaryPrimitives.ReadUInt32LittleEndian(payload[offset..]);
        offset += 4;

        var numberOfOutgoingTransfers = BinaryPrimitives.ReadUInt32LittleEndian(payload[offset..]);
        offset += 4;

        var latestIncomingTransferTick = BinaryPrimitives.ReadUInt32LittleEndian(payload[offset..]);
        offset += 4;

        var latestOutgoingTransferTick = BinaryPrimitives.ReadUInt32LittleEndian(payload[offset..]);
        offset += 4;

        var tick = BinaryPrimitives.ReadUInt32LittleEndian(payload[offset..]);
        offset += 4;

        var spectrumIndex = BinaryPrimitives.ReadInt32LittleEndian(payload[offset..]);
        offset += 4;

        // Read siblings if present (24 x 32 bytes)
        byte[][]? siblings = null;
        if (payload.Length >= offset + 24 * 32)
        {
            siblings = new byte[24][];
            for (int i = 0; i < 24; i++)
            {
                siblings[i] = payload.Slice(offset, 32).ToArray();
                offset += 32;
            }
        }

        return new EntityResponse
        {
            Entity = new EntityRecord
            {
                PublicKey = publicKey,
                IncomingAmount = incomingAmount,
                OutgoingAmount = outgoingAmount,
                NumberOfIncomingTransfers = numberOfIncomingTransfers,
                NumberOfOutgoingTransfers = numberOfOutgoingTransfers,
                LatestIncomingTransferTick = latestIncomingTransferTick,
                LatestOutgoingTransferTick = latestOutgoingTransferTick
            },
            Tick = tick,
            SpectrumIndex = spectrumIndex,
            Siblings = siblings
        };
    }
}

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

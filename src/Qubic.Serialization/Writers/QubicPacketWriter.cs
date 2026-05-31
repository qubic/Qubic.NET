using System.Buffers.Binary;
using Qubic.Core;
using Qubic.Core.Entities;

namespace Qubic.Serialization.Writers;

/// <summary>
/// Writes Qubic protocol packets to byte arrays.
/// </summary>
public sealed class QubicPacketWriter
{
    private readonly MemoryStream _stream;
    private readonly BinaryWriter _writer;

    public QubicPacketWriter(int initialCapacity = 256)
    {
        _stream = new MemoryStream(initialCapacity);
        _writer = new BinaryWriter(_stream);
    }

    /// <summary>
    /// Writes an ExchangePublicPeers packet with 4 IPv4 addresses (each 4 bytes).
    /// </summary>
    public byte[] WriteExchangePublicPeers(byte[][]? peerIPs = null)
    {
        Reset();
        // Payload: 4 peers × 4 bytes each = 16 bytes
        WriteHeader(QubicPacketTypes.ExchangePublicPeers, 16);
        for (int i = 0; i < 4; i++)
        {
            if (peerIPs != null && i < peerIPs.Length && peerIPs[i].Length == 4)
            {
                _writer.Write(peerIPs[i]);
            }
            else
            {
                _writer.Write(0); // 0.0.0.0
            }
        }
        return GetPacketBytes();
    }

    /// <summary>
    /// Writes a request for current tick info.
    /// </summary>
    public byte[] WriteRequestCurrentTickInfo()
    {
        Reset();
        WriteHeader(QubicPacketTypes.RequestCurrentTickInfo, 0);
        return GetPacketBytes();
    }

    /// <summary>
    /// Writes a request for entity (balance) information.
    /// </summary>
    public byte[] WriteRequestEntity(QubicIdentity identity)
    {
        Reset();
        WriteHeader(QubicPacketTypes.RequestEntity, 32);
        _writer.Write(identity.PublicKey);
        return GetPacketBytes();
    }

    /// <summary>
    /// Writes a transaction broadcast packet.
    /// Uses dejavu=0 so the receiving node propagates the transaction to other peers.
    /// </summary>
    public byte[] WriteBroadcastTransaction(QubicTransaction transaction)
    {
        if (!transaction.IsSigned)
            throw new InvalidOperationException("Transaction must be signed before broadcasting.");

        var txBytes = GetTransactionBytes(transaction);

        Reset();
        WriteBroadcastHeader(QubicPacketTypes.BroadcastTransaction, txBytes.Length);
        _writer.Write(txBytes);
        return GetPacketBytes();
    }


    /// <summary>
    /// Writes a RequestSystemInfo packet (no payload — just the header).
    /// Node replies with a single RespondSystemInfo (128-byte payload, packed).
    /// </summary>
    public byte[] WriteRequestSystemInfo()
    {
        Reset();
        WriteHeader(QubicPacketTypes.RequestSystemInfo, 0);
        return GetPacketBytes();
    }

    /// <summary>
    /// Writes a request for tick data.
    /// </summary>
    public byte[] WriteRequestTickData(uint tick)
    {
        Reset();
        WriteHeader(QubicPacketTypes.RequestTickData, 4);
        _writer.Write(tick);
        return GetPacketBytes();
    }

    /// <summary>
    /// Writes a RequestQuorumTick packet.
    /// Payload: 4-byte tick + (<see cref="QubicConstants.NumberOfComputors"/>+7)/8 = 85
    /// bytes of vote-flags + 3 bytes trailing alignment padding (the C++ struct's
    /// natural MSVC alignment rounds 89 to 92). Set bit at index <c>i</c> = "I already
    /// have computor <c>i</c>'s vote, skip it." All-zero flags = "send me every vote."
    /// </summary>
    /// <remarks>
    /// The 3-byte trailing pad is required: qubic-core silently drops the packet if
    /// the declared size doesn't match its expected <c>sizeof(RequestQuorumTick)</c>.
    /// </remarks>
    public byte[] WriteRequestQuorumTick(uint tick, ReadOnlySpan<byte> voteFlags = default)
    {
        Reset();
        const int flagsSize = (QubicConstants.NumberOfComputors + 7) / 8; // 85
        const int payloadSize = 4 + flagsSize + 3;                        // 92 (MSVC-aligned)
        WriteHeader(QubicPacketTypes.RequestQuorumTick, payloadSize);
        _writer.Write(tick);
        if (voteFlags.IsEmpty)
        {
            _writer.Write(new byte[flagsSize]);
        }
        else
        {
            if (voteFlags.Length != flagsSize)
                throw new ArgumentException($"voteFlags must be {flagsSize} bytes.", nameof(voteFlags));
            _writer.Write(voteFlags);
        }
        _writer.Write(new byte[3]); // trailing alignment padding
        return GetPacketBytes();
    }

    /// <summary>
    /// Writes a request to invoke a smart contract function.
    /// </summary>
    public byte[] WriteRequestContractFunction(uint contractIndex, ushort inputType, byte[] inputData)
    {
        Reset();
        var payloadSize = 4 + 2 + 2 + inputData.Length;
        WriteHeader(QubicPacketTypes.RequestContractFunction, payloadSize);
        _writer.Write(contractIndex);
        _writer.Write(inputType);
        _writer.Write((ushort)inputData.Length);
        if (inputData.Length > 0)
            _writer.Write(inputData);
        return GetPacketBytes();
    }

    /// <summary>
    /// Writes a request for a contract's IPO status.
    /// </summary>
    public byte[] WriteRequestContractIPO(uint contractIndex)
    {
        Reset();
        WriteHeader(QubicPacketTypes.RequestContractIPO, 4);
        _writer.Write(contractIndex);
        return GetPacketBytes();
    }

    /// <summary>
    /// Writes a SpecialCommand packet with the given payload and signature.
    /// </summary>
    public byte[] WriteSpecialCommand(byte[] commandPayload, byte[] signature)
    {
        Reset();
        WriteHeader(QubicPacketTypes.SpecialCommand, commandPayload.Length + signature.Length);
        _writer.Write(commandPayload);
        _writer.Write(signature);
        return GetPacketBytes();
    }

    /// <summary>
    /// Writes a request for owned assets.
    /// </summary>
    public byte[] WriteRequestOwnedAssets(QubicIdentity identity)
    {
        Reset();
        WriteHeader(QubicPacketTypes.RequestOwnedAssets, 32);
        _writer.Write(identity.PublicKey);
        return GetPacketBytes();
    }

    /// <summary>
    /// Writes a RequestTickTransactions packet.
    /// Requests all transactions in a tick (flags all zero = request everything).
    /// Flag-array size depends on epoch: 128 bytes for legacy (1024 tx/tick),
    /// 512 bytes from epoch <see cref="QubicConstants.TransactionsPerTickV2Epoch"/> (4096 tx/tick).
    /// </summary>
    /// <param name="tick">The tick number to fetch transactions for.</param>
    /// <param name="epoch">Epoch the tick belongs to. Defaults to the V2 epoch so callers
    /// targeting current/future ticks need not pass it; archive callers must pass the
    /// historical epoch to match the node's expected request size.</param>
    public byte[] WriteRequestTickTransactions(
        uint tick,
        ushort epoch = QubicConstants.TransactionsPerTickV2Epoch)
    {
        Reset();
        int flagsSize = QubicConstants.GetMaxTransactionsPerTick(epoch) / 8; // 128 or 512
        WriteHeader(QubicPacketTypes.RequestTickTransactions, 4 + flagsSize);
        _writer.Write(tick);
        _writer.Write(new byte[flagsSize]); // all zeros = request all transactions
        return GetPacketBytes();
    }

    /// <summary>
    /// Writes a RequestTickTransactions packet with an explicit transaction-flag
    /// bitmap, for replaying a previously-observed request verbatim. <paramref
    /// name="transactionFlags"/> must be 128 bytes (legacy 1024 tx/tick) or 512 bytes
    /// (V2 4096 tx/tick); the length picks the era. The dejavu is randomised — use the
    /// <paramref name="dejavu"/>-taking overload to pin it.
    /// </summary>
    public byte[] WriteRequestTickTransactions(uint tick, ReadOnlySpan<byte> transactionFlags)
    {
        ValidateFlagsSize(transactionFlags);
        Reset();
        WriteHeader(QubicPacketTypes.RequestTickTransactions, 4 + transactionFlags.Length);
        _writer.Write(tick);
        _writer.Write(transactionFlags);
        return GetPacketBytes();
    }

    /// <summary>
    /// Same as the other replay-overload but with an explicit <paramref name="dejavu"/>
    /// in the header. Use this when matching a captured request bit-for-bit, or when you
    /// want the response packets to echo back a chosen dejavu for correlation. Note: the
    /// node's dejavu filter silently drops packets whose <c>(salt, dejavu, payload)</c>
    /// hash collides with a recent one — pick a fresh value if you intend the node to
    /// actually process the request.
    /// </summary>
    public byte[] WriteRequestTickTransactions(uint tick, ReadOnlySpan<byte> transactionFlags, uint dejavu)
    {
        ValidateFlagsSize(transactionFlags);
        Reset();
        WriteHeader(QubicPacketTypes.RequestTickTransactions, 4 + transactionFlags.Length, dejavu);
        _writer.Write(tick);
        _writer.Write(transactionFlags);
        return GetPacketBytes();
    }

    private static void ValidateFlagsSize(ReadOnlySpan<byte> transactionFlags)
    {
        const int LegacyFlagsSize = 128;
        const int V2FlagsSize = 512;
        if (transactionFlags.Length != LegacyFlagsSize && transactionFlags.Length != V2FlagsSize)
            throw new ArgumentException(
                $"transactionFlags must be {LegacyFlagsSize} (legacy) or {V2FlagsSize} (V2) bytes, " +
                $"got {transactionFlags.Length}.",
                nameof(transactionFlags));
    }

    /// <summary>
    /// Writes a RequestOracleData packet.
    /// </summary>
    public byte[] WriteRequestOracleData(uint reqType, long reqTickOrId)
    {
        Reset();
        WriteHeader(QubicPacketTypes.RequestOracleData, 16);
        _writer.Write(reqType);
        _writer.Write(0u); // padding
        _writer.Write(reqTickOrId);
        return GetPacketBytes();
    }

    private void Reset()
    {
        _stream.SetLength(0);
        _stream.Position = 0;
    }

    private void WriteHeader(byte type, int payloadSize)
    {
        var header = QubicPacketHeader.Create(type, payloadSize);

        // Write size and protocol (little-endian uint with type in high byte)
        uint sizeAndType = (uint)header.PacketSize | ((uint)type << 24);
        _writer.Write(sizeAndType);
        _writer.Write(header.Dejavu);
    }

    /// <summary>
    /// Writes a header with dejavu=0, signaling the node to propagate the message to other peers.
    /// </summary>
    private void WriteBroadcastHeader(byte type, int payloadSize)
    {
        var header = QubicPacketHeader.Create(type, payloadSize, dejavu: 0);

        uint sizeAndType = (uint)header.PacketSize | ((uint)type << 24);
        _writer.Write(sizeAndType);
        _writer.Write(header.Dejavu);
    }

    /// <summary>
    /// Writes a header with an explicit dejavu — used when replaying a captured packet
    /// verbatim or when correlating request/response by a chosen dejavu value.
    /// </summary>
    private void WriteHeader(byte type, int payloadSize, uint dejavu)
    {
        var header = QubicPacketHeader.Create(type, payloadSize, dejavu);

        uint sizeAndType = (uint)header.PacketSize | ((uint)type << 24);
        _writer.Write(sizeAndType);
        _writer.Write(header.Dejavu);
    }


    private byte[] GetPacketBytes()
    {
        _writer.Flush();
        return _stream.ToArray();
    }

    private static byte[] GetTransactionBytes(QubicTransaction transaction)
    {
        var payloadSize = transaction.Payload?.Length ?? 0;
        // 32 src + 32 dst + 8 amount + 4 tick + 2 inputType + 2 inputSize + payload + 64 signature
        var totalSize = 32 + 32 + 8 + 4 + 2 + 2 + payloadSize + 64;
        var bytes = new byte[totalSize];
        var offset = 0;

        // Source public key
        Array.Copy(transaction.SourceIdentity.PublicKey, 0, bytes, offset, 32);
        offset += 32;

        // Destination public key
        Array.Copy(transaction.DestinationIdentity.PublicKey, 0, bytes, offset, 32);
        offset += 32;

        // Amount
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(offset), transaction.Amount);
        offset += 8;

        // Tick
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), transaction.Tick);
        offset += 4;

        // Input type
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset), transaction.InputType);
        offset += 2;

        // Input size
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset), transaction.InputSize);
        offset += 2;

        // Payload
        if (transaction.Payload is not null && transaction.Payload.Length > 0)
        {
            Array.Copy(transaction.Payload, 0, bytes, offset, transaction.Payload.Length);
            offset += transaction.Payload.Length;
        }

        // Signature
        Array.Copy(transaction.Signature!, 0, bytes, offset, 64);

        return bytes;
    }
}

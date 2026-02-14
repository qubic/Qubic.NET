using System.Buffers.Binary;
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
    /// </summary>
    public byte[] WriteRequestTickTransactions(uint tick)
    {
        Reset();
        // Payload: 4 bytes tick + 128 bytes flags (1024 transactions / 8 bits)
        const int flagsSize = 1024 / 8; // 128
        WriteHeader(QubicPacketTypes.RequestTickTransactions, 4 + flagsSize);
        _writer.Write(tick);
        _writer.Write(new byte[flagsSize]); // all zeros = request all transactions
        return GetPacketBytes();
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

using Qubic.Core;
using Qubic.Core.Entities;
using Qubic.Serialization;
using Qubic.Serialization.Writers;

namespace Qubic.Serialization.Tests;

public class QubicPacketWriterTests
{
    private readonly QubicPacketWriter _writer = new();

    private static QubicTransaction CreateSignedTransaction(
        QubicIdentity source,
        QubicIdentity dest,
        long amount,
        uint tick,
        ushort inputType = 0,
        ushort inputSize = 0,
        byte[]? payload = null)
    {
        var transaction = new QubicTransaction
        {
            SourceIdentity = source,
            DestinationIdentity = dest,
            Amount = amount,
            Tick = tick,
            InputType = inputType,
            InputSize = inputSize,
            Payload = payload
        };
        transaction.SetSignature(new byte[64], "testhash");
        return transaction;
    }

    [Fact]
    public void WriteRequestCurrentTickInfo_CreatesValidPacket()
    {
        var packet = _writer.WriteRequestCurrentTickInfo();

        Assert.NotNull(packet);
        Assert.Equal(QubicPacketHeader.Size, packet.Length); // Header only, no payload

        // Verify type byte is at position 3 (little-endian, type is high byte of first uint)
        Assert.Equal(QubicPacketTypes.RequestCurrentTickInfo, packet[3]);

        // Verify packet size (first 3 bytes, little-endian)
        var packetSize = packet[0] | (packet[1] << 8) | (packet[2] << 16);
        Assert.Equal(QubicPacketHeader.Size, packetSize);
    }

    [Fact]
    public void WriteRequestEntity_CreatesValidPacket()
    {
        var identity = QubicIdentity.FromIdentity("BAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAARMID");

        var packet = _writer.WriteRequestEntity(identity);

        Assert.NotNull(packet);
        Assert.Equal(QubicPacketHeader.Size + 32, packet.Length); // Header + 32-byte public key
        Assert.Equal(QubicPacketTypes.RequestEntity, packet[3]);

        // Verify public key is written after header
        var publicKeyInPacket = new byte[32];
        Array.Copy(packet, QubicPacketHeader.Size, publicKeyInPacket, 0, 32);
        Assert.Equal(identity.PublicKey, publicKeyInPacket);
    }

    [Fact]
    public void WriteRequestTickData_CreatesValidPacket()
    {
        uint tick = 12345678;

        var packet = _writer.WriteRequestTickData(tick);

        Assert.NotNull(packet);
        Assert.Equal(QubicPacketHeader.Size + 4, packet.Length); // Header + 4-byte tick
        Assert.Equal(QubicPacketTypes.RequestTickData, packet[3]);

        // Verify tick is written after header (little-endian)
        var tickInPacket = BitConverter.ToUInt32(packet, QubicPacketHeader.Size);
        Assert.Equal(tick, tickInPacket);
    }

    [Fact]
    public void WriteRequestOwnedAssets_CreatesValidPacket()
    {
        var identity = QubicIdentity.FromIdentity("BAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAARMID");

        var packet = _writer.WriteRequestOwnedAssets(identity);

        Assert.NotNull(packet);
        Assert.Equal(QubicPacketHeader.Size + 32, packet.Length); // Header + 32-byte public key
        Assert.Equal(QubicPacketTypes.RequestOwnedAssets, packet[3]);

        // Verify public key is written after header
        var publicKeyInPacket = new byte[32];
        Array.Copy(packet, QubicPacketHeader.Size, publicKeyInPacket, 0, 32);
        Assert.Equal(identity.PublicKey, publicKeyInPacket);
    }

    [Fact]
    public void WriteBroadcastTransaction_WithSignedTransaction_CreatesValidPacket()
    {
        var source = QubicIdentity.FromIdentity("BAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAARMID");
        var dest = QubicIdentity.FromIdentity("CAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAACNKL");

        var transaction = CreateSignedTransaction(source, dest, 1000000, 12345678);

        var packet = _writer.WriteBroadcastTransaction(transaction);

        Assert.NotNull(packet);
        // Header (8) + Source (32) + Dest (32) + Amount (8) + Tick (4) + InputType (2) + InputSize (2) + Signature (64) = 152
        Assert.Equal(QubicPacketHeader.Size + 144, packet.Length);
        Assert.Equal(QubicPacketTypes.BroadcastTransaction, packet[3]);

        // Dejavu must be 0 so receiving node propagates to other peers
        var dejavu = BitConverter.ToUInt32(packet, 4);
        Assert.Equal(0u, dejavu);
    }

    [Fact]
    public void WriteBroadcastTransaction_WithPayload_CreatesValidPacket()
    {
        var source = QubicIdentity.FromIdentity("BAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAARMID");
        var dest = QubicIdentity.FromIdentity("CAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAACNKL");
        var payload = new byte[100];
        new Random(42).NextBytes(payload);

        var transaction = CreateSignedTransaction(source, dest, 1000000, 12345678, 1, (ushort)payload.Length, payload);

        var packet = _writer.WriteBroadcastTransaction(transaction);

        Assert.NotNull(packet);
        // Header (8) + Source (32) + Dest (32) + Amount (8) + Tick (4) + InputType (2) + InputSize (2) + Payload (100) + Signature (64) = 252
        Assert.Equal(QubicPacketHeader.Size + 244, packet.Length);
    }

    [Fact]
    public void WriteBroadcastTransaction_UnsignedTransaction_ThrowsException()
    {
        var source = QubicIdentity.FromIdentity("BAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAARMID");
        var dest = QubicIdentity.FromIdentity("CAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAACNKL");

        var transaction = new QubicTransaction
        {
            SourceIdentity = source,
            DestinationIdentity = dest,
            Amount = 1000000,
            Tick = 12345678,
            InputType = 0,
            InputSize = 0
            // No signature
        };

        Assert.Throws<InvalidOperationException>(() => _writer.WriteBroadcastTransaction(transaction));
    }

    [Fact]
    public void WriteMultiplePackets_ReusesWriter_ProducesIndependentPackets()
    {
        var identity = QubicIdentity.FromIdentity("BAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAARMID");

        var packet1 = _writer.WriteRequestCurrentTickInfo();
        var packet2 = _writer.WriteRequestEntity(identity);
        var packet3 = _writer.WriteRequestTickData(12345);

        // Each packet should be independent
        Assert.Equal(QubicPacketHeader.Size, packet1.Length);
        Assert.Equal(QubicPacketHeader.Size + 32, packet2.Length);
        Assert.Equal(QubicPacketHeader.Size + 4, packet3.Length);

        // Types should be correct
        Assert.Equal(QubicPacketTypes.RequestCurrentTickInfo, packet1[3]);
        Assert.Equal(QubicPacketTypes.RequestEntity, packet2[3]);
        Assert.Equal(QubicPacketTypes.RequestTickData, packet3[3]);
    }

    [Fact]
    public void PacketHeader_DejavuIsRandom()
    {
        var packet1 = _writer.WriteRequestCurrentTickInfo();
        var packet2 = _writer.WriteRequestCurrentTickInfo();

        // Dejavu is at bytes 4-7
        var dejavu1 = BitConverter.ToUInt32(packet1, 4);
        var dejavu2 = BitConverter.ToUInt32(packet2, 4);

        // Very unlikely to be the same (1 in 2^32)
        Assert.NotEqual(dejavu1, dejavu2);
    }

    [Fact]
    public void WriteRequestTickTransactions_LegacyEpoch_Uses128ByteFlags()
    {
        var packet = _writer.WriteRequestTickTransactions(12345u, epoch: 213);

        // Header (8) + tick (4) + 128 flag bytes (1024/8)
        Assert.Equal(QubicPacketHeader.Size + 4 + 128, packet.Length);
        Assert.Equal(QubicPacketTypes.RequestTickTransactions, packet[3]);

        // All flag bytes must be zero (request all transactions)
        for (int i = QubicPacketHeader.Size + 4; i < packet.Length; i++)
        {
            Assert.Equal(0, packet[i]);
        }
    }

    [Fact]
    public void WriteRequestTickTransactions_V2Epoch_Uses512ByteFlags()
    {
        var packet = _writer.WriteRequestTickTransactions(12345u, epoch: 214);

        // Header (8) + tick (4) + 512 flag bytes (4096/8)
        Assert.Equal(QubicPacketHeader.Size + 4 + 512, packet.Length);
        Assert.Equal(QubicPacketTypes.RequestTickTransactions, packet[3]);
    }

    [Fact]
    public void WriteRequestTickTransactions_DefaultEpoch_UsesV2Size()
    {
        var packet = _writer.WriteRequestTickTransactions(12345u);

        // Default = TransactionsPerTickV2Epoch → V2 flag size
        Assert.Equal(QubicPacketHeader.Size + 4 + 512, packet.Length);
    }

    [Fact]
    public void WriteRequestTickTransactions_EncodesTickInLittleEndian()
    {
        const uint tick = 0x01020304u;
        var packet = _writer.WriteRequestTickTransactions(tick, epoch: 214);

        // Tick starts right after the 8-byte header
        Assert.Equal(0x04, packet[QubicPacketHeader.Size]);
        Assert.Equal(0x03, packet[QubicPacketHeader.Size + 1]);
        Assert.Equal(0x02, packet[QubicPacketHeader.Size + 2]);
        Assert.Equal(0x01, packet[QubicPacketHeader.Size + 3]);
    }
}

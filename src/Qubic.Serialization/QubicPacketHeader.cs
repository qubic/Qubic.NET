using System.Runtime.InteropServices;

namespace Qubic.Serialization;

/// <summary>
/// Qubic network packet header (8 bytes).
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct QubicPacketHeader
{
    /// <summary>
    /// Header size in bytes.
    /// </summary>
    public const int Size = 8;

    /// <summary>
    /// Protocol version and size combined field.
    /// Bits 0-23: size, Bits 24-31: protocol version (high nibble) and type (low nibble)
    /// </summary>
    private uint _sizeAndProtocol;

    /// <summary>
    /// Dejavu value for preventing duplicate processing.
    /// </summary>
    public uint Dejavu;

    /// <summary>
    /// Gets or sets the packet type.
    /// </summary>
    public byte Type
    {
        readonly get => (byte)((_sizeAndProtocol >> 24) & 0xFF);
        set => _sizeAndProtocol = (_sizeAndProtocol & 0x00FFFFFF) | ((uint)value << 24);
    }

    /// <summary>
    /// Gets or sets the total packet size (including header).
    /// </summary>
    public int PacketSize
    {
        readonly get => (int)(_sizeAndProtocol & 0x00FFFFFF);
        set => _sizeAndProtocol = (_sizeAndProtocol & 0xFF000000) | ((uint)value & 0x00FFFFFF);
    }

    /// <summary>
    /// Gets the payload size (packet size minus header).
    /// </summary>
    public readonly int PayloadSize => PacketSize - Size;

    /// <summary>
    /// Creates a new packet header.
    /// </summary>
    public static QubicPacketHeader Create(byte type, int payloadSize)
    {
        return new QubicPacketHeader
        {
            Type = type,
            PacketSize = Size + payloadSize,
            Dejavu = GenerateDejavu()
        };
    }

    /// <summary>
    /// Creates a new packet header with an explicit dejavu value.
    /// Use dejavu=0 for broadcast messages that should be propagated to other peers.
    /// </summary>
    public static QubicPacketHeader Create(byte type, int payloadSize, uint dejavu)
    {
        return new QubicPacketHeader
        {
            Type = type,
            PacketSize = Size + payloadSize,
            Dejavu = dejavu
        };
    }

    private static uint GenerateDejavu()
    {
        return (uint)Random.Shared.Next();
    }
}

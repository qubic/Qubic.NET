using Qubic.Serialization;

namespace Qubic.Network;

/// <summary>
/// Event arguments for a received Qubic packet.
/// </summary>
public sealed class QubicPacketReceivedEventArgs : EventArgs
{
    /// <summary>
    /// The packet header.
    /// </summary>
    public required QubicPacketHeader Header { get; init; }

    /// <summary>
    /// The raw packet bytes (header + payload).
    /// </summary>
    public required byte[] RawPacket { get; init; }

    /// <summary>
    /// The payload bytes (without header).
    /// </summary>
    public ReadOnlyMemory<byte> Payload => RawPacket.AsMemory(QubicPacketHeader.Size, Header.PayloadSize);
}

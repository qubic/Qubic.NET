namespace Qubic.Network;

/// <summary>
/// Connection state of a <see cref="QubicStreamClient"/>.
/// </summary>
public enum QubicStreamConnectionState
{
    Disconnected,
    Connecting,
    Connected,
    Reconnecting
}

namespace Qubic.Network;

/// <summary>
/// Event raised during the lifecycle of a <see cref="QubicStreamClient"/> connection.
/// </summary>
public sealed class QubicStreamEvent
{
    public required QubicStreamEventType Type { get; init; }
    public required string Message { get; init; }
    public string? NodeEndpoint { get; init; }
    public Exception? Exception { get; init; }
}

/// <summary>
/// Types of connection lifecycle events.
/// </summary>
public enum QubicStreamEventType
{
    Connecting,
    Connected,
    Disconnected,
    Reconnecting,
    NodeSwitched,
    NodeMarkedUnavailable,
    NodeRecovered,
    Error
}

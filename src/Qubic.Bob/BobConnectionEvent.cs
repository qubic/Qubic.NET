namespace Qubic.Bob;

/// <summary>
/// Event raised during the lifecycle of a <see cref="BobWebSocketClient"/> connection.
/// </summary>
public sealed class BobConnectionEvent
{
    public required BobConnectionEventType Type { get; init; }
    public required string Message { get; init; }
    public string? NodeUrl { get; init; }
    public Exception? Exception { get; init; }
}

/// <summary>
/// Types of connection lifecycle events.
/// </summary>
public enum BobConnectionEventType
{
    Connecting,
    Connected,
    Disconnected,
    Reconnecting,
    NodeSwitched,
    NodeHealthCheckCompleted,
    NodeMarkedUnavailable,
    NodeRecovered,
    SubscriptionRestored,
    Error,
    Info
}

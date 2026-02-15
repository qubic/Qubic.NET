namespace Qubic.Network;

/// <summary>
/// Configuration options for <see cref="QubicStreamClient"/>.
/// </summary>
public sealed class QubicStreamOptions
{
    /// <summary>
    /// Qubic node endpoints as "host" or "host:port" strings.
    /// At least one must be provided. Default port is 21841.
    /// </summary>
    public required string[] Nodes { get; init; }

    /// <summary>
    /// Timeout for individual request-response operations.
    /// Default: 10 seconds.
    /// </summary>
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Timeout for multi-response operations (tick transactions, oracle data).
    /// Default: 30 seconds.
    /// </summary>
    public TimeSpan MultiResponseTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Interval between health check probes to all nodes.
    /// Default: 30 seconds.
    /// </summary>
    public TimeSpan HealthCheckInterval { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Number of ticks the active node must fall behind the best node before switching.
    /// Default: 5 ticks.
    /// </summary>
    public uint SwitchThresholdTicks { get; init; } = 5;

    /// <summary>
    /// Number of consecutive health check failures before a node is marked unavailable.
    /// Default: 3.
    /// </summary>
    public int FailureThreshold { get; init; } = 3;

    /// <summary>
    /// Initial delay before reconnecting after a connection failure.
    /// Default: 2 seconds.
    /// </summary>
    public TimeSpan ReconnectDelay { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Maximum delay between reconnection attempts (exponential backoff cap).
    /// Default: 60 seconds.
    /// </summary>
    public TimeSpan MaxReconnectDelay { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Maximum packet size in bytes. Packets exceeding this are discarded.
    /// Default: 1 MB.
    /// </summary>
    public int MaxPacketSize { get; init; } = 1024 * 1024;

    /// <summary>
    /// Optional callback for connection lifecycle events.
    /// </summary>
    public Action<QubicStreamEvent>? OnConnectionEvent { get; init; }
}

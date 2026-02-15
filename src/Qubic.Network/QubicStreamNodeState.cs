namespace Qubic.Network;

/// <summary>
/// Tracks the health and sync state of a single Qubic node.
/// </summary>
internal sealed class QubicStreamNodeState
{
    public required string Host { get; init; }
    public required int Port { get; init; }

    public uint CurrentTick { get; set; }
    public ushort CurrentEpoch { get; set; }
    public TimeSpan Latency { get; set; }
    public DateTime LastHealthCheckUtc { get; set; }
    public int ConsecutiveFailures { get; set; }
    public bool IsAvailable { get; set; } = true;

    public string Endpoint => $"{Host}:{Port}";

    public static QubicStreamNodeState Parse(string endpoint, int defaultPort = 21841)
    {
        var parts = endpoint.Split(':', 2);
        var host = parts[0];
        var port = parts.Length > 1 && int.TryParse(parts[1], out var p) ? p : defaultPort;
        return new QubicStreamNodeState { Host = host, Port = port };
    }
}

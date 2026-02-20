using System.Text.Json.Serialization;

namespace Qubic.Bob.Models;

/// <summary>
/// Log ID range for a specific tick, returned by qubic_getTickLogRanges.
/// </summary>
public sealed class TickLogRange
{
    [JsonPropertyName("tick")]
    public uint Tick { get; set; }

    /// <summary>Starting log ID for this tick. Null if no data available.</summary>
    [JsonPropertyName("fromLogId")]
    public long? FromLogId { get; set; }

    /// <summary>Number of log entries in this tick. Null if no data available.</summary>
    [JsonPropertyName("length")]
    public int? Length { get; set; }
}

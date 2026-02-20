using System.Text.Json;
using System.Text.Json.Serialization;

namespace Qubic.Bob.Models;

/// <summary>
/// Data received from a logs subscription notification.
/// </summary>
public sealed class LogNotification
{
    [JsonPropertyName("isCatchUp")]
    public bool IsCatchUp { get; set; }

    [JsonPropertyName("catchUpComplete")]
    public bool CatchUpComplete { get; set; }

    [JsonPropertyName("catchUpProgress")]
    public bool CatchUpProgress { get; set; }

    [JsonPropertyName("percent")]
    public double? Percent { get; set; }

    [JsonPropertyName("current")]
    public long? Current { get; set; }

    [JsonPropertyName("total")]
    public long? Total { get; set; }

    [JsonPropertyName("position")]
    public long? Position { get; set; }

    [JsonPropertyName("matched")]
    public long? Matched { get; set; }

    [JsonPropertyName("lastPosition")]
    public long? LastPosition { get; set; }

    [JsonPropertyName("totalMatched")]
    public long? TotalMatched { get; set; }

    [JsonPropertyName("totalProcessed")]
    public long? TotalProcessed { get; set; }

    [JsonPropertyName("ok")]
    public bool Ok { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }

    [JsonPropertyName("tick")]
    public uint Tick { get; set; }

    [JsonPropertyName("epoch")]
    public uint Epoch { get; set; }

    [JsonPropertyName("logId")]
    public long LogId { get; set; }

    [JsonPropertyName("type")]
    public byte LogType { get; set; }

    [JsonPropertyName("logTypename")]
    public string LogTypeName { get; set; } = string.Empty;

    [JsonPropertyName("logDigest")]
    public string? LogDigest { get; set; }

    [JsonPropertyName("bodySize")]
    public int BodySize { get; set; }

    [JsonPropertyName("timestamp")]
    public JsonElement? Timestamp { get; set; }

    /// <summary>
    /// Gets the timestamp as a string, handling both string and number JSON representations.
    /// </summary>
    public string? GetTimestamp()
    {
        if (!Timestamp.HasValue) return null;
        return Timestamp.Value.ValueKind switch
        {
            JsonValueKind.String => Timestamp.Value.GetString(),
            JsonValueKind.Number => Timestamp.Value.GetRawText(),
            _ => Timestamp.Value.ToString()
        };
    }

    [JsonPropertyName("txHash")]
    public string? TxHash { get; set; }

    [JsonPropertyName("body")]
    public JsonElement? Body { get; set; }
}

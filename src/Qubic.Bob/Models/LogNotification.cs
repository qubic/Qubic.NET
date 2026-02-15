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

    [JsonPropertyName("ok")]
    public bool Ok { get; set; }

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
    public string? Timestamp { get; set; }

    [JsonPropertyName("txHash")]
    public string? TxHash { get; set; }

    [JsonPropertyName("body")]
    public JsonElement? Body { get; set; }
}

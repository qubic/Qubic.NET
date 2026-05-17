using System.Text.Json;
using System.Text.Json.Serialization;

namespace Qubic.Bob.Models;

/// <summary>
/// Transfer record response from qubic_getTransfers.
/// </summary>
public sealed class BobTransferResponse
{
    [JsonPropertyName("txHash")]
    public string TransactionHash { get; set; } = string.Empty;

    [JsonPropertyName("source")]
    public string Source { get; set; } = string.Empty;

    [JsonPropertyName("destination")]
    public string Destination { get; set; } = string.Empty;

    /// <summary>
    /// Raw amount. Bob &lt; 1.5.0 emitted this as a JSON string; 1.5.0+ emits a number.
    /// Use <see cref="AmountValue"/> to read it regardless of representation.
    /// </summary>
    [JsonPropertyName("amount")]
    public JsonElement Amount { get; set; }

    [JsonPropertyName("tick")]
    public uint Tick { get; set; }

    [JsonPropertyName("timestamp")]
    public long? Timestamp { get; set; }

    [JsonPropertyName("success")]
    public bool? Success { get; set; }

    public long AmountValue => Amount.ValueKind switch
    {
        JsonValueKind.Number => Amount.TryGetInt64(out var n) ? n : 0,
        JsonValueKind.String => long.TryParse(Amount.GetString(), out var v) ? v : 0,
        _ => 0
    };
}

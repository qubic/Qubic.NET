using System.Text.Json;
using System.Text.Json.Serialization;

namespace Qubic.Bob.Models;

/// <summary>
/// Data received from a transfers subscription notification.
/// </summary>
public sealed class TransferNotification
{
    [JsonPropertyName("isCatchUp")]
    public bool IsCatchUp { get; set; }

    [JsonPropertyName("catchUpComplete")]
    public bool CatchUpComplete { get; set; }

    [JsonPropertyName("tick")]
    public uint Tick { get; set; }

    [JsonPropertyName("epoch")]
    public uint Epoch { get; set; }

    [JsonPropertyName("logId")]
    public long LogId { get; set; }

    [JsonPropertyName("txHash")]
    public string? TxHash { get; set; }

    [JsonPropertyName("body")]
    public TransferBody? Body { get; set; }
}

/// <summary>
/// Parsed body of a transfer notification.
/// </summary>
public sealed class TransferBody
{
    [JsonPropertyName("from")]
    public string From { get; set; } = string.Empty;

    [JsonPropertyName("to")]
    public string To { get; set; } = string.Empty;

    [JsonPropertyName("amount")]
    public JsonElement Amount { get; set; }

    /// <summary>
    /// Parses the amount, handling both string and number JSON representations.
    /// </summary>
    public long GetAmount()
    {
        if (Amount.ValueKind == JsonValueKind.String)
            return long.TryParse(Amount.GetString(), out var val) ? val : 0;
        if (Amount.ValueKind == JsonValueKind.Number)
            return Amount.GetInt64();
        return 0;
    }
}

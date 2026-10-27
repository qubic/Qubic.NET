using System.Text.Json;
using System.Text.Json.Serialization;

namespace Qubic.Bob.Models;

/// <summary>
/// Transaction receipt response.
/// </summary>
public sealed class TransactionReceiptResponse
{
    [JsonPropertyName("txHash")]
    public string TxHash { get; set; } = string.Empty;

    [JsonPropertyName("hash")]
    public string Hash { get; set; } = string.Empty;

    /// <summary>
    /// Transaction hash — Bob may return it as "txHash" or "hash".
    /// </summary>
    [JsonIgnore]
    public string TransactionHash => !string.IsNullOrEmpty(TxHash) ? TxHash : Hash;

    /// <summary>
    /// Status as raw JsonElement — Bob may send as bool (true/false),
    /// a legacy string ("true"/"false"), or a Bob 1.4.0+ tri-state enum
    /// ("success"/"failed"/"pending").
    /// </summary>
    [JsonPropertyName("status")]
    public JsonElement StatusRaw { get; set; }

    /// <summary>
    /// Tri-state status string. "success", "failed", or "pending" on Bob 1.4.0+.
    /// Returns "success"/"failed" derived from legacy bool/string responses.
    /// </summary>
    [JsonIgnore]
    public string StatusString => StatusRaw.ValueKind switch
    {
        JsonValueKind.True => "success",
        JsonValueKind.False => "failed",
        JsonValueKind.String => NormalizeStatusString(StatusRaw.GetString()),
        _ => "pending"  // absent / undefined status is not yet resolved — never treat as failure
    };

    /// <summary>
    /// Whether the transaction was executed successfully.
    /// Returns null when status is "pending" (Bob 1.4.0+).
    /// </summary>
    [JsonIgnore]
    public bool? Executed => StatusString switch
    {
        "success" => true,
        "failed" => false,
        _ => null  // "pending" or anything unrecognized
    };

    /// <summary>
    /// Legacy bool accessor. Pending is reported as false — callers that need
    /// to distinguish should use <see cref="Executed"/> or <see cref="StatusString"/>.
    /// </summary>
    [JsonIgnore]
    public bool Status => Executed ?? false;

    /// <summary>True when the receipt indicates the tick is still being log-verified.</summary>
    [JsonIgnore]
    public bool IsPending => StatusString == "pending";

    [JsonPropertyName("tick")]
    public uint Tick { get; set; }

    private static string NormalizeStatusString(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return "failed";
        return raw.ToLowerInvariant() switch
        {
            "success" or "true" => "success",
            "failed" or "false" => "failed",
            "pending" => "pending",
            _ => "failed"
        };
    }
}

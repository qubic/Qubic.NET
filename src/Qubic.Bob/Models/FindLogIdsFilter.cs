using System.Text.Json.Serialization;

namespace Qubic.Bob.Models;

/// <summary>
/// Filter criteria for qubic_findLogIds RPC call.
/// Searches the log index and returns tick numbers containing matching events.
/// </summary>
public sealed class FindLogIdsFilter
{
    /// <summary>Smart contract index (0 = protocol-level, e.g. QU_TRANSFER).</summary>
    [JsonPropertyName("scIndex")]
    public int ScIndex { get; set; }

    /// <summary>Log type (0 = QU_TRANSFER, 1 = ASSET_ISSUANCE, etc.).</summary>
    [JsonPropertyName("logType")]
    public int LogType { get; set; }

    /// <summary>Identity filter 1 (e.g. sender/receiver address). Omit for wildcard.</summary>
    [JsonPropertyName("topic1")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Topic1 { get; set; }

    /// <summary>Identity filter 2. Omit for wildcard.</summary>
    [JsonPropertyName("topic2")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Topic2 { get; set; }

    /// <summary>Identity filter 3. Omit for wildcard.</summary>
    [JsonPropertyName("topic3")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Topic3 { get; set; }

    /// <summary>Start of tick range (inclusive).</summary>
    [JsonPropertyName("fromTick")]
    public uint FromTick { get; set; }

    /// <summary>End of tick range (inclusive).</summary>
    [JsonPropertyName("toTick")]
    public uint ToTick { get; set; }
}

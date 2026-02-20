using System.Text.Json;
using System.Text.Json.Serialization;

namespace Qubic.Bob.Models;

/// <summary>
/// Epoch info response from qubic_getCurrentEpoch or qubic_getEpochInfo.
/// </summary>
public sealed class EpochInfoResponse
{
    [JsonPropertyName("epoch")]
    public uint Epoch { get; set; }

    [JsonPropertyName("initialTick")]
    public JsonElement InitialTick { get; set; }

    [JsonPropertyName("endTick")]
    public JsonElement EndTick { get; set; }

    [JsonPropertyName("finalTick")]
    public JsonElement FinalTick { get; set; }

    [JsonPropertyName("endTickStartLogId")]
    public JsonElement EndTickStartLogId { get; set; }

    [JsonPropertyName("endTickEndLogId")]
    public JsonElement EndTickEndLogId { get; set; }

    [JsonPropertyName("lastLogId")]
    public JsonElement LastLogId { get; set; }

    [JsonPropertyName("latestLogId")]
    public JsonElement LatestLogId { get; set; }

    [JsonPropertyName("lastIndexedTick")]
    public JsonElement LastIndexedTick { get; set; }

    [JsonPropertyName("currentTick")]
    public JsonElement CurrentTick { get; set; }

    [JsonPropertyName("numberOfTransactions")]
    public JsonElement NumberOfTransactions { get; set; }

    /// <summary>
    /// Parses a JsonElement that may be a string or number to ulong.
    /// </summary>
    private static ulong ParseJsonElement(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Number)
        {
            if (element.TryGetUInt64(out var u)) return u;
            if (element.TryGetInt64(out var s)) return s >= 0 ? (ulong)s : 0;
            return 0;
        }
        if (element.ValueKind == JsonValueKind.String)
        {
            return ulong.TryParse(element.GetString(), out var val) ? val : 0;
        }
        return 0;
    }

    /// <summary>Gets InitialTick as ulong.</summary>
    public ulong GetInitialTick() => ParseJsonElement(InitialTick);

    /// <summary>Gets EndTick as ulong.</summary>
    public ulong GetEndTick() => ParseJsonElement(EndTick);

    /// <summary>Gets FinalTick as ulong.</summary>
    public ulong GetFinalTick() => ParseJsonElement(FinalTick);

    /// <summary>Gets EndTickStartLogId as ulong.</summary>
    public ulong GetEndTickStartLogId() => ParseJsonElement(EndTickStartLogId);

    /// <summary>Gets EndTickEndLogId as ulong.</summary>
    public ulong GetEndTickEndLogId() => ParseJsonElement(EndTickEndLogId);

    /// <summary>Gets the latest log ID (tries latestLogId first, then lastLogId, then endTickEndLogId).</summary>
    public long GetLastLogId()
    {
        // Try latestLogId (used in actual Bob responses)
        if (LatestLogId.ValueKind == JsonValueKind.Number && LatestLogId.TryGetInt64(out var lv)) return lv;
        if (LatestLogId.ValueKind == JsonValueKind.String && long.TryParse(LatestLogId.GetString(), out var ls)) return ls;
        // Fall back to lastLogId
        if (LastLogId.ValueKind == JsonValueKind.Number && LastLogId.TryGetInt64(out var v)) return v;
        if (LastLogId.ValueKind == JsonValueKind.String && long.TryParse(LastLogId.GetString(), out var s)) return s;
        // Fall back to endTickEndLogId for completed epochs
        return (long)GetEndTickEndLogId();
    }

    /// <summary>Gets LastIndexedTick as ulong.</summary>
    public ulong GetLastIndexedTick() => ParseJsonElement(LastIndexedTick);

    /// <summary>Gets CurrentTick as ulong.</summary>
    public ulong GetCurrentTick() => ParseJsonElement(CurrentTick);

    /// <summary>Gets NumberOfTransactions as ulong.</summary>
    public ulong GetNumberOfTransactions() => ParseJsonElement(NumberOfTransactions);
}

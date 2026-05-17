using System.Text.Json;
using System.Text.Json.Serialization;

namespace Qubic.Bob.Models;

/// <summary>
/// Asset balance response from qubic_getAssetBalance.
/// </summary>
/// <remarks>
/// Pre-1.5.0 emitted the balances as JSON strings; 1.5.0+ emits them as numbers.
/// Stored as <see cref="JsonElement"/> so both representations deserialize cleanly.
/// </remarks>
public sealed class BobAssetBalanceResponse
{
    [JsonPropertyName("issuer")]
    public string Issuer { get; set; } = string.Empty;

    [JsonPropertyName("assetName")]
    public string AssetName { get; set; } = string.Empty;

    [JsonPropertyName("manageSCIndex")]
    public uint ManageSCIndex { get; set; }

    [JsonPropertyName("ownershipBalance")]
    public JsonElement OwnershipBalance { get; set; }

    [JsonPropertyName("possessionBalance")]
    public JsonElement PossessionBalance { get; set; }

    [JsonPropertyName("currentTick")]
    public ulong CurrentTick { get; set; }

    public long OwnershipBalanceValue => ParseBalance(OwnershipBalance);
    public long PossessionBalanceValue => ParseBalance(PossessionBalance);

    private static long ParseBalance(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Number => element.TryGetInt64(out var n) ? n : 0,
        JsonValueKind.String => long.TryParse(element.GetString(), out var v) ? v : 0,
        _ => 0
    };
}

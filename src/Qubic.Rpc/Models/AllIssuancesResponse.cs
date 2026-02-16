namespace Qubic.Rpc.Models;

internal sealed class AllIssuancesResponse
{
    public List<IssuanceItem>? Assets { get; set; }
}

/// <summary>
/// A single asset issuance from the /live/v1/assets/issuances endpoint.
/// </summary>
public sealed class IssuanceItem
{
    public IssuedAssetData Data { get; set; } = new();
    public uint Tick { get; set; }
    public uint UniverseIndex { get; set; }
}

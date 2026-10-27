namespace Qubic.Rpc.Models;

/// <summary>A contract registered on the network (GET /query/v1/getContracts, core-lite).</summary>
public sealed class ContractInfo
{
    public uint Index { get; set; }
    public string Name { get; set; } = string.Empty;
    public uint ConstructionEpoch { get; set; }
    public uint DestructionEpoch { get; set; }
    public ulong StateSize { get; set; }
}

internal sealed class ContractsResponse
{
    public List<ContractInfo> Contracts { get; set; } = [];
}

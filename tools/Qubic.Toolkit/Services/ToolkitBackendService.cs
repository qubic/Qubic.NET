using Qubic.Bob;
using Qubic.Bob.Models;
using Qubic.Core.Entities;
using Qubic.Network;
using Qubic.Rpc;
using Qubic.Rpc.Models;

namespace Qubic.Toolkit;

public enum QueryBackend { Rpc, Bob, DirectNetwork }

public class ToolkitBackendService : IDisposable
{
    public QueryBackend ActiveBackend { get; set; } = QueryBackend.Rpc;
    public string RpcUrl { get; set; } = "https://rpc.qubic.org";
    public string BobUrl { get; set; } = "https://bob.qubic.li";
    public string NodeHost { get; set; } = "corenet.qubic.li";
    public int NodePort { get; set; } = 21841;

    private QubicRpcClient? _rpcClient;
    private BobClient? _bobClient;
    private QubicNodeClient? _nodeClient;

    private QubicRpcClient Rpc => _rpcClient ??= new QubicRpcClient(RpcUrl);
    private BobClient Bob => _bobClient ??= new BobClient(BobUrl);

    private async Task<QubicNodeClient> GetNodeClientAsync()
    {
        if (_nodeClient == null || !_nodeClient.IsConnected)
        {
            _nodeClient?.Dispose();
            _nodeClient = new QubicNodeClient(NodeHost, NodePort);
            await _nodeClient.ConnectAsync();
        }
        return _nodeClient;
    }

    // ── Tick / Network Info ──

    public async Task<CurrentTickInfo> GetTickInfoAsync(CancellationToken ct = default)
    {
        return ActiveBackend switch
        {
            QueryBackend.Rpc => await Rpc.GetTickInfoAsync(ct),
            QueryBackend.Bob => await GetTickInfoViaBobAsync(ct),
            QueryBackend.DirectNetwork => await (await GetNodeClientAsync()).GetCurrentTickInfoAsync(ct),
            _ => throw new InvalidOperationException($"Unknown backend: {ActiveBackend}")
        };
    }

    private async Task<CurrentTickInfo> GetTickInfoViaBobAsync(CancellationToken ct)
    {
        var tick = await Bob.GetTickNumberAsync(ct);
        var epoch = await Bob.GetCurrentEpochAsync(ct);
        return new CurrentTickInfo
        {
            Tick = tick,
            Epoch = (ushort)epoch.Epoch,
            InitialTick = (uint)epoch.GetInitialTick(),
            TickDuration = 0,
            NumberOfAlignedVotes = 0,
            NumberOfMisalignedVotes = 0
        };
    }

    public async Task<SyncStatusResponse> GetSyncStatusAsync(CancellationToken ct = default)
    {
        return await Bob.GetSyncingAsync(ct);
    }

    public async Task<ChainIdResponse> GetChainIdAsync(CancellationToken ct = default)
    {
        return await Bob.GetChainIdAsync(ct);
    }

    public async Task<string> GetClientVersionAsync(CancellationToken ct = default)
    {
        return await Bob.GetClientVersionAsync(ct);
    }

    public async Task<EpochInfoResponse> GetEpochInfoAsync(int epoch, CancellationToken ct = default)
    {
        return await Bob.GetEpochInfoAsync(epoch, ct);
    }

    // ── Balance ──

    public async Task<QubicBalance> GetBalanceAsync(QubicIdentity identity, CancellationToken ct = default)
    {
        return ActiveBackend switch
        {
            QueryBackend.Rpc => await Rpc.GetBalanceAsync(identity, ct),
            QueryBackend.Bob => await Bob.GetBalanceAsync(identity, ct),
            QueryBackend.DirectNetwork => await (await GetNodeClientAsync()).GetBalanceAsync(identity, ct),
            _ => throw new InvalidOperationException($"Unknown backend: {ActiveBackend}")
        };
    }

    // ── Smart Contract Query ──

    public async Task<byte[]> QuerySmartContractAsync(uint contractIndex, uint inputType, byte[] inputData, CancellationToken ct = default)
    {
        return ActiveBackend switch
        {
            QueryBackend.Rpc => await Rpc.QuerySmartContractAsync(contractIndex, inputType, inputData, ct),
            QueryBackend.Bob => await QueryViaBobAsync(contractIndex, inputType, inputData, ct),
            QueryBackend.DirectNetwork => await (await GetNodeClientAsync()).QuerySmartContractAsync(contractIndex, inputType, inputData, ct),
            _ => throw new InvalidOperationException($"Unknown backend: {ActiveBackend}")
        };
    }

    private async Task<byte[]> QueryViaBobAsync(uint contractIndex, uint inputType, byte[] inputData, CancellationToken ct)
    {
        var hexInput = Convert.ToHexString(inputData).ToLowerInvariant();
        var result = await Bob.QuerySmartContractAsync((int)contractIndex, (int)inputType, hexInput, ct);
        if (string.IsNullOrEmpty(result)) return [];
        var hex = result.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? result[2..] : result;
        return Convert.FromHexString(hex);
    }

    // ── Broadcast ──

    public async Task<BroadcastResult> BroadcastTransactionAsync(QubicTransaction tx, CancellationToken ct = default)
    {
        switch (ActiveBackend)
        {
            case QueryBackend.Rpc:
                return await Rpc.BroadcastTransactionAsync(tx, ct);
            case QueryBackend.DirectNetwork:
                var node = await GetNodeClientAsync();
                await node.BroadcastTransactionAsync(tx, ct);
                return new BroadcastResult { TransactionId = tx.TransactionHash ?? "", PeersBroadcasted = 1 };
            case QueryBackend.Bob:
                return await Rpc.BroadcastTransactionAsync(tx, ct);
            default:
                throw new InvalidOperationException($"Unknown backend: {ActiveBackend}");
        }
    }

    // ── Transaction Lookup ──

    public async Task<TransactionInfo?> GetTransactionByHashAsync(string hash, CancellationToken ct = default)
    {
        return await Rpc.GetTransactionByHashAsync(hash, ct);
    }

    public async Task<TransactionReceiptResponse?> GetTransactionReceiptAsync(string hash, CancellationToken ct = default)
    {
        return await Bob.GetTransactionReceiptAsync(hash, ct);
    }

    // ── Transfers ──

    public async Task<IReadOnlyList<QubicTransfer>> GetTransfersAsync(QubicIdentity identity, uint? startTick = null, uint? endTick = null, CancellationToken ct = default)
    {
        return await Bob.GetTransfersAsync(identity, startTick, endTick, ct);
    }

    // ── Assets ──

    public async Task<IReadOnlyList<IssuedAssetInfo>> GetIssuedAssetsAsync(QubicIdentity identity, CancellationToken ct = default)
    {
        return await Rpc.GetIssuedAssetsAsync(identity, ct);
    }

    public async Task<IReadOnlyList<OwnedAssetInfo>> GetOwnedAssetsAsync(QubicIdentity identity, CancellationToken ct = default)
    {
        return await Rpc.GetOwnedAssetsAsync(identity, ct);
    }

    public async Task<IReadOnlyList<PossessedAssetInfo>> GetPossessedAssetsAsync(QubicIdentity identity, CancellationToken ct = default)
    {
        return await Rpc.GetPossessedAssetsAsync(identity, ct);
    }

    // ── IPO ──

    public async Task<IReadOnlyList<IpoInfo>> GetActiveIposAsync(CancellationToken ct = default)
    {
        return await Rpc.GetActiveIposAsync(ct);
    }

    // ── Explorer ──

    public async Task<TickDataInfo?> GetTickDataAsync(uint tick, CancellationToken ct = default)
    {
        return await Rpc.GetTickDataAsync(tick, ct);
    }

    public async Task<IReadOnlyList<TransactionInfo>> GetTransactionsForTickAsync(uint tick, CancellationToken ct = default)
    {
        return await Rpc.GetTransactionsForTickAsync(tick, ct);
    }

    public async Task<IReadOnlyList<ComputorListInfo>> GetComputorListsForEpochAsync(uint epoch, CancellationToken ct = default)
    {
        return await Rpc.GetComputorListsForEpochAsync(epoch, ct);
    }

    public async Task<List<BobLogEntry>> GetLogsAsync(int contractIndex, long? startLogId = null, long? endLogId = null, CancellationToken ct = default)
    {
        return await Bob.GetLogsAsync(contractIndex, startLogId, endLogId, ct);
    }

    // ── Direct Network (Node) ──

    public async Task<string[]> GetNodePeerListAsync(CancellationToken ct = default)
    {
        if (ActiveBackend != QueryBackend.DirectNetwork)
            throw new InvalidOperationException("Peer list is only available with DirectNetwork backend.");
        var node = await GetNodeClientAsync();
        return await node.GetPeerListAsync(ct);
    }

    public async Task<Qubic.Core.Entities.ContractIpo> GetIpoStatusAsync(uint contractIndex, CancellationToken ct = default)
    {
        if (ActiveBackend != QueryBackend.DirectNetwork)
            throw new InvalidOperationException("IPO status is only available with DirectNetwork backend.");
        var node = await GetNodeClientAsync();
        return await node.GetContractIpoAsync(contractIndex, ct);
    }

    // ── Node Commands (DirectNetwork) ──

    public async Task<byte[]> SendNodeCommandAsync(byte[] commandPayload, byte[] signature, CancellationToken ct = default)
    {
        if (ActiveBackend != QueryBackend.DirectNetwork)
            throw new InvalidOperationException("Node commands are only available with DirectNetwork backend.");
        var node = await GetNodeClientAsync();
        return await node.SendSpecialCommandAsync(commandPayload, signature, ct);
    }

    public async Task SendRawPacketAsync(byte[] data, CancellationToken ct = default)
    {
        if (ActiveBackend != QueryBackend.DirectNetwork)
            throw new InvalidOperationException("Raw packet sending is only available with DirectNetwork backend.");
        var node = await GetNodeClientAsync();
        await node.SendRawPacketAsync(data, ct);
    }

    // ── Raw JSON-RPC (Bob Playground) ──

    public async Task<string> SendRawJsonRpcAsync(string method, string? paramsJson = null, CancellationToken ct = default)
    {
        using var http = new HttpClient();
        var id = Random.Shared.Next(1, 999999);
        var request = string.IsNullOrWhiteSpace(paramsJson)
            ? $"{{\"jsonrpc\":\"2.0\",\"method\":\"{method}\",\"params\":[],\"id\":{id}}}"
            : $"{{\"jsonrpc\":\"2.0\",\"method\":\"{method}\",\"params\":{paramsJson},\"id\":{id}}}";

        var content = new StringContent(request, System.Text.Encoding.UTF8, "application/json");
        var bobUrl = BobUrl.TrimEnd('/') + "/qubic";
        var response = await http.PostAsync(bobUrl, content, ct);
        return await response.Content.ReadAsStringAsync(ct);
    }

    // ── Lifecycle ──

    public void ResetClients()
    {
        _rpcClient?.Dispose();
        _rpcClient = null;
        _bobClient = null;
        _nodeClient?.Dispose();
        _nodeClient = null;
    }

    public void Dispose()
    {
        _rpcClient?.Dispose();
        _nodeClient?.Dispose();
    }
}

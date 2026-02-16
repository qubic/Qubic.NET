using Qubic.Bob;
using Qubic.Bob.Models;
using Qubic.Core.Entities;
using Qubic.Crypto;
using Qubic.Network;
using Qubic.Rpc;
using Qubic.Rpc.Models;

namespace Qubic.Services;

public enum QueryBackend { Rpc, Bob, DirectNetwork }

public class QubicBackendService : IDisposable
{
    private readonly QubicSettingsService _settings;

    public QueryBackend ActiveBackend { get; set; }
    public string RpcUrl { get; set; }
    public string BobUrl { get; set; }
    public string NodeHost { get; set; }
    public int NodePort { get; set; }

    public QubicBackendService(QubicSettingsService settings)
    {
        _settings = settings;
        ActiveBackend = Enum.TryParse<QueryBackend>(settings.DefaultBackend, out var b) ? b : QueryBackend.Rpc;
        RpcUrl = settings.RpcUrl;
        BobUrl = settings.BobUrl;
        NodeHost = settings.NodeHost;
        NodePort = settings.NodePort;
    }

    /// <summary>
    /// Apply current settings to the backend and reset clients.
    /// Call this after changing settings properties.
    /// </summary>
    public void ApplySettings()
    {
        _settings.DefaultBackend = ActiveBackend.ToString();
        _settings.RpcUrl = RpcUrl;
        _settings.BobUrl = BobUrl;
        _settings.NodeHost = NodeHost;
        _settings.NodePort = NodePort;
        ResetClients();
    }

    // ── URL part accessors (decompose/recompose full URLs) ──

    public string RpcScheme
    {
        get => GetScheme(RpcUrl);
        set => RpcUrl = BuildUrl(value, RpcHost, RpcPort);
    }

    public string RpcHost
    {
        get => GetHost(RpcUrl);
        set => RpcUrl = BuildUrl(RpcScheme, value, RpcPort);
    }

    public int RpcPort
    {
        get => GetPort(RpcUrl);
        set => RpcUrl = BuildUrl(RpcScheme, RpcHost, value);
    }

    public string BobScheme
    {
        get => GetScheme(BobUrl);
        set => BobUrl = BuildUrl(value, BobHost, BobPort);
    }

    public string BobHost
    {
        get => GetHost(BobUrl);
        set => BobUrl = BuildUrl(BobScheme, value, BobPort);
    }

    public int BobPort
    {
        get => GetPort(BobUrl);
        set => BobUrl = BuildUrl(BobScheme, BobHost, value);
    }

    private static string GetScheme(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Scheme : "https";

    private static string GetHost(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host : url;

    private static int GetPort(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return 443;
        // If port was explicitly in URL, return it; otherwise return the default for the scheme
        return u.IsDefaultPort ? (u.Scheme == "https" ? 443 : 80) : u.Port;
    }

    private static string BuildUrl(string scheme, string host, int port)
    {
        if (string.IsNullOrWhiteSpace(scheme)) scheme = "https";
        if (string.IsNullOrWhiteSpace(host)) host = "localhost";
        bool isDefault = (scheme == "https" && port == 443) || (scheme == "http" && port == 80);
        return isDefault ? $"{scheme}://{host}" : $"{scheme}://{host}:{port}";
    }

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

    public async Task<IReadOnlyList<IssuanceItem>> GetAllIssuancesAsync(CancellationToken ct = default)
    {
        return await Rpc.GetAllIssuancesAsync(ct);
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

        using var node = new QubicNodeClient(NodeHost, NodePort);
        await node.ConnectAsync(ct);
        return await node.GetPeerListAsync(ct);
    }

    /// <summary>
    /// Result of an auto-discover peer operation.
    /// </summary>
    public record PeerDiscoveryResult(
        bool Switched,
        string OriginalHost,
        string? NewHost,
        uint OriginalTick,
        uint BestTick,
        int PeersScanned,
        int PeersReachable);

    /// <summary>
    /// Event raised when the node host is changed by auto-discovery.
    /// </summary>
    public event Action<PeerDiscoveryResult>? OnPeerDiscovered;

    /// <summary>
    /// Discovers the most recent peer and switches to it if the current peer is behind.
    /// </summary>
    public async Task<PeerDiscoveryResult> AutoDiscoverRecentPeerAsync(int tickThreshold, CancellationToken ct = default)
    {
        if (ActiveBackend != QueryBackend.DirectNetwork)
            throw new InvalidOperationException("Auto-discover is only available with DirectNetwork backend.");

        // Step 1: Get tick from current peer
        uint currentTick = 0;
        try
        {
            using var currentNode = new QubicNodeClient(NodeHost, NodePort);
            await currentNode.ConnectAsync(ct);
            var info = await currentNode.GetCurrentTickInfoAsync(ct);
            currentTick = info.Tick;
        }
        catch { /* current peer unreachable, proceed with discovery */ }

        // Step 2: Discover peers at depth 2 (peers of peers)
        var allPeers = new HashSet<string>();
        try
        {
            using var peerNode = new QubicNodeClient(NodeHost, NodePort);
            await peerNode.ConnectAsync(ct);
            var directPeers = await peerNode.GetPeerListAsync(ct);
            foreach (var ip in directPeers.Where(ip => ip != "0.0.0.0"))
                allPeers.Add(ip);
        }
        catch
        {
            return new PeerDiscoveryResult(false, NodeHost, null, currentTick, currentTick, 0, 0);
        }

        // Depth 2: get peers from each discovered peer in parallel
        var depth2Tasks = allPeers.Select(async ip =>
        {
            try
            {
                using var node = new QubicNodeClient(ip, NodePort);
                await node.ConnectAsync(ct);
                return await node.GetPeerListAsync(ct);
            }
            catch { return Array.Empty<string>(); }
        }).ToList();

        var depth2Results = await Task.WhenAll(depth2Tasks);
        foreach (var peerList in depth2Results)
            foreach (var ip in peerList.Where(ip => ip != "0.0.0.0"))
                allPeers.Add(ip);

        // Step 3: Query tick info from all discovered peers in parallel
        var tasks = allPeers
            .Select(async ip =>
            {
                try
                {
                    using var node = new QubicNodeClient(ip, NodePort);
                    await node.ConnectAsync(ct);
                    var info = await node.GetCurrentTickInfoAsync(ct);
                    return (Ip: ip, Tick: info.Tick, Ok: true);
                }
                catch
                {
                    return (Ip: ip, Tick: (uint)0, Ok: false);
                }
            })
            .ToList();

        var results = await Task.WhenAll(tasks);
        var reachable = results.Where(r => r.Ok).ToList();

        if (reachable.Count == 0)
            return new PeerDiscoveryResult(false, NodeHost, null, currentTick, currentTick, results.Length, 0);

        // Step 4: Find the best peer (highest tick)
        var best = reachable.OrderByDescending(r => r.Tick).First();

        // Step 5: Check if current peer is behind by more than threshold
        bool shouldSwitch = currentTick == 0 || (best.Tick > currentTick && best.Tick - currentTick > (uint)tickThreshold);
        var result = new PeerDiscoveryResult(
            shouldSwitch, NodeHost, shouldSwitch ? best.Ip : null,
            currentTick, best.Tick, results.Length, reachable.Count);

        if (shouldSwitch)
        {
            // Step 6: Switch to the better peer
            NodeHost = best.Ip;
            ResetClients();
            _settings.NodeHost = best.Ip;
            OnPeerDiscovered?.Invoke(result);
        }

        return result;
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

    // ── Transaction Verification (DirectNetwork) ──

    public async Task<bool> CheckTransactionInTickAsync(string txHash, uint tick, CancellationToken ct = default)
    {
        if (ActiveBackend != QueryBackend.DirectNetwork)
            throw new InvalidOperationException("Tick transaction check is only available with DirectNetwork backend.");

        var node = await GetNodeClientAsync();
        var rawTransactions = await node.GetTickTransactionsAsync(tick, ct);

        var crypt = new QubicCrypt();
        foreach (var rawTx in rawTransactions)
        {
            var digest = crypt.KangarooTwelve(rawTx);
            var hash = crypt.GetHumanReadableBytes(digest);
            if (string.Equals(hash, txHash, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    // ── Oracle Data (DirectNetwork) ──

    public async Task<List<byte[]>> GetOracleQueryAsync(long queryId, CancellationToken ct = default)
    {
        if (ActiveBackend != QueryBackend.DirectNetwork)
            throw new InvalidOperationException("Oracle data is only available with DirectNetwork backend.");
        var node = await GetNodeClientAsync();
        return await node.GetOracleQueryAsync(queryId, ct);
    }

    public async Task<List<byte[]>> GetOracleQueryIdsByTickAsync(uint tick, uint filterType = 0, CancellationToken ct = default)
    {
        if (ActiveBackend != QueryBackend.DirectNetwork)
            throw new InvalidOperationException("Oracle data is only available with DirectNetwork backend.");
        var node = await GetNodeClientAsync();
        return await node.GetOracleQueryIdsByTickAsync(tick, filterType, ct);
    }

    public async Task<List<byte[]>> GetOracleStatisticsAsync(CancellationToken ct = default)
    {
        if (ActiveBackend != QueryBackend.DirectNetwork)
            throw new InvalidOperationException("Oracle data is only available with DirectNetwork backend.");
        var node = await GetNodeClientAsync();
        return await node.GetOracleStatisticsAsync(ct);
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

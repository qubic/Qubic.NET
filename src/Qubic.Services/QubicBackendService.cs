using Qubic.Bob;
using Qubic.Bob.Models;
using Qubic.Core.Entities;
using Qubic.Crypto;
using Qubic.Network;
using Qubic.Rpc;
using Qubic.Rpc.Models;
using Qubic.Services.Networks;

namespace Qubic.Services;

/// <summary>Legacy name for the primary method of the active network profile.</summary>
public enum QueryBackend { Rpc, Bob, DirectNetwork }

/// <summary>A computor list as reported by whichever method served it.</summary>
public sealed record ComputorListSnapshot(uint Epoch, IReadOnlyList<string> Identities, string? SignatureHex, BackendMethodKind Source);

/// <summary>
/// Routes every network operation to a method of the active <see cref="NetworkProfile"/>.
/// Each capability is served by the first enabled method (in profile order) that supports it;
/// if none can, <see cref="CapabilityUnavailableException"/> is thrown. Nothing ever falls
/// back to an endpoint outside the active profile, and all connections pass the
/// <see cref="EgressGuard"/>.
/// </summary>
public class QubicBackendService : IDisposable
{
    private readonly NetworkProfileService _profiles;
    private readonly EgressGuard _guard;
    private readonly object _clientLock = new();

    private QubicRpcClient? _rpcClient;
    private BobClient? _bobClient;
    private QubicNodeClient? _nodeClient;

    public QubicBackendService(NetworkProfileService profiles, EgressGuard guard)
    {
        _profiles = profiles;
        _guard = guard;
        _profiles.OnActiveProfileChanged += (_, _) => ResetClients();
    }

    // ── Profile view ──

    /// <summary>Snapshot of the active network profile.</summary>
    public NetworkProfile Profile => _profiles.Active;

    public EgressGuard Guard => _guard;

    /// <summary>The primary (first enabled) method of the active profile.</summary>
    public QueryBackend ActiveBackend => Profile.EnabledMethods.FirstOrDefault()?.Kind switch
    {
        BackendMethodKind.Bob => QueryBackend.Bob,
        BackendMethodKind.Direct => QueryBackend.DirectNetwork,
        _ => QueryBackend.Rpc
    };

    public bool HasMethod(BackendMethodKind kind) => Profile.HasMethod(kind);

    /// <summary>Base URL of the enabled RPC method, or empty if the profile has none.</summary>
    public string RpcUrl => Profile.GetMethod(BackendMethodKind.Rpc)?.Url ?? "";

    /// <summary>Base URL of the enabled Bob method, or empty if the profile has none.</summary>
    public string BobUrl => Profile.GetMethod(BackendMethodKind.Bob)?.Url ?? "";

    public string NodeHost => Profile.GetMethod(BackendMethodKind.Direct)?.Host ?? "";

    public int NodePort => Profile.GetMethod(BackendMethodKind.Direct)?.Port ?? 0;

    public string RpcHost => HostOf(RpcUrl);
    public string BobHost => HostOf(BobUrl);

    private static string HostOf(string url) => Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host : url;

    /// <summary>Whether the active profile can serve <paramref name="capability"/>.</summary>
    public bool IsAvailable(Capability capability) => Capabilities.Resolve(Profile, capability) != null;

    /// <summary>The method that serves <paramref name="capability"/>, or null.</summary>
    public BackendMethodKind? RouteFor(Capability capability) => Capabilities.Resolve(Profile, capability)?.Kind;

    /// <summary>The method serving <paramref name="capability"/>; throws if the profile cannot serve it.</summary>
    public BackendMethod Resolve(Capability capability)
    {
        var profile = Profile;
        return Capabilities.Resolve(profile, capability)
            ?? throw new CapabilityUnavailableException(capability, profile.Name);
    }

    // ── Clients (all connections go through the egress guard) ──

    private QubicRpcClient Rpc
    {
        get
        {
            lock (_clientLock)
            {
                if (_rpcClient != null) return _rpcClient;
                var url = Profile.GetMethod(BackendMethodKind.Rpc)?.Url
                    ?? throw new CapabilityUnavailableException(Capability.TickInfo, Profile.Name);
                return _rpcClient = new QubicRpcClient(_guard.CreateHttpClient(url, "rpc"), ownsHttpClient: true);
            }
        }
    }

    private BobClient Bob
    {
        get
        {
            lock (_clientLock)
            {
                if (_bobClient != null) return _bobClient;
                var url = Profile.GetMethod(BackendMethodKind.Bob)?.Url
                    ?? throw new CapabilityUnavailableException(Capability.ChainInfo, Profile.Name);
                return _bobClient = new BobClient(_guard.CreateHttpClient(url, "bob"), ownsHttpClient: true);
            }
        }
    }

    /// <summary>Creates a guarded Bob HTTP client for the active profile (caller disposes).</summary>
    public BobClient CreateBobClient()
    {
        var url = Profile.GetMethod(BackendMethodKind.Bob)?.Url
            ?? throw new CapabilityUnavailableException(Capability.ChainInfo, Profile.Name);
        return new BobClient(_guard.CreateHttpClient(url, "bob"), ownsHttpClient: true);
    }

    /// <summary>Creates a guarded RPC client for the active profile (caller disposes).</summary>
    public QubicRpcClient CreateRpcClient()
    {
        var url = Profile.GetMethod(BackendMethodKind.Rpc)?.Url
            ?? throw new CapabilityUnavailableException(Capability.TransactionArchive, Profile.Name);
        return new QubicRpcClient(_guard.CreateHttpClient(url, "rpc"), ownsHttpClient: true);
    }

    /// <summary>
    /// WebSocket options for the active profile's Bob method, after checking the host
    /// against the egress guard. Throws if the profile has no Bob method.
    /// </summary>
    public BobWebSocketOptions CreateBobWebSocketOptions(Action<BobConnectionEvent>? onEvent = null)
    {
        var url = Profile.GetMethod(BackendMethodKind.Bob)?.Url
            ?? throw new CapabilityUnavailableException(Capability.Logs, Profile.Name);
        _guard.Check(new Uri(url), "bob-ws");
        return onEvent == null
            ? new BobWebSocketOptions { Nodes = [url] }
            : new BobWebSocketOptions { Nodes = [url], OnConnectionEvent = onEvent };
    }

    /// <summary>Creates a node client after checking host/port against the egress guard.</summary>
    public QubicNodeClient CreateNodeClient(string host, int port)
    {
        _guard.Check(host, port, "direct");
        return new QubicNodeClient(host, port);
    }

    private async Task<QubicNodeClient> GetNodeClientAsync()
    {
        var direct = Profile.GetMethod(BackendMethodKind.Direct)
            ?? throw new CapabilityUnavailableException(Capability.Peers, Profile.Name);
        QubicNodeClient? stale = null;
        lock (_clientLock)
        {
            if (_nodeClient != null && _nodeClient.IsConnected) return _nodeClient;
            stale = _nodeClient;
            _nodeClient = null;
        }
        stale?.Dispose();
        var client = CreateNodeClient(direct.Host, direct.Port);
        await client.ConnectAsync();
        lock (_clientLock) _nodeClient = client;
        return client;
    }

    private Task<QubicNodeClient> DirectFor(Capability capability)
    {
        if (Capabilities.Resolve(Profile, capability)?.Kind != BackendMethodKind.Direct)
            throw new CapabilityUnavailableException(capability, Profile.Name);
        return GetNodeClientAsync();
    }

    // ── Tick / Network Info ──

    public async Task<CurrentTickInfo> GetTickInfoAsync(CancellationToken ct = default)
    {
        return Resolve(Capability.TickInfo).Kind switch
        {
            BackendMethodKind.Rpc => await Rpc.GetTickInfoAsync(ct),
            BackendMethodKind.Bob => await GetTickInfoViaBobAsync(ct),
            _ => await (await GetNodeClientAsync()).GetCurrentTickInfoAsync(ct),
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

    public Task<SyncStatusResponse> GetSyncStatusAsync(CancellationToken ct = default)
    {
        Resolve(Capability.ChainInfo);
        return Bob.GetSyncingAsync(ct);
    }

    public Task<ChainIdResponse> GetChainIdAsync(CancellationToken ct = default)
    {
        Resolve(Capability.ChainInfo);
        return Bob.GetChainIdAsync(ct);
    }

    public Task<string> GetClientVersionAsync(CancellationToken ct = default)
    {
        Resolve(Capability.ChainInfo);
        return Bob.GetClientVersionAsync(ct);
    }

    public Task<EpochInfoResponse> GetEpochInfoAsync(int epoch, CancellationToken ct = default)
    {
        Resolve(Capability.ChainInfo);
        return Bob.GetEpochInfoAsync(epoch, ct);
    }

    // ── Balance ──

    public async Task<QubicBalance> GetBalanceAsync(QubicIdentity identity, CancellationToken ct = default)
    {
        return Resolve(Capability.Balance).Kind switch
        {
            BackendMethodKind.Rpc => await Rpc.GetBalanceAsync(identity, ct),
            BackendMethodKind.Bob => await Bob.GetBalanceAsync(identity, ct),
            _ => await (await GetNodeClientAsync()).GetBalanceAsync(identity, ct),
        };
    }

    // ── Smart Contract Query ──

    public async Task<byte[]> QuerySmartContractAsync(uint contractIndex, uint inputType, byte[] inputData, CancellationToken ct = default)
    {
        return Resolve(Capability.SmartContractQuery).Kind switch
        {
            BackendMethodKind.Rpc => await Rpc.QuerySmartContractAsync(contractIndex, inputType, inputData, ct),
            BackendMethodKind.Bob => await QueryViaBobAsync(contractIndex, inputType, inputData, ct),
            _ => await (await GetNodeClientAsync()).QuerySmartContractAsync(contractIndex, inputType, inputData, ct),
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
        switch (Resolve(Capability.Broadcast).Kind)
        {
            case BackendMethodKind.Rpc:
                return await Rpc.BroadcastTransactionAsync(tx, ct);
            case BackendMethodKind.Bob:
                var hash = await Bob.BroadcastTransactionAsync(tx, ct);
                return new BroadcastResult
                {
                    TransactionId = string.IsNullOrEmpty(hash) ? tx.TransactionHash ?? "" : hash,
                    PeersBroadcasted = 1
                };
            default:
                var node = await GetNodeClientAsync();
                await node.BroadcastTransactionAsync(tx, ct);
                return new BroadcastResult { TransactionId = tx.TransactionHash ?? "", PeersBroadcasted = 1 };
        }
    }

    // ── Computors ──

    /// <summary>Computor list for <paramref name="epoch"/> (null = current epoch).</summary>
    public async Task<ComputorListSnapshot> GetComputorsAsync(uint? epoch = null, CancellationToken ct = default)
    {
        switch (Resolve(Capability.Computors).Kind)
        {
            case BackendMethodKind.Bob:
            {
                var r = await Bob.GetComputorsAsync(epoch, ct);
                return new ComputorListSnapshot(r.Epoch, r.Computors.Select(CleanIdentity).ToList(), r.Signature, BackendMethodKind.Bob);
            }
            case BackendMethodKind.Direct:
            {
                var node = await GetNodeClientAsync();
                var c = await node.GetComputorsAsync(ct)
                    ?? throw new InvalidOperationException("Node did not return a computor list.");
                var crypt = new QubicCrypt();
                var ids = c.PublicKeys.Select(pk => crypt.GetIdentityFromPublicKey(pk)).ToList();
                return new ComputorListSnapshot(c.Epoch, ids, Convert.ToHexString(c.Signature).ToLowerInvariant(), BackendMethodKind.Direct);
            }
            default:
            {
                var e = epoch ?? (await Rpc.GetTickInfoAsync(ct)).Epoch;
                var lists = await Rpc.GetComputorListsForEpochAsync(e, ct);
                var latest = lists.OrderByDescending(l => l.TickNumber).FirstOrDefault()
                    ?? throw new InvalidOperationException($"No computor list for epoch {e}.");
                return new ComputorListSnapshot(latest.Epoch, latest.Identities, NormalizeSignature(latest.Signature), BackendMethodKind.Rpc);
            }
        }
    }

    /// <summary>
    /// Bob's qubic_getComputors can append stray bytes after the 60 identity characters
    /// (its identity buffer is not null-terminated). Keep the leading 60 A-Z characters.
    /// </summary>
    internal static string CleanIdentity(string id)
    {
        if (id.Length <= 60) return id;
        var head = id[..60];
        return head.All(c => c is >= 'A' and <= 'Z') ? head : id;
    }

    /// <summary>Signatures arrive as hex (Bob, Direct) or base64 (query service / core-lite); returns lowercase hex.</summary>
    internal static string? NormalizeSignature(string? sig)
    {
        if (string.IsNullOrEmpty(sig)) return null;
        if (sig.Length == 128 && sig.All(Uri.IsHexDigit)) return sig.ToLowerInvariant();
        try
        {
            var bytes = Convert.FromBase64String(sig);
            return bytes.Length == 64 ? Convert.ToHexString(bytes).ToLowerInvariant() : null;
        }
        catch (FormatException) { return null; }
    }

    // ── Contract registry ──

    /// <summary>Contracts compiled into the node (core-lite). Throws if the profile has no RPC method.</summary>
    public Task<IReadOnlyList<ContractInfo>> GetContractsAsync(CancellationToken ct = default)
    {
        Resolve(Capability.ContractRegistry);
        return Rpc.GetContractsAsync(ct);
    }

    // ── Transaction Lookup ──

    public async Task<TransactionInfo?> GetTransactionByHashAsync(string hash, CancellationToken ct = default)
    {
        if (Resolve(Capability.TransactionLookup).Kind == BackendMethodKind.Rpc)
            return await Rpc.GetTransactionByHashAsync(hash, ct);

        var bobTx = await Bob.GetTransactionDetailsAsync(hash, ct);
        if (bobTx == null) return null;
        return new TransactionInfo
        {
            Hash = string.IsNullOrEmpty(bobTx.TransactionHash) ? hash : bobTx.TransactionHash,
            Source = bobTx.SourceAddress,
            Destination = bobTx.DestAddress,
            Amount = bobTx.AmountValue.ToString(),
            TickNumber = bobTx.Tick,
            InputType = (uint)Math.Max(0, bobTx.InputType),
            InputSize = (uint)Math.Max(0, bobTx.InputSize),
            InputData = bobTx.InputData ?? ""
        };
    }

    public Task<TransactionReceiptResponse?> GetTransactionReceiptAsync(string hash, CancellationToken ct = default)
    {
        Resolve(Capability.TransactionReceipt);
        return Bob.GetTransactionReceiptAsync(hash, ct);
    }

    // ── Transfers ──

    public Task<IReadOnlyList<QubicTransfer>> GetTransfersAsync(QubicIdentity identity, uint? startTick = null, uint? endTick = null, CancellationToken ct = default)
    {
        Resolve(Capability.TransferHistory);
        return Bob.GetTransfersAsync(identity, startTick, endTick, ct);
    }

    // ── Assets ──

    public async Task<IReadOnlyList<IssuedAssetInfo>> GetIssuedAssetsAsync(QubicIdentity identity, CancellationToken ct = default)
    {
        if (Resolve(Capability.AssetListings).Kind == BackendMethodKind.Rpc)
            return await Rpc.GetIssuedAssetsAsync(identity, ct);
        return await (await GetNodeClientAsync()).GetIssuedAssetsAsync(identity, ct);
    }

    public async Task<IReadOnlyList<OwnedAssetInfo>> GetOwnedAssetsAsync(QubicIdentity identity, CancellationToken ct = default)
    {
        if (Resolve(Capability.AssetListings).Kind == BackendMethodKind.Rpc)
            return await Rpc.GetOwnedAssetsAsync(identity, ct);
        return await (await GetNodeClientAsync()).GetOwnedAssetsAsync(identity, ct);
    }

    public async Task<IReadOnlyList<PossessedAssetInfo>> GetPossessedAssetsAsync(QubicIdentity identity, CancellationToken ct = default)
    {
        if (Resolve(Capability.AssetListings).Kind == BackendMethodKind.Rpc)
            return await Rpc.GetPossessedAssetsAsync(identity, ct);
        return await (await GetNodeClientAsync()).GetPossessedAssetsAsync(identity, ct);
    }

    public Task<IReadOnlyList<IssuanceItem>> GetAllIssuancesAsync(CancellationToken ct = default)
    {
        Resolve(Capability.Issuances);
        return Rpc.GetAllIssuancesAsync(ct);
    }

    /// <summary>Balance of one known asset via Bob (owned + possessed).</summary>
    public Task<BobAssetBalanceResponse?> GetAssetBalanceAsync(string identity, string issuer, string assetName, uint manageSCIndex, CancellationToken ct = default)
    {
        Resolve(Capability.AssetBalance);
        return BobAssetBalanceAsync(identity, issuer, assetName, manageSCIndex, ct);
    }

    private async Task<BobAssetBalanceResponse?> BobAssetBalanceAsync(string identity, string issuer, string assetName, uint manageSCIndex, CancellationToken ct)
    {
        using var ws = new BobWebSocketClient(CreateBobWebSocketOptions());
        await ws.ConnectAsync(ct);
        return await ws.GetAssetBalanceAsync(identity, issuer, assetName, manageSCIndex, ct);
    }

    // ── IPO ──

    public Task<IReadOnlyList<IpoInfo>> GetActiveIposAsync(CancellationToken ct = default)
    {
        Resolve(Capability.ActiveIpos);
        return Rpc.GetActiveIposAsync(ct);
    }

    public async Task<ContractIpo> GetIpoStatusAsync(uint contractIndex, CancellationToken ct = default)
    {
        var node = await DirectFor(Capability.IpoStatus);
        return await node.GetContractIpoAsync(contractIndex, ct);
    }

    // ── Explorer ──

    public Task<TickDataInfo?> GetTickDataAsync(uint tick, CancellationToken ct = default)
    {
        Resolve(Capability.TickData);
        return Rpc.GetTickDataAsync(tick, ct);
    }

    public Task<IReadOnlyList<TransactionInfo>> GetTransactionsForTickAsync(uint tick, CancellationToken ct = default)
    {
        Resolve(Capability.TickData);
        return Rpc.GetTransactionsForTickAsync(tick, ct);
    }

    public Task<IReadOnlyList<ComputorListInfo>> GetComputorListsForEpochAsync(uint epoch, CancellationToken ct = default)
    {
        if (Resolve(Capability.Computors).Kind != BackendMethodKind.Rpc)
            throw new CapabilityUnavailableException(Capability.TickData, Profile.Name);
        return Rpc.GetComputorListsForEpochAsync(epoch, ct);
    }

    public Task<List<BobLogEntry>> GetLogsAsync(int contractIndex, long? startLogId = null, long? endLogId = null, CancellationToken ct = default)
    {
        Resolve(Capability.Logs);
        return Bob.GetLogsAsync(contractIndex, startLogId, endLogId, ct);
    }

    // ── Direct Network (Node) ──

    public async Task<string[]> GetNodePeerListAsync(CancellationToken ct = default)
    {
        Resolve(Capability.Peers);
        var direct = Profile.GetMethod(BackendMethodKind.Direct)!;
        using var node = CreateNodeClient(direct.Host, direct.Port);
        await node.ConnectAsync(ct);
        return await node.GetPeerListAsync(ct);
    }

    /// <summary>Result of an auto-discover peer operation.</summary>
    public record PeerDiscoveryResult(
        bool Switched,
        string OriginalHost,
        string? NewHost,
        uint OriginalTick,
        uint BestTick,
        int PeersScanned,
        int PeersReachable);

    /// <summary>Event raised when the node host is changed by auto-discovery.</summary>
    public event Action<PeerDiscoveryResult>? OnPeerDiscovered;

    /// <summary>
    /// Discovers the most recent peer and switches the profile's Direct method to it if the
    /// current peer is behind. Only allowed when the Direct method has peer discovery enabled.
    /// </summary>
    public async Task<PeerDiscoveryResult> AutoDiscoverRecentPeerAsync(int tickThreshold, CancellationToken ct = default)
    {
        Resolve(Capability.Peers);
        var profile = Profile;
        var direct = profile.GetMethod(BackendMethodKind.Direct)!;
        if (!direct.PeerDiscovery)
            throw new InvalidOperationException($"Peer discovery is disabled for \"{profile.Name}\".");
        var host = direct.Host;
        var port = direct.Port;

        // Step 1: Get tick from current peer
        uint currentTick = 0;
        try
        {
            using var currentNode = CreateNodeClient(host, port);
            await currentNode.ConnectAsync(ct);
            var info = await currentNode.GetCurrentTickInfoAsync(ct);
            currentTick = info.Tick;
        }
        catch { /* current peer unreachable, proceed with discovery */ }

        // Step 2: Discover peers at depth 2 (peers of peers)
        var allPeers = new HashSet<string>();
        try
        {
            using var peerNode = CreateNodeClient(host, port);
            await peerNode.ConnectAsync(ct);
            var directPeers = await peerNode.GetPeerListAsync(ct);
            foreach (var ip in directPeers.Where(ip => ip != "0.0.0.0"))
                allPeers.Add(ip);
        }
        catch
        {
            return new PeerDiscoveryResult(false, host, null, currentTick, currentTick, 0, 0);
        }

        var depth2Tasks = allPeers.Select(async ip =>
        {
            try
            {
                using var node = CreateNodeClient(ip, port);
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
                    using var node = CreateNodeClient(ip, port);
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
            return new PeerDiscoveryResult(false, host, null, currentTick, currentTick, results.Length, 0);

        // Step 4: Find the best peer (highest tick)
        var best = reachable.OrderByDescending(r => r.Tick).First();

        // Step 5: Check if current peer is behind by more than threshold
        bool shouldSwitch = currentTick == 0 || (best.Tick > currentTick && best.Tick - currentTick > (uint)tickThreshold);
        var result = new PeerDiscoveryResult(
            shouldSwitch, host, shouldSwitch ? best.Ip : null,
            currentTick, best.Tick, results.Length, reachable.Count);

        if (shouldSwitch)
        {
            // Step 6: Point the profile's Direct method at the better peer
            var updated = _profiles.Get(profile.Id);
            var m = updated?.Methods.FirstOrDefault(x => x.Kind == BackendMethodKind.Direct && x.Enabled);
            if (updated != null && m != null)
            {
                m.Host = best.Ip;
                _profiles.Save(updated);
            }
            ResetClients();
            OnPeerDiscovered?.Invoke(result);
        }

        return result;
    }

    // ── Node Commands (Direct) ──

    public async Task<byte[]> SendNodeCommandAsync(byte[] commandPayload, byte[] signature, CancellationToken ct = default)
    {
        var node = await DirectFor(Capability.NodeCommands);
        return await node.SendSpecialCommandAsync(commandPayload, signature, ct);
    }

    public async Task SendRawPacketAsync(byte[] data, CancellationToken ct = default)
    {
        var node = await DirectFor(Capability.NodeCommands);
        await node.SendRawPacketAsync(data, ct);
    }

    // ── Transaction Verification (Direct) ──

    public async Task<bool> CheckTransactionInTickAsync(string txHash, uint tick, CancellationToken ct = default)
    {
        var node = await DirectFor(Capability.TickTransactionCheck);
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

    // ── Oracle Data (Direct) ──

    public async Task<List<byte[]>> GetOracleQueryAsync(long queryId, CancellationToken ct = default)
    {
        var node = await DirectFor(Capability.Oracle);
        return await node.GetOracleQueryAsync(queryId, ct);
    }

    public async Task<List<byte[]>> GetOracleQueryIdsByTickAsync(uint tick, uint filterType = 0, CancellationToken ct = default)
    {
        var node = await DirectFor(Capability.Oracle);
        return await node.GetOracleQueryIdsByTickAsync(tick, filterType, ct);
    }

    public async Task<List<byte[]>> GetOracleStatisticsAsync(CancellationToken ct = default)
    {
        var node = await DirectFor(Capability.Oracle);
        return await node.GetOracleStatisticsAsync(ct);
    }

    // ── Raw JSON-RPC (Bob Playground) ──

    public async Task<string> SendRawJsonRpcAsync(string method, string? paramsJson = null, CancellationToken ct = default)
    {
        var bobUrl = Resolve(Capability.RawJsonRpc).Url.TrimEnd('/') + "/qubic";
        using var http = _guard.CreateHttpClient("bob-playground");
        var id = Random.Shared.Next(1, 999999);
        var request = string.IsNullOrWhiteSpace(paramsJson)
            ? $"{{\"jsonrpc\":\"2.0\",\"method\":\"{method}\",\"params\":[],\"id\":{id}}}"
            : $"{{\"jsonrpc\":\"2.0\",\"method\":\"{method}\",\"params\":{paramsJson},\"id\":{id}}}";

        var content = new StringContent(request, System.Text.Encoding.UTF8, "application/json");
        var response = await http.PostAsync(bobUrl, content, ct);
        return await response.Content.ReadAsStringAsync(ct);
    }

    // ── Lifecycle ──

    public void ResetClients()
    {
        QubicRpcClient? rpc;
        BobClient? bob;
        QubicNodeClient? node;
        lock (_clientLock)
        {
            rpc = _rpcClient; bob = _bobClient; node = _nodeClient;
            _rpcClient = null; _bobClient = null; _nodeClient = null;
        }
        rpc?.Dispose();
        bob?.Dispose();
        node?.Dispose();
    }

    public void Dispose()
    {
        ResetClients();
    }
}

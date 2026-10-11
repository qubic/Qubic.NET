namespace Qubic.Services.Networks;

/// <summary>Built-in network profiles. Users get editable copies of these.</summary>
public static class NetworkPresets
{
    public const string MainnetId = "mainnet";
    public const string AioLocalId = "aio-local";
    public const string AioRemoteId = "aio-remote";

    /// <summary>Public mainnet: rpc.qubic.org, bobnet.qubic.li, corenet.qubic.li.</summary>
    public static NetworkProfile Mainnet() => new()
    {
        Id = MainnetId,
        PresetId = MainnetId,
        Name = "Mainnet",
        Kind = NetworkKind.Mainnet,
        Methods =
        [
            new BackendMethod { Kind = BackendMethodKind.Rpc, Url = "https://rpc.qubic.org" },
            new BackendMethod { Kind = BackendMethodKind.Bob, Url = "https://bobnet.qubic.li" },
            new BackendMethod { Kind = BackendMethodKind.Direct, Host = "corenet.qubic.li", Port = 21841, PeerDiscovery = true },
        ],
        ExplorerTxUrlTemplate = "https://explorer.qubic.org/network/tx/{hash}",
        UseStaticBundle = true,
        ExpectedFingerprint = NetworkFingerprintKind.ArbitratorSigned,
    };

    /// <summary>
    /// AIO Qubic Dev Kit on this machine. Bob first; core-lite's own HTTP API
    /// (same kit, bound to 127.0.0.1) serves what Bob cannot: asset listings and IPOs.
    /// Direct is available but off — the testnet node only has 4 inbound slots and Bob holds one.
    /// </summary>
    public static NetworkProfile AioLocal() => Aio(AioLocalId, "AIO (this machine)", "127.0.0.1");

    /// <summary>AIO kit on another host. core-lite ports are localhost-bound there: tunnel them via SSH.</summary>
    public static NetworkProfile AioRemote() => Aio(AioRemoteId, "AIO (remote host)", "aio-host.local");

    private static NetworkProfile Aio(string id, string name, string host) => new()
    {
        Id = id,
        PresetId = id,
        Name = name,
        Kind = NetworkKind.Devnet,
        Methods =
        [
            new BackendMethod { Kind = BackendMethodKind.Bob, Url = $"http://{host}:40420" },
            new BackendMethod { Kind = BackendMethodKind.Rpc, Url = $"http://{host}:41841", Label = "core-lite HTTP" },
            new BackendMethod { Kind = BackendMethodKind.Direct, Host = host, Port = 31841, Enabled = false },
        ],
        ExplorerTxUrlTemplate = $"http://{host}/tx/{{hash}}",
        UseStaticBundle = false,
        PendingTxWindowTicks = 32,
        ExpectedFingerprint = NetworkFingerprintKind.CoreLiteDevnet,
    };

    public static IReadOnlyList<NetworkProfile> All() => [Mainnet(), AioLocal(), AioRemote()];

    public static NetworkProfile? ById(string? presetId) => presetId switch
    {
        MainnetId => Mainnet(),
        AioLocalId => AioLocal(),
        AioRemoteId => AioRemote(),
        _ => null
    };
}

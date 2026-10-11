using System.Text.Json.Serialization;

namespace Qubic.Services.Networks;

/// <summary>What kind of chain a profile points at. Drives safety warnings and defaults.</summary>
public enum NetworkKind { Mainnet, Testnet, Devnet, Custom }

/// <summary>A way of talking to a Qubic network.</summary>
public enum BackendMethodKind { Bob, Direct, Rpc }

/// <summary>How the target tick of a new transaction is chosen.</summary>
public enum TickTargetMode
{
    /// <summary>Current tick + a fixed number of ticks.</summary>
    FixedOffset,
    /// <summary>Current tick + however many ticks fit into a chosen number of seconds.</summary>
    TimeBased
}

/// <summary>
/// One backend endpoint inside a <see cref="NetworkProfile"/>.
/// Bob and Rpc use <see cref="Url"/>; Direct uses <see cref="Host"/>/<see cref="Port"/>.
/// </summary>
public sealed class BackendMethod
{
    public BackendMethodKind Kind { get; set; }
    public bool Enabled { get; set; } = true;

    /// <summary>Base URL for Bob (e.g. http://127.0.0.1:40420) or RPC (e.g. https://rpc.qubic.org).</summary>
    public string Url { get; set; } = "";

    /// <summary>Node host for Direct.</summary>
    public string Host { get; set; } = "";

    /// <summary>Node port for Direct (21841 mainnet, 31841 core-lite testnet).</summary>
    public int Port { get; set; } = 21841;

    /// <summary>Direct only: allow scanning peers-of-peers and switching node.</summary>
    public bool PeerDiscovery { get; set; }

    /// <summary>Bob only: use the WebSocket subscriptions for ticks and logs.</summary>
    public bool UseWebSocket { get; set; } = true;

    /// <summary>Optional label shown in the UI, e.g. "core-lite HTTP".</summary>
    public string? Label { get; set; }

    [JsonIgnore]
    public string Endpoint => Kind == BackendMethodKind.Direct ? $"{Host}:{Port}" : Url;

    /// <summary>Host name this method connects to, or null if not configured.</summary>
    [JsonIgnore]
    public string? HostName
    {
        get
        {
            if (Kind == BackendMethodKind.Direct)
                return string.IsNullOrWhiteSpace(Host) ? null : Host.Trim();
            return Uri.TryCreate(Url, UriKind.Absolute, out var u) ? u.Host : null;
        }
    }

    public BackendMethod Clone() => (BackendMethod)MemberwiseClone();
}

/// <summary>
/// A network the wallet can connect to: one or more backend methods in priority
/// order plus everything that differs between networks (explorer, data bundle,
/// tick targeting). Every request the wallet makes is resolved inside the active
/// profile — there is no fallback to endpoints outside it.
/// </summary>
public sealed class NetworkProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public string Name { get; set; } = "";
    public NetworkKind Kind { get; set; } = NetworkKind.Custom;

    /// <summary>Preset this profile was created from (for "reset to defaults").</summary>
    public string? PresetId { get; set; }

    /// <summary>Backend methods in priority order. The first enabled method that supports a capability serves it.</summary>
    public List<BackendMethod> Methods { get; set; } = [];

    /// <summary>Explorer link for a transaction, with <c>{hash}</c> placeholder. Null = no links.</summary>
    public string? ExplorerTxUrlTemplate { get; set; }

    /// <summary>Whether the static.qubic.org data bundle (labels, contract names, fees) applies to this network.</summary>
    public bool UseStaticBundle { get; set; }

    public TickTargetMode TickMode { get; set; } = TickTargetMode.FixedOffset;

    /// <summary>Ticks added to the current tick in <see cref="TickTargetMode.FixedOffset"/> mode.</summary>
    public int TickOffset { get; set; } = 7;

    /// <summary>Seconds ahead in <see cref="TickTargetMode.TimeBased"/> mode.</summary>
    public int TickTargetSeconds { get; set; } = 10;

    /// <summary>
    /// How many ticks ahead the network accepts transactions (pending-tx pool).
    /// Null = derive from <see cref="Kind"/> (32 for test/dev nets, ~3 minutes on mainnet).
    /// </summary>
    public int? PendingTxWindowTicks { get; set; }

    /// <summary>What the wallet expects to find when it fingerprints this network. Null = no expectation.</summary>
    public NetworkFingerprintKind? ExpectedFingerprint { get; set; }

    /// <summary>Badge colour (CSS colour). Null = derived from <see cref="Kind"/>.</summary>
    public string? Color { get; set; }

    /// <summary>
    /// Per-operation method choice. Operations not listed use the method priority order ("Auto").
    /// A listed choice is strict: if that method is disabled or removed, the operation is unavailable.
    /// </summary>
    public Dictionary<Capability, BackendMethodKind>? Routes { get; set; }

    [JsonIgnore]
    public IEnumerable<BackendMethod> EnabledMethods => Methods.Where(m => m.Enabled);

    public BackendMethod? GetMethod(BackendMethodKind kind) => EnabledMethods.FirstOrDefault(m => m.Kind == kind);

    public bool HasMethod(BackendMethodKind kind) => GetMethod(kind) != null;

    [JsonIgnore]
    public bool IsMainnet => Kind == NetworkKind.Mainnet;

    /// <summary>CAIP-2 chain id used for WalletConnect sessions.</summary>
    [JsonIgnore]
    public string ChainId => Kind == NetworkKind.Mainnet ? "qubic:mainnet" : "qubic:testnet";

    [JsonIgnore]
    public string BadgeColor => Color ?? Kind switch
    {
        NetworkKind.Mainnet => "#0d9488",
        NetworkKind.Testnet => "#d97706",
        NetworkKind.Devnet => "#7c3aed",
        _ => "#64748b"
    };

    public string? GetExplorerTxUrl(string hash) =>
        string.IsNullOrWhiteSpace(ExplorerTxUrlTemplate) ? null : ExplorerTxUrlTemplate.Replace("{hash}", hash);

    public NetworkProfile Clone()
    {
        var copy = (NetworkProfile)MemberwiseClone();
        copy.Methods = Methods.Select(m => m.Clone()).ToList();
        copy.Routes = Routes == null ? null : new Dictionary<Capability, BackendMethodKind>(Routes);
        return copy;
    }
}

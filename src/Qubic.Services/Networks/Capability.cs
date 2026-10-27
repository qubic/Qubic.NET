namespace Qubic.Services.Networks;

/// <summary>
/// An operation the wallet needs from a network. Each capability is served by the
/// first enabled method in the active profile that supports it — never by an
/// endpoint outside the profile.
/// </summary>
public enum Capability
{
    TickInfo,
    Balance,
    Broadcast,
    SmartContractQuery,
    Computors,
    /// <summary>Lookup of a single transaction by hash.</summary>
    TransactionLookup,
    /// <summary>Bob transaction receipt (execution status).</summary>
    TransactionReceipt,
    /// <summary>Bob QU transfer history.</summary>
    TransferHistory,
    /// <summary>Paginated historical transactions for an identity (archiver/query service).</summary>
    TransactionArchive,
    /// <summary>Bob log events (subscription + indexed lookup).</summary>
    Logs,
    /// <summary>Owned / possessed / issued asset listings for an identity.</summary>
    AssetListings,
    /// <summary>All asset issuances on the network.</summary>
    Issuances,
    /// <summary>Balance of one known asset (Bob).</summary>
    AssetBalance,
    /// <summary>Active IPO list.</summary>
    ActiveIpos,
    /// <summary>Per-contract IPO bid status.</summary>
    IpoStatus,
    /// <summary>Tick data and transactions of a tick (explorer views).</summary>
    TickData,
    /// <summary>Bob chain info: sync status, chain id, client version, epoch info.</summary>
    ChainInfo,
    /// <summary>Raw Bob JSON-RPC (playground).</summary>
    RawJsonRpc,
    /// <summary>Peer list and peer discovery.</summary>
    Peers,
    /// <summary>Special node commands and raw packets.</summary>
    NodeCommands,
    /// <summary>Check whether a tx was included in a tick by reading the tick's transactions.</summary>
    TickTransactionCheck,
    /// <summary>Oracle machine queries.</summary>
    Oracle,
    /// <summary>List of contracts compiled into the node (core-lite /query/v1/getContracts).</summary>
    ContractRegistry,
}

public static class Capabilities
{
    private static readonly Dictionary<Capability, BackendMethodKind[]> Support = new()
    {
        [Capability.TickInfo] = [BackendMethodKind.Bob, BackendMethodKind.Direct, BackendMethodKind.Rpc],
        [Capability.Balance] = [BackendMethodKind.Bob, BackendMethodKind.Direct, BackendMethodKind.Rpc],
        [Capability.Broadcast] = [BackendMethodKind.Bob, BackendMethodKind.Direct, BackendMethodKind.Rpc],
        [Capability.SmartContractQuery] = [BackendMethodKind.Bob, BackendMethodKind.Direct, BackendMethodKind.Rpc],
        [Capability.Computors] = [BackendMethodKind.Bob, BackendMethodKind.Direct, BackendMethodKind.Rpc],
        [Capability.TransactionLookup] = [BackendMethodKind.Rpc, BackendMethodKind.Bob],
        [Capability.TransactionReceipt] = [BackendMethodKind.Bob],
        [Capability.TransferHistory] = [BackendMethodKind.Bob],
        [Capability.TransactionArchive] = [BackendMethodKind.Rpc],
        [Capability.Logs] = [BackendMethodKind.Bob],
        [Capability.AssetListings] = [BackendMethodKind.Rpc, BackendMethodKind.Direct],
        [Capability.Issuances] = [BackendMethodKind.Rpc],
        [Capability.AssetBalance] = [BackendMethodKind.Bob],
        [Capability.ActiveIpos] = [BackendMethodKind.Rpc],
        [Capability.IpoStatus] = [BackendMethodKind.Direct],
        [Capability.TickData] = [BackendMethodKind.Rpc],
        [Capability.ChainInfo] = [BackendMethodKind.Bob],
        [Capability.RawJsonRpc] = [BackendMethodKind.Bob],
        [Capability.Peers] = [BackendMethodKind.Direct],
        [Capability.NodeCommands] = [BackendMethodKind.Direct],
        [Capability.TickTransactionCheck] = [BackendMethodKind.Direct],
        [Capability.Oracle] = [BackendMethodKind.Direct],
        [Capability.ContractRegistry] = [BackendMethodKind.Rpc],
    };

    /// <summary>Method kinds that can serve <paramref name="capability"/>.</summary>
    public static IReadOnlyList<BackendMethodKind> SupportedBy(Capability capability) => Support[capability];

    /// <summary>
    /// The method in <paramref name="profile"/> that serves <paramref name="capability"/>, or null.
    /// An explicit per-operation choice (<see cref="NetworkProfile.Routes"/>) wins and is strict;
    /// otherwise the first enabled method (priority order) that supports it.
    /// </summary>
    public static BackendMethod? Resolve(NetworkProfile profile, Capability capability)
    {
        var supported = Support[capability];
        if (profile.Routes != null && profile.Routes.TryGetValue(capability, out var chosen) && supported.Contains(chosen))
            return profile.GetMethod(chosen);
        return profile.EnabledMethods.FirstOrDefault(m => supported.Contains(m.Kind));
    }

    /// <summary>True when <paramref name="profile"/> pins <paramref name="capability"/> to a method that is not enabled.</summary>
    public static bool IsPinnedToUnavailableMethod(NetworkProfile profile, Capability capability) =>
        profile.Routes != null && profile.Routes.TryGetValue(capability, out var chosen)
        && Support[capability].Contains(chosen) && !profile.HasMethod(chosen);

    public static string DisplayName(Capability c) => c switch
    {
        Capability.TickInfo => "Tick info",
        Capability.Balance => "Balances",
        Capability.Broadcast => "Broadcast",
        Capability.SmartContractQuery => "Contract queries",
        Capability.Computors => "Computor list",
        Capability.TransactionLookup => "Transaction lookup",
        Capability.TransactionReceipt => "Transaction receipts",
        Capability.TransferHistory => "Transfer history",
        Capability.TransactionArchive => "Transaction archive",
        Capability.Logs => "Log events",
        Capability.AssetListings => "Asset listings",
        Capability.Issuances => "Asset issuances",
        Capability.AssetBalance => "Asset balance",
        Capability.ActiveIpos => "Active IPOs",
        Capability.IpoStatus => "IPO status",
        Capability.TickData => "Tick data",
        Capability.ChainInfo => "Chain info",
        Capability.RawJsonRpc => "JSON-RPC playground",
        Capability.Peers => "Peers",
        Capability.NodeCommands => "Node commands",
        Capability.TickTransactionCheck => "Tick inclusion check",
        Capability.Oracle => "Oracle queries",
        Capability.ContractRegistry => "Contract registry",
        _ => c.ToString()
    };

    public static string MethodName(BackendMethodKind k) => k switch
    {
        BackendMethodKind.Bob => "Bob",
        BackendMethodKind.Direct => "Direct",
        BackendMethodKind.Rpc => "RPC",
        _ => k.ToString()
    };
}

/// <summary>
/// Thrown when the active network profile has no method that can serve a capability.
/// The UI should show <see cref="Exception.Message"/> as "not available on this network",
/// not as an error.
/// </summary>
public sealed class CapabilityUnavailableException : InvalidOperationException
{
    public Capability Capability { get; }
    public string ProfileName { get; }

    public CapabilityUnavailableException(Capability capability, string profileName)
        : base($"{Capabilities.DisplayName(capability)} is not available on \"{profileName}\". " +
               $"Add a {string.Join(" or ", Capabilities.SupportedBy(capability).Select(Capabilities.MethodName))} method to this network to enable it.")
    {
        Capability = capability;
        ProfileName = profileName;
    }
}

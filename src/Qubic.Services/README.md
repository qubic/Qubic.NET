# Qubic.Services

High-level services for building Qubic applications in .NET.

## Services

| Service | Description |
|---|---|
| `QubicBackendService` | Unified backend that routes every call to a method of the active network profile (Bob, RPC, Direct) — never outside it |
| `QubicSettingsService` | Persistent application settings with JSON storage |
| `TickMonitorService` | Real-time tick/epoch monitoring |
| `LabelService` | Address label resolution (built-in, remote, user-defined) |
| `QubicStaticService` | Remote data from static.qubic.org (exchanges, contracts, tokens) |
| `SeedSessionService` | In-memory seed/identity session management |
| `TransactionTrackerService` | Transaction lifecycle tracking with encrypted persistence |
| `AssetRegistryService` | Asset ownership querying |
| `PeerAutoDiscoverService` | Automatic peer discovery and failover |
| `VaultService` | Encrypted vault for seeds, contacts, and send-many templates (AES-256-GCM with Argon2id key derivation; legacy vaults written with PBKDF2-HMAC-SHA256/600k are still read and migrate to Argon2id on next save) |
| `WalletStorageService` | SQLCipher-encrypted wallet data storage, one database per seed and network |
| `BalanceService` | Balance refresh for the active identity |

### Networks (`Qubic.Services.Networks`)

| Service | Description |
|---|---|
| `NetworkProfileService` | Network profiles (Mainnet, AIO presets, custom): methods in priority order, per-operation routes, tick targeting; persisted in settings, legacy settings migrated |
| `NetworkSwitchService` | Switches the active profile and restarts dependent services in order |
| `Capabilities` | Which method kinds can serve each `Capability`; `Resolve` applies the profile's routes strictly |
| `EgressGuard` | Allow-list for outbound connections: hosts of the active profile plus enabled feature hosts |
| `NetworkFingerprintService` | Identifies the chain from its computor list: core-lite devnet (public dev seeds) or Mainnet (arbitrator signature) |
| `ChainResetService` | Detects chain restarts (epoch/tick going back) and starts a new history generation |
| `TickTargeting` / `TickRateEstimator` | Fixed-offset or time-based target ticks, pending-window checks |
| `CoreLiteDevSeeds` | The public core-lite computor seeds and identities |

### Signing (`Qubic.Services.Signing`)

| Service | Description |
|---|---|
| `SigningReviewService` | Builds a `SigningRequest` (decoded call, warnings) and asks the app's `ISigningPresenter` before every signature; replay and dev-key warnings |
| `ExternalRequestService` | Shared handling of dApp/script requests (accounts, transfers, contract calls, messages) for WalletConnect and local endpoints |

### Other

| Service | Description |
|---|---|
| `FeatureService` | Opt-in feature flags with descriptions for a first-start feature screen |
| `ContractCatalogService` | Built-in and imported contract metadata per network |
| `DevToolsService` | Dev accounts, faucet, QX asset issuance and genesis-funding commands for core-lite devnets |

## Signing review

Apps register an `ISigningPresenter` that shows the request and returns the user's decision:

```csharp
services.AddSingleton<ISigningPresenter, MySigningDialog>();

public sealed class MySigningDialog : ISigningPresenter
{
    public Task<bool> PresentAsync(SigningRequest request, CancellationToken ct)
    {
        // request.Network, request.Decoded (contract call), request.TargetTick, request.Warnings
        return ShowDialogAsync(request, ct);
    }
}
```

`SeedSessionService.SignTransferAsync`, `SignTransactionAsync` and `SignRawMessageAsync` always go through it.

## Dependencies

- `Qubic.Core` — domain models and abstractions
- `Qubic.Crypto` — cryptographic primitives
- `Qubic.Rpc` — RPC API client
- `Qubic.Bob` — Bob JSON-RPC client
- `Qubic.Network` — direct TCP node communication
- `Microsoft.Data.Sqlite.Core` + SQLCipher — encrypted wallet storage (the full `Microsoft.Data.Sqlite` package would displace SQLCipher)

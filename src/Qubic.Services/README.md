# Qubic.Services

High-level services for building Qubic applications in .NET.

## Services

| Service | Description |
|---|---|
| `QubicBackendService` | Unified backend abstraction (RPC, Bob, DirectNetwork) |
| `QubicSettingsService` | Persistent application settings with JSON storage |
| `TickMonitorService` | Real-time tick/epoch monitoring |
| `LabelService` | Address label resolution (built-in, remote, user-defined) |
| `QubicStaticService` | Remote data from static.qubic.org (exchanges, contracts, tokens) |
| `SeedSessionService` | In-memory seed/identity session management |
| `TransactionTrackerService` | Transaction lifecycle tracking with encrypted persistence |
| `AssetRegistryService` | Asset ownership querying |
| `PeerAutoDiscoverService` | Automatic peer discovery and failover |
| `VaultService` | Encrypted vault for seeds, contacts, and send-many templates (AES-256-GCM + Argon2id) |
| `WalletStorageService` | SQLite-based wallet data storage |

## Dependencies

- `Qubic.Core` — domain models and abstractions
- `Qubic.Crypto` — cryptographic primitives
- `Qubic.Rpc` — RPC API client
- `Qubic.Bob` — Bob JSON-RPC client
- `Qubic.Network` — direct TCP node communication
- `Microsoft.Data.Sqlite` — wallet storage

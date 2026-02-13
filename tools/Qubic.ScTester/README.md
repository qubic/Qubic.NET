# Qubic.ScTester

A Blazor Server web application for browsing and testing Qubic smart contract functions interactively.

## Features

- **Auto-discovery** of all generated contract bindings via reflection
- **Three query backends**: RPC, Bob (JSON-RPC), and Direct Network (TCP)
- **Dynamic form generation** for function inputs based on struct metadata
- **Smart input handling**:
  - 60-character Qubic identity strings are auto-converted to 32-byte public keys for `byte[]` fields
  - Asset name fields (`ulong`) accept text like `QX`, `CFB`, `QUTIL` and encode them automatically
- **Per-field "Show Text" toggle** for `byte[]` output values (hex vs ASCII)
- **Tabular display** for `Array<T,N>` output fields with sortable columns
- **Connection test** button on the home page (calls `Qutil.GetFees` as a smoke test)

## Usage

```bash
dotnet run --project tools/Qubic.ScTester
```

The app picks a random available port and opens your browser automatically. The URL is printed to the console.

## Query Backends

| Backend | Protocol | Default Endpoint | Description |
|---------|----------|-----------------|-------------|
| **RPC** | HTTP/JSON | `https://rpc.qubic.org` | Official Qubic RPC API (base64-encoded payloads) |
| **Bob** | JSON-RPC 2.0 | `https://bob.qubic.li` | QubicBob node with async nonce-based polling (hex-encoded payloads) |
| **Direct Network** | TCP | `corenet.qubic.li:21841` | Raw Qubic P2P protocol (binary `RequestContractFunction` type 42) |

Switch backends from the navbar dropdown or the home page radio buttons. The URL / host:port is editable inline.

## Prerequisites

- .NET 8.0 SDK
- Generated contract bindings (run `Qubic.ContractGen` first if the `Contracts/Generated/` folder is empty)

## Publishing

Build self-contained binaries for distribution:

```bash
# Windows
dotnet publish tools/Qubic.ScTester -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -o publish/win-x64

# Linux
dotnet publish tools/Qubic.ScTester -c Release -r linux-x64 --self-contained -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -o publish/linux-x64

# macOS (Apple Silicon)
dotnet publish tools/Qubic.ScTester -c Release -r osx-arm64 --self-contained -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -o publish/osx-arm64

# macOS (Intel)
dotnet publish tools/Qubic.ScTester -c Release -r osx-x64 --self-contained -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -o publish/osx-x64
```

### Running the Published Binary

**Windows:** Double-click `Qubic.ScTester.exe` or run it from a terminal.

**Linux:**
```bash
chmod +x Qubic.ScTester
./Qubic.ScTester
```

**macOS:** If cross-compiled from another OS, you need to codesign and remove quarantine before running:
```bash
chmod +x Qubic.ScTester
codesign --force --deep -s - Qubic.ScTester
xattr -d com.apple.quarantine Qubic.ScTester
./Qubic.ScTester
```

## Project Structure

```
Qubic.ScTester/
  Program.cs                                    # Host setup, DI registration
  ScQueryService.cs                             # Backend abstraction (RPC / Bob / TCP)
  ContractDiscovery.cs                          # Reflection-based contract discovery
  Components/
    App.razor                                   # Root component
    Layout/
      MainLayout.razor                          # Navbar with backend selector
      NavMenu.razor                             # Sidebar contract list
    Pages/
      Home.razor                                # Dashboard with connection settings
      ContractPage.razor                        # Contract detail: forms, queries, results
      SmokeTest.razor                           # Quick connectivity check
```

## Dependencies

- `Qubic.Core` - Contract models and identity handling
- `Qubic.Rpc` - HTTP RPC client
- `Qubic.Bob` - JSON-RPC client
- `Qubic.Crypto` - Cryptographic primitives
- `Qubic.Network` - Direct TCP node client

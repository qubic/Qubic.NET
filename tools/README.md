# Tools

Developer tools for working with Qubic smart contracts and the Qubic network.

| Tool | Description |
|------|-------------|
| [Qubic.Toolkit](Qubic.Toolkit/) | Cross-platform desktop application (Windows, macOS, Linux) for wallet management, transaction building, contract interaction, and network monitoring. Runs as a native desktop window via [Photino.Blazor](https://github.com/AhLamm/photino.Blazor), or as a Blazor Server app with `--server`. |
| [Qubic.ContractGen](Qubic.ContractGen/) | Parses C++ smart contract headers from `qubic-core` and generates C# bindings with correct struct layouts, type mappings, and alignment. |
| [Qubic.ScTester](Qubic.ScTester/) | Blazor Server web UI for browsing and testing all generated smart contract functions against live Qubic nodes via RPC, Bob, or direct TCP. |

## Quick Start

```bash
# 1. Launch the Toolkit desktop app
dotnet run --project Qubic.Toolkit

# Or run as a Blazor Server app in the browser
dotnet run --project Qubic.Toolkit -- --server

# 2. Generate C# contract bindings from the C++ headers
dotnet run --project Qubic.ContractGen

# 3. Launch the SC Tester web UI
dotnet run --project Qubic.ScTester
# Open http://localhost:5050
```

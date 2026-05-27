# Tools

Developer tools for working with Qubic smart contracts and the Qubic network.

> **Note:** The Toolkit and Wallet have moved to their own repositories:
> - [Qubic.Net.Toolkit](https://github.com/qubic/Qubic.Net.Toolkit)
> - [Qubic.Net.Wallet](https://github.com/qubic/Qubic.Net.Wallet)

| Tool | Description |
|------|-------------|
| [Qubic.ContractGen](Qubic.ContractGen/) | Parses C++ smart contract headers from `qubic-core` and generates C# bindings with correct struct layouts, type mappings, and alignment. |
| [Qubic.ScTester](Qubic.ScTester/) | Blazor Server web UI for browsing and testing all generated smart contract functions against live Qubic nodes via RPC, Bob, or direct TCP. |
| [Qubic.ChainAnalytics.Cli](Qubic.ChainAnalytics.Cli/) | Direct-mainnet tick analytics CLI — fetches tick data and transactions from a node over TCP, parses them, and verifies locally-computed K12 digests against the digests embedded in the tick. |
| [Qubic.SpectrumDiff](Qubic.SpectrumDiff/) | Streams two 1 GB `spectrum.NNN` dumps in lockstep and reports per-account differences in incoming, outgoing, transfer counts, and balance. |

## Quick Start

```bash
# Generate C# contract bindings from the C++ headers
dotnet run --project Qubic.ContractGen

# Launch the SC Tester web UI
dotnet run --project Qubic.ScTester
# Open http://localhost:5050
```

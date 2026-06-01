# Tools

Developer tools for working with Qubic smart contracts and the Qubic network.

> **Note:** The Toolkit and Wallet have moved to their own repositories:
> - [Qubic.Net.Toolkit](https://github.com/qubic/Qubic.Net.Toolkit)
> - [Qubic.Net.Wallet](https://github.com/qubic/Qubic.Net.Wallet)

| Tool | Description |
|------|-------------|
| [Qubic.ContractGen](Qubic.ContractGen/) | Parses C++ smart contract headers from `qubic-core` and generates C# bindings with correct struct layouts, type mappings, and alignment. |
| [Qubic.ScTester](Qubic.ScTester/) | Blazor Server web UI for browsing and testing all generated smart contract functions against live Qubic nodes via RPC, Bob, or direct TCP. |
| [Qubic.ChainAnalytics.Cli](Qubic.ChainAnalytics.Cli/) | Direct-mainnet tick analytics CLI — tick + tx summary with K12 chain verification, vote-distribution alignment for X/X+1, and verbatim replay of captured `RequestTickTransactions` packets. |
| [Qubic.NodeTester](Qubic.NodeTester/) | Direct-TCP peer test suite — runs an 8-step health check (connect, handshake, broadcast listen, tick info, system info, tick data, tick tx, quorum tick) with auto-reconnect on mid-suite disconnects. |
| [Qubic.NodeLogger](Qubic.NodeLogger/) | Pulls event logs from a node via `REQUEST_LOG` (44), `REQUEST_LOG_ID_RANGE_FROM_TX` (48), `REQUEST_ALL_LOG_ID_RANGES_FROM_TX` (50). Counts by type, JSON export. Requires the operator's log-reader passcode. |
| [Qubic.TxRelay](Qubic.TxRelay/) | Pulls transactions from one peer and broadcasts them to another. Single tick / range / `latest --follow` modes, K12-dedup, throttling, dry-run. |
| [Qubic.SpectrumDiff](Qubic.SpectrumDiff/) | Streams two 1 GB `spectrum.NNN` dumps in lockstep and reports per-account differences in incoming, outgoing, transfer counts, and balance. |

## Quick Start

```bash
# Generate C# contract bindings from the C++ headers
dotnet run --project Qubic.ContractGen

# Launch the SC Tester web UI
dotnet run --project Qubic.ScTester
# Open http://localhost:5050
```

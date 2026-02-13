# Qubic.ContractGen

A code generator that parses C++ smart contract headers from `qubic-core` and produces C# bindings for use with `Qubic.Core`.

## What It Does

Reads the C++ header files in `deps/qubic-core/src/contracts/` and generates one `.g.cs` file per contract into `src/Qubic.Core/Contracts/Generated/`. Each generated file contains:

- A static contract class (e.g. `QxContract`, `QswapContract`)
- Strongly-typed C# structs for every `_input` and `_output` pair
- Correct field offsets matching MSVC `/Zp8` struct alignment
- `Serialize()` / `Deserialize()` methods on each struct for binary round-tripping
- Function and procedure metadata (ID, name, input/output types)

## Supported Contracts

| Index | Name | Header |
|-------|------|--------|
| 1 | Qx | Qx.h |
| 2 | Quottery | Quottery.h |
| 3 | Random | Random.h |
| 4 | Qutil | QUtil.h |
| 5 | Mlm | MyLastMatch.h |
| 6 | Gqmprop | GeneralQuorumProposal.h |
| 7 | Swatch | SupplyWatcher.h |
| 8 | Ccf | ComputorControlledFund.h |
| 9 | Qearn | Qearn.h |
| 10 | Qvault | QVAULT.h |
| 11 | Msvault | MsVault.h |
| 12 | Qbay | Qbay.h |
| 13 | Qswap | Qswap.h |
| 14 | Nost | Nostromo.h |
| 15 | Qdraw | Qdraw.h |
| 16 | Rl | RandomLottery.h |
| 17 | Qbond | QBond.h |
| 18 | Qip | QIP.h |
| 19 | Qraffle | QRaffle.h |
| 20 | Qrwa | qRWA.h |
| 21 | Qrp | QReservePool.h |
| 22 | Qtf | QThirtyFour.h |
| 23 | Qduel | QDuel.h |

## Usage

```bash
# Run from the repo root (auto-detects paths)
dotnet run --project tools/Qubic.ContractGen

# Or pass the repo root explicitly
dotnet run --project tools/Qubic.ContractGen -- /path/to/Qubic.Net
```

Output:

```
Headers: /path/to/Qubic.Net/deps/qubic-core/src/contracts
Output:  /path/to/Qubic.Net/src/Qubic.Core/Contracts/Generated

  [01] Qx           OK  (5 functions, 7 procedures)
  [02] Quottery     OK  (5 functions, 4 procedures)
  ...

Generated 22 files, skipped 1.
```

## Prerequisites

- .NET 8.0 SDK
- The `qubic-core` git submodule must be initialized:
  ```bash
  git submodule update --init
  ```

## Architecture

```
Qubic.ContractGen/
  Program.cs                          # Entry point, contract registry, orchestration
  Parsing/
    CppHeaderParser.cs                # C++ header lexer/parser
    ContractModel.cs                  # Intermediate representation (functions, structs, fields)
    TypeMapper.cs                     # C++ → C# type mapping with alignment info
  Generation/
    CSharpEmitter.cs                  # IR → C# code emitter with MSVC /Zp8 layout
```

### Parsing Pipeline

1. **CppHeaderParser** reads the `.h` file and extracts the contract's main struct (e.g. `struct QX : public ContractBase`)
2. Finds all `_input` / `_output` struct pairs and matches them to functions (read-only) or procedures (state-changing)
3. Resolves typedefs (including contract-internal ones like `typedef Array<Order, 256> OrdersArray`)
4. Handles nested structs, `Array<T,N>` templates, and `id` (32-byte public key) types

### Code Generation

- **CSharpEmitter** walks the parsed model and produces idiomatic C# with:
  - `[StructLayout]` attributes where useful
  - Manual field offset computation matching MSVC `/Zp8` rules (min of field alignment, 8)
  - `BinaryPrimitives` for little-endian serialization
  - Proper handling of `byte[]` arrays, nested struct arrays, and padding bytes

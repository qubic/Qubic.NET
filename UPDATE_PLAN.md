# Qubic.Net Update Plan

Run this checklist each time a new version of [qubic/core](https://github.com/qubic/core) is released.

The C++ source is linked as a git submodule at `deps/qubic-core`.

## 0. Update the submodule

```bash
cd deps/qubic-core
git fetch origin
git checkout <new-release-tag-or-commit>
cd ../..
```

---

## 1. Check version

**C++ source:** `deps/qubic-core/src/public_settings.h` → `VERSION_A`, `VERSION_B`, `VERSION_C`
**C# target:** `src/Qubic.Core/QubicConstants.cs` → `QubicCoreVersion`
**C# target:** `src/Qubic.Core/Qubic.Core.csproj` → `<Version>`

Update the version string and the doc comment at the top of `QubicConstants.cs`.
The NuGet package version in `Qubic.Core.csproj` mirrors the core version (e.g., core `1.278.0` → package `1.278.0`).
For C#-only bugfixes between core releases, use a 4th segment (e.g., `1.278.0.1`).

---

## 2. Update constants

**C++ source:** `deps/qubic-core/src/network_messages/common_def.h`
**C# target:** `src/Qubic.Core/QubicConstants.cs`

Check these values:
- `NUMBER_OF_COMPUTORS` → `NumberOfComputors`
- `QUORUM` → `Quorum`
- `NUMBER_OF_EXCHANGED_PEERS` → `NumberOfExchangedPeers`
- `SPECTRUM_DEPTH` → `SpectrumDepth`
- `SPECTRUM_CAPACITY` → `SpectrumCapacity`
- `ASSETS_DEPTH` → `AssetsDepth`
- `ASSETS_CAPACITY` → `AssetsCapacity`
- `NUMBER_OF_TRANSACTIONS_PER_TICK` → `MaxTransactionsPerTick`
- `MAX_NUMBER_OF_CONTRACTS` → `MaxNumberOfContracts`
- `MAX_INPUT_SIZE` → `MaxInputSize`
- `SIGNATURE_SIZE` → `SignatureSize`
- `ISSUANCE_RATE` → `IssuanceRate`
- `MAX_AMOUNT` → `MaxAmount`
- `MAX_SUPPLY` → `MaxSupply`

Also check `deps/qubic-core/src/public_settings.h` for any new constants.

---

## 3. Update network message types

**C++ source:** `deps/qubic-core/src/network_messages/network_message_type.h` → `enum NetworkMessageType`
**C# target:** `src/Qubic.Core/NetworkMessageTypes.cs`

Add any new message types, verify existing values haven't changed.

---

## 4. Update contract definitions

**C++ source:** `deps/qubic-core/src/contract_core/contract_def.h` → `contractDescriptions[]` array
**C# target:** `src/Qubic.Core/QubicContracts.cs`

For each contract entry verify index, name, and construction epoch.

---

## 5. Update QX procedures

**C++ source:** `deps/qubic-core/src/contracts/Qx.h` → `REGISTER_USER_PROCEDURES` macro
**C# target:** `src/Qubic.Core/QxProcedures.cs`

Each `REGISTER_USER_PROCEDURE(Name, ID)` maps to `public const ushort Name = ID;`

---

## 6. Update QUTIL procedures

**C++ source:** `deps/qubic-core/src/contracts/QUtil.h` → `REGISTER_USER_PROCEDURES` macro
**C# target:** `src/Qubic.Core/QutilProcedures.cs`

---

## 7. Regenerate smart contract types

```bash
dotnet run --project tools/Qubic.ContractGen
```

This parses all C++ contract headers in `deps/qubic-core/src/contracts/` and regenerates
typed C# classes in `src/Qubic.Core/Contracts/Generated/`. Verify the output compiles
and check for new warnings about unresolved types or array sizes.

---

## 8. Check struct sizes

If network structs changed, verify derived size constants in `QubicConstants.cs`:
- `EntityRecordSize` (64) — `deps/qubic-core/src/network_messages/entity.h`
- `TransactionHeaderSize` (80) — transaction struct
- `TickSize` (344) — tick struct
- `AssetRecordSize` (48) — asset structs
- `PacketHeaderSize` (8) — `RequestResponseHeader`

Also check if `Qubic.Serialization` readers/writers need updates.

---

## 9. Build and test

```bash
dotnet build
dotnet test
```

---

## 10. Publish updated packages

If `Qubic.Crypto` changed:
1. Bump version in `src/Qubic.Crypto/Qubic.Crypto.csproj`
2. `dotnet pack src/Qubic.Crypto -c Release` → publish to NuGet
3. Update `PackageReference` version in `src/Qubic.Core/Qubic.Core.csproj`

Then for `Qubic.Core`:
1. Set version in `src/Qubic.Core/Qubic.Core.csproj` to match core (e.g., `1.279.0`)
2. `dotnet pack src/Qubic.Core -c Release` → publish to NuGet

---

## Quick reference: C++ → C# file mapping

| C++ Source (deps/qubic-core/) | C# Target (src/Qubic.Core/) |
|-------------------------------|------------------------------|
| `src/public_settings.h` | `QubicConstants.cs` |
| `src/network_messages/common_def.h` | `QubicConstants.cs` |
| `src/network_messages/network_message_type.h` | `NetworkMessageTypes.cs` |
| `src/contract_core/contract_def.h` | `QubicContracts.cs` |
| `src/contracts/Qx.h` | `QxProcedures.cs` (obsolete) |
| `src/contracts/QUtil.h` | `QutilProcedures.cs` (obsolete) |
| `src/contracts/*.h` | `Contracts/Generated/*.g.cs` (via ContractGen) |

# Qubic.ChainAnalytics.Cli

Command-line front-end for [Qubic.ChainAnalytics](../../src/Qubic.ChainAnalytics/). Pulls
tick data, transactions, and quorum votes **directly from a Qubic node over TCP** —
no RPC, no Bob — parses them, and prints summaries you can scan or pipe.

Binary name: `qubic-analytics`.

## Usage

```
qubic-analytics <host[:port]> <tick|"latest"> [options]
```

The first two args are always the target node and the tick (or `latest` to use the
node's current signed tick). Everything else is mode / filter flags.

### Modes

| Mode | What it does |
|------|--------------|
| (default) | Builds a [TickSummary](../../src/Qubic.ChainAnalytics/Models/TickSummary.cs): tick data, all transactions for the tick, local K12-digest verification against the digests in tick data. |
| `--votes` | Builds a [VoteAlignment](../../src/Qubic.ChainAnalytics/Models/VoteAlignment.cs): tick data + quorum votes for X + quorum votes for X+1, with distributions per consensus field and `FullyAligned` / `ResultPersistedIntoNextTick` flags. |
| `--replay-tick-tx FLAGS` | Replays a captured `RequestTickTransactions` packet verbatim. Pair with `--dejavu HEX` to pin the dejavu. |

### Flags

| Flag | Purpose |
|------|---------|
| `--epoch N` | Force the tick's epoch (default V2 epoch 214). Needed for archive ticks across an era. |
| `--range N` | Process N consecutive ticks starting at `<tick>` (default 1). |
| `--port P` | Override node TCP port (default 21841). |
| `--tx-range SPEC` | Limit which tx indices to print: `0-10`, `5`, `0-`, or `none`. |
| `--votes` | Switch to vote-distribution mode. |
| `--replay-tick-tx FLAGS` | Replay-mode flag bitmap. See below. |
| `--dejavu HEX` | Pin the dejavu for replay (e.g. `0xcdc636e4`). Only with `--replay-tick-tx`. |
| `--dump-dir PATH` | After processing each tick, write raw + parsed artifacts to `PATH`. See below. Default-mode only (ignored with `--votes` / `--replay-tick-tx`). |

### `--replay-tick-tx FLAGS` accepts

- `<hex>` — 256 hex chars (legacy 128 B) or 1024 hex chars (V2 512 B). `0x` prefix and whitespace tolerated.
- `@<path>` — read from file. Auto-detects: if file is 128 or 512 bytes, treated as raw; otherwise as hex text.
- `all-zero` — shortcut for "all bits 0" (request every slot).

### `--dump-dir PATH` writes

For each processed tick:

| File | Contents |
|------|----------|
| `tick-{N:D10}-tickdata.bin` | Raw `BroadcastFutureTickData` wire bytes (the canonical signed form — 139,376 B for V2). |
| `tick-{N:D10}-txs.bin` | Concatenated raw tx bytes, self-described: `u32 magic ("QBTX") \| u32 tick \| u32 count \| per tx (u32 length \| bytes)`. All little-endian. |
| `tick-{N:D10}.json` | Full parsed view: tick-data fields (computor, timestamp, signature hex, non-empty digest slots, contract fees), all transactions with hashes, identities, payload / signature / raw bytes hex. |

Use the binaries for re-verification on another tool (`tickdata.bin` is byte-for-byte
what the issuing computor signed); use the JSON for human inspection or downstream
pipelines.

## Examples

```bash
# A specific tick — full tx summary with chain-integrity check.
dotnet run --project tools/Qubic.ChainAnalytics.Cli -- \
  152.53.254.158 52810012

# The latest signed tick, only show the first 5 txs.
dotnet run --project tools/Qubic.ChainAnalytics.Cli -- \
  152.53.254.158 latest --tx-range 0-4

# Five consecutive ticks, no per-tx detail.
dotnet run --project tools/Qubic.ChainAnalytics.Cli -- \
  152.53.254.158 latest --range 5 --tx-range none

# Vote distribution for X / X+1 with full alignment summary.
dotnet run --project tools/Qubic.ChainAnalytics.Cli -- \
  152.53.254.158 54560218 --votes

# Replay a captured RequestTickTransactions verbatim.
dotnet run --project tools/Qubic.ChainAnalytics.Cli -- \
  152.53.254.158 54658297 --replay-tick-tx @flags.bin --dejavu 0xcdc636e4

# Archive a tick — raw signed bytes + JSON for inspection.
dotnet run --project tools/Qubic.ChainAnalytics.Cli -- \
  152.53.254.158 55449200 --tx-range none --dump-dir ./ticks/
```

## What you see

Default summary lines:
```
── tick 52810012 ───────────────────────────────
  epoch:       214
  computor:    #216
  timestamp:   2026-05-20T12:47:38.0000000Z
  signature:   68d2e645e65976d2e8a12bb88fcc9148…
  txs:         2236  (system: 2236, contract: 0, transfer: 0)
  total QU:    33,000,000
  verified:    YES
  timings:     ...
  transactions: (all 2236)
    [0] kduluwskoflazgyfvftmop…
        ONXWEBHCEK… -> AAAA…FXIB    0 QU  [system, type#6, payload 1008B]
```

Vote-distribution summary lines:
```
── vote alignment for tick 54560218 ───────────────────────────────
  tick data:        present (computor #258, epoch 215)
  votes for X:      635 votes (635 distinct computors)
  votes for X+1:    541 votes
  result persisted: YES
  fully aligned:    YES
    transactionDigest: 1 distinct values, dominant 635/635 ✓ (quorum 451)
         635 (100.0%)  ripdljysgtcwvcmguhqafoncwusejjdaspwkrcsxlcydsvvttnrkqeucauoe
```

Digests are rendered in **Qubic lowercase identity format** (60-char K12-checksummed),
not hex.

## Dependencies

- [Qubic.ChainAnalytics](../../src/Qubic.ChainAnalytics/) — analytics models + analyzers
- [Qubic.Network](../../src/Qubic.Network/) — direct TCP node client
- [Qubic.Core](../../src/Qubic.Core/) — constants, entities

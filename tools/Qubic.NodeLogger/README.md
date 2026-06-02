# Qubic.NodeLogger

Pulls event logs from a Qubic node over direct TCP. Lists entries, counts by
log type, dumps to JSON. Useful for monitoring, audits, and post-mortem
analysis of contract/transfer/burn events.

Binary name: `qubic-node-logger`.

> **Requires a `logReaderPasscodes` passcode.** The node operator configures a
> 32-byte secret in qubic-core; without it (or with the wrong value) the node
> replies with `EndResponse` to every log request. The tool handles that
> cleanly — empty result, no crash.

## Modes

| Mode | Packet | What it does |
|------|--------|--------------|
| `ranges <tick>` | type 50 | `REQUEST_ALL_LOG_ID_RANGES_FROM_TX` — log-id ranges for every tx slot in the tick (4096 user-tx + 6 special). |
| `range <tick> <txId>` | type 48 | `REQUEST_LOG_ID_RANGE_FROM_TX` — log-id range for one tx slot. |
| `log <fromId> <toId>` | type 44 | `REQUEST_LOG` — fetches and parses entries in `[fromId, toId]`. Shows counts by type and the first 10 entries. |

`log` mode also accepts `--json PATH` to dump every parsed entry as JSON.

All three modes accept `--raw PATH` to dump the raw response payload bytes to a
file — useful for archiving the wire form, re-parsing later, or diffing across
nodes. The file is only written when the node actually returns a response (i.e.
not when it sends `EndResponse` because the passcode was wrong or the data
isn't available).

| Mode | Raw payload size |
|------|------------------|
| `ranges` | 65,632 bytes (4102 slots × 16 bytes) |
| `range` | 16 bytes (`fromLogId` + `length`) |
| `log` | variable — concatenated 26-byte-header log entries |

## Usage

```
qubic-node-logger <host[:port]> <passcode> <mode> [args] [--json PATH]
```

### Passcode formats

| Form | Example |
|------|---------|
| 64 hex chars | `0xdeadbeef0123…` (whitespace / `:` / `,` ignored) |
| four u64 parts joined by `-` | `0-0-0-0` |
| mixed decimal / hex per part | `0x1234-0xabcd-0-1234567890` |

The dash form mirrors the C++ `unsigned long long passcode[4]` declaration —
each part is serialised little-endian, giving 32 bytes total. Per-part
`0x` prefix means hex, otherwise decimal.

## Examples

```bash
# Which tx slots in tick 55075000 produced log entries?
dotnet run --project tools/Qubic.NodeLogger -- \
  1.2.3.4 0-0-0-0 ranges 55075000

# Range for a single tx slot.
dotnet run --project tools/Qubic.NodeLogger -- \
  1.2.3.4 0xdeadbeef0123... range 55075000 0

# Fetch entries 1000..1100 and write to JSON.
dotnet run --project tools/Qubic.NodeLogger -- \
  1.2.3.4 0xdeadbeef0123... log 1000 1100 --json logs.json

# Archive both the parsed view AND the canonical raw payload.
dotnet run --project tools/Qubic.NodeLogger -- \
  1.2.3.4 0xdeadbeef0123... log 1000 1100 --json logs.json --raw logs.bin
```

## Sample output (`log` mode)

```
── log entries [1000, 1100] ───────────────────────────────
  received: 87 entries
  by type:
       42 × type#  0 QU_TRANSFER                                  (    3192 bytes total)
       28 × type#  6 CONTRACT_INFORMATION_MESSAGE                 (    8064 bytes total)
       12 × type#  8 BURNING                                      (     480 bytes total)
        5 × type#  1 ASSET_ISSUANCE                               (     720 bytes total)
  ticks covered: 14 (55075000..55075013)
  first 10 entries:
    logId=      1000  tick=  55075000  epoch=215  type#  0=QU_TRANSFER       size=   72
    …
```

## Known log types

The body bytes are exposed raw (hex in the JSON output). Names per
qubic-core's `logging.h`:

| Type | Name |
|------|------|
| 0 | QU_TRANSFER |
| 1 | ASSET_ISSUANCE |
| 2 | ASSET_OWNERSHIP_CHANGE |
| 3 | ASSET_POSSESSION_CHANGE |
| 4 | CONTRACT_ERROR_MESSAGE |
| 5 | CONTRACT_WARNING_MESSAGE |
| 6 | CONTRACT_INFORMATION_MESSAGE |
| 7 | CONTRACT_DEBUG_MESSAGE |
| 8 | BURNING |
| 9 | DUST_BURNING |
| 10 | SPECTRUM_STATS |
| 11 | ASSET_OWNERSHIP_MANAGING_CONTRACT_CHANGE |
| 12 | ASSET_POSSESSION_MANAGING_CONTRACT_CHANGE |
| 13 | CONTRACT_RESERVE_DEDUCTION |
| 14 | ORACLE_QUERY_STATUS_CHANGE |
| 15 | ORACLE_SUBSCRIBER_MESSAGE |
| 255 | CUSTOM_MESSAGE |

## Dependencies

- [Qubic.Network](../../src/Qubic.Network/) — direct TCP node client with the log methods
- [Qubic.Core](../../src/Qubic.Core/) — `LogEntry`, `LogIdRange`, `TickLogIdRanges` entities

# Qubic.NodeTester

Direct-TCP peer test suite. Connects to a Qubic node, runs a battery of
protocol-level checks, and reports pass/fail per test + timings. Useful for
verifying a peer is reachable, healthy, and serving the standard request types.

Binary name: `qubic-node-tester`.

## What it tests

| # | Test | Packet |
|---|------|--------|
| 1 | TCP connect | (just `connect()`) |
| 2 | Handshake — ExchangePublicPeers | type 0 |
| 3 | Broadcast listen (default 5s) | passive, separate connection |
| 4 | RequestCurrentTickInfo | type 27 → 28 |
| 5 | RequestSystemInfo | type 46 → 47 |
| 6 | RequestTickData | type 16 → 8 or 35 |
| 7 | RequestTickTransactions | type 29 → many 24 + 35 |
| 8 | RequestQuorumTick | type 14 → many 3 + 35 |

For tests 6–8 the probe tick is `node.currentTick - 1` (the latest signed tick),
fetched via test 4. If that fails, tests 6–8 are skipped.

## Reconnect on disconnect

If the node closes our connection mid-suite (idle reclaim, peer rotation, etc.),
each request is wrapped in a one-shot reconnect: catch the connection error,
`Disconnect()` + `ConnectAsync()`, retry once. A `[reconnecting]` marker prints
inline and a final `reconnects: N` line appears in the summary when non-zero.

## Usage

```
qubic-node-tester <host[:port]> [--listen SECONDS]
```

- `<host[:port]>` — target peer (default port 21841)
- `--listen SECONDS` — broadcast listen window (default 5)

Exit code 0 if all tests pass, 1 if any fail.

## Examples

```bash
# Quick health check
dotnet run --project tools/Qubic.NodeTester -- 152.53.254.158

# Longer listen window
dotnet run --project tools/Qubic.NodeTester -- 185.84.224.10:21841 --listen 10
```

## Sample output

```
qubic-node-tester — direct-TCP peer test suite
target: 152.53.254.158:21841

[ 1] TCP connect                              PASS       51ms  connected (local …)
[ 2] Handshake (ExchangePublicPeers)          PASS       34ms  got 4 peer(s): 207.144.234.20, …
[ 3] Broadcast listen (5s)                    PASS     5.045s  2875 packet(s) — type#3×2466, type#24×402, …
[ 4] RequestCurrentTickInfo                   PASS      213ms  tick=55072074 epoch=215 …
[ 5] RequestSystemInfo                        PASS       36ms  version=293 epoch=215 …
[ 6] RequestTickData (tick 55072073)          PASS       37ms  computor #381 epoch=215 txDigests=3 of 4096
[ 7] RequestTickTransactions (tick 55072073)  PASS       41ms  4 transaction(s) returned
[ 8] RequestQuorumTick (tick 55072073)        PASS       40ms  539 vote(s), 536 distinct computor(s), quorum 451

────────────────────────────────────────────────────────────
summary: 8 passed, 0 failed, 0 skipped — total 5.486s
```

When the node refuses a request after a transient close:
```
[ 4] RequestCurrentTickInfo  [reconnecting] PASS   17.674s  tick=…
…
reconnects: 1 (node closed the connection mid-suite)
```

## Dependencies

- [Qubic.Network](../../src/Qubic.Network/) — direct TCP node client
- [Qubic.Core](../../src/Qubic.Core/) — entities (CurrentTickInfo, TickData, …)
- [Qubic.Serialization](../../src/Qubic.Serialization/) — packet types

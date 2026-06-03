# Qubic.TxRelay

Pulls transactions from one Qubic peer and broadcasts them to another over direct
TCP. Useful for mirroring, fan-out, replay testing, or seeding a fresh node with
recent traffic from a known-good peer.

Binary name: `qubic-tx-relay`.

## How it works

1. Connect to **src** and **dst** peers (port 21841 by default).
2. For each requested tick, issue `RequestTickTransactions` to src, receive raw
   signed-tx bytes back.
3. For each tx: hash with K12 for the dedup set; broadcast the raw bytes verbatim
   to dst via `BroadcastTransaction` (dejavu = 0, so dst will propagate further).
4. Repeat per tick / per follow-poll cycle.

No re-signing, no parsing into `QubicTransaction` — bytes go through unchanged.

## Usage

```
qubic-tx-relay <src-host[:port]> <dst-host[:port]> <source> [options]
```

### `<source>` selects what to relay

| Form | Meaning |
|------|---------|
| `<tick>` | one specific tick |
| `<from>-<to>` | inclusive tick range |
| `latest` | the latest signed tick (src's current tick − 1) |

### Options

| Flag | Default | Effect |
|------|---------|--------|
| `--follow` | off | with `latest`, poll src for new signed ticks and relay each as it appears (Ctrl+C to stop) |
| `--poll-ms N` | 1000 | poll interval for `--follow` |
| `--epoch N` | 214 (V2) | epoch for `RequestTickTransactions` flag-array sizing |
| `--max-per-sec N` | unlimited | cap dst broadcasts per second |
| `--no-dedup` | off | re-broadcast every tx every pass (default skips already-relayed tx hashes within this run) |
| `--dry-run` | off | fetch from src and print, but do NOT broadcast to dst (dst is not even connected) |
| `--port-src P` | 21841 | override src port |
| `--port-dst P` | 21841 | override dst port |
| `--randomize-dejavu` | off | pick a fresh random dejavu for each relayed tx instead of using 0 (the propagation default). Each tx's chosen dejavu is printed inline. |

Dedup is **in-run only** — it remembers tx hashes seen during this process and
doesn't broadcast the same hash twice. dst's own dejavu filter handles cross-run
dupes.

## Examples

```bash
# Replay one historical tick from A to B
dotnet run --project tools/Qubic.TxRelay -- 1.2.3.4 5.6.7.8 52810012

# Mirror a range of recent ticks
dotnet run --project tools/Qubic.TxRelay -- 1.2.3.4 5.6.7.8 55072000-55072004

# Continuous mirror, capped at 100 broadcasts/sec
dotnet run --project tools/Qubic.TxRelay -- 1.2.3.4 5.6.7.8 latest \
  --follow --max-per-sec 100

# Inspect what would be relayed — broadcast nothing
dotnet run --project tools/Qubic.TxRelay -- 1.2.3.4 unused-in-dry-run latest --dry-run

# Force dst to process re-broadcasts even of payloads it has seen
# (random dejavu per tx bypasses dst's dejavu filter)
dotnet run --project tools/Qubic.TxRelay -- 1.2.3.4 5.6.7.8 55072000 --randomize-dejavu
```

## Sample output

```
connecting src 152.53.254.158:21841…
connecting dst 5.6.7.8:21841…

── tick 55449200 ── fetched 275 tx(s) from src
  SND ptatpfheoqehfffxsfwthapxocqfkxrxdluepivwjfgdfplzuihonzqcxqwd
        UYALOQPDKYUCCDPUNLGSBUAIGHBDLDKXWVDXKAELEHDRHTNEXRVQDGAFUCSA -> AAAA…FXIB
                      0 QU  [type#6, 216B]
  SND papwpvpkzjwtvfptfyqggkzbyeegemknlxlisuhcfesazczefhtwpuscnyqm
        JYVMOAEJEKDZRBTCJHCOJPXTOIGDWEDHTLTLVWXHHHIRCARHDJSEEHGFUHBH -> GEMFXBJHIPLIREURQVTAPXQMKLVAZZISMMRDHFIMBFMOZMLKGNSABMLFRRWG
                      9 QU  [type#0, 144B]
…
  tick 55449200: 275 sent, 0 deduped

summary: relayed=275 deduped=0 bytes=43,560
```

`SND` = broadcast to dst, `DRY` = dry-run (no broadcast).

## Things to know

- **dst's dejavu filter**: qubic-core silently ignores any tx with the same
  `(salt, dejavu, payload)` hash it has already seen. Replaying old txs to a
  node that already processed them is therefore a no-op, not an error. Pair
  with `--randomize-dejavu` to force dst to re-evaluate each payload.
- **Throttling**: nodes may rate-limit or drop floods. Use `--max-per-sec` if
  you're relaying a big range to a busy peer.
- **No re-signing**: relayed txs keep their original source identity and
  signature. dst sees them exactly as src did.

## Dependencies

- [Qubic.Network](../../src/Qubic.Network/) — direct TCP node client (`GetTickTransactionsAsync`, `BroadcastRawTransactionAsync`)
- [Qubic.Core](../../src/Qubic.Core/) — constants
- `Qubic.Crypto` (NuGet) — K12 for the dedup key and identity rendering

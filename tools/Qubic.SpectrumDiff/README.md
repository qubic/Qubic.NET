# Qubic.SpectrumDiff

Compares two Qubic spectrum dumps and reports per-account balance differences.

A spectrum file is a flat array of `2^24` × 64-byte `EntityRecord` (~1 GB) —
the on-chain account table at the end of an epoch (`spectrum.NNN`) or a
snapshot taken by a node operator. This tool walks both files in lockstep,
streaming 1 MiB at a time (no 1 GB load), and emits one block per slot whose
account state actually changed.

## What's compared

For each of the 16,777,216 slots, the tool parses these fields from both
sides and reports any slot where they disagree:

| field        | type    | notes                                              |
|--------------|---------|----------------------------------------------------|
| `incoming`   | int64   | total QU received by this entity                   |
| `outgoing`   | int64   | total QU sent by this entity                       |
| `n_incoming` | uint32  | number of incoming transfers                       |
| `n_outgoing` | uint32  | number of outgoing transfers                       |
| `balance`    | derived | `incoming - outgoing`; emitted only when it shifts |

Tick fields (`latestIncomingTransferTick`, `latestOutgoingTransferTick`) are
**ignored** — they flip on every transfer and would generate noise without
adding information.

A slot is treated as **empty** when its 32-byte public key is all zero. The
tool distinguishes three diff kinds:

- **both populated** — same identity on both sides, different values
- **only in A** — populated in A, empty in B (account was zeroed / not seen)
- **only in B** — empty in A, populated in B (new account)

## Usage

```
qubic-spectrum-diff <spectrum-a> <spectrum-b> [--csv] [--limit N] [--out FILE]
```

| flag           | meaning                                                  |
|----------------|----------------------------------------------------------|
| `--csv`        | machine-readable CSV instead of the pretty table         |
| `--limit N`    | stop after reporting `N` differing slots                 |
| `--out FILE`   | write the report to `FILE` (stderr still gets progress)  |

Exit code is **0** if the files are equivalent, **1** if any diffs were
found, **2** on bad args, **3** on missing/wrong-size input files.

## Examples

```bash
dotnet run --project tools/Qubic.SpectrumDiff -- \
    /var/qubic/spectrum.215 /var/qubic/spectrum.215.peer

# CSV for downstream tooling
qubic-spectrum-diff a.bin b.bin --csv --out diff.csv

# Sanity-check first 10 diffs, then bail
qubic-spectrum-diff a.bin b.bin --limit 10
```

## Output format

Each differing slot is printed as a header line followed by one row per
differing field:

```
[         8] IAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAABXSH
  incoming    A=     6,112,403,533,007  B=     6,111,776,285,097  Δ=-627,247,910
  balance     A=     1,655,602,613,770  B=     1,654,975,365,860  Δ=-627,247,910
```

The 60-character identity is computed from the populated side's public key.
Slots 0–1023 are reserved contract slots (`QX`, `QUTIL`, `QVAULT`, ...).

For presence flips the header is suffixed with `(empty -> present)` or
`(present -> empty)` and every tracked field is emitted so the full state
of the populated side is visible.

CSV output (`--csv`) emits a single header row followed by one row per
differing field:

```
index,identity,field,value_a,value_b,delta
8,IAAA...BXSH,incoming,6112403533007,6111776285097,-627247910
```

## Interpreting results

Two snapshots of the **same epoch** taken from different nodes should
differ only by transfers one node has seen and the other has not. Net
delta across all slots should be **zero** (QU is conserved). A non-zero
net delta means at least one file is inconsistent or one of the snapshots
was taken mid-write.

When comparing **different epochs**, expect changes in essentially every
populated slot — the tool is most useful for same-epoch divergence
investigations and for verifying that two nodes converged on the same
state at an epoch boundary.

## Performance

A full 1 GB vs 1 GB scan completes in roughly **10–20 seconds** on a
warm-cache SSD. Throughput is dominated by file I/O; CPU work (record
parsing, identity decoding) only kicks in on differing slots.

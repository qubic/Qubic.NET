# Qubic.ChainAnalytics

Direct-mainnet chain analytics for Qubic. Talks **straight to a node over TCP**
via [Qubic.Network](../Qubic.Network/) — no RPC, no Bob, no archive layer.

Use this for analytics that need raw chain truth: tick data, transactions for
a tick, locally-computed digests, and verification of the chain integrity
(parsed-tx K12 digests vs. the digest list embedded in tick data).

## Status

Early scaffold. Add new analytics here as use-cases come up.

## Available analytics

### `TickAnalyzer.GetTickSummaryAsync(tick, epoch)`

Flow per tick:

1. **Fetch tick data** via `RequestTickData` — returns `null` if the node has
   no data for that tick (empty slot, not yet broadcast, evicted).
2. **Fetch all transactions** for the tick via `RequestTickTransactions` with
   the flag map sized for the supplied epoch (128 B legacy, 512 B V2+).
3. **Parse each tx** into a `TickTransaction` record: source/destination identity
   (human-readable form), amount, tick, input type/size, payload, signature,
   plus the **K12 digest** and **canonical hash** computed locally from the raw bytes.
4. **Verify** the computed digests against the tick data digest slots — flips
   `TickSummary.DigestsVerified` to `false` on any mismatch.

```csharp
using Qubic.ChainAnalytics;
using Qubic.Network;

await using var node = new QubicNodeClient("185.84.224.10");
await node.ConnectAsync();

var analyzer = new TickAnalyzer(node);
var summary = await analyzer.GetTickSummaryAsync(tick: 21_500_000);

if (!summary.TickDataAvailable)
{
    Console.WriteLine($"Tick {summary.TickNumber}: no tick data on this node");
    return;
}

Console.WriteLine($"Tick {summary.TickNumber} epoch {summary.Epoch}");
Console.WriteLine($"  computor #{summary.TickData!.ComputorIndex} @ {summary.TickData.Timestamp:O}");
Console.WriteLine($"  {summary.Transactions.Count} tx | total {summary.TotalAmount} QU");
Console.WriteLine($"  {summary.ContractCallCount} contract calls");
Console.WriteLine($"  digests verified: {summary.DigestsVerified}");

foreach (var tx in summary.Transactions)
    Console.WriteLine($"    {tx.Hash}  {tx.SourceIdentity} -> {tx.DestinationIdentity}  {tx.Amount}");
```

### `TickAnalyzer.ScanAsync(fromTick, toTick, epoch)`

Streams summaries for a tick range in ascending order. Requests are issued
sequentially against a single node (the node client serialises in-flight
requests). For wider coverage, run multiple analyzers against multiple peers.

## Parsing on its own

`TickTransactionParser.Parse(rawBytes)` turns a single raw transaction
(as returned by `GetTickTransactionsAsync`) into a `TickTransaction`, computing
the digest/hash without going back to the network.

using Qubic.ChainAnalytics;
using Qubic.ChainAnalytics.Models;
using Qubic.Core;
using Qubic.Network;

if (args.Length < 2 || args.Contains("--help") || args.Contains("-h"))
{
    Console.Error.WriteLine("""
        qubic-analytics — direct-mainnet tick analytics

        usage:
          qubic-analytics <host[:port]> <tick|"latest"> [--epoch N] [--range N]
                                                       [--port P] [--tx-range SPEC]

        --tx-range filters which transactions to print (the tick summary is always shown):
          0-10     indices 0..10 inclusive
          5        single index 5
          0-       index 0 to the end
          none     hide all transactions

        examples:
          qubic-analytics 185.84.224.10 21500000
          qubic-analytics 185.84.224.10 latest
          qubic-analytics 185.84.224.10 latest --range 5
          qubic-analytics 185.84.224.10 52810012 --tx-range 0-10
          qubic-analytics 185.84.224.10 52810012 --tx-range none
        """);
    return args.Length < 2 ? 1 : 0;
}

var (host, hostPort) = ParseHost(args[0]);
ushort epoch = QubicConstants.TransactionsPerTickV2Epoch;
int port = hostPort ?? 21841;
uint range = 1;
TxRange txRange = TxRange.All;

for (var i = 2; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--epoch" when i + 1 < args.Length:
            epoch = ushort.Parse(args[++i]);
            break;
        case "--port" when i + 1 < args.Length:
            port = int.Parse(args[++i]);
            break;
        case "--range" when i + 1 < args.Length:
            range = uint.Parse(args[++i]);
            break;
        case "--tx-range" when i + 1 < args.Length:
            txRange = TxRange.Parse(args[++i]);
            break;
        default:
            Console.Error.WriteLine($"unknown arg: {args[i]}");
            return 1;
    }
}

await using var node = new QubicNodeClient(host, port);
Console.Error.WriteLine($"connecting to {host}:{port}…");
await node.ConnectAsync();

uint startTick;
if (args[1].Equals("latest", StringComparison.OrdinalIgnoreCase))
{
    var info = await node.GetCurrentTickInfoAsync();
    // Latest *signed* tick is one behind the current consensus tick.
    startTick = info.Tick > 0 ? info.Tick - 1 : 0;
    epoch = info.Epoch;
    Console.Error.WriteLine($"node tick={info.Tick} epoch={info.Epoch} — using tick {startTick}");
}
else
{
    startTick = uint.Parse(args[1]);
}

var analyzer = new TickAnalyzer(node);
var endTick = startTick + range - 1;

await foreach (var summary in analyzer.ScanAsync(startTick, endTick, epoch))
{
    PrintSummary(summary, txRange);
}

return 0;

static (string host, int? port) ParseHost(string arg)
{
    var colon = arg.LastIndexOf(':');
    if (colon < 0) return (arg, null);
    return (arg[..colon], int.Parse(arg[(colon + 1)..]));
}

static void PrintSummary(TickSummary s, TxRange txRange)
{
    Console.WriteLine();
    Console.WriteLine($"── tick {s.TickNumber} ───────────────────────────────");

    if (!s.TickDataAvailable)
    {
        Console.WriteLine("  tick data: NOT AVAILABLE on this node");
        PrintTimings(s.Timings);
        return;
    }

    var td = s.TickData!;
    Console.WriteLine($"  epoch:       {s.Epoch}");
    Console.WriteLine($"  computor:    #{td.ComputorIndex}");
    Console.WriteLine($"  timestamp:   {(td.Timestamp == DateTime.MinValue ? "—" : td.Timestamp.ToString("O"))}");
    Console.WriteLine($"  signature:   {Hex(td.Signature, max: 16)}…");
    Console.WriteLine($"  txs:         {s.Transactions.Count}  (system: {s.SystemMessageCount}, contract: {s.ContractCallCount}, transfer: {s.UserTransferCount})");
    Console.WriteLine($"  total QU:    {s.TotalAmount:N0}");
    Console.WriteLine($"  verified:    {(s.DigestsVerified ? "YES" : "NO")}");
    PrintTimings(s.Timings);

    if (s.Transactions.Count == 0) return;

    var (from, to) = txRange.Resolve(s.Transactions.Count);
    if (from > to)
    {
        Console.WriteLine($"  transactions: (hidden — {s.Transactions.Count} total, --tx-range to show)");
        return;
    }

    var total = s.Transactions.Count;
    var label = (from == 0 && to == total - 1)
        ? $"all {total}"
        : $"{from}..{to} of {total}";
    Console.WriteLine($"  transactions: ({label})");

    for (var i = from; i <= to; i++)
    {
        var tx = s.Transactions[i];
        var kind = tx.Kind switch
        {
            TickTransactionKind.SystemMessage => "system",
            TickTransactionKind.ContractCall => $"contract#{tx.DestinationContractIndex}, proc#{tx.InputType}",
            _ => $"transfer, type#{tx.InputType}",
        };
        Console.WriteLine($"    [{i}] {tx.Hash}");
        Console.WriteLine($"        {tx.SourceIdentity} -> {tx.DestinationIdentity}");
        Console.WriteLine($"        {tx.Amount,15:N0} QU  [{kind}, payload {tx.InputSize}B]");
    }
}

static void PrintTimings(TickFetchTimings t)
{
    Console.WriteLine($"  timings:     started  {t.StartedAt:HH:mm:ss.fff}");
    Console.WriteLine($"               finished {t.FinishedAt:HH:mm:ss.fff}  (total {Fmt(t.TotalDuration)})");
    Console.WriteLine($"               tick data fetch  {Fmt(t.TickDataFetch.Duration)}  [{t.TickDataFetch.StartedAt:HH:mm:ss.fff} → {t.TickDataFetch.FinishedAt:HH:mm:ss.fff}]");
    if (t.TransactionsFetch is { } txStep)
        Console.WriteLine($"               transactions     {Fmt(txStep.Duration)}  [{txStep.StartedAt:HH:mm:ss.fff} → {txStep.FinishedAt:HH:mm:ss.fff}]");
    if (t.ParseAndVerify is { } pvStep)
        Console.WriteLine($"               parse + verify   {Fmt(pvStep.Duration)}  [{pvStep.StartedAt:HH:mm:ss.fff} → {pvStep.FinishedAt:HH:mm:ss.fff}]");
}

static string Fmt(TimeSpan d) =>
    d.TotalSeconds >= 1 ? $"{d.TotalSeconds,7:0.000}s" : $"{d.TotalMilliseconds,7:0.0}ms";

static string Hex(byte[] bytes, int max) =>
    Convert.ToHexString(bytes.AsSpan(0, Math.Min(max, bytes.Length))).ToLowerInvariant();

readonly struct TxRange
{
    private readonly int _from;
    private readonly int? _to; // null = open-ended (to last)
    private readonly bool _none;

    private TxRange(int from, int? to, bool none) { _from = from; _to = to; _none = none; }

    public static TxRange All { get; } = new(0, null, false);

    public static TxRange Parse(string spec)
    {
        if (string.Equals(spec, "none", StringComparison.OrdinalIgnoreCase))
            return new(0, 0, true);

        var dash = spec.IndexOf('-');
        if (dash < 0)
        {
            var single = int.Parse(spec);
            if (single < 0) throw new ArgumentException($"--tx-range index must be ≥ 0: {spec}");
            return new(single, single, false);
        }

        var fromStr = spec[..dash];
        var toStr = spec[(dash + 1)..];
        var from = fromStr.Length == 0 ? 0 : int.Parse(fromStr);
        int? to = toStr.Length == 0 ? null : int.Parse(toStr);
        if (from < 0 || (to is int t && t < from))
            throw new ArgumentException($"--tx-range invalid: {spec}");
        return new(from, to, false);
    }

    /// <summary>
    /// Resolves the range against the actual tx count, clamping to bounds.
    /// Returns inclusive (from, to). If from &gt; to, nothing should be shown.
    /// </summary>
    public (int from, int to) Resolve(int count)
    {
        if (_none || count == 0) return (1, 0); // empty range
        var from = Math.Clamp(_from, 0, count - 1);
        var to = _to is null ? count - 1 : Math.Clamp(_to.Value, 0, count - 1);
        return (from, to);
    }
}

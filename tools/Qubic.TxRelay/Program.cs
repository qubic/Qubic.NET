using System.Buffers.Binary;
using Qubic.Core;
using Qubic.Crypto;
using Qubic.Network;

if (args.Length < 3 || args.Contains("--help") || args.Contains("-h"))
{
    Console.Error.WriteLine("""
        qubic-tx-relay — pull transactions from one peer and broadcast them to another

        usage:
          qubic-tx-relay <src-host[:port]> <dst-host[:port]> <source> [options]

        <source> selects which ticks to relay:
          <tick>          one specific tick
          <from>-<to>     inclusive tick range
          latest          the latest signed tick (one behind src's current tick)

        options:
          --follow                with `latest`, keep polling and relay each new
                                  signed tick as it appears (Ctrl+C to stop)
          --poll-ms N             poll interval for --follow (default 1000)
          --epoch N               epoch override for RequestTickTransactions
                                  (default V2 epoch 214)
          --max-per-sec N         throttle dest broadcasts (default unlimited)
          --no-dedup              re-broadcast every tx every pass (default: skip
                                  txs whose K12 hash was already broadcast in this
                                  run)
          --dry-run               fetch from src, print, but DO NOT broadcast
          --port-src P            override src port (default 21841)
          --port-dst P            override dst port (default 21841)
          --randomize-dejavu      pick a fresh random dejavu for each relayed tx
                                  (default 0 — the propagation convention).
                                  Use this to bypass dst's dejavu filter when
                                  re-broadcasting identical payloads.

        examples:
          # Replay one historical tick from A to B
          qubic-tx-relay 1.2.3.4 5.6.7.8 52810012

          # Mirror the last 5 settled ticks
          qubic-tx-relay 1.2.3.4 5.6.7.8 55072000-55072004

          # Continuous mirror, throttled
          qubic-tx-relay 1.2.3.4 5.6.7.8 latest --follow --max-per-sec 100

          # Inspect what would be relayed, broadcast nothing
          qubic-tx-relay 1.2.3.4 5.6.7.8 latest --dry-run

          # Force dst to process re-broadcasts (random dejavu per tx)
          qubic-tx-relay 1.2.3.4 5.6.7.8 55072000 --randomize-dejavu
        """);
    return args.Length < 3 ? 1 : 0;
}

var (srcHost, srcHostPort) = ParseHost(args[0]);
var (dstHost, dstHostPort) = ParseHost(args[1]);
var sourceSpec = args[2];

int srcPort = srcHostPort ?? 21841;
int dstPort = dstHostPort ?? 21841;
ushort epoch = QubicConstants.TransactionsPerTickV2Epoch;
bool follow = false;
int pollMs = 1000;
int maxPerSec = 0; // 0 = unlimited
bool dedup = true;
bool dryRun = false;
bool randomizeDejavu = false;

for (var i = 3; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--follow":
            follow = true;
            break;
        case "--poll-ms" when i + 1 < args.Length:
            pollMs = int.Parse(args[++i]);
            break;
        case "--epoch" when i + 1 < args.Length:
            epoch = ushort.Parse(args[++i]);
            break;
        case "--max-per-sec" when i + 1 < args.Length:
            maxPerSec = int.Parse(args[++i]);
            break;
        case "--no-dedup":
            dedup = false;
            break;
        case "--dry-run":
            dryRun = true;
            break;
        case "--port-src" when i + 1 < args.Length:
            srcPort = int.Parse(args[++i]);
            break;
        case "--port-dst" when i + 1 < args.Length:
            dstPort = int.Parse(args[++i]);
            break;
        case "--randomize-dejavu":
            randomizeDejavu = true;
            break;
        default:
            Console.Error.WriteLine($"unknown arg: {args[i]}");
            return 1;
    }
}

var cancel = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cancel.Cancel();
    Console.Error.WriteLine("\n(stopping…)");
};

await using var src = new QubicNodeClient(srcHost, srcPort);
Console.Error.WriteLine($"connecting src {srcHost}:{srcPort}…");
await src.ConnectAsync(cancel.Token);

QubicNodeClient? dst = null;
if (!dryRun)
{
    dst = new QubicNodeClient(dstHost, dstPort);
    Console.Error.WriteLine($"connecting dst {dstHost}:{dstPort}…");
    await dst.ConnectAsync(cancel.Token);
}
else
{
    Console.Error.WriteLine("dry-run: dst connection skipped");
}

var crypt = new QubicCrypt();
var seen = new HashSet<string>(StringComparer.Ordinal);
var totalRelayed = 0;
var totalDeduped = 0;
var totalBytes = 0L;
var throttle = maxPerSec > 0 ? new RateLimiter(maxPerSec) : null;

try
{
    if (sourceSpec.Equals("latest", StringComparison.OrdinalIgnoreCase))
    {
        if (follow)
            await FollowAsync(src, dst);
        else
            await RelayLatestOnceAsync(src, dst);
    }
    else if (sourceSpec.Contains('-'))
    {
        var parts = sourceSpec.Split('-', 2);
        var from = uint.Parse(parts[0]);
        var to = uint.Parse(parts[1]);
        if (to < from) throw new ArgumentException("range to < from");
        for (var t = from; t <= to && !cancel.IsCancellationRequested; t++)
            await RelayTickAsync(src, dst, t, epoch);
    }
    else
    {
        var tick = uint.Parse(sourceSpec);
        await RelayTickAsync(src, dst, tick, epoch);
    }
}
catch (OperationCanceledException) { /* graceful */ }

Console.Error.WriteLine();
Console.Error.WriteLine($"summary: relayed={totalRelayed} deduped={totalDeduped} bytes={totalBytes:N0}");
return 0;

async Task FollowAsync(QubicNodeClient src, QubicNodeClient? dst)
{
    uint? lastTick = null;
    while (!cancel.IsCancellationRequested)
    {
        var info = await src.GetCurrentTickInfoAsync(cancel.Token);
        var signed = info.Tick > 0 ? info.Tick - 1 : 0u;
        if (lastTick is null || signed > lastTick.Value)
        {
            // Catch up if we fell behind, but cap to avoid huge bursts.
            var from = lastTick is null ? signed : Math.Max(signed - 5, lastTick.Value + 1);
            for (var t = from; t <= signed && !cancel.IsCancellationRequested; t++)
                await RelayTickAsync(src, dst, t, info.Epoch);
            lastTick = signed;
        }
        try { await Task.Delay(pollMs, cancel.Token); }
        catch (OperationCanceledException) { return; }
    }
}

async Task RelayLatestOnceAsync(QubicNodeClient src, QubicNodeClient? dst)
{
    var info = await src.GetCurrentTickInfoAsync(cancel.Token);
    var signed = info.Tick > 0 ? info.Tick - 1 : 0u;
    Console.Error.WriteLine($"src tick={info.Tick} epoch={info.Epoch} — relaying tick {signed}");
    await RelayTickAsync(src, dst, signed, info.Epoch);
}

async Task RelayTickAsync(QubicNodeClient src, QubicNodeClient? dst, uint tick, ushort epoch)
{
    var rawTxs = await src.GetTickTransactionsAsync(tick, epoch, cancel.Token);
    Console.WriteLine();
    Console.WriteLine($"── tick {tick} ── fetched {rawTxs.Count} tx(s) from src");
    if (rawTxs.Count == 0) return;

    var sent = 0;
    var skipped = 0;
    foreach (var raw in rawTxs)
    {
        if (cancel.IsCancellationRequested) break;

        var digest = crypt.KangarooTwelve(raw);
        var hash = crypt.GetHumanReadableBytes(digest);

        if (dedup && !seen.Add(hash))
        {
            skipped++;
            totalDeduped++;
            continue;
        }

        // Decode minimal tx fields for the inspection line (no crypto, just offsets).
        var amount = BinaryPrimitives.ReadInt64LittleEndian(raw.AsSpan(64));
        var inputType = BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(76));
        var srcId = crypt.GetIdentityFromPublicKey(raw.AsSpan(0, 32).ToArray());
        var dstId = crypt.GetIdentityFromPublicKey(raw.AsSpan(32, 32).ToArray());

        uint? dvUsed = randomizeDejavu ? (uint)Random.Shared.Next(1, int.MaxValue) : null;

        if (dst is not null)
        {
            if (throttle is not null) await throttle.WaitAsync(cancel.Token);
            if (dvUsed is uint dv)
                await dst.BroadcastRawTransactionAsync(raw, dv, cancel.Token);
            else
                await dst.BroadcastRawTransactionAsync(raw, cancel.Token);
        }
        sent++;
        totalRelayed++;
        totalBytes += raw.Length;

        var dvNote = dvUsed is uint shown ? $"  dejavu=0x{shown:x8}(rnd)" : "";
        Console.WriteLine($"  {(dryRun ? "DRY" : "SND")} {hash}{dvNote}");
        Console.WriteLine($"        {srcId} -> {dstId}");
        Console.WriteLine($"        {amount,15:N0} QU  [type#{inputType}, {raw.Length}B]");
    }

    Console.Error.WriteLine($"  tick {tick}: {sent} sent, {skipped} deduped");
}

static (string host, int? port) ParseHost(string arg)
{
    var colon = arg.LastIndexOf(':');
    if (colon < 0) return (arg, null);
    return (arg[..colon], int.Parse(arg[(colon + 1)..]));
}


sealed class RateLimiter
{
    private readonly int _maxPerSec;
    private readonly Queue<long> _timestamps = new();
    public RateLimiter(int maxPerSec) { _maxPerSec = maxPerSec; }

    public async Task WaitAsync(CancellationToken ct)
    {
        var nowTicks = Environment.TickCount64;
        while (_timestamps.Count > 0 && nowTicks - _timestamps.Peek() >= 1000)
            _timestamps.Dequeue();
        if (_timestamps.Count >= _maxPerSec)
        {
            var oldest = _timestamps.Peek();
            var sleep = (int)(1000 - (nowTicks - oldest));
            if (sleep > 0) await Task.Delay(sleep, ct);
            while (_timestamps.Count > 0 && Environment.TickCount64 - _timestamps.Peek() >= 1000)
                _timestamps.Dequeue();
        }
        _timestamps.Enqueue(Environment.TickCount64);
    }
}

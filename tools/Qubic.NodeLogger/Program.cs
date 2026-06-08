using System.Text.Json;
using Qubic.Core.Entities;
using Qubic.Network;

if (args.Length < 3 || args.Contains("--help") || args.Contains("-h"))
{
    Console.Error.WriteLine("""
        qubic-node-logger — receive event logs from a Qubic node

        usage:
          qubic-node-logger <host[:port]> <passcode> <mode> [args]  [--json PATH] [--raw PATH] [--summary]

        passcode is the node-operator's 32-byte logReaderPasscodes, accepted as:
          64 hex chars                 e.g. 0xdeadbeef… (whitespace / ':' / ',' OK)
          four u64 parts joined by '-' e.g. 0-0-0-0  or  0x1234-0xabcd-0-0
                                       (decimal or 0x-prefixed hex, mirrors the C++
                                        unsigned long long passcode[4] layout —
                                        each part is serialised little-endian)

        modes:
          ranges <tick>            REQUEST_ALL_LOG_ID_RANGES_FROM_TX (type 50)
                                   → log-id ranges for every tx slot in the tick.

          range <tick> <txId>      REQUEST_LOG_ID_RANGE_FROM_TX (type 48)
                                   → log-id range for one tx slot.

          log <fromId> <toId>      REQUEST_LOG (type 44)
                                   → fetches and parses log entries in
                                     [fromId, toId] (inclusive). Shows a list +
                                     counts by type. Empty result when the node
                                     rejects the request (wrong passcode, out of
                                     range, or too large for one packet).

        --json PATH writes the parsed entries to a JSON file (log mode only).
        --raw PATH writes the raw response payload bytes to a file (all modes):
                     ranges → 65,632 B RespondAllLogIdRangesFromTick
                     range  → 16 B     RespondLogIdRangeFromTx
                     log    → variable RespondLog (concatenated entries)
                     File is not written when the node sends EndResponse (no data).
        --summary (log mode only) suppresses the per-entry listing and aggregates
                     counts by event type across the full range. Auto-chunks the
                     fetch — a single RequestLog response is capped to ~1 MB, so
                     for big ranges we issue multiple requests advancing from the
                     highest logId we've seen, accumulating type counts until the
                     range is covered. --json with --summary writes the aggregated
                     stats (not per-entry); --raw is ignored.

        examples:
          qubic-node-logger 1.2.3.4 0xdeadbeef... ranges 54658297
          qubic-node-logger 1.2.3.4 0-0-0-0 range 54658297 0
          qubic-node-logger 1.2.3.4 0x1234-0xabcd-0-0 log 1000 1100 --json out.json
          qubic-node-logger 1.2.3.4 0xdeadbeef... ranges 54658297 --raw ranges.bin
          qubic-node-logger 1.2.3.4 0xdeadbeef... log 1000 1100 --raw log.bin --json log.json
          qubic-node-logger 1.2.3.4 0xdeadbeef... log 0 100000000 --summary --json sum.json
        """);
    return args.Length < 3 ? 1 : 0;
}

var (host, hostPort) = ParseHost(args[0]);
var port = hostPort ?? 21841;
var passcode = ParsePasscode(args[1]);
var mode = args[2].ToLowerInvariant();

string? jsonPath = null;
string? rawPath = null;
bool summaryMode = false;
for (var i = 3; i < args.Length; i++)
{
    if (args[i] == "--json" && i + 1 < args.Length) { jsonPath = args[++i]; }
    else if (args[i] == "--raw" && i + 1 < args.Length) { rawPath = args[++i]; }
    else if (args[i] == "--summary") { summaryMode = true; }
}

await using var node = new QubicNodeClient(host, port);
Console.Error.WriteLine($"connecting to {host}:{port}…");
await node.ConnectAsync();
Console.Error.WriteLine($"connected (passcode 0x{Convert.ToHexString(passcode)[..8].ToLowerInvariant()}…)");

switch (mode)
{
    case "ranges":
    {
        var tick = uint.Parse(args[3]);
        var (result, raw) = await node.GetAllLogIdRangesFromTickWithRawAsync(passcode, tick);
        PrintAllRanges(tick, result);
        if (rawPath is not null) WriteRaw(rawPath, raw);
        break;
    }
    case "range":
    {
        var tick = uint.Parse(args[3]);
        var txId = uint.Parse(args[4]);
        var (result, raw) = await node.GetLogIdRangeFromTxWithRawAsync(passcode, tick, txId);
        PrintSingleRange(tick, txId, result);
        if (rawPath is not null) WriteRaw(rawPath, raw);
        break;
    }
    case "log":
    {
        var fromId = ulong.Parse(args[3]);
        var toId = ulong.Parse(args[4]);
        if (summaryMode)
        {
            if (rawPath is not null)
                Console.Error.WriteLine("warning: --raw ignored with --summary (multiple responses)");
            await RunSummary(node, passcode, fromId, toId, jsonPath);
        }
        else
        {
            var (entries, raw) = await node.GetLogsWithRawAsync(passcode, fromId, toId);
            PrintLogs(fromId, toId, entries);
            if (jsonPath is not null) WriteJson(jsonPath, entries);
            if (rawPath is not null) WriteRaw(rawPath, raw);
        }
        break;
    }
    default:
        Console.Error.WriteLine($"unknown mode: {mode}");
        return 1;
}

return 0;

static (string host, int? port) ParseHost(string arg)
{
    var colon = arg.LastIndexOf(':');
    if (colon < 0) return (arg, null);
    return (arg[..colon], int.Parse(arg[(colon + 1)..]));
}

static byte[] ParsePasscode(string spec)
{
    var s = spec.Trim();

    // Four-part form: u64-u64-u64-u64. Mirrors the C++ unsigned long long passcode[4]
    // layout — each part is serialised little-endian, giving 32 bytes total.
    if (s.Contains('-'))
    {
        var parts = s.Split('-');
        if (parts.Length != 4)
            throw new ArgumentException($"passcode '-' form expects 4 parts (u64-u64-u64-u64), got {parts.Length}");

        var bytes = new byte[32];
        for (var i = 0; i < 4; i++)
        {
            var p = parts[i].Trim();
            ulong value = p.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                ? Convert.ToUInt64(p[2..], 16)
                : ulong.Parse(p);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(
                bytes.AsSpan(i * 8, 8), value);
        }
        return bytes;
    }

    // Hex blob form: 64 chars (32 bytes).
    if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s[2..];
    s = s.Replace(" ", "").Replace(":", "").Replace(",", "");
    if (s.Length != 64)
        throw new ArgumentException($"passcode must be 64 hex chars (32 bytes) or four u64 parts joined by '-', got {s.Length} hex chars");
    return Convert.FromHexString(s);
}

static void PrintAllRanges(uint tick, TickLogIdRanges? result)
{
    Console.WriteLine();
    Console.WriteLine($"── all log-id ranges for tick {tick} ───────────────────────────────");
    if (result is null)
    {
        Console.WriteLine("  node returned EndResponse — wrong passcode or tick out of window");
        return;
    }

    var withLogs = 0;
    var notYet = 0;
    var totalEntries = 0L;
    for (var slot = 0; slot < result.Ranges.Count; slot++)
    {
        var r = result.Ranges[slot];
        if (r.TickNotYetLogged) { notYet++; continue; }
        if (r.NoLogs) continue;
        withLogs++;
        totalEntries += r.Length;
    }

    Console.WriteLine($"  slots:         {result.Ranges.Count} (4096 tx + 6 special)");
    Console.WriteLine($"  with logs:     {withLogs}");
    Console.WriteLine($"  total entries: {totalEntries}");
    if (notYet > 0)
        Console.WriteLine($"  not yet logged: {notYet} (tick > lastUpdatedTick)");

    if (withLogs == 0) return;

    Console.WriteLine($"  populated slots (up to 20 shown):");
    var shown = 0;
    for (var slot = 0; slot < result.Ranges.Count && shown < 20; slot++)
    {
        var r = result.Ranges[slot];
        if (r.NoLogs || r.TickNotYetLogged) continue;
        var label = slot < 4096 ? $"tx#{slot,4}" : $"special#{slot - 4096}";
        Console.WriteLine($"    {label}  fromLogId={r.FromLogId,12}  length={r.Length,5}");
        shown++;
    }
    if (withLogs > shown)
        Console.WriteLine($"    … {withLogs - shown} more populated slot(s)");
}

static void PrintSingleRange(uint tick, uint txId, LogIdRange? r)
{
    Console.WriteLine();
    Console.WriteLine($"── log-id range for tick {tick} tx#{txId} ───────────────────────────────");
    if (r is null)
    {
        Console.WriteLine("  node returned EndResponse — wrong passcode or tick out of window");
        return;
    }
    if (r.TickNotYetLogged)
    {
        Console.WriteLine("  tick not yet logged (tick > lastUpdatedTick)");
        return;
    }
    if (r.NoLogs)
    {
        Console.WriteLine("  no logs for this slot");
        return;
    }
    Console.WriteLine($"  fromLogId: {r.FromLogId}");
    Console.WriteLine($"  length:    {r.Length}");
    Console.WriteLine($"  toLogId:   {r.FromLogId + r.Length - 1}");
}

static void PrintLogs(ulong fromId, ulong toId, IReadOnlyList<LogEntry> entries)
{
    Console.WriteLine();
    Console.WriteLine($"── log entries [{fromId}, {toId}] ───────────────────────────────");
    if (entries.Count == 0)
    {
        Console.WriteLine("  0 entries — wrong passcode, out-of-window range, or response too large");
        return;
    }
    Console.WriteLine($"  received: {entries.Count} entries");

    var byType = entries.GroupBy(e => e.MessageType)
        .Select(g => (Type: g.Key, Name: g.First().MessageTypeName, Count: g.Count(),
                      Bytes: g.Sum(e => (long)e.MessageSize)))
        .OrderByDescending(t => t.Count)
        .ToList();
    Console.WriteLine($"  by type:");
    foreach (var t in byType)
        Console.WriteLine($"    {t.Count,5} × type#{t.Type,3} {t.Name,-44} ({t.Bytes,9} bytes total)");

    var ticks = entries.Select(e => e.Tick).Distinct().OrderBy(t => t).ToList();
    Console.WriteLine($"  ticks covered: {ticks.Count} ({(ticks.Count == 0 ? "" : $"{ticks[0]}..{ticks[^1]}")})");

    Console.WriteLine($"  first 10 entries:");
    foreach (var e in entries.Take(10))
        Console.WriteLine($"    logId={e.LogId,10}  tick={e.Tick,10}  epoch={e.Epoch,3}  type#{e.MessageType,3}={e.MessageTypeName,-38} size={e.MessageSize,5}");
    if (entries.Count > 10)
        Console.WriteLine($"    … {entries.Count - 10} more");
}

static async Task RunSummary(QubicNodeClient node, byte[] passcode, ulong fromId, ulong toId, string? jsonPath)
{
    Console.WriteLine();
    Console.WriteLine($"── log summary [{fromId}, {toId}] ───────────────────────────────");

    // Per-type aggregates.
    var counts = new Dictionary<byte, long>();
    var bytes = new Dictionary<byte, long>();
    string TypeName(byte t) => new LogEntry
    {
        Epoch = 0, Tick = 0, MessageType = t, MessageSize = 0,
        LogId = 0, LogDigest = 0, MessageBody = []
    }.MessageTypeName;

    long total = 0;
    long totalBytes = 0;
    uint? minTick = null, maxTick = null;
    ushort? minEpoch = null, maxEpoch = null;
    ulong? firstLogId = null, lastLogId = null;
    var chunks = 0;
    var emptyChunks = 0;

    var current = fromId;
    var started = DateTime.UtcNow;
    while (current <= toId)
    {
        chunks++;
        var entries = await node.GetLogsAsync(passcode, current, toId);
        if (entries.Count == 0)
        {
            emptyChunks++;
            // Empty either means we've reached the end OR a single window failed
            // (passcode wrong, ID out of buffer, response too big). Stop either way.
            break;
        }

        ulong highest = 0;
        foreach (var e in entries)
        {
            counts[e.MessageType] = counts.GetValueOrDefault(e.MessageType) + 1;
            bytes[e.MessageType] = bytes.GetValueOrDefault(e.MessageType) + e.MessageSize;
            total++;
            totalBytes += e.MessageSize;

            if (firstLogId is null) firstLogId = e.LogId;
            lastLogId = e.LogId;
            if (e.LogId > highest) highest = e.LogId;

            if (minTick is null || e.Tick < minTick) minTick = e.Tick;
            if (maxTick is null || e.Tick > maxTick) maxTick = e.Tick;
            if (minEpoch is null || e.Epoch < minEpoch) minEpoch = e.Epoch;
            if (maxEpoch is null || e.Epoch > maxEpoch) maxEpoch = e.Epoch;
        }

        // Inline progress so big sweeps don't look frozen.
        Console.Error.Write($"\r  chunk {chunks}: +{entries.Count,6} entries, total={total,9}, logId={highest}        ");

        if (highest == ulong.MaxValue) break; // overflow guard
        current = highest + 1;
    }
    Console.Error.WriteLine();

    var elapsed = DateTime.UtcNow - started;

    if (total == 0)
    {
        Console.WriteLine("  0 entries — wrong passcode, range out of window, or first window too large");
        return;
    }

    Console.WriteLine($"  total:          {total:N0} entries  ({totalBytes:N0} bytes of payload)");
    Console.WriteLine($"  chunks:         {chunks} request(s), {elapsed.TotalSeconds:0.0}s");
    Console.WriteLine($"  logId range:    {firstLogId} .. {lastLogId}");
    Console.WriteLine($"  tick range:     {minTick} .. {maxTick}");
    Console.WriteLine($"  epoch range:    {minEpoch} .. {maxEpoch}");
    Console.WriteLine();
    Console.WriteLine($"  by type:");
    var rows = counts
        .Select(kv => (Type: kv.Key, Count: kv.Value, Bytes: bytes[kv.Key], Name: TypeName(kv.Key)))
        .OrderByDescending(r => r.Count)
        .ToList();
    foreach (var r in rows)
    {
        var pct = 100.0 * r.Count / total;
        Console.WriteLine($"    {r.Count,9:N0} ({pct,5:0.0}%)  type#{r.Type,3} {r.Name,-44}  ({r.Bytes,12:N0} B)");
    }

    if (jsonPath is not null)
    {
        var dto = new
        {
            requestedRange = new { fromId, toId },
            chunks,
            durationSeconds = elapsed.TotalSeconds,
            totals = new { entries = total, payloadBytes = totalBytes },
            logIdRange = new { first = firstLogId, last = lastLogId },
            tickRange = new { min = minTick, max = maxTick },
            epochRange = new { min = minEpoch, max = maxEpoch },
            byType = rows.Select(r => new
            {
                type = r.Type,
                name = r.Name,
                count = r.Count,
                bytes = r.Bytes,
                pct = Math.Round(100.0 * r.Count / total, 2),
            }).ToArray(),
        };
        File.WriteAllText(jsonPath,
            JsonSerializer.Serialize(dto, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine();
        Console.WriteLine($"  wrote summary JSON to {jsonPath}");
    }
}

static void WriteRaw(string path, byte[]? raw)
{
    if (raw is null)
    {
        Console.WriteLine($"  raw: not written ({path}) — node sent EndResponse, no payload");
        return;
    }
    File.WriteAllBytes(path, raw);
    Console.WriteLine($"  wrote raw payload ({raw.Length:N0} bytes) to {path}");
}

static void WriteJson(string path, IReadOnlyList<LogEntry> entries)
{
    var dto = entries.Select(e => new
    {
        e.LogId,
        e.Epoch,
        e.Tick,
        MessageType = e.MessageType,
        MessageTypeName = e.MessageTypeName,
        e.MessageSize,
        LogDigest = $"0x{e.LogDigest:x16}",
        BodyHex = Convert.ToHexString(e.MessageBody).ToLowerInvariant(),
    });
    var json = JsonSerializer.Serialize(dto, new JsonSerializerOptions { WriteIndented = true });
    File.WriteAllText(path, json);
    Console.WriteLine($"  wrote JSON to {path}");
}

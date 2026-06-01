using System.Text.Json;
using Qubic.Core.Entities;
using Qubic.Network;

if (args.Length < 3 || args.Contains("--help") || args.Contains("-h"))
{
    Console.Error.WriteLine("""
        qubic-node-logger — receive event logs from a Qubic node

        usage:
          qubic-node-logger <host[:port]> <passcode> <mode> [args]  [--json PATH]

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

        examples:
          qubic-node-logger 1.2.3.4 0xdeadbeef... ranges 54658297
          qubic-node-logger 1.2.3.4 0-0-0-0 range 54658297 0
          qubic-node-logger 1.2.3.4 0x1234-0xabcd-0-0 log 1000 1100 --json out.json
        """);
    return args.Length < 3 ? 1 : 0;
}

var (host, hostPort) = ParseHost(args[0]);
var port = hostPort ?? 21841;
var passcode = ParsePasscode(args[1]);
var mode = args[2].ToLowerInvariant();

string? jsonPath = null;
for (var i = 3; i < args.Length; i++)
{
    if (args[i] == "--json" && i + 1 < args.Length) { jsonPath = args[++i]; }
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
        var result = await node.GetAllLogIdRangesFromTickAsync(passcode, tick);
        PrintAllRanges(tick, result);
        break;
    }
    case "range":
    {
        var tick = uint.Parse(args[3]);
        var txId = uint.Parse(args[4]);
        var result = await node.GetLogIdRangeFromTxAsync(passcode, tick, txId);
        PrintSingleRange(tick, txId, result);
        break;
    }
    case "log":
    {
        var fromId = ulong.Parse(args[3]);
        var toId = ulong.Parse(args[4]);
        var entries = await node.GetLogsAsync(passcode, fromId, toId);
        PrintLogs(fromId, toId, entries);
        if (jsonPath is not null) WriteJson(jsonPath, entries);
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

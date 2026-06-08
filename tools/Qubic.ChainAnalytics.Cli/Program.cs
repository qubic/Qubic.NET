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
                                                       [--votes]
                                                       [--replay-tick-tx FLAGS]
                                                       [--dump-dir PATH]
                                                       [--computors]
                                                       [--verify-signature]

        --tx-range filters which transactions to print (the tick summary is always shown):
          0-10     indices 0..10 inclusive
          5        single index 5
          0-       index 0 to the end
          none     hide all transactions

        --votes prints the vote-distribution analytic instead of the tick / tx summary.
        It fetches tick data for X, quorum votes for X, quorum votes for X+1, and
        shows how computor votes are distributed and whether they align.

        --verify-signature fetches the node's current Computors once at startup and
        SchnorrQ-verifies each scanned tick's signature: K12 of the tick-data bytes
        (minus the trailing 64-byte signature) is checked against the public key at
        computors[tickData.ComputorIndex]. Result is shown as a "sig-verified" line
        and included in the JSON dump. Skipped (with a reason) if the epochs don't
        match the fetched Computors. Default-mode only.

        --computors fetches the node's current epoch computor list (676 identities) via
        RequestComputors (type 11) → BroadcastComputors (type 2). The <tick> arg is
        ignored in this mode (the node returns whatever epoch it currently believes
        is active). Pair with --dump-dir to persist the raw signed bytes + JSON.

        --dump-dir PATH writes each processed tick to disk as:
          tick-NNNNNNNNNN-tickdata.bin   raw BroadcastFutureTickData wire bytes
                                         (the canonical signed form)
          tick-NNNNNNNNNN-txs.bin        concatenated raw tx bytes, self-described
                                         (u32 magic | u32 tick | u32 count | per tx: u32 len | bytes)
          tick-NNNNNNNNNN.json           full parsed view: tick-data fields, digests,
                                         all transactions with hashes/identities/payload
        Works with the default tick-summary mode (incl. --range) — not with --votes
        or --replay-tick-tx.

        --replay-tick-tx FLAGS replays a captured RequestTickTransactions verbatim.
        FLAGS is the transaction-flag bitmap, 128 bytes (legacy) or 512 bytes (V2),
        provided as:
          hex-string   1024 hex chars (V2) or 256 (legacy); 0x prefix and whitespace OK
          @path        read raw bytes from the file (or hex if the file is a hex dump)
          all-zero     shortcut for "all bits 0" (request every slot)
        Pair with --dejavu HEX to pin the packet header's dejavu (e.g. 0xcdc636e4) for
        a true bit-for-bit replay; default is random.
        Output: every transaction the node returns, parsed and listed.

        examples:
          qubic-analytics 185.84.224.10 21500000
          qubic-analytics 185.84.224.10 latest
          qubic-analytics 185.84.224.10 latest --range 5
          qubic-analytics 185.84.224.10 52810012 --tx-range 0-10
          qubic-analytics 185.84.224.10 52810012 --tx-range none
          qubic-analytics 185.84.224.10 52810012 --votes
          qubic-analytics 185.84.224.10 54658297 --replay-tick-tx @flags.hex
          qubic-analytics 185.84.224.10 54658297 --replay-tick-tx @flags.hex --dejavu 0xcdc636e4
        """);
    return args.Length < 2 ? 1 : 0;
}

var (host, hostPort) = ParseHost(args[0]);
ushort epoch = QubicConstants.TransactionsPerTickV2Epoch;
int port = hostPort ?? 21841;
uint range = 1;
TxRange txRange = TxRange.All;
bool votesMode = false;
byte[]? replayFlags = null;
uint? replayDejavu = null;
string? dumpDir = null;
bool computorsMode = false;
bool verifySignature = false;

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
        case "--votes":
            votesMode = true;
            break;
        case "--replay-tick-tx" when i + 1 < args.Length:
            replayFlags = ParseReplayFlags(args[++i]);
            break;
        case "--dejavu" when i + 1 < args.Length:
            replayDejavu = ParseHexUInt32(args[++i]);
            break;
        case "--dump-dir" when i + 1 < args.Length:
            dumpDir = args[++i];
            break;
        case "--computors":
            computorsMode = true;
            break;
        case "--verify-signature":
            verifySignature = true;
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

var endTick = startTick + range - 1;

if (computorsMode)
{
    await PrintComputors(node, dumpDir);
}
else if (replayFlags is not null)
{
    await PrintReplay(node, startTick, replayFlags, replayDejavu, txRange);
}
else if (replayDejavu is not null)
{
    Console.Error.WriteLine("--dejavu requires --replay-tick-tx");
    return 1;
}
else if (votesMode)
{
    var voteAnalyzer = new VoteDistributionAnalyzer(node);
    for (var t = startTick; t <= endTick; t++)
    {
        var alignment = await voteAnalyzer.GetVoteAlignmentAsync(t);
        PrintVoteAlignment(alignment);
    }
}
else
{
    Qubic.Core.Entities.Computors? computors = null;
    if (verifySignature)
    {
        Console.Error.WriteLine("fetching computor list for signature verification…");
        computors = await node.GetComputorsAsync();
        if (computors is null)
            Console.Error.WriteLine("warning: node returned no computor set — signatures will not be verified");
        else
            Console.Error.WriteLine($"computors: epoch {computors.Epoch}, {computors.PublicKeys.Length} keys");
    }

    var analyzer = new TickAnalyzer(node, computors: computors);
    await foreach (var summary in analyzer.ScanAsync(startTick, endTick, epoch))
    {
        PrintSummary(summary, txRange);
        if (dumpDir is not null)
        {
            var written = TickSummaryDump.Write(summary, dumpDir);
            Console.WriteLine($"  dumped:      {written.Count} file(s) → {dumpDir}");
            foreach (var p in written)
                Console.WriteLine($"               {Path.GetFileName(p)}");
        }
    }
}

return 0;

static async Task PrintComputors(QubicNodeClient node, string? dumpDir)
{
    var started = DateTime.UtcNow;
    var (computors, raw) = await node.GetComputorsWithRawAsync();
    var elapsed = DateTime.UtcNow - started;

    Console.WriteLine();
    Console.WriteLine($"── computor list ───────────────────────────────");
    if (computors is null || raw is null)
    {
        Console.WriteLine("  node returned EndResponse — no computor set available");
        return;
    }

    var sigPreview = Convert.ToHexString(computors.Signature.AsSpan(0, 16)).ToLowerInvariant();
    Console.WriteLine($"  epoch:       {computors.Epoch}");
    Console.WriteLine($"  count:       {computors.PublicKeys.Length}");
    Console.WriteLine($"  signature:   {sigPreview}…");
    Console.WriteLine($"  fetched in:  {Fmt(elapsed)} ({raw.Length:N0} B)");
    Console.WriteLine($"  computors:");

    var crypt = new Qubic.Crypto.QubicCrypt();
    for (var i = 0; i < computors.PublicKeys.Length; i++)
        Console.WriteLine($"    [{i,3}] {crypt.GetIdentityFromPublicKey(computors.PublicKeys[i])}");

    if (dumpDir is not null)
    {
        Directory.CreateDirectory(dumpDir);
        var stem = $"computors-epoch-{computors.Epoch}";
        var binPath = Path.Combine(dumpDir, $"{stem}.bin");
        var jsonPath = Path.Combine(dumpDir, $"{stem}.json");
        File.WriteAllBytes(binPath, raw);

        var dto = new
        {
            epoch = computors.Epoch,
            count = computors.PublicKeys.Length,
            signatureHex = Convert.ToHexString(computors.Signature).ToLowerInvariant(),
            computors = computors.PublicKeys
                .Select((pk, i) => new
                {
                    index = i,
                    identity = crypt.GetIdentityFromPublicKey(pk),
                    publicKeyHex = Convert.ToHexString(pk).ToLowerInvariant(),
                })
                .ToArray(),
        };
        File.WriteAllText(jsonPath, System.Text.Json.JsonSerializer.Serialize(
            dto, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine();
        Console.WriteLine($"  dumped:      2 file(s) → {dumpDir}");
        Console.WriteLine($"               {Path.GetFileName(binPath)}");
        Console.WriteLine($"               {Path.GetFileName(jsonPath)}");
    }
}

static async Task PrintReplay(QubicNodeClient node, uint tick, byte[] flags, uint? dejavu, TxRange txRange)
{
    Console.WriteLine();
    Console.WriteLine($"── replay RequestTickTransactions tick {tick} ───────────────────────────────");

    var requestedSlots = 0;
    foreach (var b in flags)
        requestedSlots += 8 - System.Numerics.BitOperations.PopCount(b);
    Console.WriteLine($"  flag bitmap: {flags.Length} bytes ({(flags.Length == 512 ? "V2 4096 slots" : "legacy 1024 slots")})");
    Console.WriteLine($"  requested:   {requestedSlots} slot(s)");
    Console.WriteLine($"  dejavu:      {(dejavu is uint d ? $"0x{d:x8} (pinned)" : "random")}");

    var started = DateTime.UtcNow;
    var raw = dejavu is uint dj
        ? await node.ReplayTickTransactionsAsync(tick, flags, dj)
        : await node.ReplayTickTransactionsAsync(tick, flags);
    var elapsed = DateTime.UtcNow - started;
    Console.WriteLine($"  received:    {raw.Count} transaction(s) in {Fmt(elapsed)}");

    if (raw.Count == 0) return;

    var parser = new TickTransactionParser();
    var parsed = raw.Select(r => parser.Parse(r)).ToList();
    var (from, to) = txRange.Resolve(parsed.Count);
    if (from > to)
    {
        Console.WriteLine($"  transactions: (hidden — {parsed.Count} total, --tx-range to show)");
        return;
    }

    var label = (from == 0 && to == parsed.Count - 1)
        ? $"all {parsed.Count}"
        : $"{from}..{to} of {parsed.Count}";
    Console.WriteLine($"  transactions: ({label})");

    for (var i = from; i <= to; i++)
    {
        var tx = parsed[i];
        var kind = tx.Kind switch
        {
            TickTransactionKind.SystemMessage => $"system, type#{tx.InputType}",
            TickTransactionKind.ContractCall => $"contract#{tx.DestinationContractIndex}, proc#{tx.InputType}",
            _ => $"transfer, type#{tx.InputType}",
        };
        var tickMatch = tx.Tick == tick ? "" : $"  (!! tick={tx.Tick})";
        Console.WriteLine($"    [{i}] {tx.Hash}{tickMatch}");
        Console.WriteLine($"        {tx.SourceIdentity} -> {tx.DestinationIdentity}");
        Console.WriteLine($"        {tx.Amount,15:N0} QU  [{kind}, payload {tx.InputSize}B]");
    }
}

static uint ParseHexUInt32(string spec)
{
    var s = spec.Trim();
    if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s[2..];
    if (s.Length == 0 || s.Length > 8)
        throw new ArgumentException($"--dejavu expects 1..8 hex digits, got '{spec}'");
    return Convert.ToUInt32(s, 16);
}

static byte[] ParseReplayFlags(string spec)
{
    if (string.Equals(spec, "all-zero", StringComparison.OrdinalIgnoreCase))
        return new byte[512];

    string source;
    if (spec.StartsWith('@'))
    {
        var path = spec[1..];
        var raw = File.ReadAllBytes(path);
        // If file size already matches a valid flag size, treat as raw bytes;
        // otherwise treat the file content as a hex dump (text).
        if (raw.Length is 128 or 512)
            return raw;
        source = File.ReadAllText(path);
    }
    else
    {
        source = spec;
    }

    // Strip 0x prefix and whitespace, then hex-decode.
    var cleaned = new System.Text.StringBuilder(source.Length);
    var skipPrefix = false;
    foreach (var ch in source)
    {
        if (char.IsWhiteSpace(ch) || ch == ':' || ch == ',') continue;
        if (!skipPrefix && (ch == '0') && cleaned.Length == 0) { skipPrefix = true; continue; }
        if (skipPrefix && (ch == 'x' || ch == 'X')) { skipPrefix = false; continue; }
        if (skipPrefix) { cleaned.Append('0'); skipPrefix = false; }
        cleaned.Append(ch);
    }
    if (skipPrefix) cleaned.Append('0');

    var hex = cleaned.ToString();
    if (hex.Length % 2 != 0)
        throw new ArgumentException($"--replay-tick-tx hex string has odd length: {hex.Length}");
    var bytes = Convert.FromHexString(hex);
    if (bytes.Length is not (128 or 512))
        throw new ArgumentException($"--replay-tick-tx flags must decode to 128 or 512 bytes, got {bytes.Length}");
    return bytes;
}

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
    var slotUsage = s.Transactions.Count == s.UsedTickDataSlotCount
        ? $"{s.Transactions.Count}/{s.UsedTickDataSlotCount} slots"
        : $"{s.Transactions.Count}/{s.UsedTickDataSlotCount} slots  ⚠ {s.UsedTickDataSlotCount - s.Transactions.Count} MISSING";
    Console.WriteLine($"  txs:         {slotUsage}  (system: {s.SystemMessageCount}, contract: {s.ContractCallCount}, transfer: {s.UserTransferCount})");
    Console.WriteLine($"  total QU:    {s.TotalAmount:N0}");
    Console.WriteLine($"  verified:    {(s.DigestsVerified ? "YES" : "NO")}");
    var sigLine = s.SignatureVerified switch
    {
        true => "YES",
        false => $"NO  ({s.SignatureSkipReason ?? "SchnorrQ verify failed"})",
        null => s.SignatureSkipReason is null ? "(not checked — use --verify-signature)" : $"(skipped — {s.SignatureSkipReason})",
    };
    Console.WriteLine($"  sig-verified: {sigLine}");

    if (!s.DigestsVerified && s.MissingSlotIndices.Count > 0)
    {
        var missing = s.MissingSlotIndices;
        var preview = missing.Count > 20
            ? $"{string.Join(", ", missing.Take(20))}, … +{missing.Count - 20} more"
            : string.Join(", ", missing);
        Console.WriteLine($"  missing slots: [{preview}]");
    }
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
            TickTransactionKind.SystemMessage => $"system, type#{tx.InputType}",
            TickTransactionKind.ContractCall => $"contract#{tx.DestinationContractIndex}, proc#{tx.InputType}",
            _ => $"transfer, type#{tx.InputType}",
        };
        Console.WriteLine($"    [{i}] {tx.Hash}");
        Console.WriteLine($"        {tx.SourceIdentity} -> {tx.DestinationIdentity}");
        Console.WriteLine($"        {tx.Amount,15:N0} QU  [{kind}, payload {tx.InputSize}B]");
    }
}

static void PrintVoteAlignment(VoteAlignment a)
{
    Console.WriteLine();
    Console.WriteLine($"── vote alignment for tick {a.Tick} ───────────────────────────────");
    Console.WriteLine($"  tick data:        {(a.TickDataAvailable ? $"present (computor #{a.TickData!.ComputorIndex}, epoch {a.TickData.Epoch})" : "NOT AVAILABLE")}");
    Console.WriteLine($"  votes for X:      {a.VotesForTick.TotalVotes} votes ({a.VotesForTick.DistinctComputors} distinct computors)");
    Console.WriteLine($"  votes for X+1:    {a.VotesForNextTick.TotalVotes} votes ({a.VotesForNextTick.DistinctComputors} distinct computors)");
    Console.WriteLine($"  result persisted: {(a.ResultPersistedIntoNextTick ? "YES" : "no")}");
    Console.WriteLine($"  fully aligned:    {(a.FullyAligned ? "YES" : "no")}");

    PrintVoteTimings(a.Timings);

    Console.WriteLine();
    Console.WriteLine($"  ─── tick {a.VotesForTick.Tick} (X) ─────────────────────────────────");
    PrintVoteSummary(a.VotesForTick);

    Console.WriteLine();
    Console.WriteLine($"  ─── tick {a.VotesForNextTick.Tick} (X+1) ───────────────────────────────");
    PrintVoteSummary(a.VotesForNextTick);
}

static void PrintVoteSummary(QuorumVoteSummary s)
{
    PrintFieldDistribution("    transactionDigest", s.TransactionDigest);
    PrintFieldDistribution("    prevSpectrumDigest", s.PrevSpectrumDigest);
    PrintFieldDistribution("    prevUniverseDigest", s.PrevUniverseDigest);
    PrintFieldDistribution("    prevComputerDigest", s.PrevComputerDigest);
    PrintFieldDistribution("    expectedNextTickTx", s.ExpectedNextTickTransactionDigest);
}

static void PrintFieldDistribution(string label, VoteFieldDistribution d)
{
    var quorumMark = d.QuorumReached ? "✓" : "✗";
    Console.WriteLine($"{label}: {d.Distribution.Count} distinct values, dominant {d.DominantCount}/{d.TotalVotes} {quorumMark} (quorum {Qubic.Core.QubicConstants.Quorum})");
    var topN = Math.Min(d.Distribution.Count, 5);
    for (var i = 0; i < topN; i++)
    {
        var (hex, count) = d.Distribution[i];
        var pct = d.TotalVotes == 0 ? 0.0 : 100.0 * count / d.TotalVotes;
        Console.WriteLine($"        {count,4} ({pct,5:0.0}%)  {hex}");
    }
    if (d.Distribution.Count > topN)
        Console.WriteLine($"        … {d.Distribution.Count - topN} more value(s) below");
}

static void PrintVoteTimings(VoteAlignmentTimings t)
{
    Console.WriteLine($"  timings:          total {Fmt(t.TotalDuration)}  ({t.StartedAt:HH:mm:ss.fff} → {t.FinishedAt:HH:mm:ss.fff})");
    Console.WriteLine($"                    tick data        {Fmt(t.TickDataFetch.Duration)}");
    Console.WriteLine($"                    votes for X      {Fmt(t.VotesForTickFetch.Duration)}");
    Console.WriteLine($"                    votes for X+1    {Fmt(t.VotesForNextTickFetch.Duration)}");
    Console.WriteLine($"                    distribution     {Fmt(t.DistributionCompute.Duration)}");
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

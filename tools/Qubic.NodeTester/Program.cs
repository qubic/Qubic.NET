using System.Buffers.Binary;
using System.Net.Sockets;
using Qubic.Core;
using Qubic.Core.Entities;
using Qubic.Network;
using Qubic.NodeTester;
using Qubic.Serialization;

int reconnects = 0;

if (args.Length < 1 || args.Contains("--help") || args.Contains("-h"))
{
    Console.Error.WriteLine("""
        qubic-node-tester — direct-TCP peer test suite

        usage:
          qubic-node-tester <host[:port]> [--listen SECONDS]

        runs a battery of tests against the peer:
          1) TCP connectivity
          2) Handshake (ExchangePublicPeers)
          3) Receive broadcast messages (default 5s passive listen)
          4) RequestCurrentTickInfo
          5) RequestSystemInfo
          6) RequestTickData (latest signed tick)
          7) RequestTickTransactions (latest signed tick)
          8) RequestQuorumTick (latest signed tick)

        examples:
          qubic-node-tester 152.53.254.158
          qubic-node-tester 185.84.224.10:21841 --listen 10
        """);
    return args.Length < 1 ? 1 : 0;
}

var (host, hostPort) = ParseHost(args[0]);
var port = hostPort ?? 21841;
var listenSeconds = 5;

for (var i = 1; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--listen" when i + 1 < args.Length:
            listenSeconds = int.Parse(args[++i]);
            break;
        default:
            Console.Error.WriteLine($"unknown arg: {args[i]}");
            return 1;
    }
}

Console.WriteLine($"qubic-node-tester — direct-TCP peer test suite");
Console.WriteLine($"target: {host}:{port}");
Console.WriteLine();

var runner = new TestRunner();
QubicNodeClient? node = null;
CurrentTickInfo? tickInfo = null;

await runner.RunAsync("TCP connect", async () =>
{
    using var tcp = new TcpClient();
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    await tcp.ConnectAsync(host, port, cts.Token);
    return $"connected (local {tcp.Client.LocalEndPoint})";
});

if (runner.Results[^1].Status != TestStatus.Pass)
{
    runner.Skip("Handshake (ExchangePublicPeers)", "TCP connect failed");
    runner.Skip("Broadcast listen", "TCP connect failed");
    runner.Skip("RequestCurrentTickInfo", "TCP connect failed");
    runner.Skip("RequestSystemInfo", "TCP connect failed");
    runner.Skip("RequestTickData", "TCP connect failed");
    runner.Skip("RequestTickTransactions", "TCP connect failed");
    runner.Skip("RequestQuorumTick", "TCP connect failed");
    runner.PrintSummary();
    return runner.ExitCode;
}

// Open the working node client for the remainder of the tests.
node = new QubicNodeClient(host, port);
await node.ConnectAsync();

await runner.RunAsync("Handshake (ExchangePublicPeers)", async () =>
{
    // ExchangePublicPeers is request/response on the same packet type — the node
    // returns its known peers, which is also what flips its `exchangedPublicPeers`
    // flag for our connection.
    var peers = await WithReconnect(() => node.GetPeerListAsync());
    return $"got {peers.Length} peer(s){(peers.Length > 0 ? $": {string.Join(", ", peers.Take(4))}" : "")}";
});

await runner.RunAsync($"Broadcast listen ({listenSeconds}s)", async () =>
{
    var counts = await BroadcastListener.ListenAsync(host, port, TimeSpan.FromSeconds(listenSeconds));
    if (counts.Count == 0)
        throw new InvalidOperationException("no broadcasts received");
    var top = counts.OrderByDescending(kv => kv.Value).Take(4)
        .Select(kv => $"type#{kv.Key}×{kv.Value}");
    var total = counts.Values.Sum();
    return $"{total} packet(s) — {string.Join(", ", top)}";
});

await runner.RunAsync("RequestCurrentTickInfo", async () =>
{
    tickInfo = await WithReconnect(() => node.GetCurrentTickInfoAsync());
    return $"tick={tickInfo.Tick} epoch={tickInfo.Epoch} duration={tickInfo.TickDuration}ms aligned={tickInfo.NumberOfAlignedVotes} misaligned={tickInfo.NumberOfMisalignedVotes}";
});

await runner.RunAsync("RequestSystemInfo", async () =>
{
    var raw = await WithReconnect(() => node.GetSystemInfoRawAsync());
    if (raw.Length < 32)
        throw new InvalidOperationException($"short response: {raw.Length}B");
    var version = BinaryPrimitives.ReadInt16LittleEndian(raw);
    var epoch = BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(2));
    var tick = BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(4));
    var initialTick = BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(8));
    var latestCreated = BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(12));
    var numEntities = BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(24));
    var numTxs = BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(28));
    return $"version={version} epoch={epoch} tick={tick} initial={initialTick} latestCreated={latestCreated} entities={numEntities} txs={numTxs}";
});

uint? probeTick = tickInfo is not null && tickInfo.Tick > 1 ? tickInfo.Tick - 1 : null;
if (probeTick is null)
{
    runner.Skip("RequestTickData", "no current tick info");
    runner.Skip("RequestTickTransactions", "no current tick info");
    runner.Skip("RequestQuorumTick", "no current tick info");
}
else
{
    var probe = probeTick.Value;
    var epoch = tickInfo!.Epoch;

    await runner.RunAsync($"RequestTickData (tick {probe})", async () =>
    {
        var td = await WithReconnect(() => node.GetTickDataAsync(probe));
        if (td is null) return "no tick data on this node";
        var nonEmpty = td.TransactionDigests.Count(d => d.Any(b => b != 0));
        return $"computor #{td.ComputorIndex} epoch={td.Epoch} txDigests={nonEmpty} of {td.TransactionDigests.Length}";
    });

    await runner.RunAsync($"RequestTickTransactions (tick {probe})", async () =>
    {
        var rawTxs = await WithReconnect(() => node.GetTickTransactionsAsync(probe, epoch));
        return $"{rawTxs.Count} transaction(s) returned";
    });

    await runner.RunAsync($"RequestQuorumTick (tick {probe})", async () =>
    {
        var votes = await WithReconnect(() => node.GetQuorumVotesAsync(probe));
        if (votes.Count == 0) return "0 votes (out-of-window or not yet finalised)";
        var quorum = QubicConstants.Quorum;
        var distinct = votes.Select(v => v.ComputorIndex).Distinct().Count();
        return $"{votes.Count} vote(s), {distinct} distinct computor(s), quorum {quorum}";
    });
}

await node.DisposeAsync();
runner.PrintSummary();
if (reconnects > 0)
    Console.WriteLine($"reconnects: {reconnects} (node closed the connection mid-suite)");
return runner.ExitCode;

async Task<T> WithReconnect<T>(Func<Task<T>> action)
{
    try
    {
        return await action();
    }
    catch (Exception ex) when (IsConnectionError(ex))
    {
        reconnects++;
        Console.Write("[reconnecting] ");
        node!.Disconnect();
        await node.ConnectAsync();
        return await action();
    }
}

static bool IsConnectionError(Exception ex) => ex switch
{
    // IOException covers EndOfStreamException; SocketException is separate from IOException.
    IOException => true,
    SocketException => true,
    InvalidOperationException ioe when ioe.Message.Contains("Not connected") => true,
    OperationCanceledException => true, // socket-level cancel from internal timeout
    _ => false,
};

static (string host, int? port) ParseHost(string arg)
{
    var colon = arg.LastIndexOf(':');
    if (colon < 0) return (arg, null);
    return (arg[..colon], int.Parse(arg[(colon + 1)..]));
}

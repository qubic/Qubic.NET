using Qubic.Core.Entities;
using Qubic.Serialization;

namespace Qubic.Network.Tests;

/// <summary>
/// Integration tests for QubicStreamClient against a real Qubic node.
///
/// To run these tests, set the environment variable:
///   QUBIC_NODE_HOST=your-node-ip-or-hostname
///
/// Run with: dotnet test --filter "Category=RealNode"
/// </summary>
[Trait("Category", "RealNode")]
public class QubicStreamClientRealNodeTests
{
    private static readonly string? NodeHost = Environment.GetEnvironmentVariable("QUBIC_NODE_HOST");
    private static readonly int NodePort = int.TryParse(
        Environment.GetEnvironmentVariable("QUBIC_NODE_PORT"), out var port) ? port : 21841;

    private static bool HasNodeConfigured => !string.IsNullOrEmpty(NodeHost);

    // Cache reachability so we don't wait 10s per test when unreachable
    private static bool? _nodeReachable;
    private static readonly SemaphoreSlim _reachabilityLock = new(1, 1);

    private async Task SkipIfNoNode()
    {
        Skip.If(!HasNodeConfigured,
            "Skipping real node test: Set QUBIC_NODE_HOST environment variable to run.");

        await _reachabilityLock.WaitAsync();
        try
        {
            if (_nodeReachable is null)
            {
                try
                {
                    using var probe = new QubicNodeClient(NodeHost!, NodePort, timeoutMs: 5000);
                    await probe.ConnectAsync();
                    _ = await probe.GetCurrentTickInfoAsync();
                    _nodeReachable = true;
                }
                catch
                {
                    _nodeReachable = false;
                }
            }
        }
        finally
        {
            _reachabilityLock.Release();
        }

        Skip.If(_nodeReachable == false,
            $"Skipping: node {NodeHost}:{NodePort} is not reachable.");
    }

    private QubicStreamOptions CreateOptions() => new()
    {
        Nodes = [$"{NodeHost}:{NodePort}"],
        RequestTimeout = TimeSpan.FromSeconds(10),
        HealthCheckInterval = TimeSpan.FromSeconds(300), // don't interfere with tests
        OnConnectionEvent = e => Console.WriteLine($"  [{e.Type}] {e.Message}")
    };

    #region Connection Tests

    [SkippableFact]
    public async Task ConnectAsync_ToRealNode_Succeeds()
    {
        await SkipIfNoNode();

        await using var client = new QubicStreamClient(CreateOptions());
        await client.ConnectAsync();

        Assert.Equal(QubicStreamConnectionState.Connected, client.State);
        Assert.NotNull(client.ActiveNodeEndpoint);

        Console.WriteLine($"Connected to {client.ActiveNodeEndpoint}");
    }

    [SkippableFact]
    public async Task ConnectAndDisconnect_ToRealNode_WorksCorrectly()
    {
        await SkipIfNoNode();

        await using var client = new QubicStreamClient(CreateOptions());
        await client.ConnectAsync();

        Assert.Equal(QubicStreamConnectionState.Connected, client.State);

        await client.DisconnectAsync();

        Assert.Equal(QubicStreamConnectionState.Disconnected, client.State);
    }

    #endregion

    #region Request-Response Tests

    [SkippableFact]
    public async Task GetCurrentTickInfoAsync_FromRealNode_ReturnsValidData()
    {
        await SkipIfNoNode();

        await using var client = new QubicStreamClient(CreateOptions());
        await client.ConnectAsync();

        var tickInfo = await client.GetCurrentTickInfoAsync();

        Assert.True(tickInfo.Tick > 0, "Tick should be greater than 0");
        Assert.True(tickInfo.Epoch > 0, "Epoch should be greater than 0");
        Assert.True(tickInfo.InitialTick > 0, "InitialTick should be greater than 0");
        Assert.True(tickInfo.InitialTick <= tickInfo.Tick, "InitialTick should be <= current Tick");

        Console.WriteLine($"Tick: {tickInfo.Tick}, Epoch: {tickInfo.Epoch}");
        Console.WriteLine($"InitialTick: {tickInfo.InitialTick}, Duration: {tickInfo.TickDuration}ms");
        Console.WriteLine($"Votes: {tickInfo.NumberOfAlignedVotes} aligned, {tickInfo.NumberOfMisalignedVotes} misaligned");
    }

    [SkippableFact]
    public async Task GetBalanceAsync_FromRealNode_ReturnsBalance()
    {
        await SkipIfNoNode();

        var identity = QubicIdentity.FromIdentity(
            "BAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAARMID");

        await using var client = new QubicStreamClient(CreateOptions());
        await client.ConnectAsync();

        var balance = await client.GetBalanceAsync(identity);

        Assert.NotNull(balance);
        Assert.Equal(identity.ToString(), balance.Identity.ToString());

        Console.WriteLine($"Balance: {balance.Amount} QU");
        Console.WriteLine($"Incoming: {balance.IncomingCount}, Outgoing: {balance.OutgoingCount}");
    }

    [SkippableFact]
    public async Task MultipleRequests_OnPersistentConnection_AllSucceed()
    {
        await SkipIfNoNode();

        var identity = QubicIdentity.FromIdentity(
            "BAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAARMID");

        await using var client = new QubicStreamClient(CreateOptions());
        await client.ConnectAsync();

        var tick1 = await client.GetCurrentTickInfoAsync();
        var balance = await client.GetBalanceAsync(identity);
        var tick2 = await client.GetCurrentTickInfoAsync();

        Assert.True(tick1.Tick > 0);
        Assert.True(tick2.Tick >= tick1.Tick);
        Assert.NotNull(balance);

        Console.WriteLine($"3 requests completed. Ticks: {tick1.Tick} -> {tick2.Tick}");
    }

    #endregion

    #region Broadcast Listening Tests

    [SkippableFact]
    public async Task ListenForBroadcasts_FromRealNode_ReceivesPackets()
    {
        await SkipIfNoNode();

        var receivedTypes = new List<byte>();
        var packetCount = 0;

        await using var client = new QubicStreamClient(CreateOptions());

        client.PacketReceived += (_, e) =>
        {
            var count = Interlocked.Increment(ref packetCount);
            lock (receivedTypes)
            {
                if (!receivedTypes.Contains(e.Header.Type))
                    receivedTypes.Add(e.Header.Type);
            }
            Console.WriteLine($"  Packet #{count}: type={e.Header.Type} size={e.Header.PayloadSize}");
        };

        client.BroadcastMessageReceived += (_, e) =>
            Console.WriteLine($"  ** BroadcastMessage: {e.Header.PayloadSize} bytes");

        client.BroadcastTransactionReceived += (_, e) =>
            Console.WriteLine($"  ** BroadcastTransaction: {e.Header.PayloadSize} bytes");

        client.BroadcastTickReceived += (_, e) =>
            Console.WriteLine($"  ** BroadcastTick: {e.Header.PayloadSize} bytes");

        client.BroadcastComputorsReceived += (_, e) =>
            Console.WriteLine($"  ** BroadcastComputors: {e.Header.PayloadSize} bytes");

        await client.ConnectAsync();

        // Also do a request to verify request-response works alongside broadcast listening
        var tickInfo = await client.GetCurrentTickInfoAsync();
        Console.WriteLine($"Current tick: {tickInfo.Tick}");

        // Listen for broadcasts for 10 seconds
        Console.WriteLine("Listening for broadcasts for 10 seconds...");
        await Task.Delay(TimeSpan.FromSeconds(10));

        Console.WriteLine($"\nTotal packets received: {packetCount}");
        Console.WriteLine($"Unique packet types: [{string.Join(", ", receivedTypes)}]");

        // After handshake, we should receive at least the ExchangePublicPeers response
        Assert.True(packetCount > 0, "Should have received at least one packet (ExchangePublicPeers)");
    }

    [SkippableFact]
    public async Task RequestsDuringBroadcasts_FromRealNode_RoutesCorrectly()
    {
        await SkipIfNoNode();

        var broadcastCount = 0;

        await using var client = new QubicStreamClient(CreateOptions());

        client.PacketReceived += (_, _) => Interlocked.Increment(ref broadcastCount);

        await client.ConnectAsync();

        // Wait a bit for broadcasts to start flowing
        await Task.Delay(TimeSpan.FromSeconds(3));

        // Now do several requests — they should complete correctly despite broadcasts
        for (int i = 0; i < 5; i++)
        {
            var tickInfo = await client.GetCurrentTickInfoAsync();
            Assert.True(tickInfo.Tick > 0);
            Console.WriteLine($"Request {i + 1}: tick={tickInfo.Tick} (broadcasts so far: {broadcastCount})");
        }

        Console.WriteLine($"All 5 requests succeeded. Total broadcasts received: {broadcastCount}");
    }

    #endregion
}

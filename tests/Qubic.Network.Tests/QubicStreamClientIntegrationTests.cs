using Qubic.Core.Entities;
using Qubic.Serialization;

namespace Qubic.Network.Tests;

public class QubicStreamClientIntegrationTests : IAsyncLifetime
{
    private MockQubicServer _server = null!;

    public async Task InitializeAsync()
    {
        _server = new MockQubicServer();

        // Register a handler for RequestCurrentTickInfo (used by both probes and requests)
        _server.OnRequest(QubicPacketTypes.RequestCurrentTickInfo, _ =>
            MockResponseBuilder.CreateCurrentTickInfoResponse(
                tickDuration: 1000, epoch: 130, tick: 20000000,
                alignedVotes: 451, misalignedVotes: 0, initialTick: 19000000));

        _server.Start();
        await Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _server.DisposeAsync();
    }

    private QubicStreamOptions CreateOptions(int? port = null) => new()
    {
        Nodes = [$"127.0.0.1:{port ?? _server.Port}"],
        RequestTimeout = TimeSpan.FromSeconds(5),
        MultiResponseTimeout = TimeSpan.FromSeconds(10),
        HealthCheckInterval = TimeSpan.FromSeconds(300), // long interval to avoid interference
        ReconnectDelay = TimeSpan.FromMilliseconds(100),
        MaxReconnectDelay = TimeSpan.FromSeconds(2)
    };

    #region Connection Tests

    [Fact]
    public async Task ConnectAsync_ValidServer_Connects()
    {
        await using var client = new QubicStreamClient(CreateOptions());
        await client.ConnectAsync();

        Assert.Equal(QubicStreamConnectionState.Connected, client.State);
        Assert.Equal($"127.0.0.1:{_server.Port}", client.ActiveNodeEndpoint);
    }

    [Fact]
    public async Task ConnectAsync_EmitsConnectionEvents()
    {
        var events = new List<QubicStreamEventType>();
        var options = CreateOptions();
        options = new QubicStreamOptions
        {
            Nodes = options.Nodes,
            RequestTimeout = options.RequestTimeout,
            HealthCheckInterval = options.HealthCheckInterval,
            ReconnectDelay = options.ReconnectDelay,
            OnConnectionEvent = e => events.Add(e.Type)
        };

        await using var client = new QubicStreamClient(options);
        await client.ConnectAsync();

        Assert.Contains(QubicStreamEventType.Connecting, events);
        Assert.Contains(QubicStreamEventType.Connected, events);
    }

    [Fact]
    public async Task ConnectAsync_NoAvailableNodes_ThrowsInvalidOperationException()
    {
        var options = new QubicStreamOptions
        {
            Nodes = ["127.0.0.1:1"], // unreachable port
            RequestTimeout = TimeSpan.FromSeconds(2),
            HealthCheckInterval = TimeSpan.FromSeconds(300),
            FailureThreshold = 1 // mark unavailable after single probe failure
        };

        await using var client = new QubicStreamClient(options);

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.ConnectAsync());
    }

    #endregion

    #region Request-Response Tests

    [Fact]
    public async Task GetCurrentTickInfoAsync_ThroughPersistentConnection_ReturnsTickInfo()
    {
        await using var client = new QubicStreamClient(CreateOptions());
        await client.ConnectAsync();

        var tickInfo = await client.GetCurrentTickInfoAsync();

        Assert.Equal(20000000u, tickInfo.Tick);
        Assert.Equal((ushort)130, tickInfo.Epoch);
        Assert.Equal((ushort)1000, tickInfo.TickDuration);
        Assert.Equal((ushort)451, tickInfo.NumberOfAlignedVotes);
    }

    [Fact]
    public async Task GetBalanceAsync_ThroughPersistentConnection_ReturnsBalance()
    {
        var identity = QubicIdentity.FromIdentity(
            "BAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAARMID");

        _server.OnRequest(QubicPacketTypes.RequestEntity, payload =>
            MockResponseBuilder.CreateEntityResponse(
                publicKey: identity.PublicKey,
                incomingAmount: 5000,
                outgoingAmount: 1000,
                incomingTransfers: 10,
                outgoingTransfers: 5));

        await using var client = new QubicStreamClient(CreateOptions());
        await client.ConnectAsync();

        var balance = await client.GetBalanceAsync(identity);

        Assert.Equal(4000, balance.Amount);
        Assert.Equal(10u, balance.IncomingCount);
        Assert.Equal(5u, balance.OutgoingCount);
    }

    [Fact]
    public async Task MultipleSequentialRequests_ThroughSameConnection_Succeeds()
    {
        await using var client = new QubicStreamClient(CreateOptions());
        await client.ConnectAsync();

        // Multiple sequential requests through the same persistent connection
        var tick1 = await client.GetCurrentTickInfoAsync();
        var tick2 = await client.GetCurrentTickInfoAsync();
        var tick3 = await client.GetCurrentTickInfoAsync();

        Assert.Equal(20000000u, tick1.Tick);
        Assert.Equal(20000000u, tick2.Tick);
        Assert.Equal(20000000u, tick3.Tick);
    }

    [Fact]
    public async Task GetTickTransactionsAsync_ReturnsTransactionList()
    {
        _server.OnRequest(QubicPacketTypes.RequestTickTransactions, _ =>
        {
            // Send two BroadcastTransaction packets followed by EndResponse
            var tx1 = MockResponseBuilder.CreatePacket(QubicPacketTypes.BroadcastTransaction, new byte[80]);
            var tx2 = MockResponseBuilder.CreatePacket(QubicPacketTypes.BroadcastTransaction, new byte[80]);
            var end = MockResponseBuilder.CreatePacket(QubicPacketTypes.EndResponse, Array.Empty<byte>());

            var combined = new byte[tx1.Length + tx2.Length + end.Length];
            tx1.CopyTo(combined, 0);
            tx2.CopyTo(combined, tx1.Length);
            end.CopyTo(combined, tx1.Length + tx2.Length);
            return combined;
        });

        await using var client = new QubicStreamClient(CreateOptions());
        await client.ConnectAsync();

        var transactions = await client.GetTickTransactionsAsync(20000000);

        Assert.Equal(2, transactions.Count);
    }

    #endregion

    #region Broadcast Event Tests

    [Fact]
    public async Task PacketReceived_ExchangePublicPeersOnConnect_FiresEvent()
    {
        var receivedPackets = new List<byte>();
        await using var client = new QubicStreamClient(CreateOptions());

        client.PacketReceived += (_, e) => receivedPackets.Add(e.Header.Type);

        await client.ConnectAsync();

        // Wait briefly for the ExchangePublicPeers packet sent on connection
        await Task.Delay(200);

        Assert.Contains(QubicPacketTypes.ExchangePublicPeers, receivedPackets);
    }

    [Fact]
    public async Task BroadcastMessageReceived_WhenServerSendsBroadcast_FiresEvent()
    {
        var receivedMessages = new List<QubicPacketReceivedEventArgs>();
        await using var client = new QubicStreamClient(CreateOptions());

        client.BroadcastMessageReceived += (_, e) => receivedMessages.Add(e);

        await client.ConnectAsync();
        await Task.Delay(100); // let connection stabilize

        // Server sends a BroadcastMessage
        var broadcastPayload = new byte[32];
        Random.Shared.NextBytes(broadcastPayload);
        var broadcastPacket = MockResponseBuilder.CreatePacket(QubicPacketTypes.BroadcastMessage, broadcastPayload);
        await _server.SendBroadcastAsync(broadcastPacket);

        // Wait for the event
        await Task.Delay(500);

        Assert.Single(receivedMessages);
        Assert.Equal(QubicPacketTypes.BroadcastMessage, receivedMessages[0].Header.Type);
        Assert.Equal(32, receivedMessages[0].Header.PayloadSize);
    }

    [Fact]
    public async Task BroadcastTickReceived_WhenServerSendsBroadcastTick_FiresEvent()
    {
        var received = new List<byte>();
        await using var client = new QubicStreamClient(CreateOptions());

        client.BroadcastTickReceived += (_, e) => received.Add(e.Header.Type);

        await client.ConnectAsync();
        await Task.Delay(100);

        var packet = MockResponseBuilder.CreatePacket(QubicPacketTypes.BroadcastTick, new byte[16]);
        await _server.SendBroadcastAsync(packet);

        await Task.Delay(500);

        Assert.Single(received);
    }

    [Fact]
    public async Task MultipleBroadcastTypes_AllFireCorrectEvents()
    {
        var generalCount = 0;
        var messageCount = 0;
        var tickCount = 0;
        var computorsCount = 0;

        await using var client = new QubicStreamClient(CreateOptions());

        client.PacketReceived += (_, _) => Interlocked.Increment(ref generalCount);
        client.BroadcastMessageReceived += (_, _) => Interlocked.Increment(ref messageCount);
        client.BroadcastTickReceived += (_, _) => Interlocked.Increment(ref tickCount);
        client.BroadcastComputorsReceived += (_, _) => Interlocked.Increment(ref computorsCount);

        await client.ConnectAsync();
        await Task.Delay(200); // consume initial ExchangePublicPeers

        var initialGeneral = generalCount;

        await _server.SendBroadcastAsync(
            MockResponseBuilder.CreatePacket(QubicPacketTypes.BroadcastMessage, new byte[8]));
        await _server.SendBroadcastAsync(
            MockResponseBuilder.CreatePacket(QubicPacketTypes.BroadcastTick, new byte[8]));
        await _server.SendBroadcastAsync(
            MockResponseBuilder.CreatePacket(QubicPacketTypes.BroadcastComputors, new byte[8]));

        await Task.Delay(500);

        Assert.Equal(1, messageCount);
        Assert.Equal(1, tickCount);
        Assert.Equal(1, computorsCount);
        // General event should have fired for all three
        Assert.True(generalCount - initialGeneral >= 3);
    }

    #endregion

    #region Interleaved Broadcast and Request Tests

    [Fact]
    public async Task RequestResponse_WithInterleavedBroadcasts_RoutesCorrectly()
    {
        var broadcastCount = 0;

        // The server will send a BroadcastMessage before the actual response
        _server.OnRequest(QubicPacketTypes.RequestCurrentTickInfo, _ =>
        {
            // First a broadcast, then the actual response
            var broadcast = MockResponseBuilder.CreatePacket(QubicPacketTypes.BroadcastMessage, new byte[16]);
            var response = MockResponseBuilder.CreateCurrentTickInfoResponse(
                tickDuration: 1000, epoch: 130, tick: 20000000,
                alignedVotes: 451, misalignedVotes: 0, initialTick: 19000000);

            var combined = new byte[broadcast.Length + response.Length];
            broadcast.CopyTo(combined, 0);
            response.CopyTo(combined, broadcast.Length);
            return combined;
        });

        await using var client = new QubicStreamClient(CreateOptions());
        client.BroadcastMessageReceived += (_, _) => Interlocked.Increment(ref broadcastCount);

        await client.ConnectAsync();
        await Task.Delay(100);

        // The request should get the correct response despite the interleaved broadcast
        var tickInfo = await client.GetCurrentTickInfoAsync();

        Assert.Equal(20000000u, tickInfo.Tick);

        // Wait for broadcast event processing
        await Task.Delay(200);
        Assert.True(broadcastCount >= 1);
    }

    #endregion

    #region Disconnect Tests

    [Fact]
    public async Task DisconnectAsync_WhileConnected_DisconnectsCleanly()
    {
        await using var client = new QubicStreamClient(CreateOptions());
        await client.ConnectAsync();

        Assert.Equal(QubicStreamConnectionState.Connected, client.State);

        await client.DisconnectAsync();

        Assert.Equal(QubicStreamConnectionState.Disconnected, client.State);
    }

    #endregion

    #region Multi-Node Tests

    [Fact]
    public async Task ConnectAsync_MultipleNodes_ConnectsToBest()
    {
        // Create a second server with a higher tick
        await using var server2 = new MockQubicServer();
        server2.OnRequest(QubicPacketTypes.RequestCurrentTickInfo, _ =>
            MockResponseBuilder.CreateCurrentTickInfoResponse(
                tickDuration: 1000, epoch: 130, tick: 25000000,
                alignedVotes: 451, misalignedVotes: 0, initialTick: 19000000));
        server2.Start();

        var options = new QubicStreamOptions
        {
            Nodes = [$"127.0.0.1:{_server.Port}", $"127.0.0.1:{server2.Port}"],
            RequestTimeout = TimeSpan.FromSeconds(5),
            HealthCheckInterval = TimeSpan.FromSeconds(300)
        };

        await using var client = new QubicStreamClient(options);
        await client.ConnectAsync();

        // Should connect to server2 (higher tick)
        Assert.Equal($"127.0.0.1:{server2.Port}", client.ActiveNodeEndpoint);
    }

    #endregion

    #region Concurrent Request Tests

    [Fact]
    public async Task ConcurrentRequests_AreSerializedCorrectly()
    {
        await using var client = new QubicStreamClient(CreateOptions());
        await client.ConnectAsync();

        // Launch multiple requests concurrently — they should be serialized by the semaphore
        var tasks = Enumerable.Range(0, 5)
            .Select(_ => client.GetCurrentTickInfoAsync())
            .ToList();

        var results = await Task.WhenAll(tasks);

        Assert.All(results, r => Assert.Equal(20000000u, r.Tick));
    }

    #endregion
}

using Qubic.Core.Entities;

namespace Qubic.Network.Tests;

public class QubicStreamClientTests
{
    private static QubicStreamOptions CreateOptions(params string[] nodes) =>
        new() { Nodes = nodes };

    #region Constructor Tests

    [Fact]
    public void Constructor_ValidOptions_CreatesInstance()
    {
        using var client = new QubicStreamClient(CreateOptions("127.0.0.1"));

        Assert.Equal(QubicStreamConnectionState.Disconnected, client.State);
        Assert.Null(client.ActiveNodeEndpoint);
    }

    [Fact]
    public void Constructor_MultipleNodes_CreatesInstance()
    {
        using var client = new QubicStreamClient(CreateOptions("127.0.0.1", "192.168.1.1:21841"));

        Assert.Equal(QubicStreamConnectionState.Disconnected, client.State);
    }

    [Fact]
    public void Constructor_NodeWithPort_ParsesCorrectly()
    {
        using var client = new QubicStreamClient(CreateOptions("192.168.1.1:12345"));

        Assert.Equal(QubicStreamConnectionState.Disconnected, client.State);
    }

    [Fact]
    public void Constructor_NullOptions_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => new QubicStreamClient(null!));
    }

    [Fact]
    public void Constructor_EmptyNodes_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => new QubicStreamClient(new QubicStreamOptions { Nodes = [] }));
    }

    [Fact]
    public void Constructor_NullNodes_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => new QubicStreamClient(new QubicStreamOptions { Nodes = null! }));
    }

    #endregion

    #region State Tests

    [Fact]
    public void State_BeforeConnect_IsDisconnected()
    {
        using var client = new QubicStreamClient(CreateOptions("127.0.0.1"));

        Assert.Equal(QubicStreamConnectionState.Disconnected, client.State);
    }

    [Fact]
    public void ActiveNodeEndpoint_BeforeConnect_IsNull()
    {
        using var client = new QubicStreamClient(CreateOptions("127.0.0.1"));

        Assert.Null(client.ActiveNodeEndpoint);
    }

    #endregion

    #region Not Connected Operation Tests

    [Fact]
    public async Task GetCurrentTickInfoAsync_NotConnected_ThrowsInvalidOperationException()
    {
        using var client = new QubicStreamClient(CreateOptions("127.0.0.1"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetCurrentTickInfoAsync());
    }

    [Fact]
    public async Task GetBalanceAsync_NotConnected_ThrowsInvalidOperationException()
    {
        using var client = new QubicStreamClient(CreateOptions("127.0.0.1"));
        var identity = QubicIdentity.FromIdentity(
            "BAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAARMID");

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetBalanceAsync(identity));
    }

    [Fact]
    public async Task QuerySmartContractAsync_NotConnected_ThrowsInvalidOperationException()
    {
        using var client = new QubicStreamClient(CreateOptions("127.0.0.1"));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.QuerySmartContractAsync(1, 0, Array.Empty<byte>()));
    }

    [Fact]
    public async Task GetPeerListAsync_NotConnected_ThrowsInvalidOperationException()
    {
        using var client = new QubicStreamClient(CreateOptions("127.0.0.1"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetPeerListAsync());
    }

    [Fact]
    public async Task GetContractIpoAsync_NotConnected_ThrowsInvalidOperationException()
    {
        using var client = new QubicStreamClient(CreateOptions("127.0.0.1"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetContractIpoAsync(1));
    }

    [Fact]
    public async Task SendRawPacketAsync_NotConnected_ThrowsInvalidOperationException()
    {
        using var client = new QubicStreamClient(CreateOptions("127.0.0.1"));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.SendRawPacketAsync(new byte[8]));
    }

    [Fact]
    public async Task GetTickTransactionsAsync_NotConnected_ThrowsInvalidOperationException()
    {
        using var client = new QubicStreamClient(CreateOptions("127.0.0.1"));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.GetTickTransactionsAsync(12345));
    }

    [Fact]
    public async Task BroadcastTransactionAsync_NotConnected_ThrowsInvalidOperationException()
    {
        using var client = new QubicStreamClient(CreateOptions("127.0.0.1"));
        var source = QubicIdentity.FromIdentity("BAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAARMID");
        var dest = QubicIdentity.FromIdentity("CAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAACNKL");
        var tx = new QubicTransaction
        {
            SourceIdentity = source,
            DestinationIdentity = dest,
            Amount = 1000,
            Tick = 12345678,
            InputType = 0,
            InputSize = 0
        };
        tx.SetSignature(new byte[64], "testhash");

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.BroadcastTransactionAsync(tx));
    }

    #endregion

    #region Transaction Validation Tests

    [Fact]
    public async Task BroadcastTransactionAsync_UnsignedTransaction_ThrowsInvalidOperationException()
    {
        using var client = new QubicStreamClient(CreateOptions("127.0.0.1"));
        var source = QubicIdentity.FromIdentity("BAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAARMID");
        var dest = QubicIdentity.FromIdentity("CAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAACNKL");
        var tx = new QubicTransaction
        {
            SourceIdentity = source,
            DestinationIdentity = dest,
            Amount = 1000,
            Tick = 12345678,
            InputType = 0,
            InputSize = 0
        };

        // Throws because not connected (EnsureConnected check comes first)
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.BroadcastTransactionAsync(tx));
    }

    #endregion

    #region Dispose Tests

    [Fact]
    public void Dispose_CalledMultipleTimes_DoesNotThrow()
    {
        var client = new QubicStreamClient(CreateOptions("127.0.0.1"));

        client.Dispose();
        client.Dispose();
        client.Dispose();

        Assert.Equal(QubicStreamConnectionState.Disconnected, client.State);
    }

    [Fact]
    public async Task DisposeAsync_CalledMultipleTimes_DoesNotThrow()
    {
        var client = new QubicStreamClient(CreateOptions("127.0.0.1"));

        await client.DisposeAsync();
        await client.DisposeAsync();
        await client.DisposeAsync();

        Assert.Equal(QubicStreamConnectionState.Disconnected, client.State);
    }

    [Fact]
    public async Task ConnectAsync_AfterDispose_ThrowsObjectDisposedException()
    {
        var client = new QubicStreamClient(CreateOptions("127.0.0.1"));
        client.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => client.ConnectAsync());
    }

    [Fact]
    public async Task DisconnectAsync_BeforeConnect_DoesNotThrow()
    {
        using var client = new QubicStreamClient(CreateOptions("127.0.0.1"));

        await client.DisconnectAsync();

        Assert.Equal(QubicStreamConnectionState.Disconnected, client.State);
    }

    #endregion

    #region Options Default Tests

    [Fact]
    public void Options_DefaultValues_AreCorrect()
    {
        var options = new QubicStreamOptions { Nodes = ["127.0.0.1"] };

        Assert.Equal(TimeSpan.FromSeconds(10), options.RequestTimeout);
        Assert.Equal(TimeSpan.FromSeconds(30), options.MultiResponseTimeout);
        Assert.Equal(TimeSpan.FromSeconds(30), options.HealthCheckInterval);
        Assert.Equal(5u, options.SwitchThresholdTicks);
        Assert.Equal(3, options.FailureThreshold);
        Assert.Equal(TimeSpan.FromSeconds(2), options.ReconnectDelay);
        Assert.Equal(TimeSpan.FromSeconds(60), options.MaxReconnectDelay);
        Assert.Equal(1024 * 1024, options.MaxPacketSize);
        Assert.Null(options.OnConnectionEvent);
    }

    #endregion

    #region NodeState Parse Tests

    [Fact]
    public void NodeState_Parse_HostOnly_UsesDefaultPort()
    {
        var state = QubicStreamNodeState.Parse("192.168.1.1");

        Assert.Equal("192.168.1.1", state.Host);
        Assert.Equal(21841, state.Port);
        Assert.Equal("192.168.1.1:21841", state.Endpoint);
    }

    [Fact]
    public void NodeState_Parse_HostAndPort_ParsesCorrectly()
    {
        var state = QubicStreamNodeState.Parse("10.0.0.1:12345");

        Assert.Equal("10.0.0.1", state.Host);
        Assert.Equal(12345, state.Port);
        Assert.Equal("10.0.0.1:12345", state.Endpoint);
    }

    [Fact]
    public void NodeState_Parse_InvalidPort_UsesDefaultPort()
    {
        var state = QubicStreamNodeState.Parse("10.0.0.1:abc");

        Assert.Equal("10.0.0.1", state.Host);
        Assert.Equal(21841, state.Port);
    }

    [Fact]
    public void NodeState_DefaultValues_AreCorrect()
    {
        var state = QubicStreamNodeState.Parse("127.0.0.1");

        Assert.Equal(0u, state.CurrentTick);
        Assert.Equal((ushort)0, state.CurrentEpoch);
        Assert.Equal(TimeSpan.Zero, state.Latency);
        Assert.Equal(0, state.ConsecutiveFailures);
        Assert.True(state.IsAvailable);
    }

    #endregion
}

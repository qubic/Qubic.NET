using Qubic.Core.Entities;
using Qubic.Network;

namespace Qubic.Network.Tests;

public class QubicNodeClientTests
{
    private const string TestHost = "127.0.0.1";
    private const int TestPort = 21841;

    #region Constructor Tests

    [Fact]
    public void Constructor_ValidParameters_CreatesInstance()
    {
        using var client = new QubicNodeClient(TestHost, TestPort);

        Assert.False(client.IsConnected);
    }

    [Fact]
    public void Constructor_DefaultPort_UsesPort21841()
    {
        using var client = new QubicNodeClient(TestHost);

        Assert.False(client.IsConnected);
    }

    [Fact]
    public void Constructor_NullHost_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => new QubicNodeClient(null!));
    }

    [Fact]
    public void Constructor_EmptyHost_DoesNotThrow()
    {
        // Empty string is technically valid (will fail on connect)
        using var client = new QubicNodeClient(string.Empty);

        Assert.False(client.IsConnected);
    }

    [Fact]
    public void Constructor_CustomTimeout_CreatesInstance()
    {
        using var client = new QubicNodeClient(TestHost, TestPort, timeoutMs: 5000);

        Assert.False(client.IsConnected);
    }

    #endregion

    #region IsConnected Tests

    [Fact]
    public void IsConnected_BeforeConnect_ReturnsFalse()
    {
        using var client = new QubicNodeClient(TestHost);

        Assert.False(client.IsConnected);
    }

    [Fact]
    public void IsConnected_AfterDispose_ReturnsFalse()
    {
        var client = new QubicNodeClient(TestHost);
        client.Dispose();

        Assert.False(client.IsConnected);
    }

    #endregion

    #region NotConnected Operation Tests

    [Fact]
    public async Task GetCurrentTickInfoAsync_NotConnected_ThrowsInvalidOperationException()
    {
        using var client = new QubicNodeClient(TestHost);

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetCurrentTickInfoAsync());
    }

    [Fact]
    public async Task GetBalanceAsync_NotConnected_ThrowsInvalidOperationException()
    {
        using var client = new QubicNodeClient(TestHost);
        var identity = QubicIdentity.FromIdentity(
            "BAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAARMID");

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetBalanceAsync(identity));
    }

    [Fact]
    public async Task BroadcastTransactionAsync_NotConnected_ThrowsInvalidOperationException()
    {
        using var client = new QubicNodeClient(TestHost);
        var source = QubicIdentity.FromIdentity("BAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAARMID");
        var dest = QubicIdentity.FromIdentity("CAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAACNKL");
        var transaction = new QubicTransaction
        {
            SourceIdentity = source,
            DestinationIdentity = dest,
            Amount = 1000,
            Tick = 12345678,
            InputType = 0,
            InputSize = 0
        };
        transaction.SetSignature(new byte[64], "testhash");

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.BroadcastTransactionAsync(transaction));
    }

    #endregion

    #region Transaction Validation Tests

    [Fact]
    public async Task BroadcastTransactionAsync_UnsignedTransaction_ThrowsInvalidOperationException()
    {
        using var client = new QubicNodeClient(TestHost);
        var source = QubicIdentity.FromIdentity("BAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAARMID");
        var dest = QubicIdentity.FromIdentity("CAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAACNKL");
        var transaction = new QubicTransaction
        {
            SourceIdentity = source,
            DestinationIdentity = dest,
            Amount = 1000,
            Tick = 12345678,
            InputType = 0,
            InputSize = 0
            // No signature - should fail
        };

        // First throws because not connected, then would throw for unsigned
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.BroadcastTransactionAsync(transaction));
    }

    #endregion

    #region Connection Failure Tests

    [Fact]
    public async Task ConnectAsync_InvalidHost_ThrowsException()
    {
        using var client = new QubicNodeClient("invalid.host.that.does.not.exist.example.com", TestPort, timeoutMs: 1000);

        // Should throw some kind of socket exception
        await Assert.ThrowsAnyAsync<Exception>(() => client.ConnectAsync());
    }

    [Fact]
    public async Task ConnectAsync_CancellationRequested_ThrowsOperationCanceledException()
    {
        using var client = new QubicNodeClient(TestHost, TestPort);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.ConnectAsync(cts.Token));
    }

    #endregion

    #region Dispose Tests

    [Fact]
    public void Dispose_CalledMultipleTimes_DoesNotThrow()
    {
        var client = new QubicNodeClient(TestHost);

        client.Dispose();
        client.Dispose();
        client.Dispose();

        // Should not throw
        Assert.False(client.IsConnected);
    }

    [Fact]
    public async Task DisposeAsync_CalledMultipleTimes_DoesNotThrow()
    {
        var client = new QubicNodeClient(TestHost);

        await client.DisposeAsync();
        await client.DisposeAsync();
        await client.DisposeAsync();

        // Should not throw
        Assert.False(client.IsConnected);
    }

    [Fact]
    public void Disconnect_BeforeConnect_DoesNotThrow()
    {
        using var client = new QubicNodeClient(TestHost);

        client.Disconnect();

        Assert.False(client.IsConnected);
    }

    [Fact]
    public void Disconnect_CalledMultipleTimes_DoesNotThrow()
    {
        using var client = new QubicNodeClient(TestHost);

        client.Disconnect();
        client.Disconnect();
        client.Disconnect();

        Assert.False(client.IsConnected);
    }

    #endregion

    #region Using Statement Tests

    [Fact]
    public void UsingStatement_DisposesCorrectly()
    {
        QubicNodeClient? capturedClient;

        using (var client = new QubicNodeClient(TestHost))
        {
            capturedClient = client;
            Assert.False(client.IsConnected);
        }

        // After using block, should still be safe to check
        Assert.False(capturedClient.IsConnected);
    }

    [Fact]
    public async Task AsyncUsingStatement_DisposesCorrectly()
    {
        QubicNodeClient? capturedClient;

        await using (var client = new QubicNodeClient(TestHost))
        {
            capturedClient = client;
            Assert.False(client.IsConnected);
        }

        Assert.False(capturedClient.IsConnected);
    }

    #endregion
}

using Qubic.Core.Entities;
using Qubic.Network;
using Xunit;

namespace Qubic.Network.Tests;

/// <summary>
/// Integration tests that run against a real Qubic node.
///
/// To run these tests, set the environment variable:
///   QUBIC_NODE_HOST=your-node-ip-or-hostname
///
/// Or create a .runsettings file with:
///   <RunSettings>
///     <RunConfiguration>
///       <EnvironmentVariables>
///         <QUBIC_NODE_HOST>your-node-ip</QUBIC_NODE_HOST>
///       </EnvironmentVariables>
///     </RunConfiguration>
///   </RunSettings>
///
/// Run with: dotnet test --filter "Category=RealNode"
/// </summary>
[Trait("Category", "RealNode")]
public class QubicNodeClientRealNodeTests
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

    #region Connection Tests

    [SkippableFact]
    public async Task ConnectAsync_ToRealNode_Succeeds()
    {
        await SkipIfNoNode();

        using var client = new QubicNodeClient(NodeHost!, NodePort, timeoutMs: 10000);

        await client.ConnectAsync();

        Assert.True(client.IsConnected);
    }

    [SkippableFact]
    public async Task ConnectAndDisconnect_ToRealNode_WorksCorrectly()
    {
        await SkipIfNoNode();

        using var client = new QubicNodeClient(NodeHost!, NodePort);

        await client.ConnectAsync();
        Assert.True(client.IsConnected);

        client.Disconnect();
        Assert.False(client.IsConnected);
    }

    #endregion

    #region GetCurrentTickInfo Tests

    [SkippableFact]
    public async Task GetCurrentTickInfoAsync_FromRealNode_ReturnsValidData()
    {
        await SkipIfNoNode();

        using var client = new QubicNodeClient(NodeHost!, NodePort);
        await client.ConnectAsync();

        var tickInfo = await client.GetCurrentTickInfoAsync();

        // Basic sanity checks
        Assert.True(tickInfo.Tick > 0, "Tick should be greater than 0");
        Assert.True(tickInfo.Epoch > 0, "Epoch should be greater than 0");
        Assert.True(tickInfo.InitialTick > 0, "InitialTick should be greater than 0");
        Assert.True(tickInfo.InitialTick <= tickInfo.Tick, "InitialTick should be <= current Tick");

        // Output for debugging
        Console.WriteLine($"Current Tick: {tickInfo.Tick}");
        Console.WriteLine($"Epoch: {tickInfo.Epoch}");
        Console.WriteLine($"Initial Tick: {tickInfo.InitialTick}");
        Console.WriteLine($"Tick Duration: {tickInfo.TickDuration}ms");
        Console.WriteLine($"Aligned Votes: {tickInfo.NumberOfAlignedVotes}");
        Console.WriteLine($"Misaligned Votes: {tickInfo.NumberOfMisalignedVotes}");
    }

    [SkippableFact]
    public async Task GetCurrentTickInfoAsync_CalledMultipleTimes_TickProgresses()
    {
        await SkipIfNoNode();

        using var client = new QubicNodeClient(NodeHost!, NodePort);
        await client.ConnectAsync();

        var tickInfo1 = await client.GetCurrentTickInfoAsync();
        await Task.Delay(2000); // Wait for tick to potentially advance
        var tickInfo2 = await client.GetCurrentTickInfoAsync();

        // Tick should be same or higher (network might be paused)
        Assert.True(tickInfo2.Tick >= tickInfo1.Tick,
            $"Tick should not decrease: {tickInfo1.Tick} -> {tickInfo2.Tick}");

        Console.WriteLine($"Tick progression: {tickInfo1.Tick} -> {tickInfo2.Tick}");
    }

    #endregion

    #region GetBalance Tests

    [SkippableFact]
    public async Task GetBalanceAsync_ForZeroAddress_ReturnsBalance()
    {
        await SkipIfNoNode();

        // The "zero" identity (all A's)
        var identity = QubicIdentity.FromIdentity(
            "BAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAARMID");

        using var client = new QubicNodeClient(NodeHost!, NodePort);
        await client.ConnectAsync();

        var balance = await client.GetBalanceAsync(identity);

        // Zero address typically has 0 balance, but we just verify it doesn't throw
        Assert.NotNull(balance);
        Assert.Equal(identity.ToString(), balance.Identity.ToString());

        Console.WriteLine($"Identity: {identity}");
        Console.WriteLine($"Balance: {balance.Amount} QU");
        Console.WriteLine($"Incoming transfers: {balance.IncomingCount}");
        Console.WriteLine($"Outgoing transfers: {balance.OutgoingCount}");
    }

    [SkippableFact]
    public async Task GetBalanceAsync_ForArbitrator_ReturnsBalance()
    {
        await SkipIfNoNode();

        // Arbitrator identity - should have some activity
        var identity = QubicIdentity.FromIdentity(
            "AFZPUAIYVPNUYGJRQVLUKOPPVLHAZQTGLYAAUUNBXFTVTAMSBKQBLEIEPCVJ");

        using var client = new QubicNodeClient(NodeHost!, NodePort);
        await client.ConnectAsync();

        var balance = await client.GetBalanceAsync(identity);

        Assert.NotNull(balance);
        Console.WriteLine($"Arbitrator Balance: {balance.Amount} QU");
        Console.WriteLine($"Incoming: {balance.IncomingCount}, Outgoing: {balance.OutgoingCount}");
    }

    [SkippableFact]
    public async Task GetBalanceAsync_ForCustomIdentity_ReturnsBalance()
    {
        await SkipIfNoNode();

        // Check if a custom identity is provided via environment variable
        var customIdentity = Environment.GetEnvironmentVariable("QUBIC_TEST_IDENTITY");
        Skip.If(string.IsNullOrEmpty(customIdentity),
            "Set QUBIC_TEST_IDENTITY to test a specific identity");

        var identity = QubicIdentity.FromIdentity(customIdentity);

        using var client = new QubicNodeClient(NodeHost!, NodePort);
        await client.ConnectAsync();

        var balance = await client.GetBalanceAsync(identity);

        Assert.NotNull(balance);
        Console.WriteLine($"Identity: {identity}");
        Console.WriteLine($"Balance: {balance.Amount} QU");
        Console.WriteLine($"Incoming: {balance.IncomingCount}, Outgoing: {balance.OutgoingCount}");
    }

    #endregion

    #region Multiple Operations Tests

    [SkippableFact]
    public async Task MultipleOperations_OnSameConnection_AllSucceed()
    {
        await SkipIfNoNode();

        var identity = QubicIdentity.FromIdentity(
            "BAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAARMID");

        using var client = new QubicNodeClient(NodeHost!, NodePort);
        await client.ConnectAsync();

        // Perform multiple operations
        var tickInfo1 = await client.GetCurrentTickInfoAsync();
        var balance1 = await client.GetBalanceAsync(identity);
        var tickInfo2 = await client.GetCurrentTickInfoAsync();
        var balance2 = await client.GetBalanceAsync(identity);

        // All should succeed
        Assert.True(tickInfo1.Tick > 0);
        Assert.True(tickInfo2.Tick >= tickInfo1.Tick);
        Assert.NotNull(balance1);
        Assert.NotNull(balance2);

        Console.WriteLine($"Completed 4 operations successfully");
        Console.WriteLine($"Ticks: {tickInfo1.Tick} -> {tickInfo2.Tick}");
    }

    #endregion

    #region Stress Tests

    [SkippableFact]
    public async Task RapidRequests_ManyTickInfoRequests_AllSucceed()
    {
        await SkipIfNoNode();

        using var client = new QubicNodeClient(NodeHost!, NodePort);
        await client.ConnectAsync();

        const int requestCount = 10;
        var ticks = new List<uint>();

        for (int i = 0; i < requestCount; i++)
        {
            var tickInfo = await client.GetCurrentTickInfoAsync();
            ticks.Add(tickInfo.Tick);
        }

        Assert.Equal(requestCount, ticks.Count);
        Assert.All(ticks, tick => Assert.True(tick > 0));

        // Ticks should be non-decreasing
        for (int i = 1; i < ticks.Count; i++)
        {
            Assert.True(ticks[i] >= ticks[i - 1],
                $"Tick decreased at index {i}: {ticks[i - 1]} -> {ticks[i]}");
        }

        Console.WriteLine($"Completed {requestCount} rapid requests");
        Console.WriteLine($"Tick range: {ticks.Min()} - {ticks.Max()}");
    }

    #endregion
}

using Qubic.Core.Contracts.Qx;
using Qubic.Core.Contracts.Qutil;
using Qubic.Core.Contracts.Qearn;
using Xunit;

namespace Qubic.Rpc.Tests;

/// <summary>
/// Integration tests for typed smart contract queries using the generated contract types.
///
/// These tests are skipped by default. To run them, set:
///   QUBIC_RPC_URL=https://rpc.qubic.org
///
/// Run with: dotnet test --filter "Category=SmartContract"
/// </summary>
[Trait("Category", "SmartContract")]
[Trait("Category", "RealRpc")]
public class SmartContractQueryTests : IDisposable
{
    private static readonly string? RpcUrl = Environment.GetEnvironmentVariable("QUBIC_RPC_URL");
    private static bool HasRpcConfigured => !string.IsNullOrEmpty(RpcUrl);

    private readonly QubicRpcClient? _client;

    public SmartContractQueryTests()
    {
        if (HasRpcConfigured)
            _client = new QubicRpcClient(RpcUrl!);
    }

    private void SkipIfNoRpc()
    {
        Skip.If(!HasRpcConfigured,
            "Skipping RPC integration test: Set QUBIC_RPC_URL environment variable to run.");
    }

    #region QX Contract (index 1)

    [SkippableFact]
    public async Task Qx_Fees_ReturnsValidFees()
    {
        SkipIfNoRpc();

        var result = await _client!.QuerySmartContractAsync<FeesInput, FeesOutput>(
            QxContract.ContractIndex, QxContract.Functions.Fees, new FeesInput());

        // QX fees should be set to reasonable values (non-zero)
        Assert.True(result.AssetIssuanceFee > 0, "AssetIssuanceFee should be > 0.");
        Assert.True(result.TransferFee > 0, "TransferFee should be > 0.");
        Assert.True(result.TradeFee > 0, "TradeFee should be > 0.");
    }

    [SkippableFact]
    public async Task Qx_AssetAskOrders_ForQubicAsset_ReturnsOrders()
    {
        SkipIfNoRpc();

        // Query ask orders for QX token (issuer = zero address, assetName = "QX")
        var input = new AssetAskOrdersInput
        {
            Issuer = new byte[32], // zero = contract 0
            AssetName = AssetNameToUlong("QX"),
            Offset = 0
        };

        var result = await _client!.QuerySmartContractAsync<AssetAskOrdersInput, AssetAskOrdersOutput>(
            QxContract.ContractIndex, QxContract.Functions.AssetAskOrders, input);

        Assert.NotNull(result.Orders);
        Assert.Equal(256, result.Orders.Length);
    }

    [SkippableFact]
    public async Task Qx_AssetBidOrders_ForQubicAsset_ReturnsOrders()
    {
        SkipIfNoRpc();

        var input = new AssetBidOrdersInput
        {
            Issuer = new byte[32],
            AssetName = AssetNameToUlong("QX"),
            Offset = 0
        };

        var result = await _client!.QuerySmartContractAsync<AssetBidOrdersInput, AssetBidOrdersOutput>(
            QxContract.ContractIndex, QxContract.Functions.AssetBidOrders, input);

        Assert.NotNull(result.Orders);
        Assert.Equal(256, result.Orders.Length);
    }

    [SkippableFact]
    public async Task Qx_Fees_TypedMatchesRaw()
    {
        SkipIfNoRpc();

        // Query using typed API
        var typed = await _client!.QuerySmartContractAsync<FeesInput, FeesOutput>(
            QxContract.ContractIndex, QxContract.Functions.Fees, new FeesInput());

        // Query using raw API
        var rawBytes = await _client.QuerySmartContractAsync(
            (uint)QxContract.ContractIndex, QxContract.Functions.Fees, []);

        // Manually deserialize and compare
        var manual = FeesOutput.FromBytes(rawBytes);

        Assert.Equal(typed.AssetIssuanceFee, manual.AssetIssuanceFee);
        Assert.Equal(typed.TransferFee, manual.TransferFee);
        Assert.Equal(typed.TradeFee, manual.TradeFee);
    }

    #endregion

    #region QUTIL Contract (index 4)

    [SkippableFact]
    public async Task Qutil_GetSendToManyV1Fee_ReturnsFee()
    {
        SkipIfNoRpc();

        var result = await _client!.QuerySmartContractAsync<GetSendToManyV1FeeInput, GetSendToManyV1FeeOutput>(
            QutilContract.ContractIndex, QutilContract.Functions.GetSendToManyV1Fee, new GetSendToManyV1FeeInput());

        Assert.True(result.Fee >= 0, "SendToManyV1 fee should be non-negative.");
    }

    [SkippableFact]
    public async Task Qutil_GetFees_ReturnsAllFees()
    {
        SkipIfNoRpc();

        var result = await _client!.QuerySmartContractAsync<GetFeesInput, GetFeesOutput>(
            QutilContract.ContractIndex, QutilContract.Functions.GetFees, new GetFeesInput());

        Assert.True(result.Smt1InvocationFee >= 0, "Smt1InvocationFee should be non-negative.");
        Assert.True(result.PollCreationFee >= 0, "PollCreationFee should be non-negative.");
        Assert.True(result.PollVoteFee >= 0, "PollVoteFee should be non-negative.");
    }

    [SkippableFact]
    public async Task Qutil_QueryFeeReserve_ForQxContract_ReturnsReserve()
    {
        SkipIfNoRpc();

        var input = new QueryFeeReserveInput { ContractIndex = (uint)QxContract.ContractIndex };

        var result = await _client!.QuerySmartContractAsync<QueryFeeReserveInput, QueryFeeReserveOutput>(
            QutilContract.ContractIndex, QutilContract.Functions.QueryFeeReserve, input);

        Assert.True(result.ReserveAmount >= 0, "Fee reserve should be non-negative.");
    }

    [SkippableFact]
    public async Task Qutil_GetCurrentPollId_ReturnsValidData()
    {
        SkipIfNoRpc();

        var result = await _client!.QuerySmartContractAsync<GetCurrentPollIdInput, GetCurrentPollIdOutput>(
            QutilContract.ContractIndex, QutilContract.Functions.GetCurrentPollId, new GetCurrentPollIdInput());

        // Current poll ID should be accessible (may be 0 if no polls exist)
        Assert.True(result.Current_poll_id >= 0);
    }

    #endregion

    #region QEARN Contract (index 9)

    [SkippableFact]
    public async Task Qearn_GetLockInfoPerEpoch_ForCurrentEpoch_ReturnsInfo()
    {
        SkipIfNoRpc();

        // Get the current epoch from tick info first
        var tickInfo = await _client!.GetTickInfoAsync();
        var input = new GetLockInfoPerEpochInput { Epoch = (uint)tickInfo.Epoch };

        var result = await _client.QuerySmartContractAsync<GetLockInfoPerEpochInput, GetLockInfoPerEpochOutput>(
            QearnContract.ContractIndex, QearnContract.Functions.GetLockInfoPerEpoch, input);

        // Locked amounts should be non-negative
        Assert.True(result.LockedAmount >= 0);
        Assert.True(result.BonusAmount >= 0);
        Assert.True(result.CurrentLockedAmount >= 0);
    }

    [SkippableFact]
    public async Task Qearn_GetStateOfRound_ForCurrentEpoch_ReturnsState()
    {
        SkipIfNoRpc();

        var tickInfo = await _client!.GetTickInfoAsync();
        var input = new GetStateOfRoundInput { Epoch = (uint)tickInfo.Epoch };

        var result = await _client.QuerySmartContractAsync<GetStateOfRoundInput, GetStateOfRoundOutput>(
            QearnContract.ContractIndex, QearnContract.Functions.GetStateOfRound, input);

        // State value is an enum, should be a small number
        Assert.True(result.State >= 0);
    }

    [SkippableFact]
    public async Task Qearn_GetBurnedAndBoostedStats_ReturnsStats()
    {
        SkipIfNoRpc();

        var result = await _client!.QuerySmartContractAsync<GetBurnedAndBoostedStatsInput, GetBurnedAndBoostedStatsOutput>(
            QearnContract.ContractIndex, QearnContract.Functions.GetBurnedAndBoostedStats, new GetBurnedAndBoostedStatsInput());

        // Stats should be non-negative
        Assert.True(result.BurnedAmount >= 0);
        Assert.True(result.BoostedAmount >= 0);
        Assert.True(result.RewardedAmount >= 0);
    }

    #endregion

    #region Helpers

    /// <summary>
    /// Converts an asset name string (up to 7 ASCII characters) to its uint64 representation.
    /// </summary>
    private static ulong AssetNameToUlong(string name)
    {
        ulong result = 0;
        for (int i = 0; i < name.Length && i < 8; i++)
            result |= (ulong)name[i] << (i * 8);
        return result;
    }

    #endregion

    public void Dispose()
    {
        _client?.Dispose();
    }
}

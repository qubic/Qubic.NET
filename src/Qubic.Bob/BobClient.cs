using System.Net.Http.Json;
using System.Text.Json;
using Qubic.Bob.Models;
using Qubic.Core.Entities;

namespace Qubic.Bob;

/// <summary>
/// JSON-RPC client for the QubicBob API.
/// </summary>
public sealed class BobClient : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly string _rpcEndpoint;
    private readonly JsonSerializerOptions _jsonOptions;
    private int _requestId;
    private int _scQueryNonce = (int)(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() & 0x7FFFFFFF);

    /// <summary>
    /// Creates a new BobClient with the specified base URL.
    /// </summary>
    /// <param name="baseUrl">The base URL of the Bob node (e.g., "http://localhost:40420")</param>
    /// <param name="rpcPath">The RPC endpoint path (default: "/qubic")</param>
    public BobClient(string baseUrl, string rpcPath = "/qubic")
        : this(new HttpClient { BaseAddress = new Uri(baseUrl) }, rpcPath, ownsHttpClient: true)
    {
    }

    public BobClient(HttpClient httpClient, string rpcPath = "/qubic", bool ownsHttpClient = false)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _ownsHttpClient = ownsHttpClient;
        _rpcEndpoint = rpcPath;
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };
    }

    #region Chain Information

    /// <summary>
    /// Gets the chain ID and network name.
    /// </summary>
    public Task<ChainIdResponse> GetChainIdAsync(CancellationToken cancellationToken = default)
        => CallAsync<ChainIdResponse>("qubic_chainId", null, cancellationToken);

    /// <summary>
    /// Gets the client version.
    /// </summary>
    public Task<string> GetClientVersionAsync(CancellationToken cancellationToken = default)
        => CallAsync<string>("qubic_clientVersion", null, cancellationToken);

    /// <summary>
    /// Gets the current sync status.
    /// </summary>
    public Task<SyncStatusResponse> GetSyncingAsync(CancellationToken cancellationToken = default)
        => CallAsync<SyncStatusResponse>("qubic_syncing", null, cancellationToken);

    /// <summary>
    /// Gets the current epoch.
    /// </summary>
    public Task<EpochInfoResponse> GetCurrentEpochAsync(CancellationToken cancellationToken = default)
        => CallAsync<EpochInfoResponse>("qubic_getCurrentEpoch", null, cancellationToken);

    /// <summary>
    /// Gets epoch information for a specific epoch.
    /// </summary>
    public Task<EpochInfoResponse> GetEpochInfoAsync(int epoch, CancellationToken cancellationToken = default)
        => CallAsync<EpochInfoResponse>("qubic_getEpochInfo", new object[] { epoch }, cancellationToken);

    #endregion

    #region Tick Operations

    /// <summary>
    /// Gets the current tick number.
    /// </summary>
    public Task<uint> GetTickNumberAsync(CancellationToken cancellationToken = default)
        => CallAsync<uint>("qubic_getTickNumber", null, cancellationToken);

    /// <summary>
    /// Gets tick data by tick number.
    /// </summary>
    public async Task<QubicTick> GetTickByNumberAsync(uint tickNumber, CancellationToken cancellationToken = default)
    {
        var response = await CallAsync<BobTickResponse>("qubic_getTickByNumber", new object[] { tickNumber }, cancellationToken);
        return MapToQubicTick(response);
    }

    /// <summary>
    /// Gets tick data by tick hash.
    /// </summary>
    public async Task<QubicTick> GetTickByHashAsync(string tickHash, CancellationToken cancellationToken = default)
    {
        var response = await CallAsync<BobTickResponse>("qubic_getTickByHash", new object[] { tickHash }, cancellationToken);
        return MapToQubicTick(response);
    }

    #endregion

    #region Balance & Transfers

    /// <summary>
    /// Gets the QU balance for an identity.
    /// </summary>
    public async Task<QubicBalance> GetBalanceAsync(QubicIdentity identity, CancellationToken cancellationToken = default)
    {
        var response = await CallAsync<BobBalanceResponse>("qubic_getBalance", new object[] { identity.Identity }, cancellationToken);
        return new QubicBalance
        {
            Identity = identity,
            Amount = (long)response.GetBalance()
        };
    }

    /// <summary>
    /// Gets transfer history for an identity.
    /// </summary>
    /// <param name="identity">The identity to query.</param>
    /// <param name="startTick">Optional start tick for filtering.</param>
    /// <param name="endTick">Optional end tick for filtering.</param>
    public async Task<IReadOnlyList<QubicTransfer>> GetTransfersAsync(
        QubicIdentity identity,
        uint? startTick = null,
        uint? endTick = null,
        CancellationToken cancellationToken = default)
    {
        var parameters = new List<object> { identity.Identity };
        if (startTick.HasValue) parameters.Add(startTick.Value);
        if (endTick.HasValue) parameters.Add(endTick.Value);

        var response = await CallAsync<List<BobTransferResponse>>("qubic_getTransfers", parameters.ToArray(), cancellationToken);
        return response.Select(MapToQubicTransfer).ToList();
    }

    /// <summary>
    /// Gets the asset balance for an identity. <paramref name="manageSCIndex"/> must
    /// be the contract index that manages this asset's shares — 0 for issuer-managed
    /// assets, 1 (Qx) for QDOGE and other QX-managed shares, etc. Passing the wrong
    /// index makes the node return -1 for both balances.
    /// </summary>
    /// <remarks>
    /// <see cref="QubicAssetBalance.NumberOfShares"/> is populated from the response's
    /// <c>possessionBalance</c> — that's the amount actually controlled by the owner.
    /// Use <see cref="QubicBackendService.GetAssetBalanceFullAsync"/> /
    /// <see cref="BobWebSocketClient.GetAssetBalanceAsync"/> directly if you need both.
    /// </remarks>
    public async Task<QubicAssetBalance?> GetAssetBalanceAsync(
        QubicIdentity identity,
        QubicAsset asset,
        uint manageSCIndex = 0,
        CancellationToken cancellationToken = default)
    {
        var response = await CallAsync<BobAssetBalanceResponse>(
            "qubic_getAssetBalance",
            new object[] { identity.Identity, asset.Issuer.Identity, asset.Name, manageSCIndex },
            cancellationToken);

        if (response is null || string.IsNullOrEmpty(response.Issuer))
            return null;

        return new QubicAssetBalance
        {
            Asset = new QubicAsset
            {
                Issuer = QubicIdentity.FromIdentity(response.Issuer),
                Name = response.AssetName
            },
            Owner = identity,
            NumberOfShares = response.PossessionBalanceValue
        };
    }

    #endregion

    #region Transactions

    /// <summary>
    /// Gets a transaction by its hash.
    /// </summary>
    public async Task<QubicTransfer?> GetTransactionByHashAsync(string txHash, CancellationToken cancellationToken = default)
    {
        var response = await CallAsync<BobTransactionResponse?>("qubic_getTransactionByHash", new object[] { txHash }, cancellationToken);
        if (response is null)
            return null;

        return new QubicTransfer
        {
            TransactionHash = response.TransactionHash,
            Source = QubicIdentity.FromIdentity(response.SourceId),
            Destination = QubicIdentity.FromIdentity(response.DestId),
            Amount = response.AmountValue,
            Tick = response.Tick
        };
    }

    /// <summary>
    /// Gets a transaction receipt.
    /// </summary>
    public Task<TransactionReceiptResponse?> GetTransactionReceiptAsync(string txHash, CancellationToken cancellationToken = default)
        => CallAsync<TransactionReceiptResponse?>("qubic_getTransactionReceipt", new object[] { txHash }, cancellationToken);

    #endregion

    #region Logs & Smart Contracts

    /// <summary>
    /// Gets logs for a smart contract.
    /// </summary>
    /// <param name="contractIndex">The contract index.</param>
    /// <param name="startLogId">Optional start log ID.</param>
    /// <param name="endLogId">Optional end log ID.</param>
    public Task<List<BobLogEntry>> GetLogsAsync(
        int contractIndex,
        long? startLogId = null,
        long? endLogId = null,
        CancellationToken cancellationToken = default)
    {
        var parameters = new List<object> { contractIndex };
        if (startLogId.HasValue) parameters.Add(startLogId.Value);
        if (endLogId.HasValue) parameters.Add(endLogId.Value);

        return CallAsync<List<BobLogEntry>>("qubic_getLogs", parameters.ToArray(), cancellationToken);
    }

    /// <summary>
    /// Queries a smart contract function. Bob uses an async query model:
    /// the first call enqueues the query, subsequent calls with the same nonce retrieve the result.
    /// </summary>
    /// <param name="contractIndex">Smart contract index (1-based).</param>
    /// <param name="funcNumber">Function number (inputType) to call.</param>
    /// <param name="hexData">Hex-encoded input data (without 0x prefix).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Hex-encoded output data.</returns>
    public async Task<string> QuerySmartContractAsync(int contractIndex, int funcNumber, string hexData, CancellationToken cancellationToken = default)
    {
        var nonce = Interlocked.Increment(ref _scQueryNonce);
        var parameters = new object[]
        {
            new Dictionary<string, object>
            {
                ["nonce"] = nonce,
                ["scIndex"] = contractIndex,
                ["funcNumber"] = funcNumber,
                ["data"] = hexData
            }
        };

        // Poll for up to ~5 seconds (50 attempts x 100ms)
        for (int i = 0; i < 50; i++)
        {
            var result = await CallAsync<ScQueryResponse>("qubic_querySmartContract", parameters, cancellationToken);

            if (result.Error is { } error)
                throw new BobRpcException(-1, error);

            if (result.Data != null)
                return result.Data;

            // Pending - wait and retry with same nonce
            await Task.Delay(100, cancellationToken);
        }

        throw new BobRpcException(-1, "Smart contract query timed out");
    }

    #endregion

    #region Private Methods

    private async Task<T> CallAsync<T>(string method, object? parameters, CancellationToken cancellationToken)
    {
        var request = new JsonRpcRequest
        {
            Id = Interlocked.Increment(ref _requestId),
            Method = method,
            Params = parameters
        };

        var response = await _httpClient.PostAsJsonAsync(_rpcEndpoint, request, _jsonOptions, cancellationToken);
        response.EnsureSuccessStatusCode();

        var rpcResponse = await response.Content.ReadFromJsonAsync<JsonRpcResponse<T>>(_jsonOptions, cancellationToken);

        if (rpcResponse?.Error is not null)
        {
            throw new BobRpcException(rpcResponse.Error.Code, rpcResponse.Error.Message);
        }

        return rpcResponse!.Result!;
    }

    private static QubicTick MapToQubicTick(BobTickResponse response)
    {
        return new QubicTick
        {
            TickNumber = response.TickNumber,
            Epoch = (ushort)response.Epoch,
            Timestamp = response.Timestamp,
            TickLeader = !string.IsNullOrEmpty(response.TickLeader)
                ? QubicIdentity.FromIdentity(response.TickLeader)
                : null
        };
    }

    private static QubicTransfer MapToQubicTransfer(BobTransferResponse response)
    {
        return new QubicTransfer
        {
            TransactionHash = response.TransactionHash,
            Source = QubicIdentity.FromIdentity(response.Source),
            Destination = QubicIdentity.FromIdentity(response.Destination),
            Amount = response.AmountValue,
            Tick = response.Tick,
            Timestamp = response.Timestamp,
            Success = response.Success
        };
    }

    #endregion

    public void Dispose()
    {
        if (_ownsHttpClient)
            _httpClient.Dispose();
    }
}

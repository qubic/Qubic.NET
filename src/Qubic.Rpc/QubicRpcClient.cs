using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Qubic.Core.Entities;
using Qubic.Rpc.Models;

namespace Qubic.Rpc;

/// <summary>
/// HTTP client for the official Qubic APIs:
/// - Query API (/query/v1) — archive/historical data
/// - Live API (/live/v1) — live network state, broadcasting
/// </summary>
public sealed class QubicRpcClient : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly JsonSerializerOptions _jsonOptions;

    public QubicRpcClient(string baseUrl)
        : this(new HttpClient { BaseAddress = new Uri(baseUrl) }, ownsHttpClient: true)
    {
    }

    public QubicRpcClient(HttpClient httpClient, bool ownsHttpClient = false)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _ownsHttpClient = ownsHttpClient;
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };
    }

    #region Live API — Tick Info

    /// <summary>
    /// Gets current tick info from the live network.
    /// GET /live/v1/tick-info
    /// </summary>
    public async Task<CurrentTickInfo> GetTickInfoAsync(CancellationToken cancellationToken = default)
    {
        var response = await _httpClient.GetFromJsonAsync<TickInfoResponse>(
            "/live/v1/tick-info", _jsonOptions, cancellationToken);

        if (response?.TickInfo is null)
            throw new InvalidOperationException("Failed to get tick info.");

        return new CurrentTickInfo
        {
            Tick = response.TickInfo.Tick,
            TickDuration = (ushort)response.TickInfo.Duration,
            Epoch = (ushort)response.TickInfo.Epoch,
            InitialTick = response.TickInfo.InitialTick,
            NumberOfAlignedVotes = 0,
            NumberOfMisalignedVotes = 0
        };
    }

    #endregion

    #region Live API — Balance

    /// <summary>
    /// Gets the balance for an identity from the live network.
    /// GET /live/v1/balances/{id}
    /// </summary>
    public async Task<QubicBalance> GetBalanceAsync(QubicIdentity identity, CancellationToken cancellationToken = default)
    {
        var response = await _httpClient.GetFromJsonAsync<GetBalanceResponse>(
            $"/live/v1/balances/{identity}", _jsonOptions, cancellationToken);

        if (response?.Balance is null)
            throw new InvalidOperationException("Failed to get balance.");

        return new QubicBalance
        {
            Identity = identity,
            Amount = long.Parse(response.Balance.Balance),
            IncomingCount = response.Balance.NumberOfIncomingTransfers,
            OutgoingCount = response.Balance.NumberOfOutgoingTransfers,
            AtTick = response.Balance.ValidForTick
        };
    }

    #endregion

    #region Live API — Broadcast

    /// <summary>
    /// Broadcasts a signed transaction to the network.
    /// POST /live/v1/broadcast-transaction
    /// </summary>
    public async Task<BroadcastResult> BroadcastTransactionAsync(QubicTransaction transaction, CancellationToken cancellationToken = default)
    {
        if (!transaction.IsSigned)
            throw new InvalidOperationException("Transaction must be signed before broadcasting.");

        var encodedTx = Convert.ToBase64String(GetSignedTransactionBytes(transaction));
        var request = new { encodedTransaction = encodedTx };

        var response = await _httpClient.PostAsJsonAsync(
            "/live/v1/broadcast-transaction", request, _jsonOptions, cancellationToken);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<BroadcastTransactionResponse>(
            _jsonOptions, cancellationToken);

        return new BroadcastResult
        {
            TransactionId = result?.TransactionId ?? transaction.TransactionHash ?? string.Empty,
            PeersBroadcasted = result?.PeersBroadcasted ?? 0
        };
    }

    #endregion

    #region Live API — Smart Contract Query

    /// <summary>
    /// Queries a smart contract's state on the live network.
    /// POST /live/v1/querySmartContract
    /// </summary>
    public async Task<byte[]> QuerySmartContractAsync(
        uint contractIndex,
        uint inputType,
        byte[] requestData,
        CancellationToken cancellationToken = default)
    {
        var request = new QuerySmartContractRequest
        {
            ContractIndex = contractIndex,
            InputType = inputType,
            InputSize = (uint)requestData.Length,
            RequestData = Convert.ToBase64String(requestData)
        };

        var response = await _httpClient.PostAsJsonAsync(
            "/live/v1/querySmartContract", request, _jsonOptions, cancellationToken);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<QuerySmartContractResponse>(
            _jsonOptions, cancellationToken);

        return string.IsNullOrEmpty(result?.ResponseData)
            ? Array.Empty<byte>()
            : Convert.FromBase64String(result.ResponseData);
    }

    /// <summary>
    /// Queries a smart contract's state using strongly-typed input/output.
    /// </summary>
    public async Task<TOutput> QuerySmartContractAsync<TInput, TOutput>(
        uint contractIndex,
        uint inputType,
        TInput input,
        CancellationToken cancellationToken = default)
        where TInput : Qubic.Core.Contracts.ISmartContractInput
        where TOutput : Qubic.Core.Contracts.ISmartContractOutput<TOutput>
    {
        var responseData = await QuerySmartContractAsync(
            contractIndex, inputType, input.ToBytes(), cancellationToken);
        return TOutput.FromBytes(responseData);
    }

    #endregion

    #region Live API — Assets

    /// <summary>
    /// Gets assets issued by an identity.
    /// GET /live/v1/assets/{identity}/issued
    /// </summary>
    public async Task<IReadOnlyList<IssuedAssetInfo>> GetIssuedAssetsAsync(
        QubicIdentity identity, CancellationToken cancellationToken = default)
    {
        var response = await _httpClient.GetFromJsonAsync<IssuedAssetsResponse>(
            $"/live/v1/assets/{identity}/issued", _jsonOptions, cancellationToken);

        return response?.IssuedAssets ?? [];
    }

    /// <summary>
    /// Gets assets owned by an identity.
    /// GET /live/v1/assets/{identity}/owned
    /// </summary>
    public async Task<IReadOnlyList<OwnedAssetInfo>> GetOwnedAssetsAsync(
        QubicIdentity identity, CancellationToken cancellationToken = default)
    {
        var response = await _httpClient.GetFromJsonAsync<OwnedAssetsResponse>(
            $"/live/v1/assets/{identity}/owned", _jsonOptions, cancellationToken);

        return response?.OwnedAssets ?? [];
    }

    /// <summary>
    /// Gets assets possessed by an identity.
    /// GET /live/v1/assets/{identity}/possessed
    /// </summary>
    public async Task<IReadOnlyList<PossessedAssetInfo>> GetPossessedAssetsAsync(
        QubicIdentity identity, CancellationToken cancellationToken = default)
    {
        var response = await _httpClient.GetFromJsonAsync<PossessedAssetsResponse>(
            $"/live/v1/assets/{identity}/possessed", _jsonOptions, cancellationToken);

        return response?.PossessedAssets ?? [];
    }

    #endregion

    #region Live API — IPOs

    /// <summary>
    /// Gets active IPOs.
    /// GET /live/v1/ipos/active
    /// </summary>
    public async Task<IReadOnlyList<IpoInfo>> GetActiveIposAsync(CancellationToken cancellationToken = default)
    {
        var response = await _httpClient.GetFromJsonAsync<GetActiveIposResponse>(
            "/live/v1/ipos/active", _jsonOptions, cancellationToken);

        return response?.Ipos ?? [];
    }

    #endregion

    #region Query API — Tick Processing Info

    /// <summary>
    /// Gets the last processed tick from the archive.
    /// GET /query/v1/getLastProcessedTick
    /// </summary>
    public async Task<LastProcessedTick> GetLastProcessedTickAsync(CancellationToken cancellationToken = default)
    {
        var response = await _httpClient.GetFromJsonAsync<LastProcessedTick>(
            "/query/v1/getLastProcessedTick", _jsonOptions, cancellationToken);

        return response ?? throw new InvalidOperationException("Failed to get last processed tick.");
    }

    /// <summary>
    /// Gets all available processed tick intervals.
    /// GET /query/v1/getProcessedTickIntervals
    /// </summary>
    public async Task<IReadOnlyList<ProcessedTickInterval>> GetProcessedTickIntervalsAsync(CancellationToken cancellationToken = default)
    {
        var response = await _httpClient.GetFromJsonAsync<List<ProcessedTickInterval>>(
            "/query/v1/getProcessedTickIntervals", _jsonOptions, cancellationToken);

        return response ?? [];
    }

    #endregion

    #region Query API — Computors

    /// <summary>
    /// Gets computor lists for an epoch from the archive.
    /// POST /query/v1/getComputorListsForEpoch
    /// </summary>
    public async Task<IReadOnlyList<ComputorListInfo>> GetComputorListsForEpochAsync(
        uint epoch, CancellationToken cancellationToken = default)
    {
        var request = new { epoch };
        var response = await _httpClient.PostAsJsonAsync(
            "/query/v1/getComputorListsForEpoch", request, _jsonOptions, cancellationToken);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<ComputorListsResponse>(
            _jsonOptions, cancellationToken);

        return result?.ComputorsLists ?? [];
    }

    #endregion

    #region Query API — Tick Data

    /// <summary>
    /// Gets tick data from the archive.
    /// POST /query/v1/getTickData
    /// </summary>
    public async Task<TickDataInfo?> GetTickDataAsync(uint tickNumber, CancellationToken cancellationToken = default)
    {
        var request = new { tickNumber };
        var response = await _httpClient.PostAsJsonAsync(
            "/query/v1/getTickData", request, _jsonOptions, cancellationToken);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<GetTickDataResponse>(
            _jsonOptions, cancellationToken);

        return result?.TickData;
    }

    #endregion

    #region Query API — Transactions

    /// <summary>
    /// Gets a transaction by hash from the archive.
    /// POST /query/v1/getTransactionByHash
    /// </summary>
    public async Task<TransactionInfo?> GetTransactionByHashAsync(
        string hash, CancellationToken cancellationToken = default)
    {
        var request = new { hash };
        var response = await _httpClient.PostAsJsonAsync(
            "/query/v1/getTransactionByHash", request, _jsonOptions, cancellationToken);

        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            return null;

        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<TransactionInfo>(
            _jsonOptions, cancellationToken);
    }

    /// <summary>
    /// Gets all transactions for a tick from the archive.
    /// POST /query/v1/getTransactionsForTick
    /// </summary>
    public async Task<IReadOnlyList<TransactionInfo>> GetTransactionsForTickAsync(
        uint tickNumber, CancellationToken cancellationToken = default)
    {
        var request = new { tickNumber };
        var response = await _httpClient.PostAsJsonAsync(
            "/query/v1/getTransactionsForTick", request, _jsonOptions, cancellationToken);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<List<TransactionInfo>>(
            _jsonOptions, cancellationToken);

        return result ?? [];
    }

    /// <summary>
    /// Gets transactions for an identity with optional filtering and pagination.
    /// POST /query/v1/getTransactionsForIdentity
    /// </summary>
    public async Task<TransactionsForIdentityResult> GetTransactionsForIdentityAsync(
        TransactionsForIdentityRequest request,
        CancellationToken cancellationToken = default)
    {
        var response = await _httpClient.PostAsJsonAsync(
            "/query/v1/getTransactionsForIdentity", request, _jsonOptions, cancellationToken);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<TransactionsForIdentityResult>(
            _jsonOptions, cancellationToken);

        return result ?? new TransactionsForIdentityResult();
    }

    #endregion

    #region Helpers

    private static byte[] GetSignedTransactionBytes(QubicTransaction transaction)
    {
        var payloadSize = transaction.Payload?.Length ?? 0;
        var totalSize = 32 + 32 + 8 + 4 + 2 + 2 + payloadSize + 64;
        var bytes = new byte[totalSize];
        var offset = 0;

        Array.Copy(transaction.SourceIdentity.PublicKey, 0, bytes, offset, 32);
        offset += 32;

        Array.Copy(transaction.DestinationIdentity.PublicKey, 0, bytes, offset, 32);
        offset += 32;

        BitConverter.TryWriteBytes(bytes.AsSpan(offset), transaction.Amount);
        offset += 8;

        BitConverter.TryWriteBytes(bytes.AsSpan(offset), transaction.Tick);
        offset += 4;

        BitConverter.TryWriteBytes(bytes.AsSpan(offset), transaction.InputType);
        offset += 2;

        BitConverter.TryWriteBytes(bytes.AsSpan(offset), transaction.InputSize);
        offset += 2;

        if (transaction.Payload is not null)
        {
            Array.Copy(transaction.Payload, 0, bytes, offset, payloadSize);
            offset += payloadSize;
        }

        Array.Copy(transaction.Signature!, 0, bytes, offset, 64);

        return bytes;
    }

    #endregion

    public void Dispose()
    {
        if (_ownsHttpClient)
            _httpClient.Dispose();
    }
}

using Qubic.Core.Entities;
using Qubic.Rpc.Models;

namespace Qubic.Rpc.Tests;

public class QubicRpcClientTests
{
    #region Constructor

    [Fact]
    public void Constructor_WithBaseUrl_CreatesInstance()
    {
        using var client = new QubicRpcClient("https://rpc.qubic.org");
        Assert.NotNull(client);
    }

    [Fact]
    public void Constructor_WithHttpClient_CreatesInstance()
    {
        var httpClient = new HttpClient { BaseAddress = new Uri("https://rpc.qubic.org") };
        using var client = new QubicRpcClient(httpClient);
        Assert.NotNull(client);
    }

    [Fact]
    public void Constructor_NullHttpClient_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => new QubicRpcClient((HttpClient)null!));
    }

    #endregion

    #region Live API — GetTickInfo

    [Fact]
    public async Task GetTickInfoAsync_ReturnsTickInfo()
    {
        var handler = new MockHttpMessageHandler();
        handler.OnGet("/live/v1/tick-info", """
        {
            "tickInfo": {
                "tick": 22500000,
                "duration": 4,
                "epoch": 198,
                "initialTick": 22000000
            }
        }
        """);

        using var client = CreateClient(handler);
        var result = await client.GetTickInfoAsync();

        Assert.Equal(22500000u, result.Tick);
        Assert.Equal(4, result.TickDuration);
        Assert.Equal(198, result.Epoch);
        Assert.Equal(22000000u, result.InitialTick);
    }

    [Fact]
    public async Task GetTickInfoAsync_NullResponse_Throws()
    {
        var handler = new MockHttpMessageHandler();
        handler.OnGet("/live/v1/tick-info", "{}");

        using var client = CreateClient(handler);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetTickInfoAsync());
    }

    #endregion

    #region Live API — GetBalance

    [Fact]
    public async Task GetBalanceAsync_ReturnsBalance()
    {
        var identity = QubicIdentity.FromIdentity("BAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAARMID");
        var handler = new MockHttpMessageHandler();
        handler.OnGet($"/live/v1/balances/{identity}", """
        {
            "balance": {
                "id": "BAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAARMID",
                "balance": "5000000",
                "validForTick": 22500000,
                "latestIncomingTransferTick": 22400000,
                "latestOutgoingTransferTick": 22300000,
                "incomingAmount": "10000000",
                "outgoingAmount": "5000000",
                "numberOfIncomingTransfers": 12,
                "numberOfOutgoingTransfers": 7
            }
        }
        """);

        using var client = CreateClient(handler);
        var result = await client.GetBalanceAsync(identity);

        Assert.Equal(5000000L, result.Amount);
        Assert.Equal(12u, result.IncomingCount);
        Assert.Equal(7u, result.OutgoingCount);
        Assert.Equal(22500000u, result.AtTick);
    }

    [Fact]
    public async Task GetBalanceAsync_NullResponse_Throws()
    {
        var identity = QubicIdentity.FromIdentity("BAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAARMID");
        var handler = new MockHttpMessageHandler();
        handler.OnGet($"/live/v1/balances/{identity}", "{}");

        using var client = CreateClient(handler);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetBalanceAsync(identity));
    }

    #endregion

    #region Live API — Broadcast

    [Fact]
    public async Task BroadcastTransactionAsync_UnsignedTransaction_Throws()
    {
        var handler = new MockHttpMessageHandler();
        using var client = CreateClient(handler);

        var tx = new QubicTransaction
        {
            SourceIdentity = QubicIdentity.FromIdentity("BAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAARMID"),
            DestinationIdentity = QubicIdentity.FromIdentity("BAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAARMID"),
            Amount = 1000,
            Tick = 22500000
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.BroadcastTransactionAsync(tx));
    }

    #endregion

    #region Live API — Smart Contract Query

    [Fact]
    public async Task QuerySmartContractAsync_ReturnsDecodedBytes()
    {
        var responseBytes = new byte[] { 1, 2, 3, 4, 5 };
        var handler = new MockHttpMessageHandler();
        handler.OnPost("/live/v1/querySmartContract", $$"""
        {
            "responseData": "{{Convert.ToBase64String(responseBytes)}}"
        }
        """);

        using var client = CreateClient(handler);
        var result = await client.QuerySmartContractAsync(1, 0, new byte[] { 0 });

        Assert.Equal(responseBytes, result);
    }

    [Fact]
    public async Task QuerySmartContractAsync_EmptyResponse_ReturnsEmptyArray()
    {
        var handler = new MockHttpMessageHandler();
        handler.OnPost("/live/v1/querySmartContract", """
        {
            "responseData": ""
        }
        """);

        using var client = CreateClient(handler);
        var result = await client.QuerySmartContractAsync(1, 0, new byte[] { 0 });

        Assert.Empty(result);
    }

    [Fact]
    public async Task QuerySmartContractAsync_HttpError_ThrowsWithBody()
    {
        var handler = new MockHttpMessageHandler();
        handler.OnPost("/live/v1/querySmartContract", "upstream rejected the request",
            System.Net.HttpStatusCode.BadRequest);

        using var client = CreateClient(handler);
        var ex = await Assert.ThrowsAsync<QubicRpcException>(
            () => client.QuerySmartContractAsync(1, 0, new byte[] { 0 }));

        Assert.Equal(400, ex.StatusCode);
        Assert.Contains("upstream rejected the request", ex.ResponseBody);
        Assert.Contains("/live/v1/querySmartContract", ex.Message);
    }

    [Fact]
    public async Task QuerySmartContractAsync_ErrorEnvelopeWith200_Throws()
    {
        // core-lite answers failures with HTTP 200 + {"code":..,"message":..}
        var handler = new MockHttpMessageHandler();
        handler.OnPost("/live/v1/querySmartContract", """
        { "code": 3, "message": "Invalid validity" }
        """);

        using var client = CreateClient(handler);
        var ex = await Assert.ThrowsAsync<QubicRpcException>(
            () => client.QuerySmartContractAsync(1, 0, new byte[] { 0 }));

        Assert.Equal(3, ex.NodeCode);
        Assert.Contains("Invalid validity", ex.Message);
    }

    #endregion

    #region Live API — Assets

    [Fact]
    public async Task GetIssuedAssetsAsync_ReturnsAssets()
    {
        var identity = QubicIdentity.FromIdentity("BAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAARMID");
        var handler = new MockHttpMessageHandler();
        handler.OnGet($"/live/v1/assets/{identity}/issued", """
        {
            "issuedAssets": [
                {
                    "data": {
                        "issuerIdentity": "BAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAARMID",
                        "type": 1,
                        "name": "QFT",
                        "numberOfDecimalPlaces": 0,
                        "unitOfMeasurement": [0, 0, 0, 0, 0, 0, 0]
                    },
                    "info": {
                        "tick": 100000,
                        "universeIndex": 42
                    }
                }
            ]
        }
        """);

        using var client = CreateClient(handler);
        var result = await client.GetIssuedAssetsAsync(identity);

        Assert.Single(result);
        Assert.Equal("QFT", result[0].Data.Name);
        Assert.Equal(100000u, result[0].Info.Tick);
    }

    [Fact]
    public async Task GetOwnedAssetsAsync_ReturnsAssets()
    {
        var identity = QubicIdentity.FromIdentity("BAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAARMID");
        var handler = new MockHttpMessageHandler();
        handler.OnGet($"/live/v1/assets/{identity}/owned", """
        {
            "ownedAssets": [
                {
                    "data": {
                        "ownerIdentity": "BAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAARMID",
                        "type": 1,
                        "managingContractIndex": 1,
                        "issuanceIndex": 0,
                        "numberOfUnits": "500"
                    },
                    "info": {
                        "tick": 100000,
                        "universeIndex": 43
                    }
                }
            ]
        }
        """);

        using var client = CreateClient(handler);
        var result = await client.GetOwnedAssetsAsync(identity);

        Assert.Single(result);
        Assert.Equal("500", result[0].Data.NumberOfUnits);
    }

    [Fact]
    public async Task GetPossessedAssetsAsync_ReturnsAssets()
    {
        var identity = QubicIdentity.FromIdentity("BAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAARMID");
        var handler = new MockHttpMessageHandler();
        handler.OnGet($"/live/v1/assets/{identity}/possessed", """
        {
            "possessedAssets": [
                {
                    "data": {
                        "possessorIdentity": "BAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAARMID",
                        "type": 1,
                        "managingContractIndex": 1,
                        "ownershipIndex": 0,
                        "numberOfUnits": "250"
                    },
                    "info": {
                        "tick": 100000,
                        "universeIndex": 44
                    }
                }
            ]
        }
        """);

        using var client = CreateClient(handler);
        var result = await client.GetPossessedAssetsAsync(identity);

        Assert.Single(result);
        Assert.Equal("250", result[0].Data.NumberOfUnits);
    }

    #endregion

    #region Live API — IPOs

    [Fact]
    public async Task GetActiveIposAsync_ReturnsIpos()
    {
        var handler = new MockHttpMessageHandler();
        handler.OnGet("/live/v1/ipos/active", """
        {
            "ipos": [
                {
                    "contractIndex": 5,
                    "assetName": "MYTOKEN"
                }
            ]
        }
        """);

        using var client = CreateClient(handler);
        var result = await client.GetActiveIposAsync();

        Assert.Single(result);
        Assert.Equal(5u, result[0].ContractIndex);
        Assert.Equal("MYTOKEN", result[0].AssetName);
    }

    #endregion

    #region Query API — Last Processed Tick

    [Fact]
    public async Task GetLastProcessedTickAsync_ReturnsData()
    {
        var handler = new MockHttpMessageHandler();
        handler.OnGet("/query/v1/getLastProcessedTick", """
        {
            "tickNumber": 22500000,
            "epoch": 198,
            "intervalInitialTick": 22000000
        }
        """);

        using var client = CreateClient(handler);
        var result = await client.GetLastProcessedTickAsync();

        Assert.Equal(22500000u, result.TickNumber);
        Assert.Equal(198u, result.Epoch);
        Assert.Equal(22000000u, result.IntervalInitialTick);
    }

    #endregion

    #region Query API — Processed Tick Intervals

    [Fact]
    public async Task GetProcessedTickIntervalsAsync_ReturnsIntervals()
    {
        var handler = new MockHttpMessageHandler();
        handler.OnGet("/query/v1/getProcessedTickIntervals", """
        [
            {
                "epoch": 197,
                "firstTick": 21000000,
                "lastTick": 21999999
            },
            {
                "epoch": 198,
                "firstTick": 22000000,
                "lastTick": 22500000
            }
        ]
        """);

        using var client = CreateClient(handler);
        var result = await client.GetProcessedTickIntervalsAsync();

        Assert.Equal(2, result.Count);
        Assert.Equal(197u, result[0].Epoch);
        Assert.Equal(198u, result[1].Epoch);
    }

    #endregion

    #region Query API — Computor Lists

    [Fact]
    public async Task GetComputorListsForEpochAsync_ReturnsLists()
    {
        var handler = new MockHttpMessageHandler();
        handler.OnPost("/query/v1/getComputorListsForEpoch", """
        {
            "computorsLists": [
                {
                    "epoch": 198,
                    "tickNumber": 22000100,
                    "identities": ["AAAA", "BBBB"],
                    "signature": "c2ln"
                }
            ]
        }
        """);

        using var client = CreateClient(handler);
        var result = await client.GetComputorListsForEpochAsync(198);

        Assert.Single(result);
        Assert.Equal(198u, result[0].Epoch);
        Assert.Equal(2, result[0].Identities.Count);
    }

    #endregion

    #region Query API — Tick Data

    [Fact]
    public async Task GetTickDataAsync_ReturnsTickData()
    {
        var handler = new MockHttpMessageHandler();
        handler.OnPost("/query/v1/getTickData", """
        {
            "tickData": {
                "tickNumber": 22500000,
                "epoch": 198,
                "computorIndex": 42,
                "timestamp": "1700000000000",
                "varStruct": "",
                "timeLock": "",
                "transactionHashes": ["txhash1", "txhash2"],
                "contractFees": ["100"],
                "signature": "c2ln"
            }
        }
        """);

        using var client = CreateClient(handler);
        var result = await client.GetTickDataAsync(22500000);

        Assert.NotNull(result);
        Assert.Equal(22500000u, result.TickNumber);
        Assert.Equal(198u, result.Epoch);
        Assert.Equal(42u, result.ComputorIndex);
        Assert.Equal(2, result.TransactionHashes.Count);
    }

    [Fact]
    public async Task GetTickDataAsync_NoData_ReturnsNull()
    {
        var handler = new MockHttpMessageHandler();
        handler.OnPost("/query/v1/getTickData", "{}");

        using var client = CreateClient(handler);
        var result = await client.GetTickDataAsync(1);

        Assert.Null(result);
    }

    #endregion

    #region Query API — Transactions

    [Fact]
    public async Task GetTransactionByHashAsync_ReturnsTransaction()
    {
        var handler = new MockHttpMessageHandler();
        handler.OnPost("/query/v1/getTransactionByHash", """
        {
            "hash": "abcdef123456",
            "source": "BAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAARMID",
            "destination": "BAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAARMID",
            "amount": "1000",
            "tickNumber": 22500000,
            "timestamp": "1700000000000",
            "inputType": 0,
            "inputSize": 0,
            "inputData": "",
            "signature": "c2ln",
            "moneyFlew": true
        }
        """);

        using var client = CreateClient(handler);
        var result = await client.GetTransactionByHashAsync("abcdef123456");

        Assert.NotNull(result);
        Assert.Equal("abcdef123456", result.Hash);
        Assert.Equal("1000", result.Amount);
        Assert.Equal(22500000u, result.TickNumber);
        Assert.True(result.MoneyFlew);
    }

    [Fact]
    public async Task GetTransactionByHashAsync_NotFound_ReturnsNull()
    {
        var handler = new MockHttpMessageHandler();
        handler.OnPost("/query/v1/getTransactionByHash",
            """{"code":5,"message":"transaction not found","details":[]}""",
            System.Net.HttpStatusCode.NotFound);

        using var client = CreateClient(handler);
        var result = await client.GetTransactionByHashAsync("nonexistent");

        Assert.Null(result);
    }

    [Fact]
    public async Task GetTransactionsForTickAsync_ReturnsTransactions()
    {
        var handler = new MockHttpMessageHandler();
        handler.OnPost("/query/v1/getTransactionsForTick", """
        [
            {
                "hash": "tx1",
                "source": "SRC",
                "destination": "DST",
                "amount": "500",
                "tickNumber": 22500000,
                "timestamp": "1700000000000",
                "inputType": 0,
                "inputSize": 0,
                "inputData": "",
                "signature": ""
            },
            {
                "hash": "tx2",
                "source": "SRC2",
                "destination": "DST2",
                "amount": "1500",
                "tickNumber": 22500000,
                "timestamp": "1700000000000",
                "inputType": 0,
                "inputSize": 0,
                "inputData": "",
                "signature": ""
            }
        ]
        """);

        using var client = CreateClient(handler);
        var result = await client.GetTransactionsForTickAsync(22500000);

        Assert.Equal(2, result.Count);
        Assert.Equal("tx1", result[0].Hash);
        Assert.Equal("tx2", result[1].Hash);
    }

    [Fact]
    public async Task GetTransactionsForIdentityAsync_ReturnsFilteredResults()
    {
        var handler = new MockHttpMessageHandler();
        handler.OnPost("/query/v1/getTransactionsForIdentity", """
        {
            "validForTick": 22500000,
            "hits": {
                "total": 100,
                "from": 0,
                "size": 10
            },
            "transactions": [
                {
                    "hash": "tx1",
                    "source": "SRC",
                    "destination": "DST",
                    "amount": "500",
                    "tickNumber": 22400000,
                    "timestamp": "1700000000000",
                    "inputType": 0,
                    "inputSize": 0,
                    "inputData": "",
                    "signature": ""
                }
            ]
        }
        """);

        using var client = CreateClient(handler);
        var result = await client.GetTransactionsForIdentityAsync(new TransactionsForIdentityRequest
        {
            Identity = "BAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAARMID",
            Pagination = new PaginationOptions { Offset = 0, Size = 10 }
        });

        Assert.Equal(22500000u, result.ValidForTick);
        Assert.Equal(100u, result.Hits.Total);
        Assert.Single(result.Transactions);
    }

    #endregion

    #region Helpers

    private static QubicRpcClient CreateClient(MockHttpMessageHandler handler)
    {
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://rpc.qubic.org") };
        return new QubicRpcClient(httpClient, ownsHttpClient: true);
    }

    #endregion
}

using System.Text.Json;
using Qubic.Bob.Models;

namespace Qubic.Bob.Tests;

public class NotificationModelTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    #region TickStreamNotification

    [Fact]
    public void TickStreamNotification_Deserializes()
    {
        var json = """
        {
            "epoch": 197,
            "tick": 22500000,
            "isCatchUp": true,
            "timestamp": "1700000000000",
            "filteredTxs": 5,
            "totalTxs": 12,
            "filteredLogs": 3,
            "totalLogs": 8,
            "transactions": [
                {
                    "hash": "txhash1",
                    "from": "AAAA",
                    "to": "BBBB",
                    "amount": "1000000",
                    "inputType": 0,
                    "inputData": null,
                    "executed": true,
                    "logIdFrom": 100,
                    "logIdLength": 2
                }
            ],
            "logs": [
                {
                    "ok": true,
                    "tick": 22500000,
                    "epoch": 197,
                    "logId": 100,
                    "type": 0,
                    "logTypename": "QU_TRANSFER",
                    "bodySize": 96
                }
            ]
        }
        """;

        var notification = JsonSerializer.Deserialize<TickStreamNotification>(json, JsonOptions);

        Assert.NotNull(notification);
        Assert.Equal(197u, notification.Epoch);
        Assert.Equal(22500000u, notification.Tick);
        Assert.True(notification.IsCatchUp);
        Assert.Equal(5u, notification.TxCountFiltered);
        Assert.Equal(12u, notification.TxCountTotal);
        Assert.Single(notification.Transactions!);
        Assert.Equal("txhash1", notification.Transactions![0].Hash);
        Assert.True(notification.Transactions[0].Executed);
        Assert.Single(notification.Logs!);
        Assert.Equal(0, notification.Logs![0].LogType);
        Assert.Equal("QU_TRANSFER", notification.Logs[0].LogTypeName);
    }

    [Fact]
    public void TickStreamTransaction_GetAmount_StringValue()
    {
        var json = """{"hash":"tx","from":"A","to":"B","amount":"1500000","inputType":0,"executed":true,"logIdFrom":0,"logIdLength":0}""";

        var tx = JsonSerializer.Deserialize<TickStreamTransaction>(json, JsonOptions);

        Assert.NotNull(tx);
        Assert.Equal(1500000, tx.GetAmount());
    }

    [Fact]
    public void TickStreamTransaction_GetAmount_NumberValue()
    {
        var json = """{"hash":"tx","from":"A","to":"B","amount":2500000,"inputType":0,"executed":true,"logIdFrom":0,"logIdLength":0}""";

        var tx = JsonSerializer.Deserialize<TickStreamTransaction>(json, JsonOptions);

        Assert.NotNull(tx);
        Assert.Equal(2500000, tx.GetAmount());
    }

    #endregion

    #region TransferNotification

    [Fact]
    public void TransferNotification_Deserializes()
    {
        var json = """
        {
            "isCatchUp": false,
            "tick": 22500100,
            "epoch": 197,
            "logId": 12345,
            "txHash": "sometxhash",
            "body": {
                "from": "SOURCEIDENTITY",
                "to": "DESTIDENTITY",
                "amount": "500000"
            }
        }
        """;

        var notification = JsonSerializer.Deserialize<TransferNotification>(json, JsonOptions);

        Assert.NotNull(notification);
        Assert.False(notification.IsCatchUp);
        Assert.Equal(22500100u, notification.Tick);
        Assert.Equal(197u, notification.Epoch);
        Assert.Equal(12345, notification.LogId);
        Assert.Equal("sometxhash", notification.TxHash);
        Assert.NotNull(notification.Body);
        Assert.Equal("SOURCEIDENTITY", notification.Body.From);
        Assert.Equal("DESTIDENTITY", notification.Body.To);
        Assert.Equal(500000, notification.Body.GetAmount());
    }

    [Fact]
    public void TransferBody_GetAmount_StringValue()
    {
        var json = """{"from":"A","to":"B","amount":"999"}""";
        var body = JsonSerializer.Deserialize<TransferBody>(json, JsonOptions);

        Assert.Equal(999, body!.GetAmount());
    }

    [Fact]
    public void TransferBody_GetAmount_NumberValue()
    {
        var json = """{"from":"A","to":"B","amount":777}""";
        var body = JsonSerializer.Deserialize<TransferBody>(json, JsonOptions);

        Assert.Equal(777, body!.GetAmount());
    }

    #endregion

    #region LogNotification

    [Fact]
    public void LogNotification_Deserializes()
    {
        var json = """
        {
            "isCatchUp": true,
            "ok": true,
            "tick": 22500050,
            "epoch": 197,
            "logId": 67890,
            "type": 0,
            "logTypename": "QU_TRANSFER",
            "logDigest": "abc123",
            "bodySize": 96,
            "timestamp": "1700000000000",
            "txHash": "sometx",
            "body": {
                "from": "SRC",
                "to": "DST",
                "amount": 1000
            }
        }
        """;

        var notification = JsonSerializer.Deserialize<LogNotification>(json, JsonOptions);

        Assert.NotNull(notification);
        Assert.True(notification.IsCatchUp);
        Assert.True(notification.Ok);
        Assert.Equal(22500050u, notification.Tick);
        Assert.Equal(67890, notification.LogId);
        Assert.Equal(0, notification.LogType);
        Assert.Equal("QU_TRANSFER", notification.LogTypeName);
        Assert.NotNull(notification.Body);
    }

    #endregion

    #region NewTickNotification

    [Fact]
    public void NewTickNotification_Deserializes()
    {
        var json = """
        {
            "tickNumber": 43217287,
            "epoch": 198,
            "timestamp": 1769774708,
            "timestampISO": "2026-01-30T12:05:08Z",
            "computorIndex": 607,
            "transactionCount": 5,
            "hasNoTickData": false,
            "isSkipped": false
        }
        """;

        var notification = JsonSerializer.Deserialize<NewTickNotification>(json, JsonOptions);

        Assert.NotNull(notification);
        Assert.Equal(43217287u, notification.TickNumber);
        Assert.Equal(198u, notification.Epoch);
        Assert.Equal(1769774708L, notification.Timestamp);
        Assert.Equal("2026-01-30T12:05:08Z", notification.TimestampISO);
        Assert.Equal(607, notification.ComputorIndex);
        Assert.Equal(5, notification.TransactionCount);
    }

    #endregion

    #region JsonRpcMessage

    [Fact]
    public void JsonRpcMessage_Response_IsDetected()
    {
        var json = """
        {
            "jsonrpc": "2.0",
            "id": 1,
            "result": "qubic_sub_0"
        }
        """;

        var msg = JsonSerializer.Deserialize<JsonRpcMessage>(json, JsonOptions);

        Assert.NotNull(msg);
        Assert.True(msg.IsResponse);
        Assert.False(msg.IsNotification);
        Assert.Equal(1, msg.Id);
    }

    [Fact]
    public void JsonRpcMessage_Notification_IsDetected()
    {
        var json = """
        {
            "jsonrpc": "2.0",
            "method": "qubic_subscription",
            "params": {
                "subscription": "qubic_sub_0",
                "result": {
                    "tickNumber": 22500000,
                    "epoch": 197
                }
            }
        }
        """;

        var msg = JsonSerializer.Deserialize<JsonRpcMessage>(json, JsonOptions);

        Assert.NotNull(msg);
        Assert.False(msg.IsResponse);
        Assert.True(msg.IsNotification);
        Assert.Equal("qubic_subscription", msg.Method);
        Assert.Equal("qubic_sub_0", msg.Params!.Subscription);
        Assert.NotNull(msg.Params.Result);
    }

    [Fact]
    public void JsonRpcMessage_Error_HasErrorDetails()
    {
        var json = """
        {
            "jsonrpc": "2.0",
            "id": 2,
            "error": {
                "code": -32001,
                "message": "Resource not found"
            }
        }
        """;

        var msg = JsonSerializer.Deserialize<JsonRpcMessage>(json, JsonOptions);

        Assert.NotNull(msg);
        Assert.True(msg.IsResponse);
        Assert.NotNull(msg.Error);
        Assert.Equal(-32001, msg.Error.Code);
        Assert.Equal("Resource not found", msg.Error.Message);
    }

    #endregion

    #region Subscription Options Serialization

    [Fact]
    public void TxFilter_Serializes_OmitsNulls()
    {
        var filter = new TxFilter { From = "IDENTITY", MinAmount = 1000 };
        var json = JsonSerializer.Serialize(filter, JsonOptions);

        Assert.Contains("\"from\"", json);
        Assert.Contains("\"minAmount\"", json);
        Assert.DoesNotContain("\"to\"", json);
        Assert.DoesNotContain("\"inputType\"", json);
    }

    [Fact]
    public void LogFilter_Serializes_OmitsNulls()
    {
        var filter = new LogFilter { ScIndex = 1 };
        var json = JsonSerializer.Serialize(filter, JsonOptions);

        Assert.Contains("\"scIndex\"", json);
        Assert.DoesNotContain("\"logType\"", json);
    }

    #endregion
}

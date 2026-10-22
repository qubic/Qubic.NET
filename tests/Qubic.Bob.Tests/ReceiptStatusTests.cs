using System.Text.Json;
using Qubic.Bob.Models;
using Xunit;

namespace Qubic.Bob.Tests;

/// <summary>
/// A "pending" receipt (Bob still log-verifying the tick), or one with no status field,
/// must not read as a failure — otherwise the wallet marks successful transactions "Failed".
/// </summary>
public class ReceiptStatusTests
{
    private static TransactionReceiptResponse Parse(string json) =>
        JsonSerializer.Deserialize<TransactionReceiptResponse>(json)!;

    [Fact]
    public void Pending_IsNotFailure()
    {
        var r = Parse("""{"txHash":"abc","status":"pending","tick":100}""");
        Assert.True(r.IsPending);
        Assert.Null(r.Executed);
        Assert.Equal("pending", r.StatusString);
    }

    [Fact]
    public void MissingStatus_IsPendingNotFailure()
    {
        var r = Parse("""{"txHash":"abc","tick":100}""");
        Assert.True(r.IsPending);
        Assert.Null(r.Executed);
    }

    [Theory]
    [InlineData("\"success\"", true)]
    [InlineData("true", true)]
    [InlineData("\"failed\"", false)]
    [InlineData("false", false)]
    public void DefiniteVerdicts_Resolve(string statusJson, bool executed)
    {
        var r = Parse($$"""{"txHash":"abc","status":{{statusJson}},"tick":100}""");
        Assert.False(r.IsPending);
        Assert.Equal(executed, r.Executed);
    }
}

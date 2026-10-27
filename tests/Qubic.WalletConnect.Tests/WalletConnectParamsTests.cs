using Newtonsoft.Json;
using Reown.Core.Network.Models;

namespace Qubic.WalletConnect.Tests;

public class WalletConnectParamsTests
{
    [Theory]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("{\"nonce\":\"123\"}")]
    public void RequestAccounts_AcceptsAnyParamsShape(string json)
    {
        var p = JsonConvert.DeserializeObject<RequestAccountsParams>(json);
        Assert.NotNull(p);
    }

    [Fact]
    public void Numbers_AsStringsOrNumbers_AndWrappedInArray()
    {
        var a = JsonConvert.DeserializeObject<SendQubicParams>("{\"from\":\"A\",\"amount\":\"1500\"}")!;
        var b = JsonConvert.DeserializeObject<SendQubicParams>("[{\"from\":\"A\",\"amount\":1500}]")!;
        Assert.Equal(1500, a.Long("amount"));
        Assert.Equal(1500, b.Long("amount"));
        Assert.Equal("A", b.Str("from"));
        Assert.Null(a.Long("tick"));
        Assert.Throws<FormatException>(() => JsonConvert.DeserializeObject<SendQubicParams>("{\"amount\":\"x\"}")!.Long("amount"));
    }

    [Fact]
    public void MethodNames_MatchOfficialSpec()
    {
        Assert.Equal("qubic_requestAccounts", RpcMethodAttribute.MethodForType<RequestAccountsParams>());
        Assert.Equal("qubic_sendQubic", RpcMethodAttribute.MethodForType<SendQubicParams>());
        Assert.Equal("qubic_signTransaction", RpcMethodAttribute.MethodForType<SignTransactionParams>());
        Assert.Equal("qubic_sendTransaction", RpcMethodAttribute.MethodForType<SendTransactionParams>());
        Assert.Equal("qubic_sign", RpcMethodAttribute.MethodForType<SignParams>());
        Assert.Equal("qubic_sendAsset", RpcMethodAttribute.MethodForType<SendAssetParams>());
        Assert.Equal(QubicWalletConnectService.Methods.Length, 6);
    }
}

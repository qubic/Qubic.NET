using Qubic.Core.Auth;
using Qubic.Core.Payments;
using Qubic.Crypto;

namespace Qubic.Core.Tests;

public class SignInMessageTests
{
    private const string Seed = "qubicorelitebyfeiyuivqubicqubicqubicqubicqubicquicqubic";
    private static readonly IQubicCrypt Crypt = new QubicCrypt();
    private static string Identity => Crypt.GetIdentityFromPublicKey(Crypt.GetPublicKey(Seed));

    private static SignInMessage Sample(DateTimeOffset now) => new()
    {
        Domain = "fund.qubic.li",
        Identity = Identity,
        Statement = "Sign in to the QLI Community Fund.",
        Uri = "https://fund.qubic.li/login",
        Network = "qubic:mainnet",
        Nonce = "abcDEF123456",
        IssuedAt = now,
        ExpirationTime = now.AddMinutes(10),
        Resources = ["https://fund.qubic.li/proposals/42"]
    };

    [Fact]
    public void ToString_Parse_RoundTrips()
    {
        var now = new DateTimeOffset(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);
        var msg = Sample(now);
        var parsed = SignInMessage.Parse(msg.ToString());
        Assert.Equal(msg.ToString(), parsed.ToString());
        Assert.Equal("fund.qubic.li", parsed.Domain);
        Assert.Equal("Sign in to the QLI Community Fund.", parsed.Statement);
        Assert.Single(parsed.Resources);
        Assert.True(SignInMessage.IsSignInMessage(msg.ToString()));
    }

    [Fact]
    public void Sign_Verify_WithRawConvention()
    {
        var now = DateTimeOffset.UtcNow;
        var msg = Sample(now);
        var sig = msg.Sign(Seed);
        Assert.True(msg.Verify(sig));
        // Compatible with plain raw-message verification of the same text.
        Assert.True(Crypt.VerifyRaw(Crypt.GetPublicKey(Seed), System.Text.Encoding.UTF8.GetBytes(msg.ToString()), sig));
        // Tampering breaks it.
        Assert.False((msg with { Nonce = "zzzzzzzzzzzz" }).Verify(sig));
    }

    [Fact]
    public void Verify_AcceptsWalletConnectConvention()
    {
        var msg = Sample(DateTimeOffset.UtcNow);
        var wc = QubicMessageSigning.SignWalletConnect(Seed, msg.ToString());
        Assert.True(msg.Verify(wc.Signature));
        Assert.True(QubicMessageSigning.VerifyWalletConnect(Identity, msg.ToString(), wc.Signature));
        // signedData = prefixed message || signature, digest = K12(signedData)
        var prefixed = System.Text.Encoding.UTF8.GetBytes(QubicMessageSigning.WalletConnectPrefix + msg);
        Assert.Equal(prefixed.Concat(wc.Signature), wc.SignedData);
        Assert.Equal(Crypt.KangarooTwelve(wc.SignedData), wc.Digest);
        // The two conventions are not interchangeable for plain verification.
        Assert.False(Crypt.VerifyRaw(Crypt.GetPublicKey(Seed), System.Text.Encoding.UTF8.GetBytes(msg.ToString()), wc.Signature));
    }

    [Fact]
    public void Validate_FlagsExpiryDomainAndNetwork()
    {
        var now = new DateTimeOffset(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);
        var msg = Sample(now);
        Assert.Empty(msg.Validate(now.AddMinutes(1), "fund.qubic.li", "qubic:mainnet"));
        Assert.Contains(msg.Validate(now.AddMinutes(11)), p => p.Contains("expired"));
        Assert.Contains(msg.Validate(now, "evil.example"), p => p.Contains("does not match"));
        Assert.Contains(msg.Validate(now, expectedNetwork: "qubic:testnet"), p => p.Contains("qubic:mainnet"));
    }

    [Fact]
    public void Parse_RejectsPlainText()
    {
        Assert.Throws<FormatException>(() => SignInMessage.Parse("hello world"));
    }
}

public class QubicPaymentRequestTests
{
    private const string Dest = "CFBMEMZOIDEXQAUXYYSZIURADQLAPWPMNJXQSNVQZAHYVOPYUKKJBJUCTVJL";

    [Fact]
    public void RoundTrip_AllFields()
    {
        var req = new QubicPaymentRequest
        {
            Destination = Dest,
            Amount = 1500,
            AssetName = "QXTEST",
            AssetIssuer = Dest,
            Tick = 123456,
            InputType = 5,
            Input = [0x0a, 0x0b],
            Network = "testnet",
            Label = "Shop",
            Message = "Invoice 42 & more"
        };
        var parsed = QubicPaymentRequest.Parse(req.ToUri());
        Assert.Equal(req.ToUri(), parsed.ToUri());
        Assert.Equal("Invoice 42 & more", parsed.Message);
        Assert.Equal("qubic:testnet", parsed.ChainId);
        Assert.True(parsed.IsAssetTransfer);
    }

    [Fact]
    public void BareIdentity_Parses()
    {
        var parsed = QubicPaymentRequest.Parse(Dest.ToLowerInvariant());
        Assert.Equal(Dest, parsed.Destination);
        Assert.Null(parsed.Amount);
        Assert.Equal("qubic:mainnet", parsed.ChainId);
    }

    [Theory]
    [InlineData("qubic:NOTANIDENTITY")]
    [InlineData("qubic:" + Dest + "?amount=-5")]
    [InlineData("qubic:" + Dest + "?amount=0")]
    [InlineData("qubic:" + Dest + "?network=devnet")]
    [InlineData("qubic:" + Dest + "?asset=NOISSUER")]
    public void Invalid_Throws(string uri)
    {
        Assert.False(QubicPaymentRequest.TryParse(uri, out _, out var error));
        Assert.NotNull(error);
    }
}

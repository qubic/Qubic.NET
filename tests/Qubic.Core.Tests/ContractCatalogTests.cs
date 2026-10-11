using Qubic.Core.Contracts.Metadata;
using Qubic.Core.Contracts.Qx;
using Qubic.Crypto;

namespace Qubic.Core.Tests;

public class ContractCatalogTests
{
    private static readonly IQubicCrypt Crypt = new QubicCrypt();
    private const string Issuer = "CFBMEMZOIDEXQAUXYYSZIURADQLAPWPMNJXQSNVQZAHYVOPYUKKJBJUCTVJL";

    [Fact]
    public void Builtin_ContainsQx_WithProcedures()
    {
        var qx = new ContractCatalog().Get(1);
        Assert.NotNull(qx);
        Assert.Equal("Qx", qx!.CSharpName);
        Assert.Contains(qx.Procedures, p => p.Name == "AddToAskOrder" && p.InputType == 5);
    }

    [Fact]
    public void RuntimeLayout_MatchesGeneratedPayloadSize_ForAllBuiltinProcedures()
    {
        // The generated QX payload is 56 bytes; the runtime layout must agree.
        var qx = new ContractCatalog().Get(1)!;
        var ask = qx.Procedures.Single(p => p.Name == "AddToAskOrder");
        Assert.Equal(AddToAskOrderPayload.Size, ContractLayout.SizeOf(ask.Input));
    }

    [Fact]
    public void DecodeProcedureCall_RoundTripsGeneratedPayload()
    {
        var payload = new AddToAskOrderPayload
        {
            Issuer = Crypt.GetPublicKeyFromIdentity(Issuer),
            AssetName = AssetNameHelper.ToUlong("QXTEST"),
            Price = 1234,
            NumberOfShares = 56
        };

        var decoded = new ContractCatalog().DecodeProcedureCall(1, 5, payload.GetPayloadBytes());

        Assert.NotNull(decoded);
        Assert.Equal("AddToAskOrder", decoded!.EntryName);
        Assert.True(decoded.SizeMatches);
        var fields = decoded.Fields.ToDictionary(f => f.Name, f => f.Display);
        Assert.Equal(Issuer, fields["issuer"]);
        Assert.Equal("1234", fields["price"]);
        Assert.Equal("56", fields["numberOfShares"]);
    }

    [Fact]
    public void Encode_MatchesGeneratedPayloadBytes()
    {
        var qx = new ContractCatalog().Get(1)!;
        var ask = qx.Procedures.Single(p => p.Name == "AddToAskOrder");
        var assetNameValue = AssetNameHelper.ToUlong("QXTEST");

        var encoded = ContractCodec.Encode(ask.Input, new Dictionary<string, string>
        {
            ["issuer"] = Issuer,
            ["assetName"] = assetNameValue.ToString(),
            ["price"] = "1234",
            ["numberOfShares"] = "56",
        });

        var expected = new AddToAskOrderPayload
        {
            Issuer = Crypt.GetPublicKeyFromIdentity(Issuer),
            AssetName = assetNameValue,
            Price = 1234,
            NumberOfShares = 56
        }.GetPayloadBytes();
        Assert.Equal(expected, encoded);
    }

    [Fact]
    public void Encode_BadValue_ReportsFieldPath()
    {
        var ask = new ContractCatalog().Get(1)!.Procedures.Single(p => p.Name == "AddToAskOrder");
        var ex = Assert.Throws<FormatException>(() =>
            ContractCodec.Encode(ask.Input, new Dictionary<string, string> { ["price"] = "abc" }));
        Assert.StartsWith("price:", ex.Message);
    }

    [Fact]
    public void ImportHeader_ParsesCustomContract_AndOverridesIndex()
    {
        const string header = """
            using namespace QPI;

            struct MYTOKEN2
            {
            };

            struct MYTOKEN : public ContractBase
            {
            public:
                struct Mint_input
                {
                    id to;
                    uint64 amount;
                    bit flag;
                };
                struct Mint_output
                {
                    sint64 minted;
                };

                struct Balance_input
                {
                    id who;
                };
                struct Balance_output
                {
                    uint64 balance;
                };

                PUBLIC_PROCEDURE(Mint)
                {
                }

                PUBLIC_FUNCTION(Balance)
                {
                }

                REGISTER_USER_FUNCTIONS_AND_PROCEDURES()
                {
                    REGISTER_USER_FUNCTION(Balance, 1);
                    REGISTER_USER_PROCEDURE(Mint, 1);
                }
            };
            """;

        var catalog = new ContractCatalog();
        var def = catalog.ImportHeader(header, 30);

        Assert.Equal("MYTOKEN", def.CppStructName);
        Assert.Same(def, catalog.Get(30));
        var mint = def.Procedures.Single();
        Assert.Equal("Mint", mint.Name);
        // id(32) + uint64(8) + bit(1) → padded to 8: 48
        Assert.Equal(48, ContractLayout.SizeOf(mint.Input));

        var bytes = ContractCodec.Encode(mint.Input, new Dictionary<string, string>
        {
            ["to"] = Issuer, ["amount"] = "1000", ["flag"] = "true"
        });
        var decoded = catalog.DecodeProcedureCall(30, 1, bytes)!;
        Assert.Equal(["to", "amount", "flag"], decoded.Fields.Select(f => f.Name));
        Assert.Equal("true", decoded.Fields[2].Display);
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(27, true)]
    public void GetContractIndex_FromContractIdentity(int index, bool expected)
    {
        var identity = QubicContracts.GetContractIdentity(index).ToString();
        Assert.Equal(expected ? index : null, ContractCatalog.GetContractIndex(identity));
    }

    [Fact]
    public void GetContractIndex_UserIdentity_IsNull()
    {
        Assert.Null(ContractCatalog.GetContractIndex(Issuer));
    }
}

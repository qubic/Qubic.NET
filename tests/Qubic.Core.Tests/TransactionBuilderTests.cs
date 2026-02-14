using Qubic.Core.Entities;
using Qubic.Core.Payloads;

namespace Qubic.Core.Tests;

public class TransactionBuilderTests
{
    private readonly TransactionBuilder _builder = new();

    [Fact]
    public void CreateTransfer_ValidParameters_CreatesTransaction()
    {
        var source = QubicIdentity.FromIdentity("BAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAARMID");
        var destination = QubicIdentity.FromIdentity("CAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAACNKL");

        var transaction = _builder.CreateTransfer(source, destination, 1000, 12345);

        Assert.Equal(source, transaction.SourceIdentity);
        Assert.Equal(destination, transaction.DestinationIdentity);
        Assert.Equal(1000, transaction.Amount);
        Assert.Equal(12345u, transaction.Tick);
        Assert.Equal(0, transaction.InputType);
        Assert.Equal(0, transaction.InputSize);
        Assert.False(transaction.IsSigned);
    }

    [Fact]
    public void CreateTransfer_NegativeAmount_ThrowsArgumentException()
    {
        var source = QubicIdentity.FromIdentity("BAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAARMID");
        var destination = QubicIdentity.FromIdentity("CAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAACNKL");

        Assert.Throws<ArgumentException>(() => _builder.CreateTransfer(source, destination, -100, 12345));
    }

    [Fact]
    public void CreateTransaction_WithSendManyPayload_SetsCorrectInputType()
    {
        var source = QubicIdentity.FromIdentity("BAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAARMID");
        var recipient = QubicIdentity.FromIdentity("CAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAACNKL");

        var payload = new SendManyPayload()
            .AddTransfer(recipient, 500)
            .AddTransfer(recipient, 300);

        // SendToManyV1 goes to QUTIL contract (index 4)
        var qutilContract = QubicIdentity.FromPublicKey(SendManyPayload.GetQutilContractPublicKey());

        var transaction = _builder.CreateTransaction(source, qutilContract, payload.TotalAmount, 12345, payload);

        Assert.Equal(1, transaction.InputType); // QUTIL SendToManyV1
        Assert.Equal(1000, transaction.InputSize); // Fixed 1000 byte payload (25*32 + 25*8)
        Assert.Equal(800, transaction.Amount); // 500 + 300
    }
}

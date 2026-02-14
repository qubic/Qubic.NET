using Qubic.Core;
using Qubic.Core.Entities;
using Qubic.Core.Payloads;
using Qubic.Crypto;

namespace Qubic.Toolkit;

public class SeedSessionService
{
    private string? _seed;
    private QubicIdentity? _identity;
    private readonly TransactionBuilder _txBuilder = new();
    private readonly QubicCrypt _crypt = new();

    public bool HasSeed => _seed != null;
    public QubicIdentity? Identity => _identity;
    public event Action? OnSeedChanged;

    public void SetSeed(string seed)
    {
        if (seed.Length != 55)
            throw new ArgumentException("Seed must be 55 characters.");

        _seed = seed;
        _identity = QubicIdentity.FromSeed(seed);
        OnSeedChanged?.Invoke();
    }

    public void ClearSeed()
    {
        _seed = null;
        _identity = null;
        OnSeedChanged?.Invoke();
    }

    public byte[] GetPublicKey()
    {
        EnsureSeed();
        return _crypt.GetPublicKey(_seed!);
    }

    public byte[] GetPrivateKey()
    {
        EnsureSeed();
        return _crypt.GetPrivateKey(_seed!);
    }

    public QubicTransaction CreateAndSignTransfer(QubicIdentity destination, long amount, uint tick)
    {
        EnsureSeed();
        var tx = _txBuilder.CreateTransfer(_identity!.Value, destination, amount, tick);
        _txBuilder.Sign(tx, _seed!);
        return tx;
    }

    public QubicTransaction CreateAndSignTransaction(QubicIdentity destination, long amount, uint tick, ITransactionPayload payload)
    {
        EnsureSeed();
        var tx = _txBuilder.CreateTransaction(_identity!.Value, destination, amount, tick, payload);
        _txBuilder.Sign(tx, _seed!);
        return tx;
    }

    public byte[] SignMessage(byte[] message)
    {
        EnsureSeed();
        var signed = _crypt.Sign(_seed!, message);
        var signature = new byte[64];
        Array.Copy(signed, signed.Length - 64, signature, 0, 64);
        return signature;
    }

    public byte[] SignRawMessage(byte[] message)
    {
        EnsureSeed();
        return _crypt.SignRaw(_seed!, message);
    }

    private void EnsureSeed()
    {
        if (_seed == null)
            throw new InvalidOperationException("No seed is set. Enter your seed first.");
    }
}

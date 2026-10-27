using Qubic.Core;
using Qubic.Core.Entities;
using Qubic.Core.Payloads;
using Qubic.Crypto;
using Qubic.Services.Signing;

namespace Qubic.Services;

public class SeedSessionService
{
    private string? _seed;
    private QubicIdentity? _identity;
    private string? _activeLabel;
    private readonly TransactionBuilder _txBuilder = new();
    private readonly QubicCrypt _crypt = new();
    private readonly SigningReviewService? _review;

    public SeedSessionService() { }

    /// <summary>With a review service, the async Sign* methods show the signing review sheet first.</summary>
    public SeedSessionService(SigningReviewService review)
    {
        _review = review;
    }

    public bool HasSeed => _seed != null;
    public QubicIdentity? Identity => _identity;
    public string? ActiveLabel => _activeLabel;
    public event Action? OnSeedChanged;

    public void SetSeed(string seed, string? label = null)
    {
        if (seed.Length != 55)
            throw new ArgumentException("Seed must be 55 characters.");

        _seed = seed;
        _identity = QubicIdentity.FromSeed(seed);
        _activeLabel = label;
        OnSeedChanged?.Invoke();
    }

    public void ClearSeed()
    {
        _seed = null;
        _identity = null;
        _activeLabel = null;
        OnSeedChanged?.Invoke();
    }

    /// <summary>Returns the current seed for vault storage.</summary>
    public string GetSeedForVault()
    {
        EnsureSeed();
        return _seed!;
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

    // ── Reviewed signing (use these from UI flows) ──

    /// <summary>Shows the signing review, then signs a QU transfer. Throws <see cref="SigningRejectedException"/> if declined.</summary>
    public Task<QubicTransaction> SignTransferAsync(QubicIdentity destination, long amount, uint tick,
        SigningContext? context = null, CancellationToken ct = default)
    {
        EnsureSeed();
        if (_review == null) return Task.FromResult(CreateAndSignTransfer(destination, amount, tick));
        return _review.SignTransactionAsync(_seed!, _activeLabel, destination, amount, tick, null, context ?? SigningContext.InApp(), ct);
    }

    /// <summary>Shows the signing review (with the decoded contract call), then signs. Throws <see cref="SigningRejectedException"/> if declined.</summary>
    public Task<QubicTransaction> SignTransactionAsync(QubicIdentity destination, long amount, uint tick,
        ITransactionPayload payload, SigningContext? context = null, CancellationToken ct = default)
    {
        EnsureSeed();
        if (_review == null) return Task.FromResult(CreateAndSignTransaction(destination, amount, tick, payload));
        return _review.SignTransactionAsync(_seed!, _activeLabel, destination, amount, tick, payload, context ?? SigningContext.InApp(), ct);
    }

    /// <summary>Shows the signing review, then signs a raw message (64-byte signature). Sign-In with Qubic messages are recognised.</summary>
    public Task<byte[]> SignRawMessageAsync(byte[] message, SigningContext? context = null, CancellationToken ct = default)
    {
        EnsureSeed();
        if (_review == null) return Task.FromResult(SignRawMessage(message));
        return _review.SignRawMessageAsync(_seed!, _activeLabel, message, context ?? SigningContext.InApp(), ct);
    }

    // ── Unreviewed signing (internal policy paths only, e.g. user-enabled auto-resend) ──

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
        return _crypt.Sign(_seed!, message);
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

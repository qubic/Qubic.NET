using Qubic.Core;
using Qubic.Core.Auth;
using Qubic.Core.Contracts.Qx;
using Qubic.Core.Entities;
using Qubic.Core.Payloads;
using Qubic.Services.DevTools;
using Qubic.Services.Networks;

namespace Qubic.Services.Signing;

/// <summary>An account exposed to a dApp / local client.</summary>
public sealed record ExternalAccount(string Address, string Name, long Amount, IReadOnlyList<ExternalAsset> Assets);

public sealed record ExternalAsset(string AssetName, string IssuerIdentity, long OwnedAmount, int ManagingContractIndex);

/// <summary>Transaction parameters as sent by dApps (qubic_signTransaction / qubic_sendTransaction / qubic_sendQubic).</summary>
public sealed record ExternalTxParams(string From, string To, long Amount, uint? Tick = null, ushort InputType = 0, byte[]? Payload = null);

public sealed record ExternalSignedTx(string TransactionId, uint Tick, byte[] RawTransaction)
{
    public string RawBase64 => Convert.ToBase64String(RawTransaction);
    public string RawHex => Convert.ToHexString(RawTransaction).ToLowerInvariant();
}

/// <summary>JSON-RPC style error for external requests (codes as in the official Qubic WalletConnect spec).</summary>
public sealed class ExternalRequestException : Exception
{
    public const int UserRejected = 5000;
    public const int UserUnavailable = -32001;
    public const int TickExpired = -32002;
    public const int InvalidParams = -32602;
    public const int InternalError = -32603;

    public int Code { get; }

    public ExternalRequestException(int code, string message) : base(message) => Code = code;
}

/// <summary>
/// Protocol-neutral implementation of the wallet methods dApps call (the official Qubic WalletConnect
/// method set: requestAccounts, sendQubic, signTransaction, sendTransaction, sign, sendAsset).
/// Used by WalletConnect and the local signing endpoint. Every signature goes through
/// <see cref="SigningReviewService"/>; nothing is signed without the user (or an explicit policy) approving it.
/// </summary>
public sealed class ExternalRequestService
{
    private readonly SeedSessionService _seed;
    private readonly VaultService _vault;
    private readonly QubicBackendService _backend;
    private readonly TickMonitorService _tickMonitor;
    private readonly SigningReviewService _review;
    private readonly TransactionTrackerService _tracker;
    private readonly DevToolsService _devTools;
    private readonly SemaphoreSlim _oneAtATime = new(1, 1);

    public ExternalRequestService(SeedSessionService seed, VaultService vault, QubicBackendService backend,
        TickMonitorService tickMonitor, SigningReviewService review, TransactionTrackerService tracker, DevToolsService devTools)
    {
        _seed = seed;
        _vault = vault;
        _backend = backend;
        _tickMonitor = tickMonitor;
        _review = review;
        _tracker = tracker;
        _devTools = devTools;
    }

    /// <summary>Identities this wallet can sign for right now: the active seed and unlocked vault entries.</summary>
    public IReadOnlyList<(string Identity, string Label)> SignableAccounts()
    {
        var list = new List<(string, string)>();
        if (_seed.Identity is { } id) list.Add((id.ToString(), _seed.ActiveLabel ?? "Active seed"));
        if (_vault.IsUnlocked)
            foreach (var e in _vault.Entries)
                if (list.All(x => x.Item1 != e.Identity)) list.Add((e.Identity, e.Label));
        return list;
    }

    public async Task<IReadOnlyList<ExternalAccount>> GetAccountsAsync(IEnumerable<string> identities, CancellationToken ct = default)
    {
        var names = SignableAccounts().ToDictionary(a => a.Identity, a => a.Label);
        var result = new List<ExternalAccount>();
        foreach (var identity in identities.Distinct())
        {
            long amount = -1;
            try { amount = (await _backend.GetBalanceAsync(QubicIdentity.FromIdentity(identity), ct)).Amount; } catch { }
            var assets = new List<ExternalAsset>();
            if (_backend.IsAvailable(Capability.AssetListings))
            {
                try
                {
                    foreach (var a in await _backend.GetOwnedAssetsAsync(QubicIdentity.FromIdentity(identity), ct))
                        if (a.Data.IssuedAsset is { } issued && long.TryParse(a.Data.NumberOfUnits, out var units))
                            assets.Add(new ExternalAsset(issued.Name, issued.IssuerIdentity, units, (int)a.Data.ManagingContractIndex));
                }
                catch { /* balances without assets are still useful */ }
            }
            result.Add(new ExternalAccount(identity, names.GetValueOrDefault(identity, identity[..8]), amount, assets));
        }
        return result;
    }

    /// <summary>Signs (does not broadcast) a transfer or contract call.</summary>
    public Task<ExternalSignedTx> SignTransactionAsync(ExternalTxParams p, SigningContext context, CancellationToken ct = default)
        => Exclusive(async () => (await SignCoreAsync(p, context, ct)).Signed);

    /// <summary>Signs and broadcasts a transfer or contract call.</summary>
    public Task<ExternalSignedTx> SendTransactionAsync(ExternalTxParams p, SigningContext context, CancellationToken ct = default)
        => Exclusive(async () =>
        {
            var (signed, tx) = await SignCoreAsync(p, context, ct);
            return await BroadcastAsync(signed, tx, p, context, ct);
        });

    /// <summary>Transfers QX-managed asset shares (QX TransferShareOwnershipAndPossession).</summary>
    public Task<ExternalSignedTx> SendAssetAsync(string from, string to, string assetName, string issuer, long amount,
        SigningContext context, CancellationToken ct = default)
        => Exclusive(async () =>
        {
            if (amount <= 0) throw new ExternalRequestException(ExternalRequestException.InvalidParams, "amount must be positive.");
            var name = (assetName ?? "").Trim().ToUpperInvariant();
            if (name.Length is < 1 or > 7) throw new ExternalRequestException(ExternalRequestException.InvalidParams, "assetName must be 1-7 characters.");
            var issuerId = ParseIdentity(issuer, "issuer");
            var toId = ParseIdentity(to, "to");
            var feeBytes = await _backend.QuerySmartContractAsync((uint)QxContract.ContractIndex, QxContract.Functions.Fees, [], ct);
            var fee = FeesOutput.FromBytes(feeBytes).TransferFee;
            var payload = new TransferShareOwnershipAndPossessionPayload
            {
                Issuer = issuerId.PublicKey,
                NewOwnerAndPossessor = toId.PublicKey,
                AssetName = AssetNameHelper.ToUlong(name),
                NumberOfShares = amount,
            };
            var p = new ExternalTxParams(from, QubicContracts.GetContractIdentity(QxContract.ContractIndex).ToString(), fee,
                null, payload.InputType, payload.GetPayloadBytes());
            var ctx = context with { Description = context.Description ?? $"Send {amount:N0} {name} to {toId.ToString()[..8]}…" };
            var (signed, tx) = await SignCoreAsync(p, ctx, ct);
            return await BroadcastAsync(signed, tx, p, ctx, ct);
        });

    /// <summary>Signs a message the way WalletConnect <c>qubic_sign</c> expects (prefixed, digest, base64 parts).</summary>
    public Task<PrefixedSignature> SignMessageWalletConnectAsync(string from, string message, SigningContext context, CancellationToken ct = default)
        => Exclusive(async () =>
        {
            var (seed, label) = ResolveSeed(from);
            try
            {
                return await _review.SignWalletConnectMessageAsync(seed, label, message, context, ct);
            }
            catch (SigningRejectedException)
            {
                throw new ExternalRequestException(ExternalRequestException.UserRejected, "User rejected");
            }
        });

    /// <summary>Signs raw message bytes (raw convention, same as Sign / Verify in the wallet).</summary>
    public Task<byte[]> SignMessageRawAsync(string from, byte[] message, SigningContext context, CancellationToken ct = default)
        => Exclusive(async () =>
        {
            var (seed, label) = ResolveSeed(from);
            try
            {
                return await _review.SignRawMessageAsync(seed, label, message, context, ct);
            }
            catch (SigningRejectedException)
            {
                throw new ExternalRequestException(ExternalRequestException.UserRejected, "User rejected");
            }
        });

    // ── internals ──

    private async Task<T> Exclusive<T>(Func<Task<T>> action)
    {
        // One request at a time, like the official wallet ("User is unavailable" while another is open).
        if (!await _oneAtATime.WaitAsync(TimeSpan.FromMinutes(5)))
            throw new ExternalRequestException(ExternalRequestException.UserUnavailable, "User is unavailable");
        try { return await action(); }
        finally { _oneAtATime.Release(); }
    }

    private async Task<(ExternalSignedTx Signed, QubicTransaction Tx)> SignCoreAsync(ExternalTxParams p, SigningContext context, CancellationToken ct)
    {
        if (p.Amount < 0) throw new ExternalRequestException(ExternalRequestException.InvalidParams, "amount must be 0 or positive.");
        var dest = ParseIdentity(p.To, "to");
        var (seed, label) = ResolveSeed(p.From);

        uint tick;
        var current = _tickMonitor.IsConnected ? _tickMonitor.Tick : (await _backend.GetTickInfoAsync(ct)).Tick;
        if (p.Tick is { } requested)
        {
            if (requested <= current)
                throw new ExternalRequestException(ExternalRequestException.TickExpired, "Tick expired");
            tick = requested;
        }
        else
        {
            tick = TickTargeting.Compute(_backend.Profile, current, _tickMonitor.EstimatedTickDurationMs).TargetTick;
        }

        ITransactionPayload? payload = p.InputType != 0 || p.Payload is { Length: > 0 }
            ? new GenericContractPayload(p.InputType, p.Payload ?? [])
            : null;

        QubicTransaction tx;
        try
        {
            tx = await _review.SignTransactionAsync(seed, label, dest, p.Amount, tick, payload, context, ct);
        }
        catch (SigningRejectedException)
        {
            throw new ExternalRequestException(ExternalRequestException.UserRejected, "User rejected");
        }

        // The user may have taken a while: refuse to hand out a transaction whose tick already passed.
        if (_tickMonitor.IsConnected && _tickMonitor.Tick >= tick)
            throw new ExternalRequestException(ExternalRequestException.TickExpired, "Tick expired");

        return (new ExternalSignedTx(tx.TransactionHash ?? "", tick, tx.GetRawBytes()), tx);
    }

    private async Task<ExternalSignedTx> BroadcastAsync(ExternalSignedTx signed, QubicTransaction tx, ExternalTxParams p, SigningContext context, CancellationToken ct)
    {
        var result = await _backend.BroadcastTransactionAsync(tx, ct);
        if (_seed.Identity?.ToString() == p.From)
        {
            _tracker.Track(new TrackedTransaction
            {
                Hash = result.TransactionId,
                Source = p.From,
                Destination = p.To,
                Amount = p.Amount,
                TargetTick = signed.Tick,
                InputType = p.InputType,
                PayloadHex = p.Payload is { Length: > 0 } ? Convert.ToHexString(p.Payload) : null,
                Description = $"[{context.RequesterName ?? context.Origin.ToString()}] {context.Description ?? "dApp transaction"}",
                RawData = signed.RawHex.ToUpperInvariant()
            });
        }
        return signed with { TransactionId = string.IsNullOrEmpty(result.TransactionId) ? signed.TransactionId : result.TransactionId };
    }

    private (string Seed, string Label) ResolveSeed(string from)
    {
        var id = ParseIdentity(from, "from").ToString();
        if (_seed.Identity?.ToString() == id)
            return (_seed.GetSeedForVault(), _seed.ActiveLabel ?? "Active seed");
        if (_vault.IsUnlocked && _vault.GetEntry(id) is { } entry)
            return (entry.Seed, entry.Label);
        if (_devTools.IsAvailable && CoreLiteDevSeeds.Accounts.FirstOrDefault(a => a.Identity == id) is { } dev)
            return (dev.Seed, $"Dev account #{dev.Index}");
        throw new ExternalRequestException(ExternalRequestException.InvalidParams,
            $"Account {id[..8]}… is not available in this wallet (unlock the vault or select it).");
    }

    private static QubicIdentity ParseIdentity(string? value, string field)
    {
        try
        {
            var v = (value ?? "").Trim().ToUpperInvariant();
            if (v.Length != 60) throw new FormatException();
            return QubicIdentity.FromIdentity(v);
        }
        catch
        {
            throw new ExternalRequestException(ExternalRequestException.InvalidParams, $"{field} is not a valid Qubic identity.");
        }
    }
}

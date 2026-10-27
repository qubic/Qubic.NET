using Qubic.Core;
using Qubic.Core.Contracts.Qx;
using Qubic.Core.Entities;
using Qubic.Services.Features;
using Qubic.Services.Networks;
using Qubic.Services.Signing;

namespace Qubic.Services.DevTools;

public sealed record DevAccountBalance(DevAccount Account, long? Balance, string? Error);

/// <summary>
/// Developer tools for local core-lite / AIO testnets: public dev accounts (the 676 computor
/// seeds the AIO genesis funds with 10B QU each), a faucet that signs transfers from them,
/// QX asset issuance from a dev account and the exact AIO state-file commands for genesis funding.
/// Available only when the feature is enabled and the network is proven to be a core-lite devnet
/// (or the user explicitly forced it for the active profile). Every signature still goes through
/// the signing review.
/// </summary>
public sealed class DevToolsService
{
    private readonly FeatureService _features;
    private readonly NetworkFingerprintService _fingerprint;
    private readonly NetworkProfileService _profiles;
    private readonly QubicBackendService _backend;
    private readonly TickMonitorService _tickMonitor;
    private readonly SigningReviewService _review;
    private readonly QubicSettingsService _settings;
    private int _nextAccount;

    public DevToolsService(FeatureService features, NetworkFingerprintService fingerprint, NetworkProfileService profiles,
        QubicBackendService backend, TickMonitorService tickMonitor, SigningReviewService review, QubicSettingsService settings)
    {
        _features = features;
        _fingerprint = fingerprint;
        _profiles = profiles;
        _backend = backend;
        _tickMonitor = tickMonitor;
        _review = review;
        _settings = settings;
    }

    private string ForceKey => "devtools.force." + _profiles.ActiveId;

    /// <summary>User override for a non-fingerprinted (e.g. custom) devnet profile. Never applies to Mainnet.</summary>
    public bool ForcedForActiveProfile
    {
        get => _settings.GetCustom<bool?>(ForceKey) ?? false;
        set => _settings.SetCustom(ForceKey, value);
    }

    /// <summary>True when the dev tools may be used on the active network.</summary>
    public bool IsAvailable =>
        _features.IsEnabled(WalletFeature.DeveloperTools)
        && !_profiles.Active.IsMainnet
        && (_fingerprint.IsCoreLiteDevnet || ForcedForActiveProfile);

    /// <summary>Why the tools are not available (null when they are).</summary>
    public string? UnavailableReason
    {
        get
        {
            if (!_features.IsEnabled(WalletFeature.DeveloperTools)) return "Developer tools are turned off (Settings → Features).";
            if (_profiles.Active.IsMainnet) return "Developer tools are never available on Mainnet.";
            if (!_fingerprint.IsCoreLiteDevnet && !ForcedForActiveProfile)
                return _fingerprint.Current switch
                {
                    null => "Checking the network… dev tools unlock once it is identified as a core-lite devnet.",
                    { Kind: NetworkFingerprintKind.Unknown } fp => $"Could not identify the network yet ({fp.Detail}). Dev tools unlock once it is identified as a core-lite devnet.",
                    _ => "This network was not identified as a core-lite devnet (its computors are not the public dev set)."
                };
            return null;
        }
    }

    public IReadOnlyList<DevAccount> Accounts => CoreLiteDevSeeds.Accounts;

    private void EnsureAvailable()
    {
        if (!IsAvailable) throw new InvalidOperationException(UnavailableReason ?? "Developer tools are not available.");
    }

    /// <summary>Balances for a page of dev accounts.</summary>
    public async Task<IReadOnlyList<DevAccountBalance>> GetBalancesAsync(int skip, int take, CancellationToken ct = default)
    {
        EnsureAvailable();
        var page = Accounts.Skip(skip).Take(take).ToList();
        var tasks = page.Select(async a =>
        {
            try
            {
                var b = await _backend.GetBalanceAsync(QubicIdentity.FromIdentity(a.Identity), ct);
                return new DevAccountBalance(a, b.Amount, null);
            }
            catch (Exception ex)
            {
                return new DevAccountBalance(a, null, ex.Message);
            }
        });
        return await Task.WhenAll(tasks);
    }

    /// <summary>
    /// Faucet: sends <paramref name="amount"/> QU to <paramref name="destination"/> from the next dev account
    /// (round-robin) that holds enough. Returns the transaction hash.
    /// </summary>
    public async Task<string> FundAsync(string destination, long amount, CancellationToken ct = default)
    {
        EnsureAvailable();
        if (amount <= 0) throw new ArgumentOutOfRangeException(nameof(amount), "Amount must be positive.");
        var dest = QubicIdentity.FromIdentity(destination.Trim().ToUpperInvariant());

        for (int attempt = 0; attempt < Accounts.Count; attempt++)
        {
            var account = Accounts[Interlocked.Increment(ref _nextAccount) % Accounts.Count];
            if (account.Identity == dest.ToString()) continue;
            long balance;
            try { balance = (await _backend.GetBalanceAsync(QubicIdentity.FromIdentity(account.Identity), ct)).Amount; }
            catch { continue; }
            if (balance < amount) continue;

            var target = await TransactionHelper.ResolveTickTargetAsync(_backend, _tickMonitor, ct);
            var tx = await _review.SignTransactionAsync(account.Seed, $"Dev account #{account.Index}", dest, amount,
                target.TargetTick, null, new SigningContext(SigningOrigin.DevTools, $"Faucet: {amount:N0} QU"), ct);
            var result = await _backend.BroadcastTransactionAsync(tx, ct);
            return result.TransactionId;
        }
        throw new InvalidOperationException($"No dev account holds {amount:N0} QU.");
    }

    /// <summary>Issues a QX asset from a dev account (fee taken from QX's Fees function). Returns the tx hash.</summary>
    public async Task<string> IssueAssetAsync(int accountIndex, string name, long shares, sbyte decimals = 0, CancellationToken ct = default)
    {
        EnsureAvailable();
        name = name.Trim().ToUpperInvariant();
        if (name.Length is < 1 or > 7 || !name.All(c => c is >= 'A' and <= 'Z' or >= '0' and <= '9') || !char.IsLetter(name[0]))
            throw new ArgumentException("Asset name: 1-7 characters A-Z/0-9, starting with a letter.", nameof(name));
        if (shares <= 0) throw new ArgumentOutOfRangeException(nameof(shares));

        var account = Accounts[accountIndex];
        var feeBytes = await _backend.QuerySmartContractAsync((uint)QxContract.ContractIndex, QxContract.Functions.Fees, [], ct);
        var fee = FeesOutput.FromBytes(feeBytes).AssetIssuanceFee;

        var payload = new IssueAssetPayload
        {
            AssetName = AssetNameHelper.ToUlong(name),
            NumberOfShares = shares,
            UnitOfMeasurement = 0,
            NumberOfDecimalPlaces = decimals,
        };
        var target = await TransactionHelper.ResolveTickTargetAsync(_backend, _tickMonitor, ct);
        var tx = await _review.SignTransactionAsync(account.Seed, $"Dev account #{account.Index}",
            QubicContracts.GetContractIdentity(QxContract.ContractIndex), fee, target.TargetTick, payload,
            new SigningContext(SigningOrigin.DevTools, $"Issue {shares:N0} {name}"), ct);
        var result = await _backend.BroadcastTransactionAsync(tx, ct);
        return result.TransactionId;
    }

    /// <summary>
    /// AIO kit commands that fund <paramref name="identity"/> (and optionally issue an asset to it) in the
    /// genesis state files. Applied with <c>./scripts/down.sh --wipe &amp;&amp; ./scripts/up.sh</c>.
    /// </summary>
    public IReadOnlyList<string> GenesisCommands(string identity, long amount, string? assetName = null, long assetShares = 0)
    {
        var epoch = _tickMonitor.Epoch > 0 ? _tickMonitor.Epoch.ToString() : "<EPOCH>";
        var cmds = new List<string>
        {
            "cd tools && make && cd ..",
            "mkdir -p states && unzip -n state.zip -d states",
            $"./tools/spectrum_update states/spectrum.{epoch} {identity} {amount}",
        };
        if (!string.IsNullOrWhiteSpace(assetName) && assetShares > 0)
            cmds.Add($"./tools/universe_issue states/universe.{epoch} {identity} {assetName.Trim().ToUpperInvariant()} {assetShares}");
        cmds.Add("./scripts/down.sh --wipe && ./scripts/up.sh");
        return cmds;
    }
}

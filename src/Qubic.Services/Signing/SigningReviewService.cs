using Qubic.Core;
using Qubic.Core.Auth;
using Qubic.Core.Entities;
using Qubic.Core.Payloads;
using Qubic.Crypto;
using Qubic.Services.Contracts;
using Qubic.Services.Networks;

namespace Qubic.Services.Signing;

/// <summary>
/// The single gate every signature passes through. Builds a <see cref="SigningRequest"/>
/// (decoded contract call, network badge, target tick, warnings), asks the app's
/// <see cref="ISigningPresenter"/> to show it, and only then signs.
/// <list type="bullet">
/// <item>Replay risk: Qubic transactions carry no chain id, so a seed that signs on both mainnet
/// and a test network gets a Danger warning (acknowledged once per seed and session).</item>
/// <item>Public core-lite dev keys used on mainnet, or funds sent to them, get a Danger warning.</item>
/// <item>A proven network mismatch (fingerprint) gets a Danger warning.</item>
/// </list>
/// Without a presenter (headless use) in-app requests pass and external requests are rejected.
/// </summary>
public sealed class SigningReviewService
{
    public const string MainnetClass = "mainnet";
    public const string TestClass = "test";

    private readonly NetworkProfileService _profiles;
    private readonly NetworkFingerprintService _fingerprint;
    private readonly VaultService _vault;
    private readonly ContractCatalogService _contracts;
    private readonly TickMonitorService _tickMonitor;
    private readonly LabelService? _labels;
    private readonly TransactionBuilder _txBuilder = new();
    private readonly QubicCrypt _crypt = new();
    private readonly HashSet<string> _acknowledged = [];
    private readonly Dictionary<string, HashSet<string>> _sessionSignedOn = [];

    public SigningReviewService(NetworkProfileService profiles, NetworkFingerprintService fingerprint,
        VaultService vault, ContractCatalogService contracts, TickMonitorService tickMonitor, LabelService? labels = null)
    {
        _profiles = profiles;
        _fingerprint = fingerprint;
        _vault = vault;
        _contracts = contracts;
        _tickMonitor = tickMonitor;
        _labels = labels;
    }

    /// <summary>The app's review sheet. Set once at startup.</summary>
    public ISigningPresenter? Presenter { get; set; }

    /// <summary>
    /// Optional policy that may approve a request without showing it (e.g. local endpoint +
    /// public dev keys on a proven devnet). Return true to auto-approve.
    /// </summary>
    public Func<SigningRequest, bool>? AutoApprovePolicy { get; set; }

    /// <summary>Raised after every decision (request, approved).</summary>
    public event Action<SigningRequest, bool>? OnDecided;

    public static string NetworkClass(NetworkProfile p) => p.IsMainnet ? MainnetClass : TestClass;

    // ── Request building ──

    public SigningRequest BuildTransactionRequest(SigningContext context, string sourceIdentity, string? sourceLabel,
        QubicIdentity destination, long amount, uint targetTick, ushort inputType, byte[]? payload)
    {
        var profile = _profiles.Active;
        var dest = destination.ToString();
        var current = _tickMonitor.IsConnected ? _tickMonitor.Tick : (uint?)null;
        double? eta = null;
        if (current is { } c && targetTick > c && _tickMonitor.EstimatedTickDurationMs is { } ms)
            eta = (targetTick - c) * ms / 1000.0;

        var decoded = payload != null || inputType != 0
            ? _contracts.Decode(dest, inputType, payload ?? [])
            : null;

        var req = new SigningRequest
        {
            Kind = SigningKind.Transaction,
            Context = context,
            Network = profile,
            Fingerprint = _fingerprint.Current,
            SourceIdentity = sourceIdentity,
            SourceLabel = sourceLabel,
            SourceIsPublicDevKey = CoreLiteDevSeeds.IsPublicDevIdentity(sourceIdentity),
            Destination = dest,
            DestinationLabel = _labels?.GetLabel(dest) ?? (decoded != null ? decoded.ContractName + " contract" : null),
            Amount = amount,
            TargetTick = targetTick,
            CurrentTick = current,
            EtaSeconds = eta,
            InputType = inputType,
            Payload = payload,
            Decoded = decoded,
        };
        AddWarnings(req);
        return req;
    }

    public SigningRequest BuildMessageRequest(SigningContext context, string sourceIdentity, string? sourceLabel, byte[] message)
    {
        var text = TryUtf8(message);
        SignInMessage? signIn = null;
        if (text != null && SignInMessage.IsSignInMessage(text))
        {
            try { signIn = SignInMessage.Parse(text); } catch (FormatException) { /* shown as plain message */ }
        }

        var req = new SigningRequest
        {
            Kind = signIn != null ? SigningKind.SignIn : SigningKind.Message,
            Context = context,
            Network = _profiles.Active,
            Fingerprint = _fingerprint.Current,
            SourceIdentity = sourceIdentity,
            SourceLabel = sourceLabel,
            SourceIsPublicDevKey = CoreLiteDevSeeds.IsPublicDevIdentity(sourceIdentity),
            MessageBytes = message,
            MessageText = text,
            SignIn = signIn,
        };
        AddWarnings(req);
        return req;
    }

    private void AddWarnings(SigningRequest req)
    {
        var profile = req.Network;
        var here = NetworkClass(profile);
        var other = here == MainnetClass ? TestClass : MainnetClass;

        if (_fingerprint.IsMismatch && _fingerprint.Current is { } fp)
            req.Warnings.Add(new(WarningLevel.Danger, "Network mismatch",
                $"\"{profile.Name}\" is set up as {profile.Kind}, but the connected chain was identified as {Describe(fp.Kind)}. Check the network settings before signing.",
                "network-mismatch"));

        if (req.SourceIsPublicDevKey && profile.IsMainnet)
            req.Warnings.Add(new(WarningLevel.Danger, "Public dev key on Mainnet",
                "This identity comes from a public core-lite seed. Anyone can sign with it — never hold funds on it on Mainnet.",
                "public-dev-source"));

        if (req.Destination != null && profile.IsMainnet && CoreLiteDevSeeds.IsPublicDevIdentity(req.Destination))
            req.Warnings.Add(new(WarningLevel.Danger, "Destination is a public dev key",
                "The destination belongs to a publicly known core-lite seed. Funds sent there on Mainnet can be taken by anyone.",
                "public-dev-destination"));

        if (!req.SourceIsPublicDevKey && SignedOn(req.SourceIdentity).Contains(other))
        {
            var key = req.SourceIdentity + "|" + here;
            var level = req.Kind == SigningKind.Transaction && !_acknowledged.Contains(key) ? WarningLevel.Danger : WarningLevel.Caution;
            var detail = req.Kind == SigningKind.Transaction
                ? $"This seed has also signed on {(other == MainnetClass ? "Mainnet" : "a test network")}. Qubic transactions carry no network id: this transaction (tick {req.TargetTick}) could also be valid there. Prefer separate seeds for test networks."
                : $"This seed has also signed on {(other == MainnetClass ? "Mainnet" : "a test network")}. Message signatures are valid on every network.";
            req.Warnings.Add(new(level, "Seed used on another network", detail, "cross-network"));
        }

        if (req.Kind == SigningKind.Transaction && req.CurrentTick is { } cur && req.TargetTick > cur)
        {
            var window = TickTargeting.PendingWindowTicks(profile);
            if (req.TargetTick - cur > window)
                req.Warnings.Add(new(WarningLevel.Caution, "Target tick too far ahead",
                    $"Target is {req.TargetTick - cur} ticks ahead; this network only accepts {window}. The transaction will likely be dropped.",
                    "tick-window"));
        }
        if (req.Kind == SigningKind.Transaction && req.CurrentTick is { } cur2 && req.TargetTick <= cur2)
            req.Warnings.Add(new(WarningLevel.Caution, "Target tick already passed",
                $"The network is at tick {cur2}, the transaction targets {req.TargetTick}. It will not be executed.", "tick-passed"));

        if (req.Decoded is { } d)
        {
            if (d.EntryName == null)
                req.Warnings.Add(new(WarningLevel.Info, "Call not decoded",
                    $"{d.ContractName} procedure #{d.InputType} is not in the wallet's contract metadata. Review the raw input carefully.", "not-decoded"));
            else if (!d.SizeMatches)
                req.Warnings.Add(new(WarningLevel.Caution, "Input size mismatch",
                    $"The input is {d.ActualSize} bytes, but {d.EntryName} expects {d.ExpectedSize}. The contract may reject it.", "size-mismatch"));
        }

        if (req.SignIn is { } si)
        {
            var host = req.Context.RequesterUrl != null && Uri.TryCreate(req.Context.RequesterUrl, UriKind.Absolute, out var u) ? u.Authority : null;
            foreach (var problem in si.Validate(DateTimeOffset.UtcNow, host, profile.ChainId))
                req.Warnings.Add(new(WarningLevel.Danger, "Sign-in problem", problem, "signin"));
            if (!string.Equals(si.Identity, req.SourceIdentity, StringComparison.Ordinal))
                req.Warnings.Add(new(WarningLevel.Danger, "Wrong identity",
                    $"The sign-in message names {si.Identity}, but you are signing with {req.SourceIdentity}.", "signin-identity"));
        }
    }

    private static string Describe(NetworkFingerprintKind k) => k switch
    {
        NetworkFingerprintKind.CoreLiteDevnet => "a core-lite devnet (public dev computors)",
        NetworkFingerprintKind.ArbitratorSigned => "a chain signed by the mainnet arbitrator",
        NetworkFingerprintKind.Unrecognized => "an unrecognized network",
        _ => "unknown"
    };

    private IReadOnlySet<string> SignedOn(string identity)
    {
        var set = new HashSet<string>(_vault.GetSignedOn(identity));
        lock (_sessionSignedOn)
            if (_sessionSignedOn.TryGetValue(identity, out var s)) set.UnionWith(s);
        return set;
    }

    // ── Approval ──

    /// <summary>Shows the request (or applies policy) and returns the decision.</summary>
    public async Task<bool> ApproveAsync(SigningRequest req, CancellationToken ct = default)
    {
        bool approved;
        if (AutoApprovePolicy?.Invoke(req) == true)
        {
            req.AutoApproved = true;
            approved = true;
        }
        else if (Presenter == null)
        {
            approved = !req.Context.IsExternal;
        }
        else
        {
            approved = await Presenter.PresentAsync(req, ct);
        }

        if (approved && req.RequiresAcknowledgement)
            lock (_acknowledged) _acknowledged.Add(req.SourceIdentity + "|" + NetworkClass(req.Network));

        try { OnDecided?.Invoke(req, approved); } catch { /* observers must not change the outcome */ }
        return approved;
    }

    /// <summary>Remembers that the request's seed signed on this network class (vault + session).</summary>
    private void RecordSigned(SigningRequest req)
    {
        if (req.SourceIsPublicDevKey) return;
        var cls = NetworkClass(req.Network);
        lock (_sessionSignedOn)
        {
            if (!_sessionSignedOn.TryGetValue(req.SourceIdentity, out var set))
                _sessionSignedOn[req.SourceIdentity] = set = [];
            set.Add(cls);
        }
        var identity = req.SourceIdentity;
        _ = Task.Run(() =>
        {
            try { _vault.RecordSignedOn(identity, cls); } catch { /* best effort: vault may be locked */ }
        });
    }

    // ── Signing with review ──

    /// <summary>Reviews, then builds and signs a transfer or contract call with <paramref name="seed"/>.</summary>
    public async Task<QubicTransaction> SignTransactionAsync(string seed, string? sourceLabel, QubicIdentity destination,
        long amount, uint targetTick, ITransactionPayload? payload, SigningContext context, CancellationToken ct = default)
    {
        var source = QubicIdentity.FromSeed(seed);
        var bytes = payload?.GetPayloadBytes();
        var req = BuildTransactionRequest(context, source.ToString(), sourceLabel, destination, amount, targetTick,
            payload?.InputType ?? 0, bytes is { Length: > 0 } ? bytes : null);
        if (!await ApproveAsync(req, ct))
            throw new SigningRejectedException();

        var tx = payload == null
            ? _txBuilder.CreateTransfer(source, destination, amount, targetTick)
            : _txBuilder.CreateTransaction(source, destination, amount, targetTick, payload);
        _txBuilder.Sign(tx, seed);
        RecordSigned(req);
        return tx;
    }

    /// <summary>Reviews, then signs <paramref name="message"/> with the raw-message convention (64-byte signature).</summary>
    public async Task<byte[]> SignRawMessageAsync(string seed, string? sourceLabel, byte[] message, SigningContext context, CancellationToken ct = default)
    {
        var source = QubicIdentity.FromSeed(seed).ToString();
        var req = BuildMessageRequest(context, source, sourceLabel, message);
        if (!await ApproveAsync(req, ct))
            throw new SigningRejectedException();
        var sig = _crypt.SignRaw(seed, message);
        RecordSigned(req);
        return sig;
    }

    /// <summary>
    /// Reviews, then signs <paramref name="message"/> with the WalletConnect <c>qubic_sign</c> convention
    /// ("Qubic Signed Message:\n" prefix, K12 digest) — the format dApps verify against.
    /// </summary>
    public async Task<PrefixedSignature> SignWalletConnectMessageAsync(string seed, string? sourceLabel, string message,
        SigningContext context, CancellationToken ct = default)
    {
        var source = QubicIdentity.FromSeed(seed).ToString();
        var req = BuildMessageRequest(context, source, sourceLabel, System.Text.Encoding.UTF8.GetBytes(message));
        req.Warnings.Add(new(WarningLevel.Info, "WalletConnect message",
            $"Signed as \"{QubicMessageSigning.WalletConnectPrefix.TrimEnd('\n')}\" + your message, so it can never be a transaction.", "wc-prefix"));
        if (!await ApproveAsync(req, ct))
            throw new SigningRejectedException();
        var result = QubicMessageSigning.SignWalletConnect(seed, message);
        RecordSigned(req);
        return result;
    }

    private static string? TryUtf8(byte[] bytes)
    {
        try
        {
            var text = new System.Text.UTF8Encoding(false, true).GetString(bytes);
            return text.Any(ch => char.IsControl(ch) && ch is not ('\n' or '\r' or '\t')) ? null : text;
        }
        catch
        {
            return null;
        }
    }
}

using Newtonsoft.Json.Linq;
using Qubic.Services;
using Qubic.Services.Features;
using Qubic.Services.Networks;
using Qubic.Services.Signing;
using Reown.Core;
using Reown.Core.Models;
using Reown.Core.Models.MessageHandler;
using Reown.Core.Network.Models;
using Reown.Core.Storage;
using Reown.Sign.Models;
using Reown.Sign.Models.Engine.Events;
using Reown.Sign.Models.Engine.Methods;
using Reown.WalletKit;
using Reown.WalletKit.Interfaces;

namespace Qubic.WalletConnect;

public enum WalletConnectState
{
    Off,
    NeedsProjectId,
    Starting,
    Ready,
    Error,
    /// <summary>Another wallet process on the same data folder runs WalletConnect; this one takes over when it stops.</summary>
    OtherInstance
}

/// <summary>A dApp asking to connect.</summary>
public sealed record WalletConnectProposal(long Id, string Name, string Url, string? Description, string? Icon,
    IReadOnlyList<string> RequestedChains, bool ChainSupported, string Verification, DateTime ReceivedUtc);

/// <summary>A connected dApp session.</summary>
public sealed record WalletConnectSession(string Topic, string Name, string Url, string? Icon,
    IReadOnlyList<string> Accounts, DateTimeOffset? Expiry);

/// <summary>Metadata this wallet presents to dApps.</summary>
public sealed class WalletConnectOptions
{
    public string Name { get; set; } = "Qubic.Net Wallet";
    public string Description { get; set; } = "Qubic.Net Wallet";
    public string Url { get; set; } = "https://qubic.li";
    public string? Icon { get; set; }

    /// <summary>
    /// The app's WalletConnect Cloud (Reown) project id, fixed at build time. Not a secret —
    /// wallets and dApps ship it in their apps.
    /// </summary>
    public string? ProjectId { get; set; }
}

/// <summary>
/// WalletConnect v2 wallet side for the <c>qubic</c> namespace. Runs only while the WalletConnect
/// feature is enabled and a project id is configured. Every dApp request goes through
/// <see cref="ExternalRequestService"/> and therefore the signing review.
/// </summary>
public sealed class QubicWalletConnectService : IAsyncDisposable
{
    public static readonly string[] Methods =
        ["qubic_requestAccounts", "qubic_sendQubic", "qubic_signTransaction", "qubic_sendTransaction", "qubic_sign", "qubic_sendAsset"];
    public static readonly string[] Events = ["accountsChanged", "amountChanged", "assetAmountChanged"];

    private readonly FeatureService _features;
    private readonly QubicSettingsService _settings;
    private readonly NetworkProfileService _profiles;
    private readonly ExternalRequestService _external;
    private readonly EgressGuard _guard;
    private readonly WalletConnectOptions _options;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly Dictionary<long, ProposalStruct> _proposals = [];
    private IWalletKit? _kit;
    private CoreClient? _core;
    private readonly List<IDisposable> _handlers = [];
    private FileStream? _instanceLock;
    private CancellationTokenSource? _waitForLock;

    /// <summary>How often a process in <see cref="WalletConnectState.OtherInstance"/> checks whether it can take over.</summary>
    public static TimeSpan InstanceLockRetryInterval { get; set; } = TimeSpan.FromSeconds(10);

    public WalletConnectState State { get; private set; } = WalletConnectState.Off;
    public string? LastError { get; private set; }

    public event Action? OnStateChanged;
    public event Action<WalletConnectProposal>? OnProposal;

    public QubicWalletConnectService(FeatureService features, QubicSettingsService settings, NetworkProfileService profiles,
        ExternalRequestService external, EgressGuard guard, WalletConnectOptions? options = null)
    {
        _features = features;
        _settings = settings;
        _profiles = profiles;
        _external = external;
        _guard = guard;
        _options = options ?? new WalletConnectOptions();
        _features.OnChanged += f =>
        {
            if (f != WalletFeature.WalletConnect) return;
            _ = _features.IsEnabled(f) ? StartAsync() : StopAsync();
        };
    }

    /// <summary>The app's WalletConnect Cloud (Reown) project id (fixed, from <see cref="WalletConnectOptions"/>).</summary>
    public string? ProjectId => _options.ProjectId;

    public IReadOnlyList<WalletConnectProposal> PendingProposals
    {
        get { lock (_proposals) return _proposals.Values.Select(ToProposal).ToList(); }
    }

    public IReadOnlyList<WalletConnectSession> Sessions =>
        _kit?.ActiveSessions.Values.Select(ToSession).ToList() ?? [];

    // ── Lifecycle ──

    public async Task StartAsync()
    {
        await _lifecycle.WaitAsync();
        try
        {
            if (_kit != null) return;
            if (!_features.IsEnabled(WalletFeature.WalletConnect)) { SetState(WalletConnectState.Off); return; }
            if (string.IsNullOrWhiteSpace(ProjectId)) { SetState(WalletConnectState.NeedsProjectId); return; }
            // One WalletConnect client per data folder: two processes on the same sessions would both
            // answer every dApp request and fight over walletconnect.json.
            if (!TryAcquireInstanceLock())
            {
                SetState(WalletConnectState.OtherInstance);
                WaitForInstanceLock();
                return;
            }

            SetState(WalletConnectState.Starting);
            _guard.AllowFeatureHost("walletconnect", "relay.walletconnect.org");
            _guard.AllowFeatureHost("walletconnect-verify", "verify.walletconnect.org");

            _core = new CoreClient(new CoreOptions
            {
                ProjectId = ProjectId!,
                Name = _options.Name,
                Storage = new FileSystemStorage(Path.Combine(_settings.StorageDirectory, "walletconnect.json")),
                ConnectionBuilder = new Reown.Core.Network.Websocket.WebsocketConnectionBuilder(),
            });
            await _core.Start();
            _kit = await WalletKitClient.Init(_core, new Reown.Core.Metadata
            {
                Name = _options.Name,
                Description = _options.Description,
                Url = _options.Url,
                Icons = _options.Icon != null ? [_options.Icon] : [],
            }, _options.Name);

            _kit.SessionProposed += OnSessionProposed;
            _kit.SessionDeleted += (_, _) => Raise();
            _kit.SessionExpired += (_, _) => Raise();
            _kit.SessionConnected += (_, _) => Raise();
            RegisterHandlers();
            SetState(WalletConnectState.Ready);
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            await DisposeKitAsync();
            SetState(WalletConnectState.Error);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public async Task StopAsync()
    {
        CancelWaitForLock();
        await _lifecycle.WaitAsync();
        try
        {
            await DisposeKitAsync();
            SetState(_features.IsEnabled(WalletFeature.WalletConnect) ? WalletConnectState.Error : WalletConnectState.Off);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    private Task DisposeKitAsync()
    {
        foreach (var h in _handlers) { try { h.Dispose(); } catch { } }
        _handlers.Clear();
        try { (_kit as IDisposable)?.Dispose(); } catch { }
        try { _core?.Dispose(); } catch { }
        _kit = null;
        _core = null;
        lock (_proposals) _proposals.Clear();
        _guard.RevokeFeatureHost("walletconnect");
        _guard.RevokeFeatureHost("walletconnect-verify");
        ReleaseInstanceLock();
        return Task.CompletedTask;
    }

    // ── One WalletConnect client per data folder ──

    private string InstanceLockPath => Path.Combine(_settings.StorageDirectory, "walletconnect.lock");

    private bool TryAcquireInstanceLock()
    {
        if (_instanceLock != null) return true;
        try
        {
            Directory.CreateDirectory(_settings.StorageDirectory);
            // FileShare.None is an OS lock on Windows and an advisory flock on Linux/macOS; both are
            // released automatically if the process dies.
            _instanceLock = new FileStream(InstanceLockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            return true;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private void ReleaseInstanceLock()
    {
        try { _instanceLock?.Dispose(); } catch { }
        _instanceLock = null;
    }

    /// <summary>Retries in the background so this process takes over when the other one stops.</summary>
    private void WaitForInstanceLock()
    {
        if (_waitForLock != null) return;
        var cts = _waitForLock = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(InstanceLockRetryInterval, cts.Token); }
            catch (OperationCanceledException) { return; }
            if (Interlocked.CompareExchange(ref _waitForLock, null, cts) != cts) return;
            cts.Dispose();
            if (State == WalletConnectState.OtherInstance && _features.IsEnabled(WalletFeature.WalletConnect))
                await StartAsync(); // re-arms the wait if the other process still holds the lock
        });
    }

    private void CancelWaitForLock()
    {
        var cts = Interlocked.Exchange(ref _waitForLock, null);
        if (cts == null) return;
        try { cts.Cancel(); cts.Dispose(); } catch { }
    }

    // ── Pairing & sessions ──

    /// <summary>Pairs with a dApp from its <c>wc:</c> URI (QR code or copy/paste, also <c>qubic-wallet://pairwc/wc:…</c>).</summary>
    public async Task PairAsync(string uri)
    {
        if (_kit == null) throw new InvalidOperationException("WalletConnect is not running.");
        uri = uri.Trim();
        const string deepLink = "qubic-wallet://pairwc/";
        if (uri.StartsWith(deepLink, StringComparison.OrdinalIgnoreCase)) uri = uri[deepLink.Length..];
        if (!uri.StartsWith("wc:", StringComparison.OrdinalIgnoreCase))
            throw new FormatException("Not a WalletConnect URI (expected wc:…).");
        await _kit.Pair(uri, true);
    }

    private void OnSessionProposed(object? sender, SessionProposalEvent e)
    {
        lock (_proposals) _proposals[e.Proposal.Id] = e.Proposal;
        var proposal = ToProposal(e.Proposal) with
        {
            Verification = e.VerifiedContext?.Validation.ToString() ?? "Unknown"
        };
        try { OnProposal?.Invoke(proposal); } catch { }
        Raise();
    }

    /// <summary>Approves a proposal, exposing <paramref name="identities"/> on the active network's chain.</summary>
    public async Task ApproveAsync(long proposalId, IReadOnlyList<string> identities)
    {
        if (_kit == null) throw new InvalidOperationException("WalletConnect is not running.");
        if (identities.Count == 0) throw new ArgumentException("Choose at least one account.");
        ProposalStruct proposal;
        lock (_proposals)
        {
            if (!_proposals.Remove(proposalId, out proposal!))
                throw new InvalidOperationException("The connection request is no longer pending.");
        }
        var chain = _profiles.Active.ChainId;
        var ns = new Namespace();
        ns.WithChain(chain);
        foreach (var m in Methods) ns.WithMethod(m);
        foreach (var ev in Events) ns.WithEvent(ev);
        foreach (var id in identities) ns.WithAccount($"{chain}:{id}");
        var namespaces = new Namespaces().WithNamespace("qubic", ns);
        await _kit.ApproveSession(proposalId, namespaces);
        Raise();
    }

    public async Task RejectAsync(long proposalId)
    {
        bool removed;
        lock (_proposals) removed = _proposals.Remove(proposalId);
        if (_kit != null && removed)
            await _kit.RejectSession(proposalId, new Error { Code = ExternalRequestException.UserRejected, Message = "User rejected." });
        Raise();
    }

    public async Task DisconnectAsync(string topic)
    {
        if (_kit == null) return;
        await _kit.DisconnectSession(topic, new Error { Code = 6000, Message = "User disconnected." });
        Raise();
    }

    // ── Requests ──

    private void RegisterHandlers()
    {
        var engine = ((Reown.WalletKit.Controllers.WalletKitEngine)_kit!.Engine).SignClient.Engine;
        Register(engine.SessionRequestEvents<RequestAccountsParams, JToken>(), HandleRequestAccounts);
        Register(engine.SessionRequestEvents<SendQubicParams, JToken>(), HandleSendQubic);
        Register(engine.SessionRequestEvents<SignTransactionParams, JToken>(), (t, p, c, ct) => HandleTransaction(t, p, c, broadcast: false, ct));
        Register(engine.SessionRequestEvents<SendTransactionParams, JToken>(), (t, p, c, ct) => HandleTransaction(t, p, c, broadcast: true, ct));
        Register(engine.SessionRequestEvents<SignParams, JToken>(), HandleSign);
        Register(engine.SessionRequestEvents<SendAssetParams, JToken>(), HandleSendAsset);
    }

    private delegate Task<JToken> Handler<T>(string topic, T p, SigningContext context, CancellationToken ct);

    private void Register<T>(TypedEventHandler<T, JToken> events, Handler<T> handler) where T : QubicWcParams
    {
        events.OnRequest += async args =>
        {
            try
            {
                var session = _kit!.ActiveSessions.TryGetValue(args.Topic, out var s) ? s : null;
                var sessionChain = SessionChain(args.Topic);
                if (sessionChain != null && sessionChain != _profiles.Active.ChainId)
                    throw new ExternalRequestException(ExternalRequestException.InvalidParams,
                        $"This session is for {sessionChain}, but the wallet is on {_profiles.Active.ChainId} (\"{_profiles.Active.Name}\").");
                var method = Reown.Core.Network.Models.RpcMethodAttribute.MethodForType<T>();
                var context = new SigningContext(SigningOrigin.WalletConnect,
                    RequestDescription(method),
                    session?.Peer?.Metadata?.Name ?? "dApp",
                    session?.Peer?.Metadata?.Url);
                args.Response = await handler(args.Topic, args.Request.Params, context, CancellationToken.None);
            }
            catch (ExternalRequestException ex)
            {
                args.Error = new Error { Code = ex.Code, Message = ex.Message };
            }
            catch (FormatException ex)
            {
                args.Error = new Error { Code = ExternalRequestException.InvalidParams, Message = ex.Message };
            }
            catch (Exception ex)
            {
                args.Error = new Error { Code = ExternalRequestException.InternalError, Message = ex.Message };
            }
        };
        _handlers.Add(new ActionDisposable(events.Dispose));
    }

    private static string RequestDescription(string method) => method switch
    {
        "qubic_sendQubic" => "Send QU",
        "qubic_signTransaction" => "Sign a transaction (the dApp may broadcast it later)",
        "qubic_sendTransaction" => "Sign and broadcast a transaction",
        "qubic_sign" => "Sign a message",
        "qubic_sendAsset" => "Send asset shares",
        _ => method
    };

    /// <summary>CAIP-2 chain the session's accounts were approved on (qubic:mainnet / qubic:testnet).</summary>
    private string? SessionChain(string topic)
    {
        if (_kit?.ActiveSessions.TryGetValue(topic, out var s) != true || s!.Namespaces == null) return null;
        var first = s.Namespaces.TryGetValue("qubic", out var ns) ? ns.Accounts?.FirstOrDefault() : null;
        var parts = first?.Split(':');
        return parts is { Length: 3 } ? $"{parts[0]}:{parts[1]}" : null;
    }

    private IReadOnlyList<string> SessionAccounts(string topic)
    {
        if (_kit?.ActiveSessions.TryGetValue(topic, out var s) != true || s!.Namespaces == null) return [];
        return s.Namespaces.TryGetValue("qubic", out var ns) && ns.Accounts != null
            ? ns.Accounts.Select(a => a.Split(':').Last()).ToList()
            : [];
    }

    private void EnsureSessionAccount(string topic, string? from)
    {
        if (string.IsNullOrEmpty(from)) throw new ExternalRequestException(ExternalRequestException.InvalidParams, "from is required.");
        if (!SessionAccounts(topic).Contains(from.Trim().ToUpperInvariant()))
            throw new ExternalRequestException(ExternalRequestException.InvalidParams, "from is not an account of this session.");
    }

    private async Task<JToken> HandleRequestAccounts(string topic, RequestAccountsParams p, SigningContext context, CancellationToken ct)
    {
        var accounts = await _external.GetAccountsAsync(SessionAccounts(topic), ct);
        return JArray.FromObject(accounts.Select(a => new
        {
            address = a.Address,
            name = a.Name,
            alias = a.Name,
            amount = a.Amount,
            assets = a.Assets.Select(x => new { assetName = x.AssetName, issuerIdentity = x.IssuerIdentity, ownedAmount = x.OwnedAmount, managingContractIndex = x.ManagingContractIndex })
        }));
    }

    private async Task<JToken> HandleSendQubic(string topic, SendQubicParams p, SigningContext context, CancellationToken ct)
    {
        EnsureSessionAccount(topic, p.Str("from"));
        var amount = p.Long("amount") ?? throw new ExternalRequestException(ExternalRequestException.InvalidParams, "amount is required.");
        if (amount <= 0) throw new ExternalRequestException(ExternalRequestException.InvalidParams, "amount must be positive.");
        var result = await _external.SendTransactionAsync(new ExternalTxParams(p.Str("from")!, p.Str("to") ?? "", amount), context, ct);
        return JObject.FromObject(new { transactionId = result.TransactionId, tick = result.Tick });
    }

    private async Task<JToken> HandleTransaction(string topic, QubicWcParams p, SigningContext context, bool broadcast, CancellationToken ct)
    {
        EnsureSessionAccount(topic, p.Str("from"));
        var amount = p.Long("amount") ?? 0;
        var tick = p.Long("tick");
        var inputType = p.Long("inputType") ?? 0;
        if (inputType is < 0 or > ushort.MaxValue) throw new ExternalRequestException(ExternalRequestException.InvalidParams, "inputType out of range.");
        byte[]? payload = null;
        if (p.Str("payload") is { Length: > 0 } b64)
        {
            try { payload = Convert.FromBase64String(b64); }
            catch (FormatException) { throw new ExternalRequestException(ExternalRequestException.InvalidParams, "payload must be base64."); }
        }
        var txParams = new ExternalTxParams(p.Str("from")!, p.Str("to") ?? "", amount,
            tick is > 0 ? (uint)tick.Value : null, (ushort)inputType, payload);

        var result = broadcast
            ? await _external.SendTransactionAsync(txParams, context, ct)
            : await _external.SignTransactionAsync(txParams, context, ct);

        // Official wallet fields + the field names @qubic.org/react's connector reads.
        var obj = new JObject
        {
            ["transactionId"] = result.TransactionId,
            ["tick"] = result.Tick,
            ["txId"] = result.TransactionId,
            ["targetTick"] = result.Tick,
            ["txBytesBase64"] = result.RawBase64,
            ["txBytesHex"] = result.RawHex,
        };
        if (!broadcast) obj["signedTransaction"] = result.RawBase64;
        else obj["networkTxId"] = result.TransactionId;
        return obj;
    }

    private async Task<JToken> HandleSign(string topic, SignParams p, SigningContext context, CancellationToken ct)
    {
        var from = p.Str("from");
        EnsureSessionAccount(topic, from);
        var message = p.Str("message") ?? throw new ExternalRequestException(ExternalRequestException.InvalidParams, "message is required.");
        var sig = await _external.SignMessageWalletConnectAsync(from!, message, context, ct);
        return new JObject
        {
            ["signedData"] = sig.SignedDataBase64,
            ["digest"] = sig.DigestBase64,
            ["signature"] = sig.SignatureBase64,
            ["signatureHex"] = Convert.ToHexString(sig.Signature).ToLowerInvariant(),
            ["digestHex"] = Convert.ToHexString(sig.Digest).ToLowerInvariant(),
        };
    }

    private async Task<JToken> HandleSendAsset(string topic, SendAssetParams p, SigningContext context, CancellationToken ct)
    {
        var from = p.Str("from");
        EnsureSessionAccount(topic, from);
        var amount = p.Long("amount") ?? throw new ExternalRequestException(ExternalRequestException.InvalidParams, "amount is required.");
        var result = await _external.SendAssetAsync(from!, p.Str("to") ?? "", p.Str("assetName") ?? "", p.Str("issuer") ?? "", amount, context, ct);
        return JObject.FromObject(new { transactionId = result.TransactionId, tick = result.Tick });
    }

    // ── helpers ──

    private WalletConnectProposal ToProposal(ProposalStruct p)
    {
        var md = p.Proposer?.Metadata;
        var chains = new List<string>();
        if (p.RequiredNamespaces != null && p.RequiredNamespaces.TryGetValue("qubic", out var req) && req.Chains != null) chains.AddRange(req.Chains);
        if (p.OptionalNamespaces != null && p.OptionalNamespaces.TryGetValue("qubic", out var opt) && opt.Chains != null) chains.AddRange(opt.Chains);
        var supported = chains.Count == 0 || chains.Contains(_profiles.Active.ChainId);
        return new WalletConnectProposal(p.Id, md?.Name ?? "Unknown dApp", md?.Url ?? "", md?.Description,
            md?.Icons?.FirstOrDefault(), chains.Distinct().ToList(), supported, "Unknown", DateTime.UtcNow);
    }

    private static WalletConnectSession ToSession(Session s)
    {
        var accounts = s.Namespaces != null && s.Namespaces.TryGetValue("qubic", out var ns) && ns.Accounts != null
            ? ns.Accounts.ToList()
            : [];
        DateTimeOffset? expiry = s.Expiry is { } e ? DateTimeOffset.FromUnixTimeSeconds(e) : null;
        return new WalletConnectSession(s.Topic, s.Peer?.Metadata?.Name ?? "dApp", s.Peer?.Metadata?.Url ?? "",
            s.Peer?.Metadata?.Icons?.FirstOrDefault(), accounts, expiry);
    }

    private void SetState(WalletConnectState state)
    {
        State = state;
        if (state is WalletConnectState.Ready or WalletConnectState.Off) LastError = null;
        Raise();
    }

    private void Raise()
    {
        try { OnStateChanged?.Invoke(); } catch { }
    }

    public async ValueTask DisposeAsync()
    {
        CancelWaitForLock();
        await _lifecycle.WaitAsync();
        try { await DisposeKitAsync(); }
        finally { _lifecycle.Release(); }
    }

    private sealed class ActionDisposable(Action action) : IDisposable
    {
        public void Dispose() => action();
    }
}

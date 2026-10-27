using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Qubic.Core;
using Qubic.Core.Contracts.Qx;
using Qubic.Core.Entities;
using Qubic.Crypto;
using Qubic.Services.Contracts;
using Qubic.Services.Networks;
using Qubic.Services.Signing;

namespace Qubic.Services.Tests;

/// <summary>
/// Minimal Bob JSON-RPC server (HTTP only) speaking the subset of methods the wallet uses,
/// with the AIO devnet's computor list. Records every method call and broadcast transaction.
/// </summary>
internal sealed class FakeBobServer : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _cts = new();
    private uint _tick = 80_371_600;

    public string Url { get; }
    public ConcurrentQueue<string> Methods { get; } = new();
    public ConcurrentQueue<byte[]> Broadcasts { get; } = new();

    public FakeBobServer()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        Url = $"http://127.0.0.1:{port}";
        _listener.Prefixes.Add(Url + "/");
        _listener.Start();
        _ = Task.Run(LoopAsync);
    }

    private async Task LoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync(); }
            catch { return; }
            _ = Task.Run(() => Handle(ctx));
        }
    }

    private void Handle(HttpListenerContext ctx)
    {
        using var reader = new StreamReader(ctx.Request.InputStream);
        using var doc = JsonDocument.Parse(reader.ReadToEnd());
        var method = doc.RootElement.GetProperty("method").GetString()!;
        var id = doc.RootElement.GetProperty("id").GetInt32();
        var p = doc.RootElement.TryGetProperty("params", out var pp) ? pp : default;
        Methods.Enqueue(method);

        object? result = method switch
        {
            "qubic_getTickNumber" => Interlocked.Increment(ref _tick),
            "qubic_getCurrentEpoch" => new { epoch = 231, initialTick = 80_371_514 },
            "qubic_getBalance" => new { identity = p[0].GetString(), balance = 10_000_000_000L, currentTick = _tick },
            "qubic_getComputors" => new
            {
                epoch = 231,
                computors = CoreLiteDevSeeds.Accounts.Where(a => a.IsComputor).Select(a => a.Identity).ToArray(),
                signature = new string('0', 128)
            },
            "qubic_querySmartContract" => new { nonce = 1, data = "00ca9a3b0000000000000000" }, // QX Fees: 1e9, 0, 0
            "qubic_broadcastTransaction" => Broadcast(p[0].GetString()!),
            "qubic_getTransactionByHash" => new { txHash = p[0].GetString(), sourceId = CoreLiteDevSeeds.Accounts[0].Identity, destId = CoreLiteDevSeeds.Accounts[1].Identity, amount = "5", tick = _tick - 5, inputType = 0 },
            "qubic_getTransactionReceipt" => new { txHash = p[0].GetString(), status = true },
            _ => null
        };

        var body = result == null
            ? JsonSerializer.Serialize(new { jsonrpc = "2.0", id, error = new { code = -32601, message = "Method not found" } })
            : JsonSerializer.Serialize(new { jsonrpc = "2.0", id, result });
        var bytes = Encoding.UTF8.GetBytes(body);
        ctx.Response.ContentType = "application/json";
        ctx.Response.OutputStream.Write(bytes);
        ctx.Response.Close();
    }

    private string Broadcast(string hex)
    {
        var raw = Convert.FromHexString(hex);
        Broadcasts.Enqueue(raw);
        var crypt = new QubicCrypt();
        return crypt.GetHumanReadableBytes(crypt.KangarooTwelve(raw));
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _listener.Stop(); } catch { }
        _listener.Close();
    }
}

/// <summary>
/// Acceptance gate for "AIO works through Bob without falling back to RPC":
/// a Bob-only profile with the egress guard in blocking mode must serve every
/// routed operation, report unsupported ones as unavailable, and never contact
/// a host outside the profile.
/// </summary>
public class BobOnlyIntegrationTests : SettingsTestBase
{
    private (NetworkProfileService Profiles, EgressGuard Guard, QubicBackendService Backend, FakeBobServer Bob) Setup()
    {
        var bob = new FakeBobServer();
        var profiles = new NetworkProfileService(NewSettings());
        var profile = new NetworkProfile
        {
            Id = "bob-only",
            Name = "Bob only (test)",
            Kind = NetworkKind.Devnet,
            Methods = [new BackendMethod { Kind = BackendMethodKind.Bob, Url = bob.Url, UseWebSocket = false }],
            PendingTxWindowTicks = 32,
            ExpectedFingerprint = NetworkFingerprintKind.CoreLiteDevnet,
        };
        profiles.Save(profile);
        profiles.SetActive(profile.Id);
        var guard = new EgressGuard(profiles) { Mode = EgressGuardMode.Block };
        return (profiles, guard, new QubicBackendService(profiles, guard), bob);
    }

    [Fact]
    public async Task EverythingRoutedStaysInsideTheProfile()
    {
        var (profiles, guard, backend, bob) = Setup();
        using var _ = bob;

        // Tick + monitor (polling via Bob since WS is off)
        var tickInfo = await backend.GetTickInfoAsync();
        Assert.Equal(231, tickInfo.Epoch);
        using var tickMonitor = new TickMonitorService(backend);
        await tickMonitor.StartAsync();
        for (int i = 0; i < 50 && !tickMonitor.IsConnected; i++) await Task.Delay(50);
        Assert.True(tickMonitor.IsConnected);

        // Fingerprint proves the core-lite devnet
        var fingerprint = new NetworkFingerprintService(backend, profiles);
        var fp = await fingerprint.CheckAsync();
        Assert.Equal(NetworkFingerprintKind.CoreLiteDevnet, fp.Kind);
        Assert.True(fingerprint.IsCoreLiteDevnet);
        Assert.False(fingerprint.IsMismatch);

        // Balance and contract query via Bob
        var dev = CoreLiteDevSeeds.Accounts[0];
        Assert.Equal(10_000_000_000L, (await backend.GetBalanceAsync(QubicIdentity.FromIdentity(dev.Identity))).Amount);
        var fees = FeesOutput.FromBytes(await backend.QuerySmartContractAsync(1, QxContract.Functions.Fees, []));
        Assert.Equal(1_000_000_000u, fees.AssetIssuanceFee);

        // Reviewed signing (no presenter → in-app passes) + broadcast via Bob
        using var settings = new TempVault(AppName);
        var contracts = new ContractCatalogService(settings.Settings, profiles, backend);
        var review = new SigningReviewService(profiles, fingerprint, settings.Vault, contracts, tickMonitor);
        var target = await TransactionHelper.ResolveTickTargetAsync(backend, tickMonitor);
        var tx = await review.SignTransactionAsync(dev.Seed, "dev #0",
            QubicIdentity.FromIdentity(CoreLiteDevSeeds.Accounts[1].Identity), 5, target.TargetTick, null, SigningContext.InApp("test"));
        var broadcast = await backend.BroadcastTransactionAsync(tx);
        Assert.Equal(tx.TransactionHash, broadcast.TransactionId);
        Assert.Single(bob.Broadcasts);
        Assert.Equal(tx.GetRawBytes(), bob.Broadcasts.Single());

        // Lookup and receipt via Bob
        var lookup = await backend.GetTransactionByHashAsync(broadcast.TransactionId);
        Assert.NotNull(lookup);
        Assert.Equal(CoreLiteDevSeeds.Accounts[0].Identity, lookup!.Source);
        Assert.True((await backend.GetTransactionReceiptAsync(broadcast.TransactionId))!.Status);

        // Not served by Bob → reported as unavailable, not routed anywhere else
        Assert.False(backend.IsAvailable(Capability.AssetListings));
        await Assert.ThrowsAsync<CapabilityUnavailableException>(() => backend.GetOwnedAssetsAsync(QubicIdentity.FromIdentity(dev.Identity)));
        await Assert.ThrowsAsync<CapabilityUnavailableException>(() => backend.GetActiveIposAsync());
        await Assert.ThrowsAsync<CapabilityUnavailableException>(() => backend.GetIpoStatusAsync(1));
        await Assert.ThrowsAsync<CapabilityUnavailableException>(() => backend.GetTickDataAsync(1));

        // Mainnet-only data sources stay off
        var staticData = new QubicStaticService(profiles, guard);
        Assert.False(await staticData.LoadAsync());
        var registry = new AssetRegistryService(settings.Settings, profiles);
        Assert.Equal(-1, await registry.RefreshFromNetworkAsync(backend, force: true));

        // Drift check only polls methods inside the profile
        var drift = new TickDriftService(backend, settings.Settings);
        drift.Start();
        for (int i = 0; i < 50 && drift.BobTick == null; i++) await Task.Delay(50);
        drift.Stop();
        Assert.NotNull(drift.BobTick);
        Assert.Null(drift.RpcTick);
        Assert.Null(drift.DirectTick);

        await tickMonitor.StopAsync();

        Assert.Empty(guard.Violations);
        Assert.DoesNotContain(bob.Methods, m => m == "qubic_getAssets");
    }

    [Fact]
    public async Task ForeignHostIsBlocked_EvenIfCodeTriesIt()
    {
        var (_, guard, backend, bob) = Setup();
        using var _ = bob;
        using var http = guard.CreateHttpClient("rogue");
        await Assert.ThrowsAsync<EgressBlockedException>(() => http.GetAsync("https://rpc.qubic.org/live/v1/tick-info"));
        Assert.Throws<EgressBlockedException>(() => backend.CreateNodeClient("corenet.qubic.li", 21841));
        Assert.Equal(2, guard.Violations.Count);
    }

    [Fact]
    public async Task SigningReview_WarnsAboutPublicDevKeysOnMainnet_AndCrossNetworkReplay()
    {
        var (profiles, _, backend, bob) = Setup();
        using var _b = bob;
        using var temp = new TempVault(AppName);
        using var tickMonitor = new TickMonitorService(backend);
        var fingerprint = new NetworkFingerprintService(backend, profiles);
        var contracts = new ContractCatalogService(temp.Settings, profiles, backend);
        var review = new SigningReviewService(profiles, fingerprint, temp.Vault, contracts, tickMonitor);

        // Sign once on the devnet with a vault seed.
        const string mySeed = "abcdefghijklmnopqrstuvwxyzabcdefghijklmnopqrstuvwxyzabc";
        var myId = QubicIdentity.FromSeed(mySeed);
        temp.Vault.AddEntry("mine", mySeed);
        await review.SignTransactionAsync(mySeed, "mine", myId, 1, 100, null, SigningContext.InApp());
        for (int i = 0; i < 50 && temp.Vault.GetSignedOn(myId.ToString()).Count == 0; i++) await Task.Delay(50);
        Assert.Contains("test", temp.Vault.GetSignedOn(myId.ToString()));

        // Switch to Mainnet: same seed now gets a replay Danger warning.
        profiles.SetActive(NetworkPresets.MainnetId);
        var req = review.BuildTransactionRequest(SigningContext.InApp(), myId.ToString(), "mine", myId, 1, 200, 0, null);
        Assert.Contains(req.Warnings, w => w.Code == "cross-network" && w.Level == WarningLevel.Danger);

        // Funds to a public dev key on Mainnet: Danger.
        var devDest = QubicIdentity.FromIdentity(CoreLiteDevSeeds.Accounts[3].Identity);
        var req2 = review.BuildTransactionRequest(SigningContext.InApp(), myId.ToString(), "mine", devDest, 1, 200, 0, null);
        Assert.Contains(req2.Warnings, w => w.Code == "public-dev-destination");

        // External requests are rejected when no presenter is registered.
        await Assert.ThrowsAsync<SigningRejectedException>(() =>
            review.SignRawMessageAsync(mySeed, "mine", Encoding.UTF8.GetBytes("hi"), new SigningContext(SigningOrigin.WalletConnect)));
    }

    [Fact]
    public async Task SigningReview_DecodesContractCalls()
    {
        var (profiles, _, backend, bob) = Setup();
        using var _b = bob;
        using var temp = new TempVault(AppName);
        using var tickMonitor = new TickMonitorService(backend);
        var review = new SigningReviewService(profiles, new NetworkFingerprintService(backend, profiles), temp.Vault,
            new ContractCatalogService(temp.Settings, profiles, backend), tickMonitor);

        var payload = new IssueAssetPayload { AssetName = AssetNameHelper.ToUlong("DEVCOIN"), NumberOfShares = 1000 };
        var req = review.BuildTransactionRequest(SigningContext.InApp(), CoreLiteDevSeeds.Accounts[0].Identity, null,
            QubicContracts.GetContractIdentity(1), 1_000_000_000, 100, payload.InputType, payload.GetPayloadBytes());
        Assert.Equal("Qx: IssueAsset", req.Title);
        Assert.True(req.Decoded!.SizeMatches);
        Assert.Contains(req.Decoded.Fields, f => f.Name == "numberOfShares" && f.Display == "1000");
    }

    /// <summary>Unlocked temp vault + settings sharing the test's app dir.</summary>
    private sealed class TempVault : IDisposable
    {
        public QubicSettingsService Settings { get; }
        public VaultService Vault { get; }

        public TempVault(string appName)
        {
            Settings = new QubicSettingsService(appName);
            Vault = new VaultService(Settings);
            var path = Path.Combine(Settings.StorageDirectory, $"vault-{Guid.NewGuid():N}.dat");
            Vault.CreateVault(path, "tr0ub4dor-correct-horse-battery",
                [new VaultEntry { Label = "placeholder", Seed = "zyxwvutsrqponmlkjihgfedcbazyxwvutsrqponmlkjihgfedcbazyx" }]);
        }

        public void Dispose() => Vault.LockVault();
    }
}

/// <summary>dApp-facing request handling (shared by WalletConnect and the local endpoint), against the fake Bob.</summary>
public class ExternalRequestTests : SettingsTestBase
{
    private sealed class FixedPresenter(bool approve) : ISigningPresenter
    {
        public List<SigningRequest> Seen { get; } = [];
        public Task<bool> PresentAsync(SigningRequest request, CancellationToken ct)
        {
            Seen.Add(request);
            return Task.FromResult(approve);
        }
    }

    private async Task<(ExternalRequestService Svc, FixedPresenter Presenter, FakeBobServer Bob, SeedSessionService Seed, TickMonitorService Tick)> SetupAsync(bool approve)
    {
        var bob = new FakeBobServer();
        var settings = NewSettings();
        var profiles = new NetworkProfileService(settings);
        profiles.Save(new NetworkProfile
        {
            Id = "bob-only", Name = "Bob only (test)", Kind = NetworkKind.Devnet,
            Methods = [new BackendMethod { Kind = BackendMethodKind.Bob, Url = bob.Url, UseWebSocket = false }],
        });
        profiles.SetActive("bob-only");
        var guard = new EgressGuard(profiles);
        var backend = new QubicBackendService(profiles, guard);
        var tick = new TickMonitorService(backend);
        await tick.StartAsync();
        for (int i = 0; i < 50 && !tick.IsConnected; i++) await Task.Delay(50);
        var vault = new VaultService(settings);
        var fingerprint = new NetworkFingerprintService(backend, profiles);
        var contracts = new ContractCatalogService(settings, profiles, backend);
        var review = new SigningReviewService(profiles, fingerprint, vault, contracts, tick);
        var presenter = new FixedPresenter(approve);
        review.Presenter = presenter;
        var seed = new SeedSessionService(review);
        var storage = new Storage.WalletStorageService(new Storage.WalletDatabase(),
            new Storage.WalletSyncService(backend, new Storage.WalletDatabase()), settings, backend, profiles, new NetworkChainState(settings));
        var tracker = new TransactionTrackerService(backend, tick, settings, seed, storage);
        var features = new Features.FeatureService(settings);
        var dev = new DevTools.DevToolsService(features, fingerprint, profiles, backend, tick, review, settings);
        return (new ExternalRequestService(seed, vault, backend, tick, review, tracker, dev), presenter, bob, seed, tick);
    }

    [Fact]
    public async Task SendQubic_FromActiveSeed_IsReviewedSignedAndBroadcast()
    {
        var (svc, presenter, bob, seed, tick) = await SetupAsync(approve: true);
        using var _ = bob; using var __ = tick;
        var dev = CoreLiteDevSeeds.Accounts[5];
        seed.SetSeed(dev.Seed, "dev 5");
        var to = CoreLiteDevSeeds.Accounts[6].Identity;

        var result = await svc.SendTransactionAsync(new ExternalTxParams(dev.Identity, to, 1234),
            new SigningContext(SigningOrigin.WalletConnect, "Send QU", "Test dApp", "https://dapp.example"));

        Assert.Single(presenter.Seen);
        Assert.Equal("Test dApp", presenter.Seen[0].Context.RequesterName);
        Assert.Equal(1234, presenter.Seen[0].Amount);
        Assert.Single(bob.Broadcasts);
        Assert.Equal(result.RawTransaction, bob.Broadcasts.Single());
        Assert.Equal(60, result.TransactionId.Length);
    }

    [Fact]
    public async Task Rejection_MapsToUserRejected()
    {
        var (svc, _, bob, seed, tick) = await SetupAsync(approve: false);
        using var _b = bob; using var _t = tick;
        var dev = CoreLiteDevSeeds.Accounts[5];
        seed.SetSeed(dev.Seed);
        var ex = await Assert.ThrowsAsync<ExternalRequestException>(() =>
            svc.SignTransactionAsync(new ExternalTxParams(dev.Identity, dev.Identity, 1), new SigningContext(SigningOrigin.WalletConnect)));
        Assert.Equal(ExternalRequestException.UserRejected, ex.Code);
        Assert.Empty(bob.Broadcasts);
    }

    [Fact]
    public async Task ExpiredTick_And_UnknownAccount_AreRejectedBeforeReview()
    {
        var (svc, presenter, bob, seed, tick) = await SetupAsync(approve: true);
        using var _b = bob; using var _t = tick;
        var dev = CoreLiteDevSeeds.Accounts[5];
        seed.SetSeed(dev.Seed);

        var expired = await Assert.ThrowsAsync<ExternalRequestException>(() =>
            svc.SignTransactionAsync(new ExternalTxParams(dev.Identity, dev.Identity, 1, Tick: 5), new SigningContext(SigningOrigin.WalletConnect)));
        Assert.Equal(ExternalRequestException.TickExpired, expired.Code);

        var unknown = await Assert.ThrowsAsync<ExternalRequestException>(() =>
            svc.SignTransactionAsync(new ExternalTxParams(CoreLiteDevSeeds.Accounts[7].Identity, dev.Identity, 1), new SigningContext(SigningOrigin.WalletConnect)));
        Assert.Equal(ExternalRequestException.InvalidParams, unknown.Code);
        Assert.Empty(presenter.Seen);
    }

    [Fact]
    public async Task SignMessage_UsesWalletConnectConvention()
    {
        var (svc, presenter, bob, seed, tick) = await SetupAsync(approve: true);
        using var _b = bob; using var _t = tick;
        var dev = CoreLiteDevSeeds.Accounts[5];
        seed.SetSeed(dev.Seed);
        var sig = await svc.SignMessageWalletConnectAsync(dev.Identity, "hello dApp", new SigningContext(SigningOrigin.WalletConnect));
        Assert.True(Qubic.Core.Auth.QubicMessageSigning.VerifyWalletConnect(dev.Identity, "hello dApp", sig.Signature));
        Assert.Contains(presenter.Seen.Single().Warnings, w => w.Code == "wc-prefix");
    }
}

using Qubic.Services;
using Qubic.Services.Contracts;
using Qubic.Services.DevTools;
using Qubic.Services.Features;
using Qubic.Services.Networks;
using Qubic.Services.Signing;
using Qubic.Services.Storage;

namespace Qubic.WalletConnect.Tests;

/// <summary>
/// Two wallet processes on the same data folder must not both run WalletConnect: they would both
/// answer every dApp request and write walletconnect.json concurrently.
/// </summary>
public class InstanceLockTests : IDisposable
{
    private readonly QubicSettingsService _settings = new($"wc-lock-tests.{Guid.NewGuid():N}");
    private readonly FeatureService _features;
    private readonly QubicWalletConnectService _wallet;

    public InstanceLockTests()
    {
        var profiles = new NetworkProfileService(_settings);
        var guard = new EgressGuard(profiles) { Mode = EgressGuardMode.Log };
        var backend = new QubicBackendService(profiles, guard);
        var tick = new TickMonitorService(backend);
        var vault = new VaultService(_settings);
        var fingerprint = new NetworkFingerprintService(backend, profiles);
        var review = new SigningReviewService(profiles, fingerprint, vault, new ContractCatalogService(_settings, profiles, backend), tick);
        var seed = new SeedSessionService(review);
        var storage = new WalletStorageService(new WalletDatabase(), new WalletSyncService(backend, new WalletDatabase()),
            _settings, backend, profiles, new NetworkChainState(_settings));
        var tracker = new TransactionTrackerService(backend, tick, _settings, seed, storage);
        _features = new FeatureService(_settings);
        _features.SetEnabled(WalletFeature.WalletConnect, true);
        var external = new ExternalRequestService(seed, vault, backend, tick, review, tracker,
            new DevToolsService(_features, fingerprint, profiles, backend, tick, review, _settings));
        _wallet = new QubicWalletConnectService(_features, _settings, profiles, external, guard,
            new WalletConnectOptions { ProjectId = "test-project-id" });
    }

    private FileStream HoldLock()
    {
        Directory.CreateDirectory(_settings.StorageDirectory);
        return new FileStream(Path.Combine(_settings.StorageDirectory, "walletconnect.lock"),
            FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }

    [Fact]
    public async Task Start_WhenAnotherProcessHoldsTheLock_ReportsOtherInstanceWithoutError()
    {
        using var other = HoldLock();

        await _wallet.StartAsync();

        Assert.Equal(WalletConnectState.OtherInstance, _wallet.State);
        Assert.Null(_wallet.LastError);
        Assert.Empty(_wallet.Sessions);
    }

    [Fact]
    public async Task DisablingTheFeature_WhileWaiting_StopsRetrying()
    {
        QubicWalletConnectService.InstanceLockRetryInterval = TimeSpan.FromMilliseconds(100);
        using (HoldLock())
        {
            await _wallet.StartAsync();
            Assert.Equal(WalletConnectState.OtherInstance, _wallet.State);
            _features.SetEnabled(WalletFeature.WalletConnect, false);
        }

        await Task.Delay(400);
        Assert.Equal(WalletConnectState.Off, _wallet.State);
    }

    public void Dispose()
    {
        _wallet.DisposeAsync().AsTask().GetAwaiter().GetResult();
        QubicWalletConnectService.InstanceLockRetryInterval = TimeSpan.FromSeconds(10);
        try { Directory.Delete(_settings.StorageDirectory, true); } catch { }
    }
}

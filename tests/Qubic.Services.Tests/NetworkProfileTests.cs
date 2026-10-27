using System.Text.Json;
using Qubic.Crypto;
using Qubic.Services.Networks;

namespace Qubic.Services.Tests;

/// <summary>Shared temp settings dir per test class.</summary>
public abstract class SettingsTestBase : IDisposable
{
    protected readonly string AppName = $"Qubic.Services.Tests.{Guid.NewGuid():N}";

    protected QubicSettingsService NewSettings() => new(AppName);

    public void Dispose()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppName);
        try { Directory.Delete(dir, true); } catch { }
    }
}

public class NetworkProfileTests : SettingsTestBase
{
    [Fact]
    public void FirstRun_MigratesLegacySettings_IntoMainnetProfile_WithDefaultBackendFirst()
    {
        var s = NewSettings();
        s.DefaultBackend = "Bob";
        s.BobUrl = "https://my-bob.example";
        s.NodeHost = "1.2.3.4";
        s.TickOffset = 9;

        var profiles = new NetworkProfileService(s);

        var mainnet = profiles.Active;
        Assert.Equal(NetworkPresets.MainnetId, mainnet.Id);
        Assert.Equal(BackendMethodKind.Bob, mainnet.Methods[0].Kind);
        Assert.Equal("https://my-bob.example", mainnet.GetMethod(BackendMethodKind.Bob)!.Url);
        Assert.Equal("1.2.3.4", mainnet.GetMethod(BackendMethodKind.Direct)!.Host);
        Assert.Equal(9, mainnet.TickOffset);
        Assert.Contains(profiles.Profiles, p => p.Id == NetworkPresets.AioLocalId);
    }

    [Fact]
    public void Profiles_PersistAcrossInstances()
    {
        var s = NewSettings();
        var profiles = new NetworkProfileService(s);
        profiles.SetActive(NetworkPresets.AioLocalId);
        var custom = profiles.Duplicate(NetworkPresets.AioLocalId);

        var reloaded = new NetworkProfileService(new QubicSettingsService(AppName));
        Assert.Equal(NetworkPresets.AioLocalId, reloaded.ActiveId);
        Assert.NotNull(reloaded.Get(custom.Id));
        Assert.Equal(NetworkKind.Devnet, reloaded.Active.Kind);
    }

    [Fact]
    public void Validate_RejectsProfileWithoutEnabledMethods()
    {
        var p = NetworkPresets.AioLocal();
        foreach (var m in p.Methods) m.Enabled = false;
        Assert.Throws<ArgumentException>(() => NetworkProfileService.Validate(p));
    }

    [Fact]
    public void ActiveProfile_CannotBeDeleted()
    {
        var profiles = new NetworkProfileService(NewSettings());
        Assert.Throws<InvalidOperationException>(() => profiles.Delete(profiles.ActiveId));
    }

    [Theory]
    [InlineData(Capability.TickInfo, BackendMethodKind.Bob)]
    [InlineData(Capability.Broadcast, BackendMethodKind.Bob)]
    [InlineData(Capability.Balance, BackendMethodKind.Bob)]
    [InlineData(Capability.SmartContractQuery, BackendMethodKind.Bob)]
    [InlineData(Capability.TransactionLookup, BackendMethodKind.Bob)]
    [InlineData(Capability.AssetListings, BackendMethodKind.Rpc)]   // Bob's qubic_getAssets is a stub → core-lite HTTP
    [InlineData(Capability.ActiveIpos, BackendMethodKind.Rpc)]
    [InlineData(Capability.ContractRegistry, BackendMethodKind.Rpc)]
    public void AioPreset_RoutesBobFirst_AndCoreLiteForTheRest(Capability cap, BackendMethodKind expected)
    {
        Assert.Equal(expected, Capabilities.Resolve(NetworkPresets.AioLocal(), cap)!.Kind);
    }

    [Fact]
    public void AioPreset_HasNoIpoStatusOrPeers_BecauseDirectIsOff()
    {
        var aio = NetworkPresets.AioLocal();
        Assert.Null(Capabilities.Resolve(aio, Capability.IpoStatus));
        Assert.Null(Capabilities.Resolve(aio, Capability.Peers));
    }

    [Fact]
    public void BobOnlyProfile_HasNoAssetListings()
    {
        var p = NetworkPresets.AioLocal();
        p.Methods.RemoveAll(m => m.Kind != BackendMethodKind.Bob);
        Assert.Null(Capabilities.Resolve(p, Capability.AssetListings));
        var ex = new CapabilityUnavailableException(Capability.AssetListings, p.Name);
        Assert.Contains("RPC or Direct", ex.Message);
    }

    [Fact]
    public void PerOperationRoute_OverridesPriority_AndIsStrict()
    {
        var p = NetworkPresets.Mainnet();                       // priority Rpc, Bob, Direct
        Assert.Equal(BackendMethodKind.Rpc, Capabilities.Resolve(p, Capability.Balance)!.Kind);

        p.Routes = new() { [Capability.Balance] = BackendMethodKind.Direct, [Capability.Broadcast] = BackendMethodKind.Bob };
        Assert.Equal(BackendMethodKind.Direct, Capabilities.Resolve(p, Capability.Balance)!.Kind);
        Assert.Equal(BackendMethodKind.Bob, Capabilities.Resolve(p, Capability.Broadcast)!.Kind);
        Assert.Equal(BackendMethodKind.Rpc, Capabilities.Resolve(p, Capability.TickInfo)!.Kind);   // not pinned → Auto

        p.Methods.First(m => m.Kind == BackendMethodKind.Direct).Enabled = false;
        Assert.Null(Capabilities.Resolve(p, Capability.Balance));                                  // strict: no silent switch
        Assert.True(Capabilities.IsPinnedToUnavailableMethod(p, Capability.Balance));
    }

    [Fact]
    public void Routes_PersistAndUnsupportedChoicesAreDropped()
    {
        var s = NewSettings();
        var profiles = new NetworkProfileService(s);
        var p = profiles.Get(NetworkPresets.AioLocalId)!;
        p.Routes = new() { [Capability.Balance] = BackendMethodKind.Rpc, [Capability.Logs] = BackendMethodKind.Rpc };   // Logs: Bob only
        profiles.Save(p);
        var reloaded = new NetworkProfileService(new QubicSettingsService(AppName)).Get(NetworkPresets.AioLocalId)!;
        Assert.Equal(BackendMethodKind.Rpc, reloaded.Routes![Capability.Balance]);
        Assert.False(reloaded.Routes.ContainsKey(Capability.Logs));
    }

    [Fact]
    public void NetworkStorage_KeepsMainnetFileNames()
    {
        Assert.Equal("assets.json", NetworkStorage.PartitionFileName("assets.json", NetworkPresets.MainnetId));
        Assert.Equal("assets.aio-local.json", NetworkStorage.PartitionFileName("assets.json", NetworkPresets.AioLocalId));
    }
}

public class EgressGuardTests : SettingsTestBase
{
    [Fact]
    public void Allows_ProfileHosts_Blocks_Others()
    {
        var profiles = new NetworkProfileService(NewSettings());
        profiles.SetActive(NetworkPresets.AioLocalId);
        var guard = new EgressGuard(profiles);

        Assert.True(guard.IsAllowed("127.0.0.1"));
        Assert.False(guard.IsAllowed("rpc.qubic.org"));
        Assert.Throws<EgressBlockedException>(() => guard.Check("rpc.qubic.org", null, "test"));
        Assert.Single(guard.Violations);
    }

    [Fact]
    public void FeatureHost_IsAllowedUntilRevoked()
    {
        var profiles = new NetworkProfileService(NewSettings());
        var guard = new EgressGuard(profiles);
        Assert.False(guard.IsAllowed("static.qubic.org"));
        guard.AllowFeatureHost("labels", "static.qubic.org");
        Assert.True(guard.IsAllowed("static.qubic.org"));
        guard.RevokeFeatureHost("labels");
        Assert.False(guard.IsAllowed("static.qubic.org"));
    }

    [Fact]
    public void PeerDiscovery_AllowsAnyHostOnDirectPortOnly()
    {
        var profiles = new NetworkProfileService(NewSettings()); // Mainnet: Direct with PeerDiscovery
        var guard = new EgressGuard(profiles);
        Assert.True(guard.IsAllowed("5.6.7.8", 21841));
        Assert.False(guard.IsAllowed("5.6.7.8", 443));
    }

    [Fact]
    public async Task GuardedHttpClient_RefusesForeignHost_WithoutNetworkCall()
    {
        var profiles = new NetworkProfileService(NewSettings());
        profiles.SetActive(NetworkPresets.AioLocalId);
        var guard = new EgressGuard(profiles);
        using var http = guard.CreateHttpClient("test");
        await Assert.ThrowsAsync<EgressBlockedException>(() => http.GetAsync("https://rpc.qubic.org/live/v1/tick-info"));
    }

    [Fact]
    public void LogMode_RecordsButAllows()
    {
        var guard = new EgressGuard(new NetworkProfileService(NewSettings())) { Mode = EgressGuardMode.Log };
        guard.Check("example.com", 443, "test");
        Assert.Single(guard.Violations);
    }
}

public class FingerprintTests
{
    private static ComputorListSnapshot LoadMainnetFixture()
    {
        var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "mainnet-computors-epoch234.json")));
        var list = json.RootElement.GetProperty("computorsLists")[0];
        return new ComputorListSnapshot(
            list.GetProperty("epoch").GetUInt32(),
            list.GetProperty("identities").EnumerateArray().Select(e => e.GetString()!).ToList(),
            QubicBackendService.NormalizeSignature(list.GetProperty("signature").GetString()),
            BackendMethodKind.Rpc);
    }

    [Fact]
    public void RealMainnetComputorList_VerifiesAgainstArbitrator()
    {
        var snapshot = LoadMainnetFixture();
        Assert.Equal(676, snapshot.Identities.Count);
        Assert.True(NetworkFingerprintService.VerifyArbitratorSignature(snapshot));
        Assert.Equal(NetworkFingerprintKind.ArbitratorSigned, NetworkFingerprintService.Classify(snapshot, "mainnet").Kind);
    }

    [Fact]
    public void TamperedMainnetList_IsUnrecognized()
    {
        var s = LoadMainnetFixture();
        var ids = s.Identities.ToList();
        (ids[0], ids[1]) = (ids[1], ids[0]);
        var tampered = s with { Identities = ids };
        Assert.Equal(NetworkFingerprintKind.Unrecognized, NetworkFingerprintService.Classify(tampered, "x").Kind);
    }

    [Fact]
    public void CoreLiteComputors_AreRecognizedAsDevnet()
    {
        var ids = CoreLiteDevSeeds.Accounts.Where(a => a.IsComputor).Select(a => a.Identity).Reverse().ToList();
        var fp = NetworkFingerprintService.Classify(new ComputorListSnapshot(231, ids, null, BackendMethodKind.Bob), "aio-local");
        Assert.Equal(NetworkFingerprintKind.CoreLiteDevnet, fp.Kind);
    }

    [Fact]
    public void DevSeeds_DeriveExpectedIdentities()
    {
        Assert.Equal(676, CoreLiteDevSeeds.ComputorIdentities.Count);
        var crypt = new QubicCrypt();
        var first = CoreLiteDevSeeds.ComputorSeeds[0];
        Assert.Equal(crypt.GetIdentityFromPublicKey(crypt.GetPublicKey(first)), CoreLiteDevSeeds.Accounts[0].Identity);
        Assert.True(CoreLiteDevSeeds.IsPublicDevSeed(first));
        Assert.True(CoreLiteDevSeeds.IsPublicDevIdentity(CoreLiteDevSeeds.Accounts[^1].Identity));
    }

    [Fact]
    public void CleanIdentity_StripsTrailingGarbage_FromBob()
    {
        var id = CoreLiteDevSeeds.Accounts[0].Identity;
        Assert.Equal(id, QubicBackendService.CleanIdentity(id + "\uFFFD\uFFFD\u0001x"));
        Assert.Equal(id, QubicBackendService.CleanIdentity(id));
        Assert.Equal("short", QubicBackendService.CleanIdentity("short"));
    }

    [Theory]
    [InlineData("JsOgEAhNkZDmvemnDVH3")]
    [InlineData("")]
    public void NormalizeSignature_HandlesBadInput(string input)
    {
        Assert.Null(QubicBackendService.NormalizeSignature(input));
    }
}

public class TickTargetingTests
{
    [Fact]
    public void FixedOffset_IsDefault()
    {
        var t = TickTargeting.Compute(NetworkPresets.Mainnet(), 1000, null);
        Assert.Equal(1007u, t.TargetTick);
        Assert.Null(t.Warning);
    }

    [Fact]
    public void TimeBased_UsesMeasuredTickDuration()
    {
        var p = NetworkPresets.AioLocal();
        p.TickMode = TickTargetMode.TimeBased;
        p.TickTargetSeconds = 10;
        var t = TickTargeting.Compute(p, 1000, 2000); // 2 s ticks → 5 ticks
        Assert.Equal(5u, t.Offset);
        Assert.Equal(10, t.EtaSeconds);
    }

    [Fact]
    public void TimeBased_CapsAtPendingWindow_OnFastDevnet()
    {
        var p = NetworkPresets.AioLocal();
        p.TickMode = TickTargetMode.TimeBased;
        p.TickTargetSeconds = 10;
        var t = TickTargeting.Compute(p, 1000, 50); // 50 ms ticks → 200 wanted, window 32
        Assert.Equal(30u, t.Offset);
        Assert.False(t.ExceedsPendingWindow);
        Assert.NotNull(t.Warning);
    }

    [Fact]
    public void FixedOffset_BeyondWindow_WarnsButDoesNotBlock()
    {
        var p = NetworkPresets.AioLocal();
        p.TickOffset = 50;
        var t = TickTargeting.Compute(p, 1000, null);
        Assert.Equal(1050u, t.TargetTick);
        Assert.True(t.ExceedsPendingWindow);
        Assert.Contains("32", t.Warning);
    }

    [Fact]
    public void TimeBased_WithoutMeasurement_FallsBackToFixed()
    {
        var p = NetworkPresets.Mainnet();
        p.TickMode = TickTargetMode.TimeBased;
        var t = TickTargeting.Compute(p, 1000, null);
        Assert.Equal(7u, t.Offset);
        Assert.NotNull(t.Warning);
    }
}

public class ChainResetTests
{
    [Fact]
    public void Devnet_TickGoingBack_IsAReset()
    {
        var p = NetworkPresets.AioLocal();
        var r = new ChainStateRecord();
        Assert.Null(ChainResetService.Observe(p, r, 231, 80_400_000, DateTime.UtcNow));
        Assert.Null(ChainResetService.Observe(p, r, 231, 80_400_500, DateTime.UtcNow));
        Assert.Null(ChainResetService.Observe(p, r, 231, 80_400_495, DateTime.UtcNow)); // jitter within tolerance
        var reset = ChainResetService.Observe(p, r, 231, 80_371_514, DateTime.UtcNow); // down --wipe && up
        Assert.NotNull(reset);
        Assert.Equal(1, reset!.Generation);
        Assert.Equal(80_371_514u, r.LastTick);
    }

    [Fact]
    public void Mainnet_TickGoingBack_IsNotAReset()
    {
        var p = NetworkPresets.Mainnet();
        var r = new ChainStateRecord();
        ChainResetService.Observe(p, r, 234, 83_700_000, DateTime.UtcNow);
        Assert.Null(ChainResetService.Observe(p, r, 234, 83_600_000, DateTime.UtcNow)); // lagging node
        Assert.Equal(0, r.Generation);
    }

    [Fact]
    public void EpochGoingBack_IsAResetEverywhere()
    {
        var p = NetworkPresets.Mainnet();
        var r = new ChainStateRecord();
        ChainResetService.Observe(p, r, 234, 83_700_000, DateTime.UtcNow);
        Assert.NotNull(ChainResetService.Observe(p, r, 233, 83_700_100, DateTime.UtcNow));
    }

    [Fact]
    public void TickRateEstimator_HandlesSkippedTicks()
    {
        var est = new TickRateEstimator();
        var f = System.Diagnostics.Stopwatch.Frequency;
        est.Observe(100, 0);
        est.Observe(102, 2 * f);   // 2 ticks in 2 s
        est.Observe(103, 3 * f);
        est.Observe(105, 5 * f);
        Assert.NotNull(est.EstimatedMs);
        Assert.InRange(est.EstimatedMs!.Value, 990, 1010);
    }
}

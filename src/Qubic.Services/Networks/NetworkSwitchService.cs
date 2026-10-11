using Qubic.Services.Storage;

namespace Qubic.Services.Networks;

/// <summary>
/// Switches the active network profile and restarts everything that depends on it,
/// in order: stop tick/drift monitoring → switch profile (clients reset) → restart
/// monitoring → reopen the seed's wallet DB for the new network → fingerprint.
/// </summary>
public sealed class NetworkSwitchService
{
    private readonly NetworkProfileService _profiles;
    private readonly TickMonitorService _tickMonitor;
    private readonly TickDriftService _drift;
    private readonly WalletStorageService _storage;
    private readonly NetworkFingerprintService _fingerprint;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public bool IsSwitching { get; private set; }

    /// <summary>Raised after a switch (or applied edit) completed.</summary>
    public event Action? OnSwitched;

    public NetworkSwitchService(NetworkProfileService profiles, TickMonitorService tickMonitor,
        TickDriftService drift, WalletStorageService storage, NetworkFingerprintService fingerprint)
    {
        _profiles = profiles;
        _tickMonitor = tickMonitor;
        _drift = drift;
        _storage = storage;
        _fingerprint = fingerprint;
    }

    public async Task SwitchAsync(string profileId)
    {
        await _lock.WaitAsync();
        try
        {
            if (profileId == _profiles.ActiveId) return;
            IsSwitching = true;
            Raise();

            var driftWasRunning = _drift.IsRunning;
            await _tickMonitor.StopAsync();
            _drift.Stop();

            _profiles.SetActive(profileId);

            await _tickMonitor.StartAsync();
            if (driftWasRunning) _drift.Start();
            _storage.ReopenForActiveProfile(_tickMonitor.Epoch);
        }
        finally
        {
            IsSwitching = false;
            _lock.Release();
        }
        _ = _fingerprint.CheckAsync();
        Raise();
    }

    /// <summary>
    /// Call after saving edits to the active profile (endpoints may have changed):
    /// restarts monitoring and sync against the new endpoints.
    /// </summary>
    public async Task ApplyActiveProfileEditsAsync()
    {
        await _lock.WaitAsync();
        try
        {
            IsSwitching = true;
            Raise();
            var driftWasRunning = _drift.IsRunning;
            _drift.Stop();
            await _tickMonitor.StartAsync();
            if (driftWasRunning) _drift.Start();
            _storage.RestartSync();
        }
        finally
        {
            IsSwitching = false;
            _lock.Release();
        }
        _ = _fingerprint.CheckAsync();
        Raise();
    }

    private void Raise()
    {
        try { OnSwitched?.Invoke(); } catch { /* observer errors are not ours */ }
    }
}

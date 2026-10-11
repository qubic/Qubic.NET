using Qubic.Services.Storage;

namespace Qubic.Services.Networks;

public sealed record ChainResetEvent(string ProfileId, string ProfileName, string Detail, int Generation);

/// <summary>
/// Detects that the chain behind a profile was restarted (e.g. AIO <c>down --wipe &amp;&amp; up</c>,
/// which restarts at the same tick numbers) and purges that profile's cached history.
/// <list type="bullet">
/// <item>Non-mainnet: tick goes back by more than <see cref="BackwardsTolerance"/> within an epoch, or the epoch goes back.</item>
/// <item>Mainnet: only the epoch going back counts (a lagging node must never wipe history).</item>
/// </list>
/// </summary>
public sealed class ChainResetService : IDisposable
{
    public const uint BackwardsTolerance = 10;
    private static readonly TimeSpan PersistInterval = TimeSpan.FromSeconds(30);

    private readonly TickMonitorService _tickMonitor;
    private readonly NetworkProfileService _profiles;
    private readonly NetworkChainState _state;
    private readonly WalletStorageService _storage;
    private readonly object _lock = new();
    private string? _profileId;
    private ChainStateRecord? _record;
    private DateTime _lastPersist = DateTime.MinValue;

    /// <summary>Raised once per detected reset.</summary>
    public event Action<ChainResetEvent>? OnChainReset;

    /// <summary>The most recent reset of the active profile in this session, if any.</summary>
    public ChainResetEvent? LastReset { get; private set; }

    public ChainResetService(TickMonitorService tickMonitor, NetworkProfileService profiles,
        NetworkChainState state, WalletStorageService storage)
    {
        _tickMonitor = tickMonitor;
        _profiles = profiles;
        _state = state;
        _storage = storage;
        _tickMonitor.OnTickChanged += OnTick;
        _profiles.OnActiveProfileChanged += OnProfileChanged;
    }

    private void OnProfileChanged(NetworkProfile oldP, NetworkProfile newP)
    {
        lock (_lock)
        {
            Flush();
            _profileId = null;
            _record = null;
            LastReset = null;
        }
    }

    private void OnTick()
    {
        if (!_tickMonitor.IsConnected || _tickMonitor.Tick == 0) return;
        ChainResetEvent? reset = null;
        lock (_lock)
        {
            var profile = _profiles.Active;
            if (_profileId != profile.Id || _record == null)
            {
                _profileId = profile.Id;
                _record = _state.Get(profile.Id);
            }
            reset = Observe(profile, _record, _tickMonitor.Epoch, _tickMonitor.Tick, DateTime.UtcNow);
            if (reset != null || DateTime.UtcNow - _lastPersist > PersistInterval)
                Flush();
        }

        if (reset == null) return;
        LastReset = reset;
        try { _storage.ApplyChainGeneration(reset.ProfileId); } catch { /* storage may be closing */ }
        try { OnChainReset?.Invoke(reset); } catch { /* observer errors are not ours */ }
    }

    /// <summary>Applies one tick observation to <paramref name="record"/>; returns a reset event if one was detected.</summary>
    internal static ChainResetEvent? Observe(NetworkProfile profile, ChainStateRecord record, uint epoch, uint tick, DateTime nowUtc)
    {
        if (record.LastTick == 0)
        {
            record.Epoch = epoch;
            record.LastTick = tick;
            return null;
        }

        string? detail = null;
        if (epoch < record.Epoch)
            detail = $"Epoch went back from {record.Epoch} to {epoch}.";
        else if (!profile.IsMainnet && epoch == record.Epoch && tick + BackwardsTolerance < record.LastTick)
            detail = $"Tick went back from {record.LastTick} to {tick} in epoch {epoch}.";

        if (detail == null)
        {
            if (epoch > record.Epoch || tick > record.LastTick)
            {
                record.Epoch = epoch;
                record.LastTick = tick;
            }
            return null;
        }

        record.Generation++;
        record.Epoch = epoch;
        record.LastTick = tick;
        record.LastResetUtc = nowUtc;
        record.LastResetDetail = detail;
        return new ChainResetEvent(profile.Id, profile.Name, detail, record.Generation);
    }

    private void Flush()
    {
        if (_profileId == null || _record == null) return;
        _state.Set(_profileId, _record);
        _lastPersist = DateTime.UtcNow;
    }

    public void Dispose()
    {
        _tickMonitor.OnTickChanged -= OnTick;
        _profiles.OnActiveProfileChanged -= OnProfileChanged;
        lock (_lock) Flush();
    }
}

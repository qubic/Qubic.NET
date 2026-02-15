namespace Qubic.Toolkit;

/// <summary>
/// Background service that periodically auto-discovers the most recent peer
/// when DirectNetwork backend is active and the interval is configured.
/// </summary>
public sealed class PeerAutoDiscoverService : IDisposable
{
    private readonly ToolkitBackendService _backend;
    private readonly ToolkitSettingsService _settings;
    private readonly TickMonitorService _tickMonitor;
    private Timer? _timer;
    private bool _running;

    public PeerAutoDiscoverService(
        ToolkitBackendService backend,
        ToolkitSettingsService settings,
        TickMonitorService tickMonitor)
    {
        _backend = backend;
        _settings = settings;
        _tickMonitor = tickMonitor;
        _settings.OnChanged += OnSettingsChanged;
        ApplyInterval();
    }

    /// <summary>
    /// Raised after a background discovery completes (on a thread-pool thread).
    /// </summary>
    public event Action<ToolkitBackendService.PeerDiscoveryResult>? OnDiscoveryCompleted;

    /// <summary>
    /// Raised when a background discovery encounters an error.
    /// </summary>
    public event Action<string>? OnDiscoveryError;

    /// <summary>
    /// Whether a discovery is currently in progress.
    /// </summary>
    public bool IsRunning => _running;

    /// <summary>
    /// Timestamp of the last completed discovery.
    /// </summary>
    public DateTime? LastRun { get; private set; }

    private void OnSettingsChanged()
    {
        ApplyInterval();
    }

    private void ApplyInterval()
    {
        var minutes = _settings.AutoDiscoverIntervalMinutes;
        if (minutes <= 0 || _backend.ActiveBackend != QueryBackend.DirectNetwork)
        {
            _timer?.Change(Timeout.Infinite, Timeout.Infinite);
            _timer?.Dispose();
            _timer = null;
            return;
        }

        var interval = TimeSpan.FromMinutes(minutes);
        if (_timer == null)
            _timer = new Timer(OnTimerTick, null, interval, interval);
        else
            _timer.Change(interval, interval);
    }

    /// <summary>
    /// Re-evaluate whether the timer should be running (e.g. after backend change).
    /// </summary>
    public void Restart()
    {
        ApplyInterval();
    }

    private async void OnTimerTick(object? state)
    {
        if (_running) return; // skip if previous run still in progress
        if (_backend.ActiveBackend != QueryBackend.DirectNetwork) return;

        _running = true;
        try
        {
            var result = await _backend.AutoDiscoverRecentPeerAsync(
                _settings.PeerTickThreshold);

            LastRun = DateTime.Now;

            if (result.Switched)
            {
                // Reconnect tick monitor to the new peer
                _backend.ResetClients();
                await _tickMonitor.StartAsync();
            }

            OnDiscoveryCompleted?.Invoke(result);
        }
        catch (Exception ex)
        {
            OnDiscoveryError?.Invoke(ex.Message);
        }
        finally
        {
            _running = false;
        }
    }

    public void Dispose()
    {
        _settings.OnChanged -= OnSettingsChanged;
        _timer?.Dispose();
        _timer = null;
    }
}

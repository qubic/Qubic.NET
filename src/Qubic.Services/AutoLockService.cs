namespace Qubic.Services;

public sealed class AutoLockService : IDisposable
{
    private readonly QubicSettingsService _settings;
    private Timer? _timer;
    private DateTime _lastActivity;
    private bool _disposed;

    public int TimeoutMinutes
    {
        get => _settings.GetCustom<int?>("autolock_minutes") ?? 15;
        set { _settings.SetCustom("autolock_minutes", Math.Max(0, value)); ResetTimer(); }
    }

    public event Action? OnAutoLockTriggered;

    public AutoLockService(QubicSettingsService settings) { _settings = settings; _lastActivity = DateTime.UtcNow; }

    public void RecordActivity() { _lastActivity = DateTime.UtcNow; }

    public void Start()
    {
        Stop();
        if (TimeoutMinutes <= 0) return;
        _timer = new Timer(CheckInactivity, null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
        _lastActivity = DateTime.UtcNow;
    }

    public void Stop() { _timer?.Dispose(); _timer = null; }

    private void CheckInactivity(object? state)
    {
        if (_disposed || TimeoutMinutes <= 0) return;
        if ((DateTime.UtcNow - _lastActivity).TotalMinutes >= TimeoutMinutes)
        {
            Stop();
            OnAutoLockTriggered?.Invoke();
        }
    }

    private void ResetTimer() { if (_timer != null) { Stop(); Start(); } }

    public void Dispose() { _disposed = true; Stop(); }
}

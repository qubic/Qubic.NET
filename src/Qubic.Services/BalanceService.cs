using Qubic.Core.Entities;

namespace Qubic.Services;

/// <summary>
/// Central service that maintains the current identity's balance.
/// All UI components subscribe to <see cref="OnBalanceChanged"/> instead of
/// fetching the balance independently.
/// </summary>
public sealed class BalanceService : IDisposable
{
    private readonly QubicBackendService _backend;
    private readonly SeedSessionService _seed;
    private readonly TickMonitorService _tickMonitor;
    private readonly SemaphoreSlim _fetchLock = new(1, 1);
    private bool _disposed;

    public long? Balance { get; private set; }
    public bool IsLoading { get; private set; }
    public string? Error { get; private set; }

    /// <summary>Fired after every fetch attempt (success or failure) and on identity change.</summary>
    public event Action? OnBalanceChanged;

    public BalanceService(QubicBackendService backend, SeedSessionService seed, TickMonitorService tickMonitor)
    {
        _backend = backend;
        _seed = seed;
        _tickMonitor = tickMonitor;

        _seed.OnSeedChanged += OnSeedChanged;
        _tickMonitor.OnTickChanged += OnTickChanged;
    }

    /// <summary>
    /// Fetches the balance from the backend for the current identity.
    /// Concurrent calls are deduplicated via a semaphore.
    /// </summary>
    public async Task RefreshAsync()
    {
        if (_disposed || !_seed.HasSeed || _seed.Identity == null) return;

        if (!await _fetchLock.WaitAsync(0)) return; // skip if already in-flight
        try
        {
            IsLoading = true;
            RaiseChanged();

            var bal = await _backend.GetBalanceAsync(_seed.Identity.Value);
            Balance = bal.Amount;
            Error = null;
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
        finally
        {
            IsLoading = false;
            _fetchLock.Release();
            RaiseChanged();
        }
    }

    /// <summary>Fire-and-forget refresh, e.g. after broadcasting a transaction.</summary>
    public void RequestRefresh() => RefreshAsync().FireAndForget();

    private void OnSeedChanged()
    {
        if (_disposed) return;

        if (!_seed.HasSeed || _seed.Identity == null)
        {
            Balance = null;
            Error = null;
            IsLoading = false;
            RaiseChanged();
            return;
        }

        // New identity → clear stale value and fetch
        Balance = null;
        Error = null;
        RequestRefresh();
    }

    private void OnTickChanged()
    {
        if (_disposed) return;
        // Refresh every ~30 ticks (~30 seconds)
        if (_seed.HasSeed && _tickMonitor.Tick % 30 == 0)
            RequestRefresh();
    }

    private void RaiseChanged()
    {
        var handler = OnBalanceChanged;
        if (handler == null) return;
        foreach (var d in handler.GetInvocationList())
        {
            try { ((Action)d)(); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[BalanceService] subscriber error: {ex.Message}"); }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _seed.OnSeedChanged -= OnSeedChanged;
        _tickMonitor.OnTickChanged -= OnTickChanged;
        _fetchLock.Dispose();
    }
}

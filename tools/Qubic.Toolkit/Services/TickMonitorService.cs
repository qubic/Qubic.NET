using Qubic.Bob;
using Qubic.Bob.Models;

namespace Qubic.Toolkit;

/// <summary>
/// Monitors the current tick/epoch, using Bob WebSocket for real-time updates
/// or polling for RPC/Direct Network backends.
/// </summary>
public sealed class TickMonitorService : IDisposable
{
    private readonly ToolkitBackendService _backend;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private CancellationTokenSource? _cts;
    private BobWebSocketClient? _wsClient;
    private BobSubscription<NewTickNotification>? _tickSub;
    private Task? _runTask;

    public uint Tick { get; private set; }
    public ushort Epoch { get; private set; }
    public bool IsConnected { get; private set; }
    public string? Error { get; private set; }

    public event Action? OnTickChanged;

    private void RaiseTickChanged()
    {
        var handler = OnTickChanged;
        if (handler == null) return;
        foreach (var d in handler.GetInvocationList())
        {
            try { ((Action)d)(); }
            catch { /* subscriber may be disposed */ }
        }
    }

    public TickMonitorService(ToolkitBackendService backend)
    {
        _backend = backend;
    }

    /// <summary>
    /// Starts monitoring. Stops any previous monitoring first.
    /// </summary>
    public async Task StartAsync()
    {
        await _lock.WaitAsync();
        try
        {
            await StopInternalAsync();

            _cts = new CancellationTokenSource();
            var ct = _cts.Token;

            if (_backend.ActiveBackend == QueryBackend.Bob)
                _runTask = Task.Run(() => RunWebSocketAsync(ct), ct);
            else
                _runTask = Task.Run(() => RunPollingAsync(ct), ct);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task StopAsync()
    {
        await _lock.WaitAsync();
        try
        {
            await StopInternalAsync();
            IsConnected = false;
            RaiseTickChanged();
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task StopInternalAsync()
    {
        if (_cts != null)
        {
            await _cts.CancelAsync();
        }

        // Wait for the background task to finish
        if (_runTask != null)
        {
            try { await _runTask.WaitAsync(TimeSpan.FromSeconds(3)); }
            catch { /* timeout or cancelled — that's fine */ }
            _runTask = null;
        }

        if (_tickSub != null)
        {
            _tickSub.Dispose();
            _tickSub = null;
        }

        if (_wsClient != null)
        {
            try { await _wsClient.DisposeAsync(); } catch { }
            _wsClient = null;
        }

        if (_cts != null)
        {
            _cts.Dispose();
            _cts = null;
        }
    }

    private async Task RunWebSocketAsync(CancellationToken ct)
    {
        try
        {
            // Initial tick via HTTP first
            await FetchOnce(ct);

            var wsOptions = new BobWebSocketOptions
            {
                Nodes = [_backend.BobUrl]
            };
            _wsClient = new BobWebSocketClient(wsOptions);
            await _wsClient.ConnectAsync(ct);
            _tickSub = await _wsClient.SubscribeNewTicksAsync(ct);

            await foreach (var tick in _tickSub.WithCancellation(ct))
            {
                Tick = tick.TickNumber;
                Epoch = (ushort)tick.Epoch;
                IsConnected = true;
                Error = null;
                RaiseTickChanged();
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Error = ex.Message;
            IsConnected = false;
            RaiseTickChanged();

            // Fall back to polling if WS fails
            if (!ct.IsCancellationRequested)
                await RunPollingAsync(ct);
        }
    }

    private async Task RunPollingAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await FetchOnce(ct);

            try { await Task.Delay(1000, ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task FetchOnce(CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            var info = await _backend.GetTickInfoAsync(ct);
            Tick = info.Tick;
            Epoch = info.Epoch;
            IsConnected = true;
            Error = null;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Error = ex.Message.Length > 80 ? ex.Message[..80] + "..." : ex.Message;
            IsConnected = false;
        }
        RaiseTickChanged();
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _tickSub?.Dispose();
        _wsClient?.Dispose();
        _cts?.Dispose();
        _lock.Dispose();
    }
}

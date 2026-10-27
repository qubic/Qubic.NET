using Qubic.Services.Networks;

namespace Qubic.Services;

/// <summary>
/// Periodically queries the enabled methods of the active network profile (RPC, Bob, Direct)
/// for their current tick and detects drift between them. Methods outside the profile are
/// never contacted; with fewer than two methods there is nothing to compare.
/// </summary>
public sealed class TickDriftService : IDisposable
{
    private readonly QubicBackendService _backend;
    private readonly QubicSettingsService _settings;
    private CancellationTokenSource? _cts;
    private Task? _runTask;

    /// <summary>Snapshot of a single backend's tick query result.</summary>
    public record BackendTickSnapshot(
        QueryBackend Backend, uint Tick, bool Reachable,
        string? Error, DateTime CheckedAtUtc);

    public BackendTickSnapshot? RpcTick { get; private set; }
    public BackendTickSnapshot? BobTick { get; private set; }
    public BackendTickSnapshot? DirectTick { get; private set; }

    /// <summary>Maximum tick difference between backends before showing a warning.</summary>
    public int Threshold => _settings.GetCustom<int?>("tickDriftThreshold") ?? 15;

    /// <summary>Highest tick among reachable backends.</summary>
    public uint MaxTick { get; private set; }

    /// <summary>Difference between highest and lowest tick among reachable backends.</summary>
    public int MaxDrift { get; private set; }

    /// <summary>True when MaxDrift exceeds the configured threshold.</summary>
    public bool HasDrift => MaxDrift > Threshold;

    public bool IsRunning { get; private set; }

    public event Action? OnDriftChanged;

    public TickDriftService(QubicBackendService backend, QubicSettingsService settings)
    {
        _backend = backend;
        _settings = settings;
    }

    /// <summary>Starts the background polling loop. Stops any previous loop first.</summary>
    public void Start()
    {
        Stop();
        _cts = new CancellationTokenSource();
        IsRunning = true;
        _runTask = Task.Run(() => PollLoopAsync(_cts.Token));
    }

    /// <summary>Stops the background polling loop.</summary>
    public void Stop()
    {
        if (_cts != null)
        {
            _cts.Cancel();
            try { _runTask?.Wait(TimeSpan.FromSeconds(3)); } catch { }
            _cts.Dispose();
            _cts = null;
            _runTask = null;
        }
        IsRunning = false;
    }

    private async Task PollLoopAsync(CancellationToken ct)
    {
        // Initial poll immediately
        await PollOnceAsync(ct);

        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(60), ct); }
            catch (OperationCanceledException) { break; }

            await PollOnceAsync(ct);
        }
    }

    /// <summary>Number of tick-serving methods in the active profile (drift needs at least two).</summary>
    public int ComparableMethodCount => _backend.Profile.EnabledMethods.Count();

    private async Task PollOnceAsync(CancellationToken ct)
    {
        var profile = _backend.Profile;
        var rpcTask = profile.HasMethod(BackendMethodKind.Rpc) ? QueryRpcAsync(ct) : Task.FromResult<BackendTickSnapshot?>(null);
        var bobTask = profile.HasMethod(BackendMethodKind.Bob) ? QueryBobAsync(ct) : Task.FromResult<BackendTickSnapshot?>(null);
        var directTask = profile.HasMethod(BackendMethodKind.Direct) ? QueryDirectAsync(ct) : Task.FromResult<BackendTickSnapshot?>(null);

        await Task.WhenAll(rpcTask, bobTask, directTask);

        RpcTick = rpcTask.Result;
        BobTick = bobTask.Result;
        DirectTick = directTask.Result;

        // Calculate drift from reachable methods
        var reachable = new List<uint>();
        if (RpcTick is { Reachable: true }) reachable.Add(RpcTick.Tick);
        if (BobTick is { Reachable: true }) reachable.Add(BobTick.Tick);
        if (DirectTick is { Reachable: true }) reachable.Add(DirectTick.Tick);

        if (reachable.Count >= 2)
        {
            MaxTick = reachable.Max();
            MaxDrift = (int)(reachable.Max() - reachable.Min());
        }
        else if (reachable.Count == 1)
        {
            MaxTick = reachable[0];
            MaxDrift = 0;
        }
        else
        {
            MaxTick = 0;
            MaxDrift = 0;
        }

        RaiseDriftChanged();
    }

    private async Task<BackendTickSnapshot?> QueryRpcAsync(CancellationToken ct)
    {
        try
        {
            using var rpc = _backend.CreateRpcClient();
            var info = await rpc.GetTickInfoAsync(ct);
            return new BackendTickSnapshot(QueryBackend.Rpc, info.Tick, true, null, DateTime.UtcNow);
        }
        catch (Exception ex)
        {
            return new BackendTickSnapshot(QueryBackend.Rpc, 0, false,
                ex.Message.Length > 80 ? ex.Message[..80] + "..." : ex.Message, DateTime.UtcNow);
        }
    }

    private async Task<BackendTickSnapshot?> QueryBobAsync(CancellationToken ct)
    {
        try
        {
            using var bob = _backend.CreateBobClient();
            var tick = await bob.GetTickNumberAsync(ct);
            return new BackendTickSnapshot(QueryBackend.Bob, tick, true, null, DateTime.UtcNow);
        }
        catch (Exception ex)
        {
            return new BackendTickSnapshot(QueryBackend.Bob, 0, false,
                ex.Message.Length > 80 ? ex.Message[..80] + "..." : ex.Message, DateTime.UtcNow);
        }
    }

    private async Task<BackendTickSnapshot?> QueryDirectAsync(CancellationToken ct)
    {
        try
        {
            using var node = _backend.CreateNodeClient(_backend.NodeHost, _backend.NodePort);
            await node.ConnectAsync(ct);
            var info = await node.GetCurrentTickInfoAsync(ct);
            return new BackendTickSnapshot(QueryBackend.DirectNetwork, info.Tick, true, null, DateTime.UtcNow);
        }
        catch (Exception ex)
        {
            return new BackendTickSnapshot(QueryBackend.DirectNetwork, 0, false,
                ex.Message.Length > 80 ? ex.Message[..80] + "..." : ex.Message, DateTime.UtcNow);
        }
    }

    private void RaiseDriftChanged()
    {
        var handler = OnDriftChanged;
        if (handler == null) return;
        foreach (var d in handler.GetInvocationList())
        {
            try { ((Action)d)(); }
            catch { /* subscriber may be disposed */ }
        }
    }

    public void Dispose()
    {
        Stop();
    }
}

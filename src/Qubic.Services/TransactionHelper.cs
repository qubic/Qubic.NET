using Qubic.Core.Entities;
using Qubic.Core.Payloads;
using Qubic.Services.Networks;

namespace Qubic.Services;

/// <summary>
/// Shared helpers for transaction building, broadcasting, and tracking.
/// Eliminates duplicated tick resolution and broadcast+track patterns across pages.
/// </summary>
public static class TransactionHelper
{
    /// <summary>
    /// Resolves a target tick for a new transaction using the active network profile's
    /// tick policy (fixed offset or time-based). If explicitTick > 0, returns it unchanged.
    /// </summary>
    public static async Task<uint> ResolveTargetTickAsync(
        QubicBackendService backend, TickMonitorService tickMonitor,
        QubicSettingsService settings, uint explicitTick = 0,
        CancellationToken ct = default)
    {
        if (explicitTick > 0) return explicitTick;
        return (await ResolveTickTargetAsync(backend, tickMonitor, ct)).TargetTick;
    }

    /// <summary>
    /// Computes the target tick plus offset, ETA and pending-window warning for display.
    /// Uses the monitored tick when connected, otherwise asks the active profile.
    /// </summary>
    public static async Task<TickTarget> ResolveTickTargetAsync(
        QubicBackendService backend, TickMonitorService tickMonitor, CancellationToken ct = default)
    {
        var baseTick = tickMonitor.IsConnected
            ? tickMonitor.Tick
            : (await backend.GetTickInfoAsync(ct)).Tick;
        return TickTargeting.Compute(backend.Profile, baseTick, tickMonitor.EstimatedTickDurationMs);
    }

    /// <summary>Same as <see cref="ResolveTickTargetAsync"/> but synchronous; null when not connected.</summary>
    public static TickTarget? PreviewTickTarget(QubicBackendService backend, TickMonitorService tickMonitor) =>
        tickMonitor.IsConnected
            ? TickTargeting.Compute(backend.Profile, tickMonitor.Tick, tickMonitor.EstimatedTickDurationMs)
            : null;

    /// <summary>
    /// Broadcasts an already-signed transaction, tracks it, and returns the transaction ID.
    /// </summary>
    public static async Task<string> BroadcastAndTrackAsync(
        QubicBackendService backend,
        TransactionTrackerService tracker,
        SeedSessionService seed,
        QubicTransaction tx,
        string destination,
        long amount,
        uint targetTick,
        string description,
        ushort inputType = 0,
        string? payloadHex = null,
        CancellationToken ct = default)
    {
        var result = await backend.BroadcastTransactionAsync(tx, ct);

        tracker.Track(new TrackedTransaction
        {
            Hash = result.TransactionId,
            Source = seed.Identity?.ToString() ?? "",
            Destination = destination,
            Amount = amount,
            TargetTick = targetTick,
            InputType = inputType,
            PayloadHex = payloadHex,
            Description = description,
            RawData = Convert.ToHexString(tx.GetRawBytes())
        });

        return result.TransactionId;
    }

    /// <summary>
    /// Convenience overload for contract calls with an ITransactionPayload.
    /// Signs, broadcasts, and tracks the transaction.
    /// </summary>
    /// <param name="autoTick">
    /// True (the default) when <paramref name="targetTick"/> was auto-resolved rather than typed by the user:
    /// the tick is then re-picked at signing time so it can't go stale while the signing review is open.
    /// Pass false when the user set a manual tick.
    /// </param>
    public static async Task<string> BroadcastContractCallAsync(
        QubicBackendService backend,
        TransactionTrackerService tracker,
        SeedSessionService seed,
        QubicIdentity destination,
        long amount,
        uint targetTick,
        ITransactionPayload payload,
        string description,
        bool autoTick = true,
        CancellationToken ct = default)
    {
        var tx = await seed.SignTransactionAsync(destination, amount, targetTick, payload,
            Signing.SigningContext.InApp(description), autoTick, ct);
        // The signed transaction carries the tick actually used (refreshed on approval for auto-tick).
        return await BroadcastAndTrackAsync(
            backend, tracker, seed, tx,
            destination.ToString(), amount, tx.Tick, description,
            payload.InputType, Convert.ToHexString(payload.GetPayloadBytes()), ct);
    }
}

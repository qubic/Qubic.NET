namespace Qubic.Services.Networks;

/// <summary>Where a new transaction will be scheduled and whether the network will accept it.</summary>
public sealed record TickTarget(
    uint CurrentTick,
    uint TargetTick,
    uint Offset,
    TickTargetMode Mode,
    double? TickDurationMs,
    int PendingWindowTicks,
    string? Warning)
{
    /// <summary>Estimated seconds until the target tick, if the tick rate is known.</summary>
    public double? EtaSeconds => TickDurationMs is { } ms ? Offset * ms / 1000.0 : null;

    /// <summary>The network drops transactions scheduled further ahead than its pending-tx window.</summary>
    public bool ExceedsPendingWindow => Offset > PendingWindowTicks;
}

/// <summary>
/// Chooses the target tick for a new transaction from the active profile's policy:
/// a fixed tick offset (default, works everywhere) or "execute in ~N seconds" using
/// the observed tick duration. Never blocks: offsets beyond the network's pending
/// window produce a warning.
/// </summary>
public static class TickTargeting
{
    /// <summary>Mainnet core: PENDING_TXS_POOL_NUM_TICKS = 3 min / TICK_DURATION_FOR_ALLOCATION_MS (225 ms) = 800.</summary>
    public const int MainnetPendingWindowTicks = 800;

    /// <summary>Testnet / core-lite: PENDING_TXS_POOL_NUM_TICKS = 32.</summary>
    public const int TestnetPendingWindowTicks = 32;

    public static int PendingWindowTicks(NetworkProfile profile) =>
        profile.PendingTxWindowTicks
        ?? (profile.Kind is NetworkKind.Testnet or NetworkKind.Devnet ? TestnetPendingWindowTicks : MainnetPendingWindowTicks);

    public static TickTarget Compute(NetworkProfile profile, uint currentTick, double? tickDurationMs)
    {
        var window = PendingWindowTicks(profile);
        uint offset;
        string? note = null;

        if (profile.TickMode == TickTargetMode.TimeBased && tickDurationMs is > 0)
        {
            var wanted = Math.Ceiling(profile.TickTargetSeconds * 1000.0 / tickDurationMs.Value);
            // Time mode is a convenience: keep the computed offset inside the window the network accepts.
            var cap = Math.Max(2, window - 2);
            offset = (uint)Math.Clamp(wanted, 2, cap);
            if (wanted > cap)
                note = $"~{profile.TickTargetSeconds}s would be {wanted:0} ticks; capped at {cap} because the network only accepts {window} ticks ahead.";
        }
        else
        {
            offset = (uint)Math.Clamp(profile.TickOffset, 1, 1000);
            if (profile.TickMode == TickTargetMode.TimeBased)
                note = "Tick rate not measured yet — using the fixed tick offset.";
        }

        if (offset > window)
            note = $"Target is {offset} ticks ahead, but this network only accepts transactions up to {window} ticks ahead. It will likely be dropped.";

        return new TickTarget(currentTick, currentTick + offset, offset, profile.TickMode, tickDurationMs, window, note);
    }
}

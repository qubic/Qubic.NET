using System.Diagnostics;

namespace Qubic.Services;

/// <summary>
/// Estimates milliseconds per tick from observed (tick, time) pairs.
/// Works with streamed ticks (every tick) and with polling (ticks may be skipped):
/// each sample contributes elapsed time divided by ticks advanced.
/// </summary>
public sealed class TickRateEstimator
{
    private const double Alpha = 0.2;
    private const int MinSamples = 3;
    private readonly object _lock = new();
    private uint _lastTick;
    private long _lastTimestamp;
    private double? _ema;
    private int _samples;

    /// <summary>Estimated ms per tick, or null until enough samples were observed.</summary>
    public double? EstimatedMs
    {
        get { lock (_lock) return _samples >= MinSamples ? _ema : null; }
    }

    public void Reset()
    {
        lock (_lock)
        {
            _lastTick = 0;
            _lastTimestamp = 0;
            _ema = null;
            _samples = 0;
        }
    }

    public void Observe(uint tick) => Observe(tick, Stopwatch.GetTimestamp());

    internal void Observe(uint tick, long timestamp)
    {
        lock (_lock)
        {
            if (_lastTick == 0 || tick < _lastTick)
            {
                // First sample, or chain went backwards (reset): restart the baseline.
                if (tick < _lastTick) { _ema = null; _samples = 0; }
                _lastTick = tick;
                _lastTimestamp = timestamp;
                return;
            }
            if (tick == _lastTick) return;

            var elapsedMs = (timestamp - _lastTimestamp) * 1000.0 / Stopwatch.Frequency;
            var perTick = elapsedMs / (tick - _lastTick);
            _lastTick = tick;
            _lastTimestamp = timestamp;
            if (perTick <= 0) return;

            _ema = _ema is null ? perTick : Alpha * perTick + (1 - Alpha) * _ema.Value;
            _samples++;
        }
    }
}

namespace Qubic.ChainAnalytics.Models;

/// <summary>
/// Per-step wall-clock timings for building a <see cref="TickSummary"/>.
/// All timestamps are UTC; durations are derived from start/end pairs.
/// </summary>
public sealed class TickFetchTimings
{
    /// <summary>When the analyzer started building this summary.</summary>
    public required DateTime StartedAt { get; init; }
    /// <summary>When the analyzer finished building this summary.</summary>
    public required DateTime FinishedAt { get; init; }

    /// <summary>RequestTickData round-trip.</summary>
    public required TimingStep TickDataFetch { get; init; }
    /// <summary>RequestTickTransactions round-trip. Null when tick data was unavailable (step skipped).</summary>
    public TimingStep? TransactionsFetch { get; init; }
    /// <summary>Local parse + digest computation across all transactions. Null when no transactions were fetched.</summary>
    public TimingStep? ParseAndVerify { get; init; }

    /// <summary>Total wall-clock duration for the whole summary build.</summary>
    public TimeSpan TotalDuration => FinishedAt - StartedAt;
}

/// <summary>One timed step inside a <see cref="TickFetchTimings"/> record.</summary>
public sealed record TimingStep(DateTime StartedAt, DateTime FinishedAt)
{
    /// <summary>Wall-clock duration of this step.</summary>
    public TimeSpan Duration => FinishedAt - StartedAt;
}

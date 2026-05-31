using Qubic.Core.Entities;

namespace Qubic.ChainAnalytics.Models;

/// <summary>
/// Combined view of consensus around a single tick: tick data (the tx digest list
/// the issuing computor signed), votes for the tick itself, and votes for the
/// following tick. Together these reveal whether the network agreed on what
/// happened in tick X (votes for X) and whether they agreed on the state X left
/// behind (the <c>prev*</c> fields in votes for X+1).
/// </summary>
public sealed class VoteAlignment
{
    /// <summary>The tick being analysed.</summary>
    public required uint Tick { get; init; }

    /// <summary>Tick data for the tick. Null if the node didn't have it.</summary>
    public TickData? TickData { get; init; }

    /// <summary>True when the node reported tick data.</summary>
    public bool TickDataAvailable => TickData is not null;

    /// <summary>Quorum vote distribution for tick <see cref="Tick"/>.</summary>
    public required QuorumVoteSummary VotesForTick { get; init; }

    /// <summary>Quorum vote distribution for tick <see cref="Tick"/> + 1.</summary>
    public required QuorumVoteSummary VotesForNextTick { get; init; }

    /// <summary>Per-step wall-clock timings collected while building this alignment.</summary>
    public required VoteAlignmentTimings Timings { get; init; }

    /// <summary>
    /// True when the dominant <c>TransactionDigest</c> in the votes for tick X equals the
    /// dominant <c>PrevTransactionDigest</c> consensus implied by tick X+1's votes —
    /// i.e. computors voting on X agreed on the same tx set that the next tick built on.
    /// (We compare <c>VotesForNextTick.PrevSpectrumDigest</c> dominance as proxy for
    /// "the network finalised X", since X's tx digest doesn't appear directly in X+1's votes.)
    /// </summary>
    public bool ResultPersistedIntoNextTick =>
        VotesForTick.TransactionDigest.QuorumReached
        && VotesForNextTick.PrevSpectrumDigest.QuorumReached;

    /// <summary>
    /// True when both ticks reached full consensus and the tick data is present —
    /// i.e. the chain is fully aligned across X, X+1 and the tx data the issuer signed.
    /// </summary>
    public bool FullyAligned =>
        TickDataAvailable
        && VotesForTick.AllQuorumsReached
        && VotesForNextTick.AllQuorumsReached;
}

/// <summary>Per-step wall-clock timings for building a <see cref="VoteAlignment"/>.</summary>
public sealed class VoteAlignmentTimings
{
    public required DateTime StartedAt { get; init; }
    public required DateTime FinishedAt { get; init; }
    public required TimingStep TickDataFetch { get; init; }
    public required TimingStep VotesForTickFetch { get; init; }
    public required TimingStep VotesForNextTickFetch { get; init; }
    public required TimingStep DistributionCompute { get; init; }
    public TimeSpan TotalDuration => FinishedAt - StartedAt;
}

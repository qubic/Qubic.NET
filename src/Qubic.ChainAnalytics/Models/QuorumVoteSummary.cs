using Qubic.Core.Entities;

namespace Qubic.ChainAnalytics.Models;

/// <summary>
/// Aggregated view of the quorum votes the node holds for a single tick.
/// Each consensus-critical field is bucketed across the received votes so you can
/// see how the network split (or didn't) on that tick.
/// </summary>
public sealed class QuorumVoteSummary
{
    /// <summary>The tick these votes are for.</summary>
    public required uint Tick { get; init; }

    /// <summary>Raw votes (one per computor) as returned by the node, in the node's randomised order.</summary>
    public required IReadOnlyList<Tick> Votes { get; init; }

    /// <summary>Total number of votes the node reported for this tick.</summary>
    public int TotalVotes => Votes.Count;

    /// <summary>Distinct computors that voted (some may have voted multiple times in pathological cases).</summary>
    public int DistinctComputors => Votes.Select(v => v.ComputorIndex).Distinct().Count();

    /// <summary>
    /// Distribution of <c>transactionDigest</c> — K12 of the transactions this tick executed.
    /// Computors that agree on which transactions ran in this tick share a value.
    /// </summary>
    public required VoteFieldDistribution TransactionDigest { get; init; }

    /// <summary>Distribution of <c>prevSpectrumDigest</c> — state digest at the start of this tick.</summary>
    public required VoteFieldDistribution PrevSpectrumDigest { get; init; }

    /// <summary>Distribution of <c>prevUniverseDigest</c> — assets digest at the start of this tick.</summary>
    public required VoteFieldDistribution PrevUniverseDigest { get; init; }

    /// <summary>Distribution of <c>prevComputerDigest</c> — contracts digest at the start of this tick.</summary>
    public required VoteFieldDistribution PrevComputerDigest { get; init; }

    /// <summary>Distribution of <c>expectedNextTickTransactionDigest</c> — what computors expect for the next tick's tx set.</summary>
    public required VoteFieldDistribution ExpectedNextTickTransactionDigest { get; init; }

    /// <summary>
    /// True when every consensus-critical distribution reached <see cref="Qubic.Core.QubicConstants.Quorum"/>
    /// on the same dominant value. The tick can finalise iff this is true.
    /// </summary>
    public bool AllQuorumsReached =>
        TransactionDigest.QuorumReached
        && PrevSpectrumDigest.QuorumReached
        && PrevUniverseDigest.QuorumReached
        && PrevComputerDigest.QuorumReached;
}

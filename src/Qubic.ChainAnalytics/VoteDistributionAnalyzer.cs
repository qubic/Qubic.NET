using Qubic.ChainAnalytics.Models;
using Qubic.Core;
using Qubic.Core.Entities;
using Qubic.Crypto;
using Qubic.Network;

namespace Qubic.ChainAnalytics;

/// <summary>
/// Direct-mainnet vote-distribution analytics. For a given tick X, pulls
/// (1) the tick data, (2) the quorum votes the node holds for X, and
/// (3) the quorum votes the node holds for X+1. Buckets each consensus-critical
/// field across the votes to surface alignment / split among computors.
/// </summary>
public sealed class VoteDistributionAnalyzer
{
    private readonly QubicNodeClient _client;
    private readonly QubicCrypt _crypt;

    public VoteDistributionAnalyzer(QubicNodeClient client, QubicCrypt? crypt = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        _client = client;
        _crypt = crypt ?? new QubicCrypt();
    }

    /// <summary>
    /// Builds the vote-alignment summary for tick X by talking directly to the node.
    /// Three sequential round-trips: tick data, votes for X, votes for X+1.
    /// </summary>
    public async Task<VoteAlignment> GetVoteAlignmentAsync(
        uint tick,
        CancellationToken cancellationToken = default)
    {
        var startedAt = DateTime.UtcNow;

        var tdStart = DateTime.UtcNow;
        var tickData = await _client.GetTickDataAsync(tick, cancellationToken).ConfigureAwait(false);
        var tdStep = new TimingStep(tdStart, DateTime.UtcNow);

        var v1Start = DateTime.UtcNow;
        var votesX = await _client.GetQuorumVotesAsync(tick, cancellationToken).ConfigureAwait(false);
        var v1Step = new TimingStep(v1Start, DateTime.UtcNow);

        var v2Start = DateTime.UtcNow;
        var votesXPlus1 = await _client.GetQuorumVotesAsync(tick + 1, cancellationToken).ConfigureAwait(false);
        var v2Step = new TimingStep(v2Start, DateTime.UtcNow);

        var distStart = DateTime.UtcNow;
        var summaryX = BuildSummary(tick, votesX, _crypt);
        var summaryXPlus1 = BuildSummary(tick + 1, votesXPlus1, _crypt);
        var distStep = new TimingStep(distStart, DateTime.UtcNow);

        return new VoteAlignment
        {
            Tick = tick,
            TickData = tickData,
            VotesForTick = summaryX,
            VotesForNextTick = summaryXPlus1,
            Timings = new VoteAlignmentTimings
            {
                StartedAt = startedAt,
                FinishedAt = DateTime.UtcNow,
                TickDataFetch = tdStep,
                VotesForTickFetch = v1Step,
                VotesForNextTickFetch = v2Step,
                DistributionCompute = distStep,
            },
        };
    }

    private static QuorumVoteSummary BuildSummary(uint tick, IReadOnlyList<Tick> votes, QubicCrypt crypt)
    {
        return new QuorumVoteSummary
        {
            Tick = tick,
            Votes = votes,
            TransactionDigest = BucketBy(nameof(Tick.TransactionDigest), votes, v => v.TransactionDigest, crypt),
            PrevSpectrumDigest = BucketBy(nameof(Tick.PrevSpectrumDigest), votes, v => v.PrevSpectrumDigest, crypt),
            PrevUniverseDigest = BucketBy(nameof(Tick.PrevUniverseDigest), votes, v => v.PrevUniverseDigest, crypt),
            PrevComputerDigest = BucketBy(nameof(Tick.PrevComputerDigest), votes, v => v.PrevComputerDigest, crypt),
            ExpectedNextTickTransactionDigest = BucketBy(
                nameof(Tick.ExpectedNextTickTransactionDigest), votes, v => v.ExpectedNextTickTransactionDigest, crypt),
        };
    }

    private static VoteFieldDistribution BucketBy(
        string fieldName,
        IReadOnlyList<Tick> votes,
        Func<Tick, byte[]> selector,
        QubicCrypt crypt)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var vote in votes)
        {
            var id = crypt.GetHumanReadableBytes(selector(vote));
            counts[id] = counts.GetValueOrDefault(id, 0) + 1;
        }

        var ordered = counts
            .OrderByDescending(kv => kv.Value)
            .Select(kv => (kv.Key, kv.Value))
            .ToList();

        var dominant = ordered.Count > 0 ? ordered[0].Key : string.Empty;
        var dominantCount = ordered.Count > 0 ? ordered[0].Value : 0;
        var quorumReached = dominantCount >= QubicConstants.Quorum;

        return new VoteFieldDistribution(
            FieldName: fieldName,
            Distribution: ordered,
            TotalVotes: votes.Count,
            DominantValue: dominant,
            DominantCount: dominantCount,
            QuorumReached: quorumReached);
    }
}

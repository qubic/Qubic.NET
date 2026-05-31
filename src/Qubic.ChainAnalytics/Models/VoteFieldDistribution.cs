namespace Qubic.ChainAnalytics.Models;

/// <summary>
/// Distribution of a single consensus-critical field across a set of computor votes
/// (e.g. <c>transactionDigest</c>, <c>prevSpectrumDigest</c>). Values are rendered as
/// Qubic lowercase identity strings (60-char K12-checksummed encoding via
/// <c>QubicCrypt.GetHumanReadableBytes</c>) so they're directly comparable to the
/// identity-style hashes shown elsewhere in the toolchain.
/// </summary>
/// <param name="FieldName">Human label for the field (e.g. "TransactionDigest").</param>
/// <param name="Distribution">Distinct identity-encoded values with their vote counts, descending by count.</param>
/// <param name="TotalVotes">Total votes counted into the distribution (sum of counts).</param>
/// <param name="DominantValue">Identity-encoded value with the highest count, or empty when no votes.</param>
/// <param name="DominantCount">Count of votes carrying <paramref name="DominantValue"/>.</param>
/// <param name="QuorumReached">
/// True when <paramref name="DominantCount"/> meets <see cref="Qubic.Core.QubicConstants.Quorum"/>
/// (451 of 676) — i.e. the network agrees on this field for this tick.
/// </param>
public sealed record VoteFieldDistribution(
    string FieldName,
    IReadOnlyList<(string Value, int Count)> Distribution,
    int TotalVotes,
    string DominantValue,
    int DominantCount,
    bool QuorumReached);

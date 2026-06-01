using System.Runtime.CompilerServices;
using Qubic.ChainAnalytics.Models;
using Qubic.Core;
using Qubic.Network;

namespace Qubic.ChainAnalytics;

/// <summary>
/// Direct-mainnet tick analytics: pulls tick data and the corresponding
/// transactions over raw TCP via <see cref="QubicNodeClient"/>, parses them,
/// and produces a <see cref="TickSummary"/> with digest/hash/chain info.
/// No RPC, no Bob — talks straight to a node.
/// </summary>
public sealed class TickAnalyzer
{
    private readonly QubicNodeClient _client;
    private readonly TickTransactionParser _parser;

    public TickAnalyzer(QubicNodeClient client, TickTransactionParser? parser = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        _client = client;
        _parser = parser ?? new TickTransactionParser();
    }

    /// <summary>
    /// Builds a full summary for <paramref name="tick"/>:
    /// (1) fetches the tick data and checks availability,
    /// (2) requests all tx for the tick using a flag map sized for
    ///     <paramref name="epoch"/>, (3) parses each tx and computes its digest,
    /// (4) cross-checks the computed digests against the tick data digest slots.
    /// Wall-clock timings for each step are captured in <see cref="TickSummary.Timings"/>.
    /// </summary>
    public async Task<TickSummary> GetTickSummaryAsync(
        uint tick,
        ushort epoch = QubicConstants.TransactionsPerTickV2Epoch,
        CancellationToken cancellationToken = default)
    {
        var startedAt = DateTime.UtcNow;

        var tickDataStart = DateTime.UtcNow;
        var (tickData, tickDataRaw) = await _client.GetTickDataWithRawAsync(tick, cancellationToken).ConfigureAwait(false);
        var tickDataStep = new TimingStep(tickDataStart, DateTime.UtcNow);

        if (tickData is null)
        {
            return new TickSummary
            {
                TickNumber = tick,
                Epoch = epoch,
                TickDataAvailable = false,
                TickData = null,
                TickDataRawBytes = null,
                TickDataDigests = [],
                Transactions = [],
                DigestsVerified = false,
                Timings = new TickFetchTimings
                {
                    StartedAt = startedAt,
                    FinishedAt = DateTime.UtcNow,
                    TickDataFetch = tickDataStep,
                    TransactionsFetch = null,
                    ParseAndVerify = null,
                },
            };
        }

        var txFetchStart = DateTime.UtcNow;
        var rawTxs = await _client.GetTickTransactionsAsync(tick, epoch, cancellationToken).ConfigureAwait(false);
        var txFetchStep = new TimingStep(txFetchStart, DateTime.UtcNow);

        var parseStart = DateTime.UtcNow;
        var parsed = new List<TickTransaction>(rawTxs.Count);
        foreach (var raw in rawTxs)
            parsed.Add(_parser.Parse(raw));
        var verified = VerifyDigests(parsed, tickData.TransactionDigests);
        var parseStep = new TimingStep(parseStart, DateTime.UtcNow);

        return new TickSummary
        {
            TickNumber = tick,
            Epoch = tickData.Epoch == 0 ? epoch : tickData.Epoch,
            TickDataAvailable = true,
            TickData = tickData,
            TickDataRawBytes = tickDataRaw,
            TickDataDigests = tickData.TransactionDigests,
            Transactions = parsed,
            DigestsVerified = verified,
            Timings = new TickFetchTimings
            {
                StartedAt = startedAt,
                FinishedAt = DateTime.UtcNow,
                TickDataFetch = tickDataStep,
                TransactionsFetch = txFetchStep,
                ParseAndVerify = parseStep,
            },
        };
    }

    /// <summary>
    /// Streams <see cref="TickSummary"/> objects for every tick in
    /// <c>[fromTick, toTick]</c> in ascending order. Requests are issued
    /// sequentially — <see cref="QubicNodeClient"/> serialises in-flight
    /// requests, so range scans are connection-bound. Use multiple analyzers
    /// against multiple peers for parallel coverage.
    /// </summary>
    public async IAsyncEnumerable<TickSummary> ScanAsync(
        uint fromTick,
        uint toTick,
        ushort epoch = QubicConstants.TransactionsPerTickV2Epoch,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (toTick < fromTick)
            throw new ArgumentException("toTick must be >= fromTick.", nameof(toTick));

        for (var t = fromTick; t <= toTick; t++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return await GetTickSummaryAsync(t, epoch, cancellationToken).ConfigureAwait(false);
        }
    }

    private static bool VerifyDigests(
        IReadOnlyList<TickTransaction> txs,
        IReadOnlyList<byte[]> tickDataDigests)
    {
        var nonEmpty = new HashSet<string>(StringComparer.Ordinal);
        foreach (var digest in tickDataDigests)
        {
            if (IsAllZero(digest)) continue;
            nonEmpty.Add(Convert.ToHexString(digest));
        }

        if (nonEmpty.Count != txs.Count)
            return false;

        foreach (var tx in txs)
        {
            if (!nonEmpty.Contains(Convert.ToHexString(tx.Digest)))
                return false;
        }
        return true;
    }

    private static bool IsAllZero(ReadOnlySpan<byte> bytes)
    {
        foreach (var b in bytes)
            if (b != 0) return false;
        return true;
    }
}

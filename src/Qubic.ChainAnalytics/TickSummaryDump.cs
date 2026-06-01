using System.Buffers.Binary;
using System.Text.Json;
using Qubic.ChainAnalytics.Models;

namespace Qubic.ChainAnalytics;

/// <summary>
/// Persists a <see cref="TickSummary"/> to disk in three forms:
/// <list type="bullet">
///   <item><c>tick-{N}-tickdata.bin</c> — raw <c>BroadcastFutureTickData</c> wire bytes (the canonical signed form).</item>
///   <item><c>tick-{N}-txs.bin</c> — concatenated raw tx bytes with a small framing header (count + per-tx length).</item>
///   <item><c>tick-{N}.json</c> — full parsed view (tick data fields, digests, parsed transactions with hashes / identities / payload).</item>
/// </list>
/// Use the binaries for round-tripping or re-verification on another tool;
/// use the JSON for human inspection or feeding into downstream pipelines.
/// </summary>
public static class TickSummaryDump
{
    private const int TxBundleMagic = 0x58544251; // "QBTX" little-endian

    /// <summary>
    /// Writes the three artefacts to <paramref name="directory"/>, creating it if needed.
    /// Returns the absolute paths actually written (only files relevant for the summary).
    /// </summary>
    public static IReadOnlyList<string> Write(TickSummary summary, string directory, bool indentedJson = true)
    {
        ArgumentNullException.ThrowIfNull(summary);
        ArgumentNullException.ThrowIfNull(directory);
        Directory.CreateDirectory(directory);
        var stem = $"tick-{summary.TickNumber:D10}";
        var written = new List<string>(3);

        if (summary.TickDataRawBytes is byte[] raw)
        {
            var path = Path.Combine(directory, $"{stem}-tickdata.bin");
            File.WriteAllBytes(path, raw);
            written.Add(path);
        }

        if (summary.Transactions.Count > 0)
        {
            var path = Path.Combine(directory, $"{stem}-txs.bin");
            File.WriteAllBytes(path, BuildTxBundle(summary.Transactions));
            written.Add(path);
        }

        {
            var path = Path.Combine(directory, $"{stem}.json");
            File.WriteAllText(path, BuildJson(summary, indentedJson));
            written.Add(path);
        }

        return written;
    }

    /// <summary>
    /// Wire layout for <c>tick-{N}-txs.bin</c>:
    /// <c>u32 magic ("QBTQ") | u32 tick | u32 count | for each tx: u32 length | bytes</c>.
    /// All little-endian. Self-describing so external tools can decode it without
    /// referencing Qubic.ChainAnalytics.
    /// </summary>
    private static byte[] BuildTxBundle(IReadOnlyList<TickTransaction> txs)
    {
        var total = 4 + 4 + 4; // magic + tick + count
        foreach (var tx in txs)
            total += 4 + tx.RawBytes.Length;

        var buf = new byte[total];
        var offset = 0;
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(offset), (uint)TxBundleMagic); offset += 4;
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(offset), txs[0].Tick); offset += 4;
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(offset), (uint)txs.Count); offset += 4;
        foreach (var tx in txs)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(offset), (uint)tx.RawBytes.Length); offset += 4;
            Buffer.BlockCopy(tx.RawBytes, 0, buf, offset, tx.RawBytes.Length);
            offset += tx.RawBytes.Length;
        }
        return buf;
    }

    private static string BuildJson(TickSummary s, bool indented)
    {
        var dto = new
        {
            tickNumber = s.TickNumber,
            epoch = s.Epoch,
            tickDataAvailable = s.TickDataAvailable,
            digestsVerified = s.DigestsVerified,
            tickData = s.TickData is null ? null : new
            {
                computorIndex = s.TickData.ComputorIndex,
                epoch = s.TickData.Epoch,
                tickNumber = s.TickData.TickNumber,
                timestamp = s.TickData.Timestamp == DateTime.MinValue ? null : s.TickData.Timestamp.ToString("O"),
                timelockHex = HexLower(s.TickData.Timelock),
                signatureHex = HexLower(s.TickData.Signature),
                transactionDigests = s.TickData.TransactionDigests
                    .Select((d, i) => new { slot = i, hex = HexLower(d), empty = IsAllZero(d) })
                    .Where(x => !x.empty)
                    .Select(x => new { x.slot, x.hex })
                    .ToArray(),
                emptyDigestSlots = s.TickData.TransactionDigests.Count(IsAllZero),
                contractFees = s.TickData.ContractFees
                    .Select((fee, idx) => new { idx, fee })
                    .Where(x => x.fee != 0)
                    .ToArray(),
            },
            counts = new
            {
                total = s.Transactions.Count,
                userTransfer = s.UserTransferCount,
                contractCall = s.ContractCallCount,
                systemMessage = s.SystemMessageCount,
            },
            totals = new
            {
                amountQu = s.TotalAmount,
            },
            transactions = s.Transactions.Select((tx, i) => new
            {
                index = i,
                hash = tx.Hash,
                digestHex = HexLower(tx.Digest),
                kind = tx.Kind.ToString(),
                source = tx.SourceIdentity,
                destination = tx.DestinationIdentity,
                sourcePublicKeyHex = HexLower(tx.SourcePublicKey),
                destinationPublicKeyHex = HexLower(tx.DestinationPublicKey),
                destinationContractIndex = tx.DestinationContractIndex,
                amount = tx.Amount,
                tick = tx.Tick,
                inputType = tx.InputType,
                inputSize = tx.InputSize,
                payloadHex = HexLower(tx.Payload),
                signatureHex = HexLower(tx.Signature),
                rawBytesHex = HexLower(tx.RawBytes),
            }).ToArray(),
            timings = new
            {
                startedAt = s.Timings.StartedAt.ToString("O"),
                finishedAt = s.Timings.FinishedAt.ToString("O"),
                totalMs = s.Timings.TotalDuration.TotalMilliseconds,
                tickDataFetchMs = s.Timings.TickDataFetch.Duration.TotalMilliseconds,
                transactionsFetchMs = s.Timings.TransactionsFetch?.Duration.TotalMilliseconds,
                parseAndVerifyMs = s.Timings.ParseAndVerify?.Duration.TotalMilliseconds,
            },
        };

        var options = new JsonSerializerOptions
        {
            WriteIndented = indented,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        };
        return JsonSerializer.Serialize(dto, options);
    }

    private static string HexLower(byte[]? bytes) =>
        bytes is null || bytes.Length == 0 ? "" : Convert.ToHexString(bytes).ToLowerInvariant();

    private static bool IsAllZero(byte[] bytes)
    {
        foreach (var b in bytes)
            if (b != 0) return false;
        return true;
    }
}

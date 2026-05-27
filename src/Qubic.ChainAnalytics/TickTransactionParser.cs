using System.Buffers.Binary;
using Qubic.ChainAnalytics.Models;
using Qubic.Crypto;

namespace Qubic.ChainAnalytics;

/// <summary>
/// Parses raw transaction bytes (as returned by RequestTickTransactions) into a
/// <see cref="TickTransaction"/>, computing the K12 digest and human-readable hash.
/// </summary>
/// <remarks>
/// Wire layout (mirrors <c>QubicTransaction.GetRawBytes()</c>):
/// 32 src pubkey | 32 dst pubkey | 8 amount | 4 tick | 2 inputType | 2 inputSize
/// | inputSize payload | 64 signature.
/// </remarks>
public sealed class TickTransactionParser
{
    private const int MinTxBytes = 32 + 32 + 8 + 4 + 2 + 2 + 64; // 144 bytes for an empty-payload tx.

    private readonly QubicCrypt _crypt;

    public TickTransactionParser() : this(new QubicCrypt()) { }

    public TickTransactionParser(QubicCrypt crypt)
    {
        ArgumentNullException.ThrowIfNull(crypt);
        _crypt = crypt;
    }

    public TickTransaction Parse(byte[] rawBytes)
    {
        ArgumentNullException.ThrowIfNull(rawBytes);
        if (rawBytes.Length < MinTxBytes)
            throw new ArgumentException($"Transaction too short: {rawBytes.Length} < {MinTxBytes}.", nameof(rawBytes));

        var span = rawBytes.AsSpan();
        var offset = 0;

        var sourcePubKey = span.Slice(offset, 32).ToArray(); offset += 32;
        var destPubKey = span.Slice(offset, 32).ToArray(); offset += 32;
        var amount = BinaryPrimitives.ReadInt64LittleEndian(span[offset..]); offset += 8;
        var tick = BinaryPrimitives.ReadUInt32LittleEndian(span[offset..]); offset += 4;
        var inputType = BinaryPrimitives.ReadUInt16LittleEndian(span[offset..]); offset += 2;
        var inputSize = BinaryPrimitives.ReadUInt16LittleEndian(span[offset..]); offset += 2;

        var expected = MinTxBytes + inputSize;
        if (rawBytes.Length != expected)
            throw new ArgumentException(
                $"Transaction length {rawBytes.Length} does not match declared inputSize {inputSize} (expected {expected}).",
                nameof(rawBytes));

        var payload = inputSize == 0 ? [] : span.Slice(offset, inputSize).ToArray();
        offset += inputSize;
        var signature = span.Slice(offset, 64).ToArray();

        var digest = _crypt.KangarooTwelve(rawBytes);
        var hash = _crypt.GetHumanReadableBytes(digest);
        var sourceIdentity = _crypt.GetIdentityFromPublicKey(sourcePubKey);
        var destIdentity = _crypt.GetIdentityFromPublicKey(destPubKey);

        return new TickTransaction(
            SourceIdentity: sourceIdentity,
            DestinationIdentity: destIdentity,
            SourcePublicKey: sourcePubKey,
            DestinationPublicKey: destPubKey,
            Amount: amount,
            Tick: tick,
            InputType: inputType,
            InputSize: inputSize,
            Payload: payload,
            Signature: signature,
            Digest: digest,
            Hash: hash,
            RawBytes: rawBytes);
    }
}

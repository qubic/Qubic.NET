using Qubic.Core.Entities;
using Qubic.Crypto;

namespace Qubic.ChainAnalytics;

/// <summary>
/// SchnorrQ verification of a <see cref="TickData"/> signature against the
/// computor public key referenced by <see cref="TickData.ComputorIndex"/>.
/// </summary>
/// <remarks>
/// Verification follows the qubic-core convention: K12-hash the tick data bytes
/// excluding the trailing 64-byte signature, then SchnorrQ-verify the hash with
/// the computor's 32-byte public key.
/// </remarks>
public static class TickDataSignatureVerifier
{
    private const int SignatureSize = 64;
    private const byte BroadcastFutureTickDataType = 8;

    /// <summary>
    /// Verifies the signature on <paramref name="tickDataRawBytes"/> using
    /// <paramref name="computors"/>. Returns one of:
    /// <list type="bullet">
    ///   <item>(true, null) — signature verified against the expected computor.</item>
    ///   <item>(false, reason) — signature did not verify; reason may be diagnostic.</item>
    ///   <item>(null, reason) — verification skipped (epoch mismatch / bad index / no data).</item>
    /// </list>
    /// </summary>
    public static (bool? Result, string? Reason) Verify(
        TickData tickData,
        byte[]? tickDataRawBytes,
        Computors? computors,
        QubicCrypt? crypt = null)
    {
        if (computors is null)
            return (null, "no computors supplied");

        if (tickDataRawBytes is null || tickDataRawBytes.Length <= SignatureSize)
            return (null, "no raw tick data bytes (verification needs them)");

        if (computors.Epoch != tickData.Epoch)
            return (null, $"computor set epoch {computors.Epoch} ≠ tick epoch {tickData.Epoch}");

        var idx = tickData.ComputorIndex;
        if (idx >= computors.PublicKeys.Length)
            return (false, $"computor index {idx} out of range (0..{computors.PublicKeys.Length - 1})");

        var pubKey = computors.PublicKeys[idx];
        var message = new byte[tickDataRawBytes.Length - SignatureSize];
        Buffer.BlockCopy(tickDataRawBytes, 0, message, 0, message.Length);

        // qubic-core XORs computorIndex (u16 at offset 0) with the packet type before
        // computing the K12 challenge — see processBroadcastFutureTickData. Mirror it
        // here: byte 0 (low byte of the LE u16) carries the XOR.
        message[0] ^= BroadcastFutureTickDataType;

        var signature = new byte[SignatureSize];
        Buffer.BlockCopy(tickDataRawBytes, message.Length, signature, 0, SignatureSize);

        crypt ??= new QubicCrypt();
        var ok = crypt.Verify(pubKey, message, signature);
        return ok ? (true, null) : (false, $"SchnorrQ verify failed (computor #{idx})");
    }
}

using System;
using System.Buffers.Binary;
using System.Numerics;

namespace Qubic.Crypto.FourQ;

/// <summary>
/// Point encoding and decoding for FourQ.
/// Uses 32-byte compressed format: y-coordinate with x-sign bit.
/// </summary>
public static class FourQCodec
{
    /// <summary>
    /// Encodes a point to 32 bytes.
    /// Format: y-coordinate (32 bytes) with sign bit of x in highest bit of last byte.
    /// </summary>
    public static byte[] Encode(PointExt point)
    {
        var result = new byte[32];
        Encode(point, result);
        return result;
    }

    /// <summary>
    /// Encodes a point to the provided buffer.
    /// </summary>
    public static void Encode(PointExt point, Span<byte> output)
    {
        if (output.Length < 32)
            throw new ArgumentException("Output must be at least 32 bytes", nameof(output));

        var (x, y) = point.ToAffine();

        // Write y-coordinate
        y.ToBytes(output);

        // Set sign bit of x in the highest bit of byte 31
        var xSign = FourQPoint.GetXSign(x);
        output[31] = (byte)((output[31] & 0x7F) | (xSign << 7));
    }

    /// <summary>
    /// Decodes a point from 32 bytes.
    /// </summary>
    public static PointExt? Decode(ReadOnlySpan<byte> input)
    {
        if (input.Length < 32)
            throw new ArgumentException("Input must be at least 32 bytes", nameof(input));

        // Extract sign bit
        int xSign = (input[31] >> 7) & 1;

        // Read y-coordinate (masking out sign bit)
        Span<byte> yBytes = stackalloc byte[32];
        input.Slice(0, 32).CopyTo(yBytes);
        yBytes[31] &= 0x7F; // Clear sign bit

        var y = Fp2.FromBytes(yBytes);

        // Recover x from curve equation: -x² + y² = 1 + d·x²·y²
        // Rearranging: x² = (y² - 1) / (d·y² + 1)
        var x = RecoverX(y);
        if (x == null)
            return null;

        // Adjust sign
        var currentSign = FourQPoint.GetXSign(x.Value);
        var xFinal = currentSign != xSign ? new Fp2(-x.Value.A, -x.Value.B) : x.Value;

        var point = PointExt.FromAffine(xFinal, y);

        // Verify point is on curve
        if (!FourQPoint.IsOnCurve(point))
            return null;

        return point;
    }

    // Curve parameter d for FourQ (little-endian format from FourQlib)
    private static readonly Fp2 D = new(
        Fp.FromU64LE(0x0000000000000142UL, 0x00000000000000E4UL),
        Fp.FromU64LE(0xB3821488F1FC0C8DUL, 0x5E472F846657E0FCUL)
    );

    private static Fp2? RecoverX(Fp2 y)
    {
        // x² = (y² - 1) / (d·y² + 1)
        var y2 = y.Square();
        var numerator = y2 - Fp2.One;
        var denominator = D * y2 + Fp2.One;

        if (denominator.IsZero)
            return null;

        var x2 = numerator * denominator.Inverse();
        return x2.Sqrt();
    }

    /// <summary>
    /// Validates that a 32-byte public key has valid encoding.
    /// </summary>
    public static bool IsValidPublicKeyEncoding(ReadOnlySpan<byte> publicKey)
    {
        if (publicKey.Length != 32)
            return false;

        // Check that y₀ (real part of y) is in range [0, p)
        // Bit 127 of y₀ must be 0 (excluding sign bit which is in position 255)
        // Position 127 is bit 7 of byte 15
        if ((publicKey[15] & 0x80) != 0)
            return false;

        return true;
    }

    // Curve order N (little-endian limbs), kept in sync with EccMul.CURVE_ORDER_*.
    // N = 0x0029CBC14E5E0A72_F05397829CBC14E5_DFBD004DFE0F7999_2FB2540EC7768CE7
    private const ulong CURVE_ORDER_0 = 0x2FB2540EC7768CE7;
    private const ulong CURVE_ORDER_1 = 0xDFBD004DFE0F7999;
    private const ulong CURVE_ORDER_2 = 0xF05397829CBC14E5;
    private const ulong CURVE_ORDER_3 = 0x0029CBC14E5E0A72;

    /// <summary>
    /// Validates that a 64-byte signature has valid encoding.
    /// Enforces the canonical scalar form S &lt; N (curve order) on the last 32 bytes — the
    /// previous "S &lt; 2^246" mask is insufficient because the range [N, 2^246) admits a
    /// malleable twin S' = S + N that verifies for the same message but yields a different
    /// transaction hash, bypassing replay/dedup protection. See qubic/core 05f7348
    /// ("Security patch of four_q.h").
    /// </summary>
    public static bool IsValidSignatureEncoding(ReadOnlySpan<byte> signature)
    {
        if (signature.Length != 64)
            return false;

        // First 32 bytes: R (point encoding). Check that R's y₀ has bit 127 clear.
        if ((signature[15] & 0x80) != 0)
            return false;

        // Last 32 bytes: S (scalar). Reject non-canonical S, i.e. require S < N.
        // S is stored little-endian, so compare most-significant limb first; strict "<"
        // also rejects S == N.
        var sBytes = signature.Slice(32, 32);
        ulong s0 = BinaryPrimitives.ReadUInt64LittleEndian(sBytes.Slice(0, 8));
        ulong s1 = BinaryPrimitives.ReadUInt64LittleEndian(sBytes.Slice(8, 8));
        ulong s2 = BinaryPrimitives.ReadUInt64LittleEndian(sBytes.Slice(16, 8));
        ulong s3 = BinaryPrimitives.ReadUInt64LittleEndian(sBytes.Slice(24, 8));

        if (s3 != CURVE_ORDER_3) return s3 < CURVE_ORDER_3;
        if (s2 != CURVE_ORDER_2) return s2 < CURVE_ORDER_2;
        if (s1 != CURVE_ORDER_1) return s1 < CURVE_ORDER_1;
        return s0 < CURVE_ORDER_0;
    }
}

using System;
using System.Buffers.Binary;

namespace Qubic.Crypto;

/// <summary>
/// KangarooTwelve (K12) hash function implementation.
/// Based on TurboSHAKE128 with Keccak-p[1600,12] permutation.
/// Compatible with @noble/hashes K12 implementation used by Qubic.
/// </summary>
public static class K12
{
    private const int Rate = 168; // 1344 bits = 168 bytes (capacity = 256 bits)
    private const int ChunkLen = 8192; // 8KB chunks
    private const int StateSize = 25; // 5x5 state of 64-bit words

    private static readonly ulong[] RC = new ulong[24]
    {
        0x0000000000000001UL, 0x0000000000008082UL, 0x800000000000808AUL, 0x8000000080008000UL,
        0x000000000000808BUL, 0x0000000080000001UL, 0x8000000080008081UL, 0x8000000000008009UL,
        0x000000000000008AUL, 0x0000000000000088UL, 0x0000000080008009UL, 0x000000008000000AUL,
        0x000000008000808BUL, 0x800000000000008BUL, 0x8000000000008089UL, 0x8000000000008003UL,
        0x8000000000008002UL, 0x8000000000000080UL, 0x000000000000800AUL, 0x800000008000000AUL,
        0x8000000080008081UL, 0x8000000000008080UL, 0x0000000080000001UL, 0x8000000080008008UL
    };

    /// <summary>
    /// Computes the K12 hash of the input data.
    /// </summary>
    public static byte[] Hash(ReadOnlySpan<byte> input, int outputLength = 32)
    {
        if (outputLength <= 0)
            throw new ArgumentOutOfRangeException(nameof(outputLength));

        var output = new byte[outputLength];
        Hash(input, output.AsSpan());
        return output;
    }

    /// <summary>
    /// Computes the K12 hash of the input data into the provided buffer.
    /// </summary>
    public static void Hash(ReadOnlySpan<byte> input, Span<byte> output)
    {
        Hash(input, ReadOnlySpan<byte>.Empty, output);
    }

    /// <summary>
    /// Computes K12 with a customization string.
    /// </summary>
    public static byte[] Hash(ReadOnlySpan<byte> input, ReadOnlySpan<byte> customization, int outputLength = 32)
    {
        var output = new byte[outputLength];
        Hash(input, customization, output.AsSpan());
        return output;
    }

    /// <summary>
    /// Computes K12 with a customization string into the provided buffer.
    /// Compatible with noble/hashes K12 implementation.
    /// </summary>
    public static void Hash(ReadOnlySpan<byte> input, ReadOnlySpan<byte> customization, Span<byte> output)
    {
        if (output.Length == 0)
            throw new ArgumentException("Output buffer cannot be empty");

        // For messages <= 8192 bytes with short customization, use single-block mode
        // S = M || C || right_encode(|C|)

        // Calculate right_encode for customization length
        Span<byte> rightEnc = stackalloc byte[9];
        int rightEncLen = RightEncodeK12(rightEnc, (ulong)customization.Length);

        int totalLen = input.Length + customization.Length + rightEncLen;

        if (totalLen <= ChunkLen)
        {
            // Single-block mode (short message)
            HashSingleBlock(input, customization, rightEnc.Slice(0, rightEncLen), output);
        }
        else
        {
            // Multi-block mode (long message) - not commonly used in Qubic
            HashMultiBlock(input, customization, rightEnc.Slice(0, rightEncLen), output);
        }
    }

    private static void HashSingleBlock(ReadOnlySpan<byte> input, ReadOnlySpan<byte> customization,
                                         ReadOnlySpan<byte> rightEnc, Span<byte> output)
    {
        // TurboSHAKE128 with domain separator 0x07
        var state = new ulong[StateSize];
        var block = new byte[Rate];
        int blockPos = 0;

        // Absorb message
        for (int i = 0; i < input.Length; i++)
        {
            block[blockPos++] = input[i];
            if (blockPos == Rate)
            {
                XorBlock(state, block);
                KeccakP12(state);
                Array.Clear(block, 0, Rate);
                blockPos = 0;
            }
        }

        // Absorb customization
        for (int i = 0; i < customization.Length; i++)
        {
            block[blockPos++] = customization[i];
            if (blockPos == Rate)
            {
                XorBlock(state, block);
                KeccakP12(state);
                Array.Clear(block, 0, Rate);
                blockPos = 0;
            }
        }

        // Absorb right_encode(|C|)
        for (int i = 0; i < rightEnc.Length; i++)
        {
            block[blockPos++] = rightEnc[i];
            if (blockPos == Rate)
            {
                XorBlock(state, block);
                KeccakP12(state);
                Array.Clear(block, 0, Rate);
                blockPos = 0;
            }
        }

        // Finalize: clear rest of block, add domain separator and padding
        for (int i = blockPos; i < Rate; i++)
            block[i] = 0;

        block[blockPos] ^= 0x07;    // Domain separator for K12
        block[Rate - 1] ^= 0x80;    // Padding 10*1
        XorBlock(state, block);
        KeccakP12(state);

        // Squeeze
        int outPos = 0;
        while (outPos < output.Length)
        {
            int toCopy = Math.Min(Rate, output.Length - outPos);
            ExtractBytes(state, output.Slice(outPos, toCopy));
            outPos += toCopy;
            if (outPos < output.Length)
                KeccakP12(state);
        }
    }

    private static void HashMultiBlock(ReadOnlySpan<byte> input, ReadOnlySpan<byte> customization,
                                        ReadOnlySpan<byte> rightEnc, Span<byte> output)
    {
        // KangarooTwelve tree hashing for messages > 8192 bytes
        // S = M || C || right_encode(|C|)
        // Split S into 8192-byte chunks: S0, S1, S2, ...
        // Final = S0 || 0x03 0x00 0x00 0x00 0x00 0x00 0x00 0x00 || CV1 || CV2 || ... || right_encode(n) || 0xFF 0xFF
        // where CVi = TurboSHAKE128(Si, domain=0x0B, 32 bytes) for i >= 1

        // Build S = M || C || right_encode(|C|)
        int totalLen = input.Length + customization.Length + rightEnc.Length;
        var s = new byte[totalLen];
        input.CopyTo(s.AsSpan());
        customization.CopyTo(s.AsSpan(input.Length));
        rightEnc.CopyTo(s.AsSpan(input.Length + customization.Length));

        // Calculate number of chunks
        int numChunks = (totalLen + ChunkLen - 1) / ChunkLen;

        // First chunk S0 goes directly into final hash input
        int s0Len = Math.Min(ChunkLen, totalLen);
        var s0 = s.AsSpan(0, s0Len);

        // Compute chaining values for chunks S1, S2, ...
        int numCVs = numChunks - 1;
        var cvs = new byte[numCVs * 32];

        for (int i = 1; i < numChunks; i++)
        {
            int chunkStart = i * ChunkLen;
            int chunkLen = Math.Min(ChunkLen, totalLen - chunkStart);
            var chunk = s.AsSpan(chunkStart, chunkLen);

            // CV_i = TurboSHAKE128(S_i, domain=0x0B, 32 bytes)
            TurboShake128(chunk, 0x0B, cvs.AsSpan((i - 1) * 32, 32));
        }

        // Build final message:
        // S0 || 0x03 0x00 0x00 0x00 0x00 0x00 0x00 0x00 || CV1 || CV2 || ... || right_encode(numCVs) || 0xFF 0xFF
        Span<byte> rightEncN = stackalloc byte[9];
        int rightEncNLen = RightEncodeK12(rightEncN, (ulong)numCVs);

        int finalLen = s0Len + 8 + (numCVs * 32) + rightEncNLen + 2;
        var finalInput = new byte[finalLen];
        int pos = 0;

        // S0
        s0.CopyTo(finalInput.AsSpan(pos));
        pos += s0Len;

        // 0x03 followed by 7 zero bytes (marks start of tree hashing)
        finalInput[pos++] = 0x03;
        for (int i = 0; i < 7; i++)
            finalInput[pos++] = 0x00;

        // Chaining values
        cvs.AsSpan().CopyTo(finalInput.AsSpan(pos));
        pos += numCVs * 32;

        // right_encode(numCVs)
        rightEncN.Slice(0, rightEncNLen).CopyTo(finalInput.AsSpan(pos));
        pos += rightEncNLen;

        // 0xFF 0xFF (final tree marker)
        finalInput[pos++] = 0xFF;
        finalInput[pos++] = 0xFF;

        // Hash the final message with TurboSHAKE128 domain 0x06 (final node)
        TurboShake128(finalInput, 0x06, output);
    }

    /// <summary>
    /// TurboSHAKE128 - the underlying sponge function for K12.
    /// </summary>
    private static void TurboShake128(ReadOnlySpan<byte> input, byte domainSeparator, Span<byte> output)
    {
        var state = new ulong[StateSize];
        var block = new byte[Rate];
        int blockPos = 0;

        // Absorb input
        for (int i = 0; i < input.Length; i++)
        {
            block[blockPos++] = input[i];
            if (blockPos == Rate)
            {
                XorBlock(state, block);
                KeccakP12(state);
                Array.Clear(block, 0, Rate);
                blockPos = 0;
            }
        }

        // Finalize: clear rest of block, add domain separator and padding
        for (int i = blockPos; i < Rate; i++)
            block[i] = 0;

        block[blockPos] ^= domainSeparator;
        block[Rate - 1] ^= 0x80;
        XorBlock(state, block);
        KeccakP12(state);

        // Squeeze
        int outPos = 0;
        while (outPos < output.Length)
        {
            int toCopy = Math.Min(Rate, output.Length - outPos);
            ExtractBytes(state, output.Slice(outPos, toCopy));
            outPos += toCopy;
            if (outPos < output.Length)
                KeccakP12(state);
        }
    }

    private static void XorBlock(ulong[] state, byte[] block)
    {
        for (int i = 0; i < Rate / 8; i++)
        {
            state[i] ^= BitConverter.ToUInt64(block, i * 8);
        }
    }

    private static void ExtractBytes(ulong[] state, Span<byte> output)
    {
        int fullWords = output.Length / 8;
        int remaining = output.Length % 8;

        for (int i = 0; i < fullWords; i++)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(output.Slice(i * 8, 8), state[i]);
        }

        if (remaining > 0)
        {
            Span<byte> temp = stackalloc byte[8];
            BinaryPrimitives.WriteUInt64LittleEndian(temp, state[fullWords]);
            temp.Slice(0, remaining).CopyTo(output.Slice(fullWords * 8, remaining));
        }
    }

    private static void KeccakP12(ulong[] A)
    {
        // Keccak-p[1600, 12] - uses rounds 12-23
        for (int round = 12; round < 24; round++)
        {
            // θ (theta)
            ulong C0 = A[0] ^ A[5] ^ A[10] ^ A[15] ^ A[20];
            ulong C1 = A[1] ^ A[6] ^ A[11] ^ A[16] ^ A[21];
            ulong C2 = A[2] ^ A[7] ^ A[12] ^ A[17] ^ A[22];
            ulong C3 = A[3] ^ A[8] ^ A[13] ^ A[18] ^ A[23];
            ulong C4 = A[4] ^ A[9] ^ A[14] ^ A[19] ^ A[24];

            ulong D0 = C4 ^ ROL(C1, 1);
            ulong D1 = C0 ^ ROL(C2, 1);
            ulong D2 = C1 ^ ROL(C3, 1);
            ulong D3 = C2 ^ ROL(C4, 1);
            ulong D4 = C3 ^ ROL(C0, 1);

            A[0] ^= D0; A[5] ^= D0; A[10] ^= D0; A[15] ^= D0; A[20] ^= D0;
            A[1] ^= D1; A[6] ^= D1; A[11] ^= D1; A[16] ^= D1; A[21] ^= D1;
            A[2] ^= D2; A[7] ^= D2; A[12] ^= D2; A[17] ^= D2; A[22] ^= D2;
            A[3] ^= D3; A[8] ^= D3; A[13] ^= D3; A[18] ^= D3; A[23] ^= D3;
            A[4] ^= D4; A[9] ^= D4; A[14] ^= D4; A[19] ^= D4; A[24] ^= D4;

            // ρ (rho) and π (pi) combined
            ulong B00 = A[0];
            ulong B01 = ROL(A[6], 44);
            ulong B02 = ROL(A[12], 43);
            ulong B03 = ROL(A[18], 21);
            ulong B04 = ROL(A[24], 14);
            ulong B05 = ROL(A[3], 28);
            ulong B06 = ROL(A[9], 20);
            ulong B07 = ROL(A[10], 3);
            ulong B08 = ROL(A[16], 45);
            ulong B09 = ROL(A[22], 61);
            ulong B10 = ROL(A[1], 1);
            ulong B11 = ROL(A[7], 6);
            ulong B12 = ROL(A[13], 25);
            ulong B13 = ROL(A[19], 8);
            ulong B14 = ROL(A[20], 18);
            ulong B15 = ROL(A[4], 27);
            ulong B16 = ROL(A[5], 36);
            ulong B17 = ROL(A[11], 10);
            ulong B18 = ROL(A[17], 15);
            ulong B19 = ROL(A[23], 56);
            ulong B20 = ROL(A[2], 62);
            ulong B21 = ROL(A[8], 55);
            ulong B22 = ROL(A[14], 39);
            ulong B23 = ROL(A[15], 41);
            ulong B24 = ROL(A[21], 2);

            // χ (chi)
            A[0] = B00 ^ (~B01 & B02);
            A[1] = B01 ^ (~B02 & B03);
            A[2] = B02 ^ (~B03 & B04);
            A[3] = B03 ^ (~B04 & B00);
            A[4] = B04 ^ (~B00 & B01);

            A[5] = B05 ^ (~B06 & B07);
            A[6] = B06 ^ (~B07 & B08);
            A[7] = B07 ^ (~B08 & B09);
            A[8] = B08 ^ (~B09 & B05);
            A[9] = B09 ^ (~B05 & B06);

            A[10] = B10 ^ (~B11 & B12);
            A[11] = B11 ^ (~B12 & B13);
            A[12] = B12 ^ (~B13 & B14);
            A[13] = B13 ^ (~B14 & B10);
            A[14] = B14 ^ (~B10 & B11);

            A[15] = B15 ^ (~B16 & B17);
            A[16] = B16 ^ (~B17 & B18);
            A[17] = B17 ^ (~B18 & B19);
            A[18] = B18 ^ (~B19 & B15);
            A[19] = B19 ^ (~B15 & B16);

            A[20] = B20 ^ (~B21 & B22);
            A[21] = B21 ^ (~B22 & B23);
            A[22] = B22 ^ (~B23 & B24);
            A[23] = B23 ^ (~B24 & B20);
            A[24] = B24 ^ (~B20 & B21);

            // ι (iota)
            A[0] ^= RC[round];
        }
    }

    private static ulong ROL(ulong x, int n) => (x << n) | (x >> (64 - n));

    /// <summary>
    /// right_encode for K12 - compatible with noble/hashes.
    /// For n=0, returns [0] (single byte).
    /// For n>0, returns bytes of n (big-endian) followed by length byte.
    /// </summary>
    private static int RightEncodeK12(Span<byte> buffer, ulong value)
    {
        if (value == 0)
        {
            // noble/hashes returns [0] for zero
            buffer[0] = 0x00;
            return 1;
        }

        int n = 0;
        ulong v = value;
        while (v > 0)
        {
            n++;
            v >>= 8;
        }

        v = value;
        for (int i = n - 1; i >= 0; i--)
        {
            buffer[i] = (byte)(v & 0xFF);
            v >>= 8;
        }
        buffer[n] = (byte)n;
        return n + 1;
    }
}

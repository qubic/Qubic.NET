using System;

namespace Qubic.Crypto;

/// <summary>
/// Interface for Qubic cryptographic operations.
/// </summary>
public interface IQubicCrypt
{
    /// <summary>
    /// Computes KangarooTwelve hash of input data.
    /// </summary>
    byte[] KangarooTwelve(byte[] inputData, int outputByteLen, int? fixedInputDataLength = null);

    /// <summary>
    /// Computes KangarooTwelve hash with 32-byte output.
    /// </summary>
    byte[] KangarooTwelve(byte[] inputData, int? fixedInputDataLength = null);

    /// <summary>
    /// Computes KangarooTwelve hash of 64 bytes to 32 bytes output.
    /// </summary>
    byte[] KangarooTwelve64To32(byte[] inputData);

    /// <summary>
    /// Computes ECDH shared key using seed and peer's public key.
    /// </summary>
    byte[] GetSharedKey(string seed, byte[] publicKey);

    /// <summary>
    /// Computes ECDH shared key using private key and peer's public key.
    /// </summary>
    byte[] GetSharedKey(byte[] privateKey, byte[] publicKey);

    /// <summary>
    /// Gets the 32-byte private key from a 55-character seed.
    /// </summary>
    byte[] GetPrivateKey(string seed);

    /// <summary>
    /// Gets the 32-byte public key from a 55-character seed.
    /// </summary>
    byte[] GetPublicKey(string seed);

    /// <summary>
    /// Converts a 32-byte public key to a 60-character Qubic identity.
    /// </summary>
    string GetIdentityFromPublicKey(byte[] publicKey);

    /// <summary>
    /// Converts 32-byte data to human-readable lowercase identity format (60 chars).
    /// </summary>
    string GetHumanReadableBytes(byte[] data);

    /// <summary>
    /// Signs a message using the seed. Returns message with signature appended.
    /// Uses the qubic protocol convention (K12 digest in nonce/challenge inputs).
    /// </summary>
    byte[] Sign(string seed, byte[] message);

    /// <summary>
    /// Signs a raw message using the FourQ SchnorrQ convention.
    /// Raw message bytes are used directly in nonce/challenge K12 inputs.
    /// Compatible with the Qubic wallet JS tool's sign/verify feature.
    /// Returns only the 64-byte signature.
    /// </summary>
    byte[] SignRaw(string seed, byte[] message);

    /// <summary>
    /// Verifies a message where signature is the last 64 bytes.
    /// </summary>
    bool Verify(byte[] publicKey, byte[] message);

    /// <summary>
    /// Verifies a message with separate signature.
    /// </summary>
    bool Verify(byte[] publicKey, byte[] message, byte[] signature);

    /// <summary>
    /// Verifies a raw message with separate signature using the FourQ SchnorrQ convention.
    /// Raw message bytes are used directly in the challenge K12 input.
    /// Compatible with the Qubic wallet JS tool's sign/verify feature.
    /// </summary>
    bool VerifyRaw(byte[] publicKey, byte[] message, byte[] signature);

    /// <summary>
    /// Verifies that a 60-character identity string has a valid checksum.
    /// The last 4 characters encode an 18-bit K12 hash of the public key derived from the first 56 characters.
    /// </summary>
    bool VerifyIdentityChecksum(string identity)
    {
        if (string.IsNullOrEmpty(identity) || identity.Length != 60)
            return false;

        try
        {
            var pubKey = GetPublicKeyFromIdentity(identity);
            var regenerated = GetIdentityFromPublicKey(pubKey);
            return string.Equals(identity, regenerated, StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Encodes bytes to shifted hex (uppercase A-P, where A=0, B=1, ..., P=15).
    /// Each byte becomes 2 characters. Compatible with the Qubic ecosystem encoding.
    /// </summary>
    string BytesToShiftedHex(byte[] data)
    {
        if (data == null) throw new ArgumentNullException(nameof(data));
        var chars = new char[data.Length * 2];
        for (int i = 0; i < data.Length; i++)
        {
            chars[i * 2] = (char)('A' + (data[i] >> 4));
            chars[i * 2 + 1] = (char)('A' + (data[i] & 0x0F));
        }
        return new string(chars);
    }

    /// <summary>
    /// Decodes a shifted hex string (A-P per nibble) back to bytes.
    /// Case-insensitive. If the string has odd length, 'a'/'A' (=0) is prepended.
    /// </summary>
    byte[] ShiftedHexToBytes(string hex)
    {
        if (hex == null) throw new ArgumentNullException(nameof(hex));
        var lower = hex.ToLowerInvariant();
        if (lower.Length % 2 != 0)
            lower = "a" + lower;
        var bytes = new byte[lower.Length / 2];
        for (int i = 0; i < bytes.Length; i++)
        {
            int hi = lower[i * 2] - 'a';
            int lo = lower[i * 2 + 1] - 'a';
            if (hi < 0 || hi > 15 || lo < 0 || lo > 15)
                throw new ArgumentException($"Invalid shifted hex character at position {i * 2}");
            bytes[i] = (byte)((hi << 4) | lo);
        }
        return bytes;
    }

    /// <summary>
    /// Converts human-readable string (e.g., txid) to digest bytes.
    /// </summary>
    byte[] GetDigestFromHumanReadableString(string humanReadableString)
    {
        if (string.IsNullOrEmpty(humanReadableString))
            throw new ArgumentException("humanReadableString must not be null");
        return GetPublicKeyFromIdentity(humanReadableString.ToUpper());
    }

    /// <summary>
    /// Converts a 60-character identity to 32-byte public key.
    /// Last 4 characters are checksum (ignored).
    /// </summary>
    byte[] GetPublicKeyFromIdentity(string identity)
    {
        if (identity == null) throw new ArgumentException("Identity must not be null");
        if (identity.Length < 56) throw new ArgumentException("Identity must be 56-60 chars");
        if (identity.Length > 60) throw new ArgumentException("Identity must be 56-60 chars");

        byte[] buffer = new byte[32];

        for (int i = 0; i < 4; i++)
        {
            ulong value = 0;

            for (int j = 13; j >= 0; j--)
            {
                char c = identity[i * 14 + j];
                if (c < 'A' || c > 'Z')
                    throw new ArgumentException("Invalid Identity [A-Z]");

                value = value * 26UL + (ulong)(c - 'A');
            }

            int offset = i * 8;
            buffer[offset + 0] = (byte)(value >> 0);
            buffer[offset + 1] = (byte)(value >> 8);
            buffer[offset + 2] = (byte)(value >> 16);
            buffer[offset + 3] = (byte)(value >> 24);
            buffer[offset + 4] = (byte)(value >> 32);
            buffer[offset + 5] = (byte)(value >> 40);
            buffer[offset + 6] = (byte)(value >> 48);
            buffer[offset + 7] = (byte)(value >> 56);
        }

        return buffer;
    }
}

using System;
using System.Linq;
using Qubic.Crypto.FourQ;

namespace Qubic.Crypto;

/// <summary>
/// Qubic cryptography implementation providing K12 hashing, SchnorrQ signatures,
/// and FourQ-based key operations.
/// </summary>
public class QubicCrypt : IQubicCrypt
{
    /// <summary>
    /// Computes KangarooTwelve hash of input data.
    /// </summary>
    public byte[] KangarooTwelve(byte[] inputData, int outputByteLen, int? fixedInputDataLength = null)
    {
        if (inputData == null)
            throw new ArgumentNullException(nameof(inputData));

        var input = fixedInputDataLength.HasValue
            ? inputData.AsSpan(0, Math.Min(fixedInputDataLength.Value, inputData.Length))
            : inputData.AsSpan();

        return K12.Hash(input, outputByteLen);
    }

    /// <summary>
    /// Computes KangarooTwelve hash with 32-byte output.
    /// </summary>
    public byte[] KangarooTwelve(byte[] inputData, int? fixedInputDataLength = null)
    {
        return KangarooTwelve(inputData, 32, fixedInputDataLength);
    }

    /// <summary>
    /// Computes KangarooTwelve hash of 64 bytes to 32 bytes output.
    /// </summary>
    public byte[] KangarooTwelve64To32(byte[] inputData)
    {
        if (inputData == null)
            throw new ArgumentNullException(nameof(inputData));

        return K12.Hash(inputData.AsSpan(0, Math.Min(64, inputData.Length)), 32);
    }

    /// <summary>
    /// Computes ECDH shared key using seed and peer's public key.
    /// SharedKey = K12(privateScalar * peerPublicKey)
    /// </summary>
    public byte[] GetSharedKey(string seed, byte[] publicKey)
    {
        var privateKey = GetPrivateKey(seed);
        return GetSharedKey(privateKey, publicKey);
    }

    /// <summary>
    /// Computes ECDH shared key using private key and peer's public key.
    /// SharedKey = K12(privateScalar * peerPublicKey)
    /// </summary>
    public byte[] GetSharedKey(byte[] privateKey, byte[] publicKey)
    {
        if (privateKey == null || privateKey.Length != 32)
            throw new ArgumentException("Private key must be 32 bytes", nameof(privateKey));
        if (publicKey == null || publicKey.Length != 32)
            throw new ArgumentException("Public key must be 32 bytes", nameof(publicKey));

        // Decode peer's public key
        var peerPoint = FourQCodec.Decode(publicKey);
        if (peerPoint == null)
            throw new ArgumentException("Invalid public key encoding", nameof(publicKey));

        // Convert private key bytes to scalar
        var scalar = ScalarField.FromBytes32LE(privateKey);

        // Compute shared point: scalar * peerPublicKey
        var sharedPoint = FourQPoint.ScalarMul(peerPoint.Value, scalar);

        // Encode the shared point and hash it
        var sharedPointEncoded = FourQCodec.Encode(sharedPoint);
        return K12.Hash(sharedPointEncoded, 32);
    }

    /// <summary>
    /// Gets the 32-byte private key from a 55-character seed.
    /// </summary>
    public byte[] GetPrivateKey(string seed)
    {
        var subSeed = SchnorrQ.GetSubSeedFromSeed(seed);
        return SchnorrQ.GeneratePrivateKey(subSeed);
    }

    /// <summary>
    /// Gets the 32-byte public key from a 55-character seed.
    /// </summary>
    public byte[] GetPublicKey(string seed)
    {
        var subSeed = SchnorrQ.GetSubSeedFromSeed(seed);
        return SchnorrQ.GeneratePublicKey(subSeed);
    }

    /// <summary>
    /// Converts a 32-byte public key to a 60-character Qubic identity.
    /// </summary>
    public string GetIdentityFromPublicKey(byte[] publicKey)
    {
        if (publicKey == null || publicKey.Length != 32)
            throw new ArgumentException("Public key must be 32 bytes", nameof(publicKey));

        var identity = new char[60];

        // Convert each 8-byte chunk to 14 base-26 characters
        for (int i = 0; i < 4; i++)
        {
            ulong publicKeyFragment = BitConverter.ToUInt64(publicKey, i * 8);
            for (int j = 0; j < 14; j++)
            {
                identity[i * 14 + j] = (char)((publicKeyFragment % 26) + 'A');
                publicKeyFragment /= 26;
            }
        }

        // Compute checksum: K12 hash of public key, take 3 bytes (18 bits used)
        var checksumBytes = K12.Hash(publicKey, 3);
        uint checksum = (uint)(checksumBytes[0] | (checksumBytes[1] << 8) | (checksumBytes[2] << 16));
        checksum &= 0x3FFFF; // 18 bits

        // Convert checksum to 4 base-26 characters
        for (int i = 0; i < 4; i++)
        {
            identity[56 + i] = (char)((checksum % 26) + 'A');
            checksum /= 26;
        }

        return new string(identity);
    }

    /// <summary>
    /// Converts 32-byte data to human-readable lowercase identity format (60 chars).
    /// Same encoding as <see cref="GetIdentityFromPublicKey"/> but lowercase.
    /// </summary>
    public string GetHumanReadableBytes(byte[] data)
    {
        if (data == null || data.Length != 32)
            throw new ArgumentException("Data must be 32 bytes", nameof(data));

        return GetIdentityFromPublicKey(data).ToLowerInvariant();
    }

    /// <summary>
    /// Signs a message using the seed. Returns signature appended to message.
    /// Uses the qubic protocol convention (K12 digest used in nonce/challenge inputs).
    /// </summary>
    public byte[] Sign(string seed, byte[] message)
    {
        if (message == null)
            throw new ArgumentNullException(nameof(message));

        var subSeed = SchnorrQ.GetSubSeedFromSeed(seed);
        var publicKey = SchnorrQ.GeneratePublicKey(subSeed);
        var signature = SchnorrQ.SignMessage(subSeed, publicKey, message);

        // Return message with signature appended
        var result = new byte[message.Length + 64];
        message.CopyTo(result, 0);
        signature.CopyTo(result, message.Length);
        return result;
    }

    /// <summary>
    /// Signs a raw message using the FourQ SchnorrQ convention.
    /// The raw message bytes are used directly in nonce/challenge K12 inputs.
    /// Compatible with the Qubic wallet JS tool's sign/verify feature.
    /// Returns only the 64-byte signature (not appended to message).
    /// </summary>
    public byte[] SignRaw(string seed, byte[] message)
    {
        if (message == null)
            throw new ArgumentNullException(nameof(message));

        var subSeed = SchnorrQ.GetSubSeedFromSeed(seed);
        var publicKey = SchnorrQ.GeneratePublicKey(subSeed);
        return SchnorrQ.SignRaw(subSeed, publicKey, message);
    }

    /// <summary>
    /// Verifies a message where signature is the last 64 bytes.
    /// Uses the qubic protocol convention (K12 digest used in challenge).
    /// </summary>
    public bool Verify(byte[] publicKey, byte[] message)
    {
        if (publicKey == null || publicKey.Length != 32)
            return false;
        if (message == null || message.Length < 64)
            return false;

        var messageContent = message.AsSpan(0, message.Length - 64);
        var signature = message.AsSpan(message.Length - 64, 64);

        var digest = K12.Hash(messageContent, 32);
        return SchnorrQ.Verify(publicKey, digest, signature);
    }

    /// <summary>
    /// Verifies a message with separate signature.
    /// Uses the qubic protocol convention (K12 digest used in challenge).
    /// </summary>
    public bool Verify(byte[] publicKey, byte[] message, byte[] signature)
    {
        if (publicKey == null || publicKey.Length != 32)
            return false;
        if (message == null)
            return false;
        if (signature == null || signature.Length != 64)
            return false;

        var digest = K12.Hash(message, 32);
        return SchnorrQ.Verify(publicKey, digest, signature);
    }

    /// <summary>
    /// Verifies a raw message with separate signature using the FourQ SchnorrQ convention.
    /// The raw message bytes are used directly in the challenge K12 input.
    /// Compatible with the Qubic wallet JS tool's sign/verify feature.
    /// </summary>
    public bool VerifyRaw(byte[] publicKey, byte[] message, byte[] signature)
    {
        if (publicKey == null || publicKey.Length != 32)
            return false;
        if (message == null)
            return false;
        if (signature == null || signature.Length < 64)
            return false;

        return SchnorrQ.VerifyRaw(publicKey, message, signature);
    }
}

using System;
using System.Numerics;
using Qubic.Crypto.FourQ;

namespace Qubic.Crypto;

/// <summary>
/// SchnorrQ digital signature scheme using the FourQ elliptic curve and K12 hashing.
/// Compatible with Qubic protocol signatures.
/// </summary>
public static class SchnorrQ
{
    /// <summary>
    /// Generates a subSeed from a 55-character lowercase seed (a-z).
    /// The seed is converted to bytes (each char minus 'a') and hashed with K12.
    /// </summary>
    /// <param name="seed">55-character lowercase seed string (a-z only)</param>
    /// <returns>32-byte subSeed</returns>
    /// <exception cref="ArgumentException">If seed is invalid</exception>
    public static byte[] GetSubSeedFromSeed(string seed)
    {
        if (seed == null || seed.Length != 55)
            throw new ArgumentException("Seed must be exactly 55 characters", nameof(seed));

        var seedBytes = new byte[55];
        for (int i = 0; i < 55; i++)
        {
            char c = seed[i];
            if (c < 'a' || c > 'z')
                throw new ArgumentException("Seed must contain only lowercase letters a-z", nameof(seed));
            seedBytes[i] = (byte)(c - 'a');
        }

        return K12.Hash(seedBytes, 32);
    }

    /// <summary>
    /// Tries to generate a subSeed from a 55-character lowercase seed (a-z).
    /// </summary>
    /// <param name="seed">55-character lowercase seed string (a-z only)</param>
    /// <param name="subSeed">32-byte subSeed output</param>
    /// <returns>true if successful, false if seed is invalid</returns>
    public static bool TryGetSubSeedFromSeed(string seed, out byte[] subSeed)
    {
        subSeed = null!;

        if (seed == null || seed.Length != 55)
            return false;

        var seedBytes = new byte[55];
        for (int i = 0; i < 55; i++)
        {
            char c = seed[i];
            if (c < 'a' || c > 'z')
                return false;
            seedBytes[i] = (byte)(c - 'a');
        }

        subSeed = K12.Hash(seedBytes, 32);
        return true;
    }

    /// <summary>
    /// Generates a private key (scalar) from a 32-byte seed (subSeed in Qubic terminology).
    /// The private key is the first 32 bytes of K12(subSeed, 64) reduced mod curve order.
    /// </summary>
    /// <param name="subSeed">32-byte seed/secret</param>
    /// <returns>32-byte private key (scalar in little-endian)</returns>
    public static byte[] GeneratePrivateKey(ReadOnlySpan<byte> subSeed)
    {
        if (subSeed.Length != 32)
            throw new ArgumentException("SubSeed must be exactly 32 bytes", nameof(subSeed));

        // Hash the seed with K12 to get 64 bytes
        var hash = K12.Hash(subSeed, 64);

        // Take first 32 bytes and reduce mod curve order to get scalar
        var scalar = ScalarField.FromBytes32LE(hash.AsSpan(0, 32));

        // Return as 32 bytes
        return ScalarField.ToBytes32LE(scalar);
    }

    /// <summary>
    /// Generates a public key from a 32-byte seed (subSeed in Qubic terminology).
    /// </summary>
    /// <param name="subSeed">32-byte seed/secret</param>
    /// <returns>32-byte encoded public key</returns>
    public static byte[] GeneratePublicKey(ReadOnlySpan<byte> subSeed)
    {
        if (subSeed.Length != 32)
            throw new ArgumentException("SubSeed must be exactly 32 bytes", nameof(subSeed));

        // Hash the seed with K12 to get 64 bytes
        var hash = K12.Hash(subSeed, 64);

        // Take first 32 bytes and reduce mod curve order to get scalar
        var scalar = ScalarField.FromBytes32LE(hash.AsSpan(0, 32));

        // Compute public key: P = scalar * G
        var publicPoint = FourQPoint.ScalarMul(FourQPoint.BasePoint, scalar);

        // Encode the public key
        return FourQCodec.Encode(publicPoint);
    }

    /// <summary>
    /// Signs a message digest using SchnorrQ.
    ///
    /// WARNING: This is a pure managed implementation and is NOT side-channel hardened.
    /// Do not use in environments where timing attacks are a concern.
    /// </summary>
    /// <param name="subSeed">32-byte seed/secret key</param>
    /// <param name="publicKey">32-byte public key (for verification in signature)</param>
    /// <param name="messageDigest">32-byte message digest (typically K12 hash of message)</param>
    /// <returns>64-byte signature (R || s)</returns>
    public static byte[] Sign(ReadOnlySpan<byte> subSeed, ReadOnlySpan<byte> publicKey, ReadOnlySpan<byte> messageDigest)
    {
        if (subSeed.Length != 32)
            throw new ArgumentException("SubSeed must be exactly 32 bytes", nameof(subSeed));
        if (publicKey.Length != 32)
            throw new ArgumentException("Public key must be exactly 32 bytes", nameof(publicKey));
        if (messageDigest.Length != 32)
            throw new ArgumentException("Message digest must be exactly 32 bytes", nameof(messageDigest));

        // Derive private scalar from seed
        var seedHash = K12.Hash(subSeed, 64);
        var privateScalar = ScalarField.FromBytes32LE(seedHash.AsSpan(0, 32));

        // Generate deterministic nonce: k = K12(seedHash[32:64] || messageDigest) mod N
        Span<byte> nonceInput = stackalloc byte[64];
        seedHash.AsSpan(32, 32).CopyTo(nonceInput);
        messageDigest.CopyTo(nonceInput.Slice(32));

        var nonceHash = K12.Hash(nonceInput, 64);
        var k = ScalarField.FromBytes32LE(nonceHash.AsSpan(0, 32));

        // Compute R = k * G
        var R = FourQPoint.ScalarMul(FourQPoint.BasePoint, k);
        var encodedR = FourQCodec.Encode(R);

        // Compute challenge: h = K12(R || publicKey || messageDigest) mod N
        Span<byte> challengeInput = stackalloc byte[96];
        encodedR.AsSpan().CopyTo(challengeInput);
        publicKey.CopyTo(challengeInput.Slice(32));
        messageDigest.CopyTo(challengeInput.Slice(64));

        var challengeHash = K12.Hash(challengeInput, 64);
        var h = ScalarField.FromBytes32LE(challengeHash.AsSpan(0, 32));

        // Compute s = k - h * privateScalar mod N
        var s = ScalarField.Sub(k, ScalarField.Mul(h, privateScalar));

        // Signature is R || s (64 bytes)
        var signature = new byte[64];
        encodedR.AsSpan().CopyTo(signature);
        ScalarField.ToBytes32LE(s, signature.AsSpan(32));

        return signature;
    }

    /// <summary>
    /// Verifies a SchnorrQ signature.
    /// </summary>
    /// <param name="publicKey">32-byte encoded public key</param>
    /// <param name="messageDigest">32-byte message digest</param>
    /// <param name="signature">64-byte signature</param>
    /// <returns>true if the signature is valid, false otherwise</returns>
    public static bool Verify(ReadOnlySpan<byte> publicKey, ReadOnlySpan<byte> messageDigest, ReadOnlySpan<byte> signature)
    {
        if (publicKey.Length != 32)
            return false;
        if (messageDigest.Length != 32)
            return false;
        if (signature.Length != 64)
            return false;

        // Validate encodings
        if (!FourQCodec.IsValidPublicKeyEncoding(publicKey))
            return false;
        if (!FourQCodec.IsValidSignatureEncoding(signature))
            return false;

        // Decode public key
        var P = FourQCodec.Decode(publicKey);
        if (P == null)
            return false;

        // Decode R from signature
        var R = FourQCodec.Decode(signature.Slice(0, 32));
        if (R == null)
            return false;

        // Extract s from signature
        var s = ScalarField.FromBytes32LE(signature.Slice(32, 32));

        // Recompute challenge: h = K12(R || publicKey || messageDigest) mod N
        Span<byte> challengeInput = stackalloc byte[96];
        signature.Slice(0, 32).CopyTo(challengeInput);
        publicKey.CopyTo(challengeInput.Slice(32));
        messageDigest.CopyTo(challengeInput.Slice(64));

        var challengeHash = K12.Hash(challengeInput, 64);
        var h = ScalarField.FromBytes32LE(challengeHash.AsSpan(0, 32));

        // Verify: s * G + h * P == R
        var sG = FourQPoint.ScalarMul(FourQPoint.BasePoint, s);
        var hP = FourQPoint.ScalarMul(P.Value, h);
        var computed = FourQPoint.Add(sG, hP);

        return computed == R.Value;
    }

    /// <summary>
    /// Computes the K12 digest of a message.
    /// Convenience method for the common pattern of hashing before signing.
    /// </summary>
    /// <param name="message">Message to hash</param>
    /// <returns>32-byte digest</returns>
    public static byte[] Digest(ReadOnlySpan<byte> message)
    {
        return K12.Hash(message, 32);
    }

    /// <summary>
    /// Full sign operation: computes K12 digest, then signs the digest.
    /// Uses the qubic protocol convention where the 32-byte digest is used
    /// directly in the nonce and challenge K12 inputs.
    /// Suitable for signing fixed-size data (e.g., transaction digests).
    /// </summary>
    public static byte[] SignMessage(ReadOnlySpan<byte> subSeed, ReadOnlySpan<byte> publicKey, ReadOnlySpan<byte> message)
    {
        var digest = Digest(message);
        return Sign(subSeed, publicKey, digest);
    }

    /// <summary>
    /// Full verify operation: computes K12 digest, then verifies against the digest.
    /// Uses the qubic protocol convention. Suitable for verifying transaction signatures.
    /// </summary>
    public static bool VerifyMessage(ReadOnlySpan<byte> publicKey, ReadOnlySpan<byte> message, ReadOnlySpan<byte> signature)
    {
        var digest = Digest(message);
        return Verify(publicKey, digest, signature);
    }

    /// <summary>
    /// Signs an arbitrary-length message using the FourQ SchnorrQ convention.
    /// The raw message bytes are used directly in the K12 inputs for nonce and
    /// challenge computation (not pre-hashed to a 32-byte digest).
    /// Compatible with the FourQ library's SchnorrQ_Sign and the Qubic wallet JS tool.
    /// </summary>
    public static byte[] SignRaw(ReadOnlySpan<byte> subSeed, ReadOnlySpan<byte> publicKey, ReadOnlySpan<byte> message)
    {
        if (subSeed.Length != 32)
            throw new ArgumentException("SubSeed must be exactly 32 bytes", nameof(subSeed));
        if (publicKey.Length != 32)
            throw new ArgumentException("Public key must be exactly 32 bytes", nameof(publicKey));

        // Derive private scalar from seed
        var seedHash = K12.Hash(subSeed, 64);
        var privateScalar = ScalarField.FromBytes32LE(seedHash.AsSpan(0, 32));

        // Generate deterministic nonce: k = K12(seedHash[32:64] || message) mod N
        var nonceInput = new byte[32 + message.Length];
        seedHash.AsSpan(32, 32).CopyTo(nonceInput);
        message.CopyTo(nonceInput.AsSpan(32));

        var nonceHash = K12.Hash(nonceInput, 64);
        var k = ScalarField.FromBytes32LE(nonceHash.AsSpan(0, 32));

        // Compute R = k * G
        var R = FourQPoint.ScalarMul(FourQPoint.BasePoint, k);
        var encodedR = FourQCodec.Encode(R);

        // Compute challenge: h = K12(R || publicKey || message) mod N
        var challengeInput = new byte[64 + message.Length];
        encodedR.AsSpan().CopyTo(challengeInput);
        publicKey.CopyTo(challengeInput.AsSpan(32));
        message.CopyTo(challengeInput.AsSpan(64));

        var challengeHash = K12.Hash(challengeInput, 64);
        var h = ScalarField.FromBytes32LE(challengeHash.AsSpan(0, 32));

        // Compute s = k - h * privateScalar mod N
        var s = ScalarField.Sub(k, ScalarField.Mul(h, privateScalar));

        // Signature is R || s (64 bytes)
        var signature = new byte[64];
        encodedR.AsSpan().CopyTo(signature);
        ScalarField.ToBytes32LE(s, signature.AsSpan(32));

        return signature;
    }

    /// <summary>
    /// Verifies a SchnorrQ signature over an arbitrary-length message using the FourQ convention.
    /// The raw message bytes are used directly in the K12 challenge computation.
    /// Compatible with the FourQ library's SchnorrQ_Verify and the Qubic wallet JS tool.
    /// </summary>
    public static bool VerifyRaw(ReadOnlySpan<byte> publicKey, ReadOnlySpan<byte> message, ReadOnlySpan<byte> signature)
    {
        if (publicKey.Length != 32)
            return false;
        if (signature.Length < 64)
            return false;

        // Validate encodings
        if (!FourQCodec.IsValidPublicKeyEncoding(publicKey))
            return false;
        if (!FourQCodec.IsValidSignatureEncoding(signature))
            return false;

        // Decode public key
        var P = FourQCodec.Decode(publicKey);
        if (P == null)
            return false;

        // Decode R from signature
        var R = FourQCodec.Decode(signature.Slice(0, 32));
        if (R == null)
            return false;

        // Extract s from signature
        var s = ScalarField.FromBytes32LE(signature.Slice(32, 32));

        // Recompute challenge: h = K12(R || publicKey || message) mod N
        var challengeInput = new byte[64 + message.Length];
        signature.Slice(0, 32).CopyTo(challengeInput);
        publicKey.CopyTo(challengeInput.AsSpan(32));
        message.CopyTo(challengeInput.AsSpan(64));

        var challengeHash = K12.Hash(challengeInput, 64);
        var h = ScalarField.FromBytes32LE(challengeHash.AsSpan(0, 32));

        // Verify: s * G + h * P == R
        var sG = FourQPoint.ScalarMul(FourQPoint.BasePoint, s);
        var hP = FourQPoint.ScalarMul(P.Value, h);
        var computed = FourQPoint.Add(sG, hP);

        return computed == R.Value;
    }
}

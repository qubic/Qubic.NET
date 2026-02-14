using Qubic.Crypto;

namespace Qubic.Core.Entities;

/// <summary>
/// Represents a Qubic identity (60-character address).
/// </summary>
public readonly struct QubicIdentity : IEquatable<QubicIdentity>
{
    public const int IdentityLength = 60;
    public const int PublicKeyLength = 32;

    private readonly string _identity;
    private readonly byte[]? _publicKey;

    /// <summary>
    /// The 60-character identity string (e.g., "BAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAARMID").
    /// </summary>
    public string Identity => _identity;

    /// <summary>
    /// The 32-byte public key derived from the identity.
    /// </summary>
    public byte[] PublicKey => _publicKey ?? GetPublicKeyFromIdentity();

    private QubicIdentity(string identity, byte[]? publicKey = null)
    {
        _identity = identity;
        _publicKey = publicKey;
    }

    /// <summary>
    /// Creates a QubicIdentity from a 60-character identity string.
    /// Validates length, character set, and checksum.
    /// </summary>
    public static QubicIdentity FromIdentity(string identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (identity.Length != IdentityLength)
            throw new ArgumentException($"Identity must be {IdentityLength} characters.", nameof(identity));

        var upper = identity.ToUpperInvariant();

        if (!VerifyChecksum(upper))
            throw new ArgumentException("Identity has an invalid checksum.", nameof(identity));

        return new QubicIdentity(upper);
    }

    /// <summary>
    /// Creates a QubicIdentity from a 32-byte public key.
    /// </summary>
    public static QubicIdentity FromPublicKey(byte[] publicKey)
    {
        ArgumentNullException.ThrowIfNull(publicKey);
        if (publicKey.Length != PublicKeyLength)
            throw new ArgumentException($"Public key must be {PublicKeyLength} bytes.", nameof(publicKey));

        var crypt = new QubicCrypt();
        var identity = crypt.GetIdentityFromPublicKey(publicKey);
        return new QubicIdentity(identity, publicKey);
    }

    /// <summary>
    /// Creates a QubicIdentity from a 55-character seed.
    /// </summary>
    public static QubicIdentity FromSeed(string seed)
    {
        ArgumentNullException.ThrowIfNull(seed);
        if (seed.Length != 55)
            throw new ArgumentException("Seed must be 55 characters.", nameof(seed));

        var crypt = new QubicCrypt();
        var publicKey = crypt.GetPublicKey(seed);
        var identity = crypt.GetIdentityFromPublicKey(publicKey);
        return new QubicIdentity(identity, publicKey);
    }

    /// <summary>
    /// Tries to parse an identity string. Validates length, character set, and checksum.
    /// </summary>
    public static bool TryParse(string? identity, out QubicIdentity result)
    {
        if (identity is null || identity.Length != IdentityLength)
        {
            result = default;
            return false;
        }

        var upper = identity.ToUpperInvariant();
        if (!VerifyChecksum(upper))
        {
            result = default;
            return false;
        }

        result = new QubicIdentity(upper);
        return true;
    }

    /// <summary>
    /// Verifies that the last 4 characters (checksum) of a 60-character identity are valid.
    /// </summary>
    private static bool VerifyChecksum(string identity)
    {
        try
        {
            IQubicCrypt crypt = new QubicCrypt();
            var pubKey = crypt.GetPublicKeyFromIdentity(identity);
            var regenerated = crypt.GetIdentityFromPublicKey(pubKey);
            return string.Equals(identity, regenerated, StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    private byte[] GetPublicKeyFromIdentity()
    {
        IQubicCrypt crypt = new QubicCrypt();
        return crypt.GetPublicKeyFromIdentity(_identity);
    }

    public bool Equals(QubicIdentity other) =>
        string.Equals(_identity, other._identity, StringComparison.OrdinalIgnoreCase);

    public override bool Equals(object? obj) => obj is QubicIdentity other && Equals(other);

    public override int GetHashCode() =>
        _identity?.GetHashCode(StringComparison.OrdinalIgnoreCase) ?? 0;

    public override string ToString() => _identity ?? string.Empty;

    public static bool operator ==(QubicIdentity left, QubicIdentity right) => left.Equals(right);
    public static bool operator !=(QubicIdentity left, QubicIdentity right) => !left.Equals(right);

    public static implicit operator string(QubicIdentity identity) => identity.Identity;
}

using System.Security.Cryptography;

namespace Qubic.Core;

/// <summary>
/// The one place that creates and checks Qubic seeds: 55 lowercase letters a–z.
/// Apps must call <see cref="Generate"/> instead of building seeds themselves.
/// </summary>
public static class QubicSeed
{
    /// <summary>The seed alphabet.</summary>
    public const string Alphabet = "abcdefghijklmnopqrstuvwxyz";

    /// <summary>
    /// Creates a new seed from the operating system's cryptographically secure random number generator.
    /// Each character is uniform over the 26 letters (rejection sampling, no modulo bias),
    /// giving 55 · log2(26) ≈ 258.5 bits of entropy.
    /// </summary>
    /// <remarks>
    /// Never use <see cref="System.Random"/> or a time-based generator for seeds: their output is not
    /// guaranteed to be unpredictable.
    /// </remarks>
    public static string Generate() => RandomNumberGenerator.GetString(Alphabet, QubicConstants.SeedLength);

    /// <summary>True if <paramref name="seed"/> is exactly 55 lowercase letters a–z.</summary>
    public static bool IsValid(string? seed)
    {
        if (seed is null || seed.Length != QubicConstants.SeedLength) return false;
        foreach (var c in seed)
            if (c is < 'a' or > 'z') return false;
        return true;
    }
}

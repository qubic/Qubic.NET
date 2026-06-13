using System;
using System.Numerics;

namespace Qubic.Crypto.FourQ;

/// <summary>
/// Point on the FourQ curve in extended twisted Edwards coordinates.
/// Curve equation: -x² + y² = 1 + d·x²·y² over Fp2
/// Extended coordinates: (X : Y : Z : T) where x = X/Z, y = Y/Z, T = XY/Z
/// </summary>
public readonly struct PointExt : IEquatable<PointExt>
{
    public readonly Fp2 X;
    public readonly Fp2 Y;
    public readonly Fp2 Z;
    public readonly Fp2 T;

    public PointExt(Fp2 x, Fp2 y, Fp2 z, Fp2 t)
    {
        X = x;
        Y = y;
        Z = z;
        T = t;
    }

    /// <summary>
    /// The identity element (neutral point): (0, 1, 1, 0)
    /// </summary>
    public static readonly PointExt Identity = new(Fp2.Zero, Fp2.One, Fp2.One, Fp2.Zero);

    /// <summary>
    /// Creates a point from affine coordinates.
    /// </summary>
    public static PointExt FromAffine(Fp2 x, Fp2 y)
    {
        return new PointExt(x, y, Fp2.One, x * y);
    }

    /// <summary>
    /// Converts to affine coordinates (x, y).
    /// </summary>
    public (Fp2 X, Fp2 Y) ToAffine()
    {
        var zInv = Z.Inverse();
        return (X * zInv, Y * zInv);
    }

    /// <summary>
    /// Normalizes the point (Z = 1).
    /// </summary>
    public PointExt Normalize()
    {
        var (x, y) = ToAffine();
        return FromAffine(x, y);
    }

    public bool IsIdentity => X.IsZero && !Y.IsZero && Y == Z && T.IsZero;

    public bool Equals(PointExt other)
    {
        // Compare in projective form: X1*Z2 == X2*Z1 and Y1*Z2 == Y2*Z1
        return X * other.Z == other.X * Z && Y * other.Z == other.Y * Z;
    }

    public override bool Equals(object? obj) => obj is PointExt other && Equals(other);
    public override int GetHashCode()
    {
        var (x, y) = ToAffine();
        return HashCode.Combine(x, y);
    }

    public static bool operator ==(PointExt left, PointExt right) => left.Equals(right);
    public static bool operator !=(PointExt left, PointExt right) => !left.Equals(right);
}

/// <summary>
/// Point operations on the FourQ curve.
/// </summary>
public static class FourQPoint
{
    // Curve parameter d for FourQ (little-endian format from FourQlib)
    // d = { 0x0000000000000142, 0x00000000000000E4, 0xB3821488F1FC0C8D, 0x5E472F846657E0FC }
    // d = d0 + d1*i where d0 and d1 are 128-bit values
    private static readonly Fp2 D = new(
        Fp.FromU64LE(0x0000000000000142UL, 0x00000000000000E4UL),
        Fp.FromU64LE(0xB3821488F1FC0C8DUL, 0x5E472F846657E0FCUL)
    );

    private static readonly Fp2 Two = new(new Fp(2), Fp.Zero);

    // Base point for FourQ (generator) from FourQlib
    // GENERATOR_x = { 0x286592AD7B3833AA, 0x1A3472237C2FB305, 0x96869FB360AC77F6, 0x1E1F553F2878AA9C }
    // GENERATOR_y = { 0xB924A2462BCBB287, 0x0E3FEE9BA120785A, 0x49A7C344844C8B5C, 0x6E1C4AF8630E0242 }
    private static readonly Fp2 BaseX = new(
        Fp.FromU64LE(0x286592AD7B3833AAUL, 0x1A3472237C2FB305UL),
        Fp.FromU64LE(0x96869FB360AC77F6UL, 0x1E1F553F2878AA9CUL)
    );

    private static readonly Fp2 BaseY = new(
        Fp.FromU64LE(0xB924A2462BCBB287UL, 0x0E3FEE9BA120785AUL),
        Fp.FromU64LE(0x49A7C344844C8B5CUL, 0x6E1C4AF8630E0242UL)
    );

    /// <summary>
    /// The base point (generator) of the FourQ curve.
    /// </summary>
    public static readonly PointExt BasePoint = PointExt.FromAffine(BaseX, BaseY);

    /// <summary>
    /// Point addition using extended coordinates for a=-1 twisted Edwards curves.
    /// Formula from EFD: add-2008-hwcd
    /// </summary>
    public static PointExt Add(PointExt p, PointExt q)
    {
        // A = X1 * X2
        var A = p.X * q.X;
        // B = Y1 * Y2
        var B = p.Y * q.Y;
        // C = T1 * d * T2
        var C = p.T * D * q.T;
        // D = Z1 * Z2
        var E = p.Z * q.Z;

        // E = (X1 + Y1) * (X2 + Y2) - A - B
        var F = (p.X + p.Y) * (q.X + q.Y) - A - B;
        // G = D - C
        var G = E - C;
        // H = D + C
        var H = E + C;
        // For a = -1: I = B + A (instead of B - a*A = B + A)
        var I = B + A;

        // X3 = F * G
        var X3 = F * G;
        // Y3 = H * I
        var Y3 = H * I;
        // T3 = F * I
        var T3 = F * I;
        // Z3 = G * H
        var Z3 = G * H;

        return new PointExt(X3, Y3, Z3, T3);
    }

    /// <summary>
    /// Point doubling using extended coordinates.
    /// Formula from EFD: dbl-2008-hwcd
    /// </summary>
    public static PointExt Double(PointExt p)
    {
        // A = X1²
        var A = p.X.Square();
        // B = Y1²
        var B = p.Y.Square();
        // C = 2 * Z1²
        var C = p.Z.Square() * Two;
        // D = a * A = -A (for a = -1)
        var E = -A;
        // E = (X1 + Y1)² - A - B
        var F = (p.X + p.Y).Square() - A - B;
        // G = D + B = B - A
        var G = E + B;
        // H = G - C
        var H = G - C;
        // I = D - B = -A - B
        var I = E - B;

        // X3 = E * H
        var X3 = F * H;
        // Y3 = G * I
        var Y3 = G * I;
        // T3 = E * I
        var T3 = F * I;
        // Z3 = H * G
        var Z3 = H * G;

        return new PointExt(X3, Y3, Z3, T3);
    }

    /// <summary>
    /// Negates a point: -(x, y) = (-x, y) on Edwards curves.
    /// </summary>
    public static PointExt Negate(PointExt p)
    {
        return new PointExt(-p.X, p.Y, p.Z, -p.T);
    }

    /// <summary>
    /// Scalar multiplication using double-and-add.
    /// Iterates over pre-serialized byte array to avoid BigInteger allocations in the loop.
    /// </summary>
    public static PointExt ScalarMul(PointExt p, BigInteger n)
    {
        if (n == 0)
            return PointExt.Identity;

        if (n < 0)
        {
            n = -n;
            p = Negate(p);
        }

        // Convert scalar to bytes once, then iterate over bits directly
        var scalarBytes = n.ToByteArray(isUnsigned: true, isBigEndian: false);

        var result = PointExt.Identity;
        var current = p;

        for (int byteIdx = 0; byteIdx < scalarBytes.Length; byteIdx++)
        {
            byte b = scalarBytes[byteIdx];
            for (int bit = 0; bit < 8; bit++)
            {
                if ((b & (1 << bit)) != 0)
                    result = Add(result, current);
                current = Double(current);
            }
        }

        return result;
    }

    /// <summary>
    /// Verifies that a point lies on the FourQ curve.
    /// Checks: -x² + y² = 1 + d·x²·y²
    /// </summary>
    public static bool IsOnCurve(PointExt p)
    {
        var (x, y) = p.ToAffine();
        var x2 = x.Square();
        var y2 = y.Square();
        var lhs = y2 - x2; // -x² + y² = y² - x²
        var rhs = Fp2.One + D * x2 * y2;
        return lhs == rhs;
    }

    /// <summary>
    /// Returns true if <paramref name="p"/> lies in FourQ's 392-element cofactor subgroup
    /// (i.e. [392]·P == identity). FourQ's group order is 392·r with cofactor 392 = 2³·7²
    /// and prime subgroup order r. A legitimate public key has order r, so [392]·A ≠ O.
    /// Any low-order key — including the identity (QX address <c>{1,0,0,0}</c>), NULL_ID,
    /// and the order-2/4 twins — allows signature forgery without a private key, because
    /// <c>h·A</c> then depends only on <c>h</c> mod ord(A) and is enumerable.
    /// See qubic/core PR #921 ("hotfix fourq library").
    /// </summary>
    public static bool IsLowOrderPoint(PointExt p)
    {
        // ScalarMul handles the projective math; equality is the projective identity check.
        var cofactorMultiple = ScalarMul(p, new BigInteger(392));
        return cofactorMultiple.Equals(PointExt.Identity);
    }

    /// <summary>
    /// Gets the sign bit of the x-coordinate.
    /// Uses bit 126 of the real part (a), or if a is 0, uses bit 126 of the imaginary part (b).
    /// </summary>
    public static int GetXSign(Fp2 x)
    {
        // Sign is bit 126 of x.a, or x.b if x.a == 0
        var component = x.A.RawValue == 0 ? x.B.RawValue : x.A.RawValue;
        return (int)((component >> 126) & 1);
    }
}

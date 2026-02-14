using Qubic.Core.Entities;

namespace Qubic.Core.Tests;

public class QubicIdentityTests
{
    [Fact]
    public void FromIdentity_ValidIdentity_CreatesInstance()
    {
        // The null identity (all zeros)
        var identity = "BAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAARMID";

        var qubicIdentity = QubicIdentity.FromIdentity(identity);

        Assert.Equal(identity, qubicIdentity.Identity);
        Assert.Equal(60, qubicIdentity.Identity.Length);
    }

    [Fact]
    public void FromIdentity_InvalidLength_ThrowsArgumentException()
    {
        var invalidIdentity = "SHORT";

        Assert.Throws<ArgumentException>(() => QubicIdentity.FromIdentity(invalidIdentity));
    }

    [Fact]
    public void FromIdentity_Null_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => QubicIdentity.FromIdentity(null!));
    }

    [Fact]
    public void TryParse_ValidIdentity_ReturnsTrue()
    {
        var identity = "BAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAARMID";

        var result = QubicIdentity.TryParse(identity, out var qubicIdentity);

        Assert.True(result);
        Assert.Equal(identity, qubicIdentity.Identity);
    }

    [Fact]
    public void TryParse_InvalidIdentity_ReturnsFalse()
    {
        var result = QubicIdentity.TryParse("invalid", out _);

        Assert.False(result);
    }

    [Fact]
    public void FromIdentity_InvalidChecksum_ThrowsArgumentException()
    {
        // Valid format but wrong checksum (AAAA instead of RMID)
        Assert.Throws<ArgumentException>(() =>
            QubicIdentity.FromIdentity("BAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"));
    }

    [Fact]
    public void TryParse_InvalidChecksum_ReturnsFalse()
    {
        var result = QubicIdentity.TryParse("BAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", out _);

        Assert.False(result);
    }

    [Fact]
    public void Equality_SameIdentity_AreEqual()
    {
        var identity1 = QubicIdentity.FromIdentity("BAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAARMID");
        var identity2 = QubicIdentity.FromIdentity("BAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAARMID");

        Assert.Equal(identity1, identity2);
        Assert.True(identity1 == identity2);
    }

    [Fact]
    public void ImplicitConversion_ToString_ReturnsIdentity()
    {
        var qubicIdentity = QubicIdentity.FromIdentity("BAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAARMID");

        string identityString = qubicIdentity;

        Assert.Equal("BAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAARMID", identityString);
    }
}

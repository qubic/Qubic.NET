namespace Qubic.Core.Tests;

#pragma warning disable CS0618 // tests intentionally pin obsolete legacy/V2 constants
public class QubicConstantsTests
{
    [Theory]
    [InlineData((ushort)0, QubicConstants.LegacyMaxTransactionsPerTick)]
    [InlineData((ushort)100, QubicConstants.LegacyMaxTransactionsPerTick)]
    [InlineData((ushort)213, QubicConstants.LegacyMaxTransactionsPerTick)]
    [InlineData(QubicConstants.TransactionsPerTickV2Epoch, QubicConstants.MaxTransactionsPerTickV2)]
    [InlineData((ushort)215, QubicConstants.MaxTransactionsPerTickV2)]
    [InlineData(ushort.MaxValue, QubicConstants.MaxTransactionsPerTickV2)]
    public void GetMaxTransactionsPerTick_SwitchesAtForkEpoch(ushort epoch, int expected)
    {
        Assert.Equal(expected, QubicConstants.GetMaxTransactionsPerTick(epoch));
    }

    [Fact]
    public void TransactionsPerTickV2Epoch_IsExpectedValue()
    {
        Assert.Equal((ushort)214, QubicConstants.TransactionsPerTickV2Epoch);
    }

    [Fact]
    public void LegacyMaxTransactionsPerTick_Is1024()
    {
        Assert.Equal(1024, QubicConstants.LegacyMaxTransactionsPerTick);
    }

    [Fact]
    public void MaxTransactionsPerTickV2_Is4096()
    {
        Assert.Equal(4096, QubicConstants.MaxTransactionsPerTickV2);
    }

    [Fact]
    public void MaxTransactionsPerTick_IsUpperBoundForBuffers()
    {
        Assert.Equal(QubicConstants.MaxTransactionsPerTickV2, QubicConstants.MaxTransactionsPerTick);
    }

    [Fact]
    public void LegacyTxRevenuePoints_Has1025Entries()
    {
        Assert.Equal(QubicConstants.LegacyMaxTransactionsPerTick + 1, QubicConstants.LegacyTxRevenuePoints.Length);
    }

    [Fact]
    public void LegacyTxRevenuePoints_FirstIsZero_LastMatchesConstant()
    {
        var span = QubicConstants.LegacyTxRevenuePoints;
        Assert.Equal(0, span[0]);
        Assert.Equal(QubicConstants.LegacyMaxTxRevPoints, span[^1]);
        Assert.Equal(7099, QubicConstants.LegacyMaxTxRevPoints);
    }

    [Fact]
    public void TxRevenuePointsV2_Has4097Entries()
    {
        Assert.Equal(QubicConstants.MaxTransactionsPerTickV2 + 1, QubicConstants.TxRevenuePointsV2.Length);
    }

    [Fact]
    public void TxRevenuePointsV2_FirstIsZero_LastMatchesConstant()
    {
        var span = QubicConstants.TxRevenuePointsV2;
        Assert.Equal(0, span[0]);
        Assert.Equal(QubicConstants.MaxTxRevPointsV2, span[^1]);
        Assert.Equal(34071, QubicConstants.MaxTxRevPointsV2);
    }

    [Fact]
    public void TxRevenuePointsV2_KnownEntriesMatchCppTable()
    {
        // Spot-check values from qubic/core PR #881 src/revenue.h.
        var span = QubicConstants.TxRevenuePointsV2;
        Assert.Equal(2839, span[1]);
        Assert.Equal(4500, span[2]);
        Assert.Equal(5678, span[3]);
        Assert.Equal(9000, span[8]);
    }

    [Fact]
    public void TxRevenuePointsV2_IsStrictlyMonotonic()
    {
        // PR #881 asserts strict monotonicity in C++; mirror that here so any future
        // edit of the table fails fast on a typo or copy/paste error.
        var span = QubicConstants.TxRevenuePointsV2;
        for (int i = 1; i < span.Length; i++)
        {
            Assert.True(span[i] > span[i - 1],
                $"TxRevenuePointsV2 not strictly monotonic at index {i}: {span[i - 1]} → {span[i]}");
        }
    }

    [Fact]
    public void LegacyTxRevenuePoints_IsStrictlyMonotonic()
    {
        var span = QubicConstants.LegacyTxRevenuePoints;
        for (int i = 1; i < span.Length; i++)
        {
            Assert.True(span[i] > span[i - 1],
                $"LegacyTxRevenuePoints not strictly monotonic at index {i}: {span[i - 1]} → {span[i]}");
        }
    }

    [Theory]
    [InlineData((ushort)0, 1025)]
    [InlineData((ushort)213, 1025)]
    [InlineData(QubicConstants.TransactionsPerTickV2Epoch, 4097)]
    [InlineData((ushort)9999, 4097)]
    public void GetTxRevenuePoints_ReturnsCorrectTablePerEpoch(ushort epoch, int expectedLength)
    {
        Assert.Equal(expectedLength, QubicConstants.GetTxRevenuePoints(epoch).Length);
    }

    [Theory]
    [InlineData((ushort)0, 7099)]
    [InlineData((ushort)213, 7099)]
    [InlineData(QubicConstants.TransactionsPerTickV2Epoch, 34071)]
    [InlineData((ushort)500, 34071)]
    public void GetMaxTxRevPoints_SwitchesAtForkEpoch(ushort epoch, int expected)
    {
        Assert.Equal(expected, QubicConstants.GetMaxTxRevPoints(epoch));
    }

    [Fact]
    public void TxRevenuePointsV2_LegacyEndpointMatchesLegacyMax()
    {
        // At index 1024 the V2 table should award more points than the legacy max
        // (the formula changed coefficient from 1024·ln(1+n) to 4096·ln(1+n)).
        // This pins behavior so a regression that accidentally reuses the legacy table is caught.
        Assert.True(QubicConstants.TxRevenuePointsV2[1024] > QubicConstants.LegacyMaxTxRevPoints);
    }
}
#pragma warning restore CS0618

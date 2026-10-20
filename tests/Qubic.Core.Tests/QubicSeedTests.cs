using Qubic.Core.Entities;

namespace Qubic.Core.Tests;

public class QubicSeedTests
{
    [Fact]
    public void Generate_Returns55LowercaseLetters()
    {
        for (var i = 0; i < 1000; i++)
        {
            var seed = QubicSeed.Generate();
            Assert.Equal(QubicConstants.SeedLength, seed.Length);
            Assert.True(QubicSeed.IsValid(seed), seed);
        }
    }

    [Fact]
    public void Generate_ProducesDistinctSeedsThatDeriveIdentities()
    {
        var seeds = Enumerable.Range(0, 200).Select(_ => QubicSeed.Generate()).ToList();
        Assert.Equal(seeds.Count, seeds.Distinct().Count());
        foreach (var seed in seeds.Take(5))
            Assert.Equal(60, QubicIdentity.FromSeed(seed).ToString().Length);
    }

    [Fact]
    public void Generate_IsUniformOverTheAlphabet()
    {
        // 20,000 seeds = 1.1M letters. Chi-square with 25 degrees of freedom: p = 0.001 at 52.6.
        // A byte % 26 generator (the old desktop code) lands near 1,500 at this sample size.
        var counts = new long[26];
        for (var i = 0; i < 20_000; i++)
            foreach (var c in QubicSeed.Generate())
                counts[c - 'a']++;
        double expected = counts.Sum() / 26.0;
        var chi2 = counts.Sum(n => (n - expected) * (n - expected) / expected);
        Assert.True(chi2 < 52.6, $"chi-square {chi2:F1}");
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("abcdefghijklmnopqrstuvwxyzabcdefghijklmnopqrstuvwxyzab", false)]   // 54
    [InlineData("abcdefghijklmnopqrstuvwxyzabcdefghijklmnopqrstuvwxyzabc", true)]   // 55
    [InlineData("abcdefghijklmnopqrstuvwxyzabcdefghijklmnopqrstuvwxyzabcd", false)] // 56
    [InlineData("Abcdefghijklmnopqrstuvwxyzabcdefghijklmnopqrstuvwxyzabc", false)]
    [InlineData("abcdefghijklmnopqrstuvwxyzabcdefghijklmnopqrstuvwxyzab1", false)]
    public void IsValid_ChecksLengthAndAlphabet(string? seed, bool valid) =>
        Assert.Equal(valid, QubicSeed.IsValid(seed));
}

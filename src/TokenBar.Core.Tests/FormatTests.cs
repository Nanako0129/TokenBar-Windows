using Xunit;

namespace TokenBar.Core.Tests;

// Format outputs must match the Swift Format.swift character for character
// (cross-checked by the Phase 3 fixture pass).
public class FormatTests
{
    [Theory]
    [InlineData(0, "0")]
    [InlineData(999, "999")]
    [InlineData(1_000, "1K")]
    [InlineData(12_345, "12.3K")]
    [InlineData(100_000, "100K")]
    [InlineData(999_950, "1000K")] // matches Swift: <1M stays in the K band
    [InlineData(1_234_567, "1.2M")]
    [InlineData(1_000_000_000, "1B")]
    [InlineData(1_500_000_000, "1.5B")]
    public void CompactTokensMatchesSwift(long count, string expected) =>
        Assert.Equal(expected, Format.CompactTokens(count));

    [Theory]
    [InlineData(0.0, "$0.00")]
    [InlineData(5.205, "$5.21")]
    [InlineData(4845.174, "$4845.17")]
    public void UsdMatchesSwift(double amount, string expected) =>
        Assert.Equal(expected, Format.Usd(amount));

    [Theory]
    [InlineData("2026-06-10", "Jun 10")]
    [InlineData("2026-12-01", "Dec 1")]
    [InlineData("garbage", "garbage")]
    [InlineData("2026-13-01", "2026-13-01")]
    public void MonthDay(string iso, string expected) =>
        Assert.Equal(expected, Format.MonthDay(iso));

    [Fact]
    public void MmddSplits() => Assert.Equal("06/10", Format.Mmdd("2026-06-10"));

    [Fact]
    public void ExactTokensGroups() => Assert.Equal("1,234,567", Format.ExactTokens(1_234_567));

    [Theory]
    [InlineData(0, "just now")]
    [InlineData(59, "just now")]
    [InlineData(300, "5m ago")]
    [InlineData(10_800, "3h ago")]
    [InlineData(172_800, "2d ago")]
    public void RelativeTime(long ageSecs, string expected)
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1_750_000_000);
        var epoch = (ulong)(1_750_000_000 - ageSecs);
        Assert.Equal(expected, Format.RelativeTime(epoch, now));
    }
}

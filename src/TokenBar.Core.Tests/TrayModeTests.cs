using TokenBar.Interop;
using Xunit;

namespace TokenBar.Core.Tests;

public class TrayModeTests
{
    [Fact]
    public void ParseRoundTripsEveryMode()
    {
        foreach (var mode in TrayModes.All)
        {
            Assert.Equal(mode, TrayModes.Parse(mode.RawValue()));
        }
    }

    [Fact]
    public void ParseFallsBackToTodayTokens()
    {
        Assert.Equal(TrayMode.TodayTokens, TrayModes.Parse(null));
        Assert.Equal(TrayMode.TodayTokens, TrayModes.Parse(""));
        Assert.Equal(TrayMode.TodayTokens, TrayModes.Parse("bogus"));
    }

    [Fact]
    public void QuotaLeftFormatsAndClamps()
    {
        Assert.Equal("57%", TrayMode.QuotaLeft.Title(null, null, 57.4));
        Assert.Equal("58%", TrayMode.QuotaLeft.Title(null, null, 57.5)); // away-from-zero
        Assert.Equal("100%", TrayMode.QuotaLeft.Title(null, null, 150));
        Assert.Equal("0%", TrayMode.QuotaLeft.Title(null, null, -3));
        Assert.Equal("—%", TrayMode.QuotaLeft.Title(null, null, null));
    }

    [Fact]
    public void HiddenAndMissingGraphAreEmpty()
    {
        Assert.Equal("", TrayMode.Hidden.Title(null, 5000, 50));
        Assert.Equal("", TrayMode.TodayTokens.Title(null, 5000));
        Assert.Equal("", TrayMode.TotalCost.Title(null, null));
    }

    [Theory]
    [InlineData("12.3K", "12K")] // ≥2 integer digits: decimal goes
    [InlineData("12.4K/m", "12K/m")]
    [InlineData("999.9K/m", "999K/m")]
    [InlineData("1.5B", "1.5B")] // one digit: the fraction is the value
    [InlineData("57%", "57%")] // no decimal: untouched
    [InlineData("—/m", "—/m")]
    public void IconTitleDropsTheDecimalRunPastTwoDigits(string title, string expected)
    {
        Assert.Equal(expected, TrayModes.IconTitle(title));
    }

    [Theory]
    [InlineData("$889.13", "$889")] // ≥$100: compact, cents gone
    [InlineData("$4637.49", "$4.6K")]
    [InlineData("$150.00", "$150")]
    [InlineData("$5.20", "$5.20")] // <$100: cents kept
    [InlineData("$0.00", "$0.00")]
    public void IconTitleCompactsBigMoney(string title, string expected)
    {
        Assert.Equal(expected, TrayModes.IconTitle(title));
    }

    [Fact]
    public void UsageModesFormatProvidedVisibleTotals()
    {
        var totals = new TrayTotals(
            TodayTokens: 12_345,
            TodayCost: 1.25,
            TotalTokens: 2_500_000,
            TotalCost: 42.5);

        Assert.Equal("12.3K", TrayMode.TodayTokens.Title(totals, null));
        Assert.Equal("$1.25", TrayMode.TodayCost.Title(totals, null));
        Assert.Equal("2.5M", TrayMode.TotalTokens.Title(totals, null));
        Assert.Equal("$42.50", TrayMode.TotalCost.Title(totals, null));
        Assert.Equal("1.5K/m", TrayMode.TokensPerMin.Title(totals, 1_500));
    }

    [Fact]
    public void TokensPerMinWithoutRateShowsPlaceholder()
    {
        Assert.Equal("", TrayMode.TokensPerMin.Title(null, null));
        Assert.Equal(
            "—/m",
            TrayMode.TokensPerMin.Title(new TrayTotals(0, 0, 0, 0), null));
    }
}

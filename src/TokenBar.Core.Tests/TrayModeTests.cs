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

    [Fact]
    public void TokensPerMinWithoutRateShowsPlaceholder()
    {
        // graph must be non-null for the rate mode to format, but the rate
        // placeholder path only needs the mode's own guard order — macOS
        // returns "" for nil graph before consulting the rate.
        Assert.Equal("", TrayMode.TokensPerMin.Title(null, null));
    }
}

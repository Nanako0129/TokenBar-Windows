using TokenBar.Interop;
using Xunit;

namespace TokenBar.Core.Tests;

// Ported from SelfTest.swift's UsagePace block. Fixture: 60-minute window,
// 30 minutes elapsed (linear expected 50%).
public class UsagePaceTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_750_000_000);

    private static UsageWindow Window(
        double used, long minutes = 60, double untilReset = 1800,
        double? historical = null, double? runOut = null) =>
        new(
            Label: "Session", UsedPercent: used, RemainingPercent: 100 - used,
            ResetsAt: Now.AddSeconds(untilReset).UtcDateTime.ToString(
                "yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture),
            ResetText: null, WindowMinutes: minutes,
            HistoricalExpectedPercent: historical, RunOutProbability: runOut);

    [Fact]
    public void OnTrackAtHalfway()
    {
        var pace = UsagePace.Compute(Window(used: 50), Now);
        Assert.NotNull(pace);
        Assert.Equal(PaceStage.OnTrack, pace.Stage);
        Assert.Equal("On pace", pace.Label);
    }

    [Fact]
    public void FarAheadLabelAndEta()
    {
        var pace = UsagePace.Compute(Window(used: 80), Now);
        Assert.NotNull(pace);
        Assert.Equal(PaceStage.FarAhead, pace.Stage);
        Assert.Equal("30% in deficit", pace.Label);
        // 80% in 30min → 100% in 37.5min, before the 30min reset → ETA 7.5min.
        Assert.False(pace.WillLastToReset);
        Assert.True(Math.Abs(pace.EtaSeconds!.Value - 450) < 1);
        Assert.Equal("Projected empty in 8m", pace.EtaText);
    }

    [Fact]
    public void BehindPaceIsReserveAndLasts()
    {
        var pace = UsagePace.Compute(Window(used: 40), Now);
        Assert.NotNull(pace);
        Assert.Equal(PaceStage.Behind, pace.Stage);
        Assert.Equal("10% in reserve", pace.Label);
        Assert.True(pace.WillLastToReset);
        Assert.Equal("Lasts until reset", pace.EtaText);
    }

    [Fact]
    public void NoWindowLengthNoPace() =>
        Assert.Null(UsagePace.Compute(Window(used: 50, minutes: 0), Now));

    [Fact]
    public void PastResetNoPace() =>
        Assert.Null(UsagePace.Compute(Window(used: 50, untilReset: -10), Now));

    [Fact]
    public void ModeOffIsNull() =>
        Assert.Null(UsagePace.Compute(Window(used: 50), PaceMode.Off, Now));

    [Fact]
    public void HistoricalExpectedOverride()
    {
        var pace = UsagePace.Compute(
            Window(used: 50, historical: 80, runOut: 0.2), PaceMode.Historical, Now);
        Assert.NotNull(pace);
        Assert.Equal(80, pace.ExpectedUsedPercent);
        Assert.Equal(PaceStage.FarBehind, pace.Stage);
        Assert.True(pace.WillLastToReset); // low run-out risk lasts to reset
    }

    [Fact]
    public void HighRunOutRiskProjectsEmpty()
    {
        var pace = UsagePace.Compute(
            Window(used: 90, historical: 50, runOut: 0.8), PaceMode.Historical, Now);
        Assert.NotNull(pace);
        Assert.False(pace.WillLastToReset);
        Assert.NotNull(pace.EtaSeconds);
    }

    [Fact]
    public void LinearModeIgnoresHistorical()
    {
        var pace = UsagePace.Compute(Window(used: 50, historical: 80), PaceMode.Linear, Now);
        Assert.NotNull(pace);
        Assert.Equal(50, pace.ExpectedUsedPercent);
    }

    [Fact]
    public void RunOutRiskLabelFormats()
    {
        Assert.Equal("≈ 30% run-out risk", UsagePace.RunOutRiskLabel(Window(used: 50, runOut: 0.3)));
        Assert.Null(UsagePace.RunOutRiskLabel(Window(used: 50)));
    }

    [Theory]
    [InlineData(130 * 60, "2h 10m")]
    [InlineData(26 * 3600, "1d 2h")]
    [InlineData(20, "now")]
    [InlineData(30, "1m")] // Swift .rounded() is away-from-zero: 0.5 min → 1m
    [InlineData(5 * 60, "5m")]
    public void DurationTextBands(double seconds, string expected) =>
        Assert.Equal(expected, UsagePace.DurationText(seconds));

    [Fact]
    public void FractionalSecondsTimestampParses()
    {
        var window = Window(used: 50) with
        {
            ResetsAt = Now.AddSeconds(1800).UtcDateTime.ToString(
                "yyyy-MM-dd'T'HH:mm:ss.123'Z'", System.Globalization.CultureInfo.InvariantCulture),
        };
        Assert.NotNull(UsagePace.Compute(window, Now));
    }
}

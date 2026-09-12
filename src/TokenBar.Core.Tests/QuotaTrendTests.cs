using TokenBar.Core;
using Xunit;

namespace TokenBar.Core.Tests;

// The per-row recent-trend fold (port of QuotaTrend.swift).
public class QuotaTrendTests
{
    private const long Start = 0;
    private const long Hour = 3600 * 1000;
    private const long End = 5 * Hour;

    [Fact]
    public void ARisingCurveProjectsForwardFromTheCurrentReading()
    {
        // Half the window elapsed, 10 points consumed in the last hour.
        var trend = QuotaTrendFold.Trend(
            usedPercent: 50,
            windowStartMs: Start,
            windowEndMs: End,
            nowMs: 2 * Hour + (30 * 60 * 1000),
            samples:
            [
                new QuotaSample(Hour, 30),
                new QuotaSample(2 * Hour, 40),
                new QuotaSample(2 * Hour + (30 * 60 * 1000), 50),
            ]);

        Assert.NotNull(trend);
        Assert.Equal(QuotaTrendDirection.Rising, trend!.Direction);
        Assert.True(trend.ProjectedUsedPercent > 50);
        // The delta and the projection are recomputed from one another, so a
        // reader adding the delta to the current reading lands on the
        // projection beside it.
        Assert.Equal(trend.ProjectedUsedPercent - 50, trend.ProjectedDeltaPercent, 6);
    }

    // The grok case: a window whose lifetime ratio is the highest of the four
    // measured but which stopped being used days ago. Any implementation whose
    // row reads as "burning" is wrong.
    [Fact]
    public void ACurveThatStoppedMovingReadsAsFlatRatherThanBurning()
    {
        var trend = QuotaTrendFold.Trend(
            usedPercent: 63,
            windowStartMs: Start,
            windowEndMs: End,
            nowMs: 4 * Hour,
            samples: [new QuotaSample(3 * Hour, 63), new QuotaSample(4 * Hour, 63)]);

        Assert.NotNull(trend);
        Assert.Equal(QuotaTrendDirection.Flat, trend!.Direction);
        Assert.Equal(0, trend.ProjectedDeltaPercent, 6);
    }

    // One sample inside the lookback span would invent a slope from a single
    // point. No indicator, not a zero.
    [Fact]
    public void OneSampleInTheLookbackSpanProducesNoIndicator()
    {
        Assert.Null(QuotaTrendFold.Trend(
            usedPercent: 20,
            windowStartMs: Start,
            windowEndMs: End,
            nowMs: 4 * Hour,
            samples: [new QuotaSample(4 * Hour, 20)]));
    }

    // Floor only, and deliberately no ceiling: `RunsOutEarly` IS
    // `ProjectedUsedPercent > 100`, so capping there would delete the signal.
    [Fact]
    public void TheProjectionIsFlooredAtZeroAndNotCappedAtAHundred()
    {
        var burning = QuotaTrendFold.Trend(
            usedPercent: 60,
            windowStartMs: Start,
            windowEndMs: End,
            nowMs: Hour,
            samples: [new QuotaSample(0, 0), new QuotaSample(Hour, 60)]);
        Assert.NotNull(burning);
        Assert.True(burning!.ProjectedUsedPercent > 100);
        Assert.True(burning.RunsOutEarly);

        // A provider correction falling steeply would otherwise project a
        // window to a negative reading and a drop larger than the amount that
        // exists.
        var corrected = QuotaTrendFold.Trend(
            usedPercent: 5,
            windowStartMs: Start,
            windowEndMs: End,
            nowMs: Hour,
            samples: [new QuotaSample(0, 90), new QuotaSample(Hour, 5)]);
        Assert.NotNull(corrected);
        Assert.Equal(0, corrected!.ProjectedUsedPercent);
        Assert.Equal(-5, corrected.ProjectedDeltaPercent, 6);
        Assert.False(corrected.RunsOutEarly);
    }

    [Fact]
    public void AWindowWithNoDurationHasNoTrend()
    {
        Assert.Null(QuotaTrendFold.Trend(
            usedPercent: 20,
            windowStartMs: Start,
            windowEndMs: Start,
            nowMs: Hour,
            samples: [new QuotaSample(0, 10), new QuotaSample(Hour, 20)]));
    }
}

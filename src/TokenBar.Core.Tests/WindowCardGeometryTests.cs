using TokenBar.Core;
using TokenBar.Interop;
using Xunit;

namespace TokenBar.Core.Tests;

// The Session-window card's geometry (port of WindowCardGeometry.swift). Every
// rule here is one the SwiftUI original records a defect for.
public class WindowCardGeometryTests
{
    private const long Start = 1_000_000;
    private const long End = 1_000_000 + (5 * 3600 * 1000);

    private static WindowMessage Message(long at, long output = 100) =>
        new(at, "claude", "anthropic", "sonnet", 0, output, 0, 0, 0, 0.5, true);

    [Fact]
    public void ZonesTileUpToNowAndNeverPastIt()
    {
        var now = Start + 3_600_000;
        var zones = WindowCardGeometry.Zones(
            Start, End, now,
            [new QuotaSample(Start + 600_000, 10), new QuotaSample(Start + 1_200_000, 20)]);

        Assert.Equal(3, zones.Count);
        Assert.Equal(Start, zones[0].LoMs);
        Assert.Equal(now, zones[^1].HiMs);
        for (var i = 1; i < zones.Count; i++)
        {
            // No gap and no overlap: each zone opens exactly where the last
            // closed. A minimum width is what pushes the last zone past `now`.
            Assert.Equal(zones[i - 1].HiMs, zones[i].LoMs);
        }

        // The future keeps its share of the width and none of the zones.
        Assert.True(zones[^1].X + zones[^1].Width < 1);
    }

    // A sample at the same instant as an existing edge must not open a
    // zero-width zone, and a sample past `now` is not an edge at all.
    [Fact]
    public void SamplesOnAnEdgeOrInTheFutureAddNoZone()
    {
        var now = Start + 600_000;
        var zones = WindowCardGeometry.Zones(
            Start, End, now,
            [new QuotaSample(Start, 5), new QuotaSample(now, 9), new QuotaSample(now + 1, 12)]);

        Assert.Single(zones);
        Assert.Equal(Start, zones[0].LoMs);
        Assert.Equal(now, zones[0].HiMs);
    }

    // Zone 0 owns its own lower bound. For an inferred window the message
    // landing exactly on the window start IS the start, and an exclusive zone 0
    // put it in the card's totals and in no bar at all.
    [Fact]
    public void ZoneZeroAdmitsAMessageOnTheWindowStart()
    {
        var now = Start + 600_000;
        var (bars, _) = WindowCardGeometry.UsageGeometry(
            Start, End, now, [], [Message(Start)]);

        Assert.Single(bars);
        Assert.False(bars[0].IsEmpty);
    }

    // A flow has no "remaining" version, so flipping the metric must leave the
    // usage geometry bit-identical.
    [Fact]
    public void BarsAndZonesDoNotSeeTheMetric()
    {
        QuotaSample[] samples = [new(Start + 600_000, 10), new(Start + 1_200_000, 30)];
        WindowMessage[] messages = [Message(Start + 700_000), Message(Start + 1_300_000, 400)];
        var now = Start + 1_800_000;

        var used = WindowCardGeometry.Chart(
            Start, End, now, samples, messages, QuotaMetric.Used);
        var remaining = WindowCardGeometry.Chart(
            Start, End, now, samples, messages, QuotaMetric.Remaining);

        Assert.Equal(used.Bars, remaining.Bars);
        Assert.Equal(used.Hits, remaining.Hits);
        // Only the curve moves, and it moves to the complement.
        Assert.Equal(10, used.SamplePoints[0].Y);
        Assert.Equal(90, remaining.SamplePoints[0].Y);
    }

    // An interval with no usage still gets a bar record — flagged empty, not
    // omitted: "nothing was spent" and "no data here" are different answers.
    [Fact]
    public void AnIntervalWithNoUsageIsAnEmptyBarRatherThanAMissingOne()
    {
        var now = Start + 1_800_000;
        var (bars, _) = WindowCardGeometry.UsageGeometry(
            Start, End, now,
            [new QuotaSample(Start + 600_000, 10)],
            [Message(Start + 700_000)]);

        Assert.Equal(2, bars.Count);
        Assert.True(bars[0].IsEmpty);
        Assert.False(bars[1].IsEmpty);
        Assert.Equal(1, bars[1].Height);
    }

    // Round 12's P2 finding: UsageGeometry now sorts messages once and takes a
    // binary-search slice per zone instead of walking every zone per message.
    // The sort must not be load-bearing on the CALLER already handing messages
    // in time order — this pins that an out-of-order list still lands every
    // message in its correct zone.
    [Fact]
    public void MessagesOutOfChronologicalOrderStillLandInTheirCorrectZone()
    {
        var now = Start + 1_800_000;
        QuotaSample[] samples = [new(Start + 600_000, 10), new(Start + 1_200_000, 30)];
        WindowMessage[] messages =
        [
            Message(Start + 1_300_000, 40), // zone 2: (1_200_000, now]
            Message(Start + 700_000, 10),   // zone 1: (600_000, 1_200_000]
            Message(Start + 100_000, 5),    // zone 0: [Start, 600_000]
        ];

        var (bars, _) = WindowCardGeometry.UsageGeometry(Start, End, now, samples, messages);

        Assert.Equal(3, bars.Count);
        Assert.False(bars[0].IsEmpty);
        Assert.False(bars[1].IsEmpty);
        Assert.False(bars[2].IsEmpty);
        // Zone 2 carried the heaviest message (40), so it is the tallest bar.
        Assert.Equal(1, bars[2].Height);
        Assert.True(bars[0].Height < bars[2].Height);
    }

    // The binary-search bounds must exclude a message that falls before the
    // window start or after `now` exactly as the old per-message zone scan
    // did (neither zone's `insideStart`/`<= HiMs` check ever matched it). Two
    // real in-window messages of different weight (not just one) so a stray
    // inclusion at either end shows up as a changed HEIGHT, not just a bar
    // losing its IsEmpty flag — the earlier draft of this test used a single
    // non-empty bar, whose height normalizes to 1 regardless of how much
    // extra weight leaks in, and missed that the "after now" bound had no
    // coverage at all (confirmed by hand-mutation, see the commit body).
    [Fact]
    public void AMessageOutsideEveryZoneContributesToNoBar()
    {
        var now = Start + 1_800_000;
        QuotaSample[] samples = [new(Start + 600_000, 10), new(Start + 1_200_000, 30)];
        WindowMessage[] messages =
        [
            Message(Start - 1, 500),       // before the window: outside every zone
            Message(now + 1, 500),         // after now: outside every zone
            Message(Start + 700_000, 1000), // zone 1: the tallest legitimate bar
            Message(Start + 1_300_000, 100), // zone 2: the reference for the assertion below
        ];

        var (bars, _) = WindowCardGeometry.UsageGeometry(Start, End, now, samples, messages);

        Assert.Equal(3, bars.Count);
        Assert.True(bars[0].IsEmpty); // zone 0: the before-window message must not land here
        Assert.Equal(1, bars[1].Height); // zone 1 is the tallest: 1000 / 1000
        // zone 2 is exactly 100/1000 — 0.15 (600/1000) would mean the
        // after-`now` message leaked into the last zone.
        Assert.Equal(0.1, bars[2].Height);
    }

    [Fact]
    public void ConsumedIsNullWhenEitherEndHasNoReading()
    {
        var now = Start + 1_800_000;
        var zones = WindowCardGeometry.Zones(
            Start, End, now,
            [new QuotaSample(Start + 600_000, 10), new QuotaSample(Start + 1_200_000, 30)]);

        // The opening region has no reading to open on: nothing was measured.
        Assert.Null(zones[0].Consumed(QuotaMetric.Used));
        Assert.Equal(20, zones[1].Consumed(QuotaMetric.Used));
        // Counting down, the same interval reads negative.
        Assert.Equal(-20, zones[1].Consumed(QuotaMetric.Remaining));
        // The trailing region past the last sample closes on nothing.
        Assert.Null(zones[^1].Consumed(QuotaMetric.Used));
    }

    // Fritsch-Carlson, not Catmull-Rom: an overshoot between two rising samples
    // draws a refill that never happened.
    [Fact]
    public void TheCurveNeverLeavesTheBoxItsEndpointsDefine()
    {
        // The direction reversal (80 → 90 → 89) is the case that overshot 90
        // before the tangent was zeroed at a turn.
        List<CurvePoint> points =
        [
            new(0, 80),
            new(0.5, 90),
            new(1, 89),
        ];
        var curve = WindowCardGeometry.MonotoneCurve(points);

        Assert.NotEmpty(curve);
        Assert.All(curve, point => Assert.InRange(point.Y, 80, 90));
    }

    [Fact]
    public void OneSampleDrawsDotsButNoLine()
    {
        var now = Start + 1_800_000;
        var chart = WindowCardGeometry.Chart(
            Start, End, now, [new QuotaSample(Start + 600_000, 10)], [], QuotaMetric.Used);

        Assert.Single(chart.SamplePoints);
        Assert.Empty(chart.Curve);
        // The line needs two points to interpolate between; the dot does not.
        Assert.True(chart.FirstSampleX > 0);
        Assert.True(chart.NowX > chart.FirstSampleX);
    }

    // No sample at all: the line's start collapses onto `now`, which is what
    // leaves the whole box hatched rather than half of it.
    [Fact]
    public void WithNoSamplesTheFirstSampleSitsOnNow()
    {
        var now = Start + 1_800_000;
        var chart = WindowCardGeometry.Chart(Start, End, now, [], [], QuotaMetric.Used);

        Assert.Equal(chart.NowX, chart.FirstSampleX);
        Assert.Empty(chart.SamplePoints);
    }
}

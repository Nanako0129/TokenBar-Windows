using TokenBar.App;
using Xunit;

namespace TokenBar.Core.Tests;

// The Quota lens's state choice and copy. DashboardView.xaml.cs is WinUI and no
// test project compiles it, so this — reached with no XAML type involved — is
// where the four-state branch is asserted.
public class QuotaLensTextTests
{
    public QuotaLensTextTests() => Localization.Load("en", AppContext.BaseDirectory);

    private static QuotaHeatmap Grid(double total = 0, double unplaced = 0, int days = 0) =>
        QuotaHeatmap.Empty with { Total = total, UnplacedPercent = unplaced, ObservedDays = days };

    private static QuotaWindowSummary Summary(double peak) =>
        new(
            new QuotaWindowIdentity("claude", "primary", "session.v1"),
            "Session",
            Recent: [peak],
            RecentPeaks: [peak],
            PeakPercent: peak,
            NeverExhausted: peak < QuotaOverviewFold.ExhaustedPercent,
            CycleCount: 1);

    // All four, in macOS's order (QuotaHeatmapCard.swift:59-82), each on the
    // input that separates it from its neighbour.
    [Fact]
    public void EveryHeatmapStateIsDistinct()
    {
        Assert.Equal(
            QuotaHeatmapState.Grid,
            QuotaLensText.State(Grid(total: 12), attempted: true));

        // Total == 0 with movement that could not be placed. This must NOT fall
        // through to "nothing recorded yet": that states the opposite of the
        // truth and hides the one line explaining it.
        Assert.Equal(
            QuotaHeatmapState.Unplaced,
            QuotaLensText.State(Grid(total: 0, unplaced: 7), attempted: true));

        // The pair that differs only in `attempted`, which is the whole point:
        // a card that has lost the distinction passes every assertion above.
        Assert.Equal(
            QuotaHeatmapState.NoMovement,
            QuotaLensText.State(Grid(), attempted: true));
        Assert.Equal(
            QuotaHeatmapState.Loading,
            QuotaLensText.State(Grid(), attempted: false));
    }

    // A lazy lens fetches on first visit, so a null grid before the fetch is the
    // first paint of every cold start — not "nothing recorded yet".
    [Fact]
    public void NoGridYetIsLoadingUntilTheFetchHasBeenAttempted()
    {
        Assert.Equal(QuotaHeatmapState.Loading, QuotaLensText.State((QuotaHeatmap?)null, attempted: false));
        Assert.Equal(QuotaHeatmapState.NoMovement, QuotaLensText.State((QuotaHeatmap?)null, attempted: true));
    }

    [Fact]
    public void EveryStripStateIsDistinct()
    {
        Assert.Equal(
            QuotaStripState.Rows,
            QuotaLensText.State([Summary(40)], attempted: true));
        Assert.Equal(
            QuotaStripState.NoCompletedWindows,
            QuotaLensText.State([], attempted: true));
        Assert.Equal(
            QuotaStripState.Loading,
            QuotaLensText.State([], attempted: false));
    }

    [Fact]
    public void HeadlineReadsTheCeilingOffThePeak()
    {
        Assert.Equal("Peaked at 98% · never ran out", QuotaLensText.Headline(Summary(98)));
        Assert.Equal("Peaked at 99% · ran out at least once", QuotaLensText.Headline(Summary(99)));
    }

    [Fact]
    public void StripSubtitleOnlyClaimsWhatHoldsForEveryWindow()
    {
        Assert.Equal("never exhausted", QuotaLensText.StripSubtitle([Summary(40), Summary(80)]));
        Assert.Null(QuotaLensText.StripSubtitle([Summary(40), Summary(100)]));
        Assert.Null(QuotaLensText.StripSubtitle([]));
    }

    [Fact]
    public void HeatmapSubtitleCountsObservedDaysAndIsAbsentWithoutAGrid()
    {
        Assert.Equal("21 days observed", QuotaLensText.HeatmapSubtitle(Grid(total: 5, days: 21)));
        Assert.Null(QuotaLensText.HeatmapSubtitle(Grid(days: 21)));
        Assert.Null(QuotaLensText.HeatmapSubtitle(null));
    }

    // The footnote's one-point threshold is about whether a line is worth
    // drawing; HasMovement's is about whether anything happened. Different
    // questions, different thresholds — a window can be in the picker with no
    // footnote.
    [Fact]
    public void FootnoteAppearsOnlyFromAFullPointOfUnplacedConsumption()
    {
        Assert.Equal(
            "7% consumed between readings too far apart to place",
            QuotaLensText.Footnote(Grid(unplaced: 6.6)));
        Assert.Null(QuotaLensText.Footnote(Grid(unplaced: 0.5)));
        Assert.True(Grid(unplaced: 0.5).HasMovement);
    }

    // "A 0.4% slot and an idle one must not both render as 0%."
    [Theory]
    [InlineData(0.4, "0.4")]
    [InlineData(9.94, "9.9")]
    [InlineData(72.4, "72")]
    public void SlotPercentKeepsADecimalBelowTen(double value, string expected) =>
        Assert.Equal(expected, QuotaLensText.Percent(value));

    [Fact]
    public void SlotCopySeparatesSpendFromSilence()
    {
        Assert.Equal("72 allowance-points spent here", QuotaLensText.SlotSpend(72));
        Assert.Equal("No allowance consumed in this slot", QuotaLensText.SlotEmpty());
        Assert.Equal("Fri 16:00", QuotaLensText.SlotHeader(weekday: 4, hour: 16));
        Assert.Equal("Mon 00:00", QuotaLensText.SlotHeader(weekday: 0, hour: 0));
    }

    // The strip is oldest-to-newest, so the newest bar is zero windows ago.
    [Fact]
    public void BarAgeNamesTheMostRecentWindowRatherThanCountingItAsZero()
    {
        Assert.Equal("Most recent window", QuotaLensText.BarAge(0));
        Assert.Equal("3 windows ago", QuotaLensText.BarAge(3));
    }
}

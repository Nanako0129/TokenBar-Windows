using TokenBar.Interop;
using Xunit;

namespace TokenBar.Core.Tests;

// Delta coverage for DayBars.swift → DayBars.cs: saturating token folds and
// the selection-derived range-end anchoring of the trailing window.
public class DayBarsTests
{
    private static Contribution Day(string date, params ContributionClient[] clients) =>
        new(date, new ContributionTotals(0, 0, 0), 1, new TokenBreakdown(0, 0, 0, 0, 0), clients);

    private static ContributionClient Client(string id, long tokens, double cost = 1) =>
        new(id, "m", "anthropic", new TokenBreakdown(tokens, 0, 0, 0, 0), cost, Messages: 1);

    private static UsagePayload Payload(string metaEnd, params Contribution[] days) =>
        new(
            new UsageMeta("g", "v", new DateRange("2026-06-01", metaEnd)),
            new UsageSummary(0, 0, 0, 0, 0, 0, [], []),
            [],
            days);

    [Fact]
    public void RangeEndOverrideAnchorsWindowToSelectedClients()
    {
        // meta range end is 2026-06-30 (a hidden client's later activity), but
        // the selected clients last acted on 2026-06-10. Passing rangeEnd keeps
        // 2026-06-10 as the last bar instead of trailing empty days to the 30th.
        var payload = Payload(
            "2026-06-30",
            Day("2026-06-10", Client("claude", 100)));

        var bars = DayBars.Build(
            payload, ["claude"], StackBy.Agent, new ModelColorMap([]),
            endFallback: "2026-07-15", rangeEnd: "2026-06-10");

        Assert.Equal("2026-06-10", bars[^1].Date);
        Assert.Equal(100, bars[^1].TotalTokens);
    }

    [Fact]
    public void NullRangeEndFallsBackToMetaRange()
    {
        var payload = Payload(
            "2026-06-20",
            Day("2026-06-10", Client("claude", 100)));

        var bars = DayBars.Build(
            payload, ["claude"], StackBy.Agent, new ModelColorMap([]),
            endFallback: "2026-07-15");

        Assert.Equal("2026-06-20", bars[^1].Date);
    }

    [Fact]
    public void TotalTokensSaturatesInsteadOfOverflowing()
    {
        // Two lanes each near long.MaxValue in the same day must clamp, not wrap.
        var payload = Payload(
            "2026-06-10",
            Day("2026-06-10",
                Client("claude", long.MaxValue), Client("codex", long.MaxValue)));

        var bars = DayBars.Build(
            payload, ["claude", "codex"], StackBy.Agent, new ModelColorMap([]),
            endFallback: "2026-07-15", rangeEnd: "2026-06-10");

        Assert.Equal(long.MaxValue, bars[^1].TotalTokens);
    }
}

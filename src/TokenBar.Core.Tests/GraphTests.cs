using TokenBar.Interop;
using Xunit;

namespace TokenBar.Core.Tests;

// Delta coverage for Graph.swift → Interop/Graph.cs: saturating Int64 folds,
// TokenBreakdown.Total, and the hidden-client-aware tray totals.
public class GraphTests
{
    [Fact]
    public void SaturatingAddMatchesPlusForNormalValues() =>
        Assert.Equal(30L, 10L.SaturatingAdd(20L));

    [Fact]
    public void SaturatingAddClampsPositiveOverflowToMax() =>
        Assert.Equal(long.MaxValue, long.MaxValue.SaturatingAdd(1));

    [Fact]
    public void SaturatingAddClampsNegativeOverflowToMin() =>
        Assert.Equal(long.MinValue, long.MinValue.SaturatingAdd(-1));

    [Fact]
    public void SaturatingAddKeysClampOffTheAddendSign()
    {
        // max + max overflows; addend > 0 → clamps to max.
        Assert.Equal(long.MaxValue, long.MaxValue.SaturatingAdd(long.MaxValue));
        // min + min overflows; addend < 0 → clamps to min.
        Assert.Equal(long.MinValue, long.MinValue.SaturatingAdd(long.MinValue));
    }

    [Fact]
    public void TokenBreakdownTotalSums() =>
        Assert.Equal(150L, new TokenBreakdown(10, 20, 15, 5, 100).Total);

    [Fact]
    public void TokenBreakdownTotalSaturatesCorruptLane() =>
        Assert.Equal(long.MaxValue, new TokenBreakdown(long.MaxValue, 1, 0, 0, 0).Total);

    private static ContributionClient Client(string id, TokenBreakdown tokens, double cost) =>
        new(id, "m", "p", tokens, cost, Messages: 1);

    private static UsagePayload PayloadWith(params Contribution[] contributions) =>
        new(
            new UsageMeta("g", "v", new DateRange("2026-07-01", "2026-07-02"), PricingMode.BestEffort, CostCoverage.Complete),
            new UsageSummary(999, 9.9, 2, 2, 5, 5, ["claude", "codex"], ["m"]),
            [],
            contributions);

    [Fact]
    public void TrayTotalsEmptyHiddenTakesSummaryFastPath()
    {
        var payload = PayloadWith(
            new Contribution(
                "2026-07-02", new ContributionTotals(50, 0.7, 3), 2,
                new TokenBreakdown(0, 0, 0, 0, 0),
                [Client("claude", new TokenBreakdown(10, 20, 15, 5, 0), 0.7)]));

        var t = payload.TrayTotals(new HashSet<string>(), today: "2026-07-02");
        // Fast path reads Summary + the today contribution's totals verbatim.
        Assert.Equal(999L, t.TotalTokens);
        Assert.Equal(9.9, t.TotalCost);
        Assert.Equal(50L, t.TodayTokens);
        Assert.Equal(0.7, t.TodayCost);
    }

    [Fact]
    public void TrayTotalsExcludesHiddenClientsOnSlowPath()
    {
        var payload = PayloadWith(
            new Contribution(
                "2026-07-01", new ContributionTotals(0, 0, 0), 1,
                new TokenBreakdown(0, 0, 0, 0, 0),
                [
                    Client("claude", new TokenBreakdown(100, 0, 0, 0, 0), 1.0),
                    Client("codex", new TokenBreakdown(40, 0, 0, 0, 0), 0.4),
                ]),
            new Contribution(
                "2026-07-02", new ContributionTotals(0, 0, 0), 1,
                new TokenBreakdown(0, 0, 0, 0, 0),
                [
                    Client("claude", new TokenBreakdown(10, 0, 0, 0, 0), 0.1),
                    Client("codex", new TokenBreakdown(7, 0, 0, 0, 0), 0.07),
                ]));

        var t = payload.TrayTotals(new HashSet<string> { "codex" }, today: "2026-07-02");
        // Only claude survives: 100 + 10 total, 10 today.
        Assert.Equal(110L, t.TotalTokens);
        Assert.Equal(1.1, t.TotalCost, 6);
        Assert.Equal(10L, t.TodayTokens);
        Assert.Equal(0.1, t.TodayCost, 6);
    }
}

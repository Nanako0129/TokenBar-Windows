using TokenBar.App;
using TokenBar.Core;
using Xunit;

namespace TokenBar.Core.Tests;

// WindowEquivalenceText: DashboardView.Quota.cs is WinUI and no test project
// compiles it, so this is where the strip card's ≈ line and the heatmap
// hover's per-slot branches are asserted — the same reason QuotaLensText has
// its own test file.
public class WindowEquivalenceTextTests
{
    public WindowEquivalenceTextTests() => Localization.Load("en", AppContext.BaseDirectory);

    // ── Line (strip card) ────────────────────────────────────────────────

    [Fact]
    public void LineFormatsTokensAndMoneyThroughTheAppsOwnFormatters()
    {
        var line = WindowEquivalenceText.Line(new WindowEquivalence.Row.Ratio(1_500_000, 9.5, 4));
        Assert.Equal("10% of quota ~ 1.5M · $9.50 API-equivalent, ±4%", line);
    }

    // ── Slot (heatmap hover) ─────────────────────────────────────────────

    // The four productive cases — TokensOnly, CostOnly, Ratio, Spread — are
    // the ones QuotaHeatmapCard.swift's `equivalent` originally missed one of
    // (`.spread` fell through to "not enough history"). Each is asserted here
    // so a future merge cannot silently drop one back into the default arm.

    [Fact]
    public void SlotTokensOnlyScalesByShareAndUsesTheLiteralApproxPrefix()
    {
        var slot = WindowEquivalenceText.Slot(
            percent: 5, row: new WindowEquivalence.Row.TokensOnly(1_000_000, 7));
        // share = 5/10 = 0.5, so 500_000 tokens.
        Assert.Equal("≈ 500K", slot.Primary);
        Assert.Equal("unpriced models, ±7%", slot.Secondary);
    }

    [Fact]
    public void SlotCostOnlyScalesByShareAndUsesTheLiteralTildePrefix()
    {
        var slot = WindowEquivalenceText.Slot(
            percent: 5, row: new WindowEquivalence.Row.CostOnly(10.0, 12));
        Assert.Equal("~ $5.00", slot.Primary);
        Assert.Equal("tokens unavailable, ±12%", slot.Secondary);
    }

    [Fact]
    public void SlotRatioScalesBothTokensAndMoney()
    {
        var slot = WindowEquivalenceText.Slot(
            percent: 10, row: new WindowEquivalence.Row.Ratio(2_000_000, 8.0, 3));
        // share = 10/10 = 1.0, so the full per-tenth figures.
        Assert.Equal("≈ 2M", slot.Primary);
        Assert.Equal("~ $8.00 API-equivalent, ±3%", slot.Secondary);
    }

    // The case QuotaHeatmapCard.swift's own comment names as the one missed
    // when the other three were written.
    [Fact]
    public void SlotSpreadRendersARangeNotAPoint()
    {
        var slot = WindowEquivalenceText.Slot(
            percent: 10, row: new WindowEquivalence.Row.Spread(1_000_000, 3_000_000, 1.0, 3.0));
        Assert.Equal("≈ 1M–3M", slot.Primary);
        Assert.Equal("~ $1.00 – $3.00 API-equivalent", slot.Secondary);
    }

    [Fact]
    public void SlotWithNoRowIsTheGenericNotEnoughHistoryReason()
    {
        var slot = WindowEquivalenceText.Slot(percent: 5, row: null);
        Assert.Equal("Not enough history to convert this window to a figure", slot.Primary);
        Assert.Null(slot.Secondary);
    }

    // ── NoFigureReason ───────────────────────────────────────────────────

    // Each stated per case rather than blamed on "not enough history" — the
    // defect QuotaHeatmapCard.swift's own comment records: three of the four
    // named cases have PLENTY of history and fail for an unrelated reason.

    [Fact]
    public void NoFigureReasonIsSpecificForUndeclared() =>
        Assert.Equal(
            "Classify your usage in Settings to see what this window is worth",
            WindowEquivalenceText.NoFigureReason(new WindowEquivalence.Row.Undeclared()));

    [Fact]
    public void NoFigureReasonIsSpecificForNotMoved() =>
        Assert.Equal(
            "The allowance did not move in this window",
            WindowEquivalenceText.NoFigureReason(new WindowEquivalence.Row.NotMoved()));

    [Fact]
    public void NoFigureReasonIsSpecificForInsufficient() =>
        Assert.Equal(
            "The allowance moved too little to convert reliably",
            WindowEquivalenceText.NoFigureReason(new WindowEquivalence.Row.Insufficient(2, 25)));

    [Fact]
    public void NoFigureReasonIsSpecificForUnaccounted() =>
        Assert.Equal(
            "The allowance moved, but no usage was recorded on this machine",
            WindowEquivalenceText.NoFigureReason(new WindowEquivalence.Row.Unaccounted(20)));

    [Fact]
    public void NoFigureReasonFallsBackForUnavailableAndTooFewCycles()
    {
        Assert.Equal(
            "Not enough history to convert this window to a figure",
            WindowEquivalenceText.NoFigureReason(new WindowEquivalence.Row.Unavailable()));
        Assert.Equal(
            "Not enough history to convert this window to a figure",
            WindowEquivalenceText.NoFigureReason(new WindowEquivalence.Row.TooFewCycles(1, 3)));
        Assert.Equal(
            "Not enough history to convert this window to a figure",
            WindowEquivalenceText.NoFigureReason(null));
    }
}

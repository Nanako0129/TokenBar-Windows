using TokenBar.Core;
using TokenBar.Interop;
using Xunit;

namespace TokenBar.Core.Tests;

// Port coverage for WindowEquivalence.swift → TokenBar.Core/WindowEquivalence.cs.
// Every Row case is pinned against the input that distinguishes it from its
// nearest neighbour, per the file's own doc comments — a merged pair here
// would misreport a dollar figure, which is the one failure this file exists
// to prevent.
public class WindowEquivalenceTests
{
    public WindowEquivalenceTests() => Localization.Load("en", AppContext.BaseDirectory);

    private static WindowMessage Message(long timestampMs, long tokens, double cost) =>
        new(
            Timestamp: timestampMs,
            Client: "claude-code",
            ProviderId: "anthropic",
            ModelId: "claude-fable-5",
            Input: tokens,
            Output: 0,
            CacheRead: 0,
            CacheWrite: 0,
            Reasoning: 0,
            Cost: cost,
            IsTurnStart: true);

    // ── Clamped ──────────────────────────────────────────────────────────

    [Fact]
    public void ClampedReturnsZeroForNaN() =>
        Assert.Equal(0, WindowEquivalence.Clamped(double.NaN));

    // isFinite is false for infinity too, matching Swift's guard — not the
    // saturating branch below it.
    [Fact]
    public void ClampedReturnsZeroForInfinity() =>
        Assert.Equal(0, WindowEquivalence.Clamped(double.PositiveInfinity));

    [Fact]
    public void ClampedSaturatesAFiniteValueAboveLongRange() =>
        Assert.Equal(long.MaxValue, WindowEquivalence.Clamped(1e30));

    [Fact]
    public void ClampedSaturatesAFiniteValueBelowLongRange() =>
        Assert.Equal(long.MinValue, WindowEquivalence.Clamped(-1e30));

    [Fact]
    public void ClampedPassesThroughAnOrdinaryValue() =>
        Assert.Equal(42, WindowEquivalence.Clamped(42.4));

    // ── LiveRow ──────────────────────────────────────────────────────────

    [Fact]
    public void FewerThanTwoSamplesIsUnavailable()
    {
        var row = WindowEquivalence.LiveRow(
            declared: true, attempted: true,
            [new WindowEquivalence.Sample(0, 10)], []);
        Assert.IsType<WindowEquivalence.Row.Unavailable>(row);
    }

    // The neighbour NotMoved is distinguished from: two-or-more samples exist,
    // but the reading did not move (delta <= 0), so no error term is defined.
    [Fact]
    public void TwoSamplesWithNoMovementIsNotMoved()
    {
        var row = WindowEquivalence.LiveRow(
            declared: true, attempted: true,
            [
                new WindowEquivalence.Sample(0, 40),
                new WindowEquivalence.Sample(1000, 40),
            ],
            [Message(500, 999, 1.0)]);
        Assert.IsType<WindowEquivalence.Row.NotMoved>(row);
    }

    // The distinction the plan calls out by name: Unaccounted must not
    // collapse into a Ratio carrying a zero token/cost figure. Quota moved
    // (delta = 20, well above minimumDelta) but no message falls in the
    // sample span, so this machine recorded none of it.
    [Fact]
    public void MovementWithNoMessagesInSpanIsUnaccountedNotAZeroRatio()
    {
        var row = WindowEquivalence.LiveRow(
            declared: true, attempted: true,
            [
                new WindowEquivalence.Sample(0, 10),
                new WindowEquivalence.Sample(1000, 30),
            ],
            [Message(2000, 999, 5.0)]); // outside (0, 1000]
        var unaccounted = Assert.IsType<WindowEquivalence.Row.Unaccounted>(row);
        Assert.Equal(20, unaccounted.DeltaPercent);
    }

    // The other neighbour of NotMoved: movement occurred (delta > 0) but is
    // too small to survive quantisation (delta < minimumDelta = 5).
    [Fact]
    public void SmallMovementWithEvidenceIsInsufficientNotNotMoved()
    {
        var row = WindowEquivalence.LiveRow(
            declared: true, attempted: true,
            [
                new WindowEquivalence.Sample(0, 10),
                new WindowEquivalence.Sample(1000, 12), // delta = 2 < 5
            ],
            [Message(500, 1000, 5.0)]);
        var insufficient = Assert.IsType<WindowEquivalence.Row.Insufficient>(row);
        Assert.Equal(2, insufficient.DeltaPercent);
        // error = round(0.5 / 2 * 100) = 25
        Assert.Equal(25, insufficient.ErrorPercent);
    }

    [Fact]
    public void TokensWithNoCostIsTokensOnlyNotAZeroDollarRatio()
    {
        var row = WindowEquivalence.LiveRow(
            declared: true, attempted: true,
            [
                new WindowEquivalence.Sample(0, 10),
                new WindowEquivalence.Sample(1000, 20), // delta = 10
            ],
            [Message(500, 1000, 0.0)]);
        var tokensOnly = Assert.IsType<WindowEquivalence.Row.TokensOnly>(row);
        Assert.Equal(1000, tokensOnly.TokensPerTenth); // 1000 / 10 * 10
    }

    [Fact]
    public void CostWithNoTokensIsCostOnlyNotZeroTokenRatio()
    {
        var row = WindowEquivalence.LiveRow(
            declared: true, attempted: true,
            [
                new WindowEquivalence.Sample(0, 10),
                new WindowEquivalence.Sample(1000, 20),
            ],
            [Message(500, 0, 4.0)]);
        var costOnly = Assert.IsType<WindowEquivalence.Row.CostOnly>(row);
        Assert.Equal(4.0, costOnly.CostPerTenth); // 4 / 10 * 10
    }

    [Fact]
    public void BothTokensAndCostIsRatio()
    {
        var row = WindowEquivalence.LiveRow(
            declared: true, attempted: true,
            [
                new WindowEquivalence.Sample(0, 10),
                new WindowEquivalence.Sample(1000, 20),
            ],
            [Message(500, 1000, 4.0)]);
        var ratio = Assert.IsType<WindowEquivalence.Row.Ratio>(row);
        Assert.Equal(1000, ratio.TokensPerTenth);
        Assert.Equal(4.0, ratio.CostPerTenth);
        Assert.True(ratio.IsRatio);
    }

    // The span filter itself: only messages strictly after the first sample
    // and at-or-before the last count, matching the doc comment's
    // "numerator and denominator must cover the same interval" rule.
    [Fact]
    public void OnlyMessagesInsideTheSampleSpanCount()
    {
        var row = WindowEquivalence.LiveRow(
            declared: true, attempted: true,
            [
                new WindowEquivalence.Sample(1000, 10),
                new WindowEquivalence.Sample(2000, 20),
            ],
            [
                Message(1000, 500, 1.0), // at first.AtMs: excluded (not >)
                Message(1500, 1000, 4.0), // inside: included
                Message(2000, 500, 1.0), // at last.AtMs: included (<=)
                Message(2001, 999, 9.0), // after last.AtMs: excluded
            ]);
        var ratio = Assert.IsType<WindowEquivalence.Row.Ratio>(row);
        Assert.Equal(1500, ratio.TokensPerTenth); // (1000 + 500) / 10 * 10
        Assert.Equal(5.0, ratio.CostPerTenth); // (4 + 1) / 10 * 10
    }

    // `declared` wins before evidence is even looked at, the same rule
    // Aggregate already enforces — plenty of movement and plenty of
    // attributed tokens/cost here, and the row still must not read
    // Unaccounted, which is the round-5 P2 this parameter exists to make
    // structurally unreachable.
    [Fact]
    public void UndeclaredWinsOverPlentifulEvidence()
    {
        var row = WindowEquivalence.LiveRow(
            declared: false, attempted: true,
            [
                new WindowEquivalence.Sample(0, 10),
                new WindowEquivalence.Sample(1000, 30),
            ],
            [Message(500, 1000, 4.0)]);
        Assert.IsType<WindowEquivalence.Row.Undeclared>(row);
    }

    // The other half of the same defect: the message scan has not landed yet
    // (attempted = false), so an empty/partial `messages` list must read as
    // "still reading", not "nothing recorded on this machine".
    [Fact]
    public void NotYetAttemptedIsLoadingNotUnaccounted()
    {
        var row = WindowEquivalence.LiveRow(
            declared: true, attempted: false,
            [
                new WindowEquivalence.Sample(0, 10),
                new WindowEquivalence.Sample(1000, 30),
            ],
            []);
        Assert.IsType<WindowEquivalence.Row.Loading>(row);
    }

    // declared is checked before attempted: an undeclared user with no scan
    // yet still gets the actionable "classify your usage" sentence, not the
    // passive "still reading" one.
    [Fact]
    public void UndeclaredWinsOverNotYetAttempted()
    {
        var row = WindowEquivalence.LiveRow(
            declared: false, attempted: false,
            [
                new WindowEquivalence.Sample(0, 10),
                new WindowEquivalence.Sample(1000, 30),
            ],
            []);
        Assert.IsType<WindowEquivalence.Row.Undeclared>(row);
    }

    [Fact]
    public void IsRatioIsFalseForEveryOtherCase()
    {
        Assert.False(new WindowEquivalence.Row.NotMoved().IsRatio);
        Assert.False(new WindowEquivalence.Row.Undeclared().IsRatio);
    }

    // ── Aggregate ────────────────────────────────────────────────────────

    private static WindowEquivalence.Cycle Cycle(
        double delta, long tokens, double cost, double observed = 1.0) =>
        new(DeltaPercent: delta, SpanTokens: tokens, SpanCost: cost, ObservedFraction: observed);

    [Fact]
    public void UndeclaredWinsBeforeAnyCycleIsExamined()
    {
        var row = WindowEquivalence.Aggregate(
            declared: false, [Cycle(50, 1000, 10), Cycle(50, 1000, 10), Cycle(50, 1000, 10)]);
        Assert.IsType<WindowEquivalence.Row.Undeclared>(row);
    }

    [Fact]
    public void NoCyclesAtAllIsUnavailable()
    {
        var row = WindowEquivalence.Aggregate(declared: true, []);
        Assert.IsType<WindowEquivalence.Row.Unavailable>(row);
    }

    [Fact]
    public void NoAdmittedCycleAndNoMovementIsNotMoved()
    {
        var row = WindowEquivalence.Aggregate(declared: true, [Cycle(0, 0, 0)]);
        Assert.IsType<WindowEquivalence.Row.NotMoved>(row);
    }

    // Movement happened, evidence exists (so not Unaccounted), but the one
    // cycle was too small to be admitted on its own.
    [Fact]
    public void UnadmittedCycleWithEvidenceIsInsufficient()
    {
        var row = WindowEquivalence.Aggregate(declared: true, [Cycle(3, 500, 2.5)]);
        var insufficient = Assert.IsType<WindowEquivalence.Row.Insufficient>(row);
        Assert.Equal(3, insufficient.DeltaPercent);
        // round(0.5 * 1 / 3 * 100) = 17
        Assert.Equal(17, insufficient.ErrorPercent);
    }

    // The distinction the plan calls out for the pooled path too: movement
    // occurred and nothing was recorded — not a zero-valued estimate.
    [Fact]
    public void UnadmittedCycleWithNoEvidenceAtAllIsUnaccounted()
    {
        var row = WindowEquivalence.Aggregate(declared: true, [Cycle(3, 0, 0)]);
        var unaccounted = Assert.IsType<WindowEquivalence.Row.Unaccounted>(row);
        Assert.Equal(3, unaccounted.DeltaPercent);
    }

    [Fact]
    public void FewerThanThreeAdmittedCyclesIsTooFewCycles()
    {
        var row = WindowEquivalence.Aggregate(
            declared: true, [Cycle(50, 1000, 10), Cycle(50, 1000, 10)]);
        var tooFew = Assert.IsType<WindowEquivalence.Row.TooFewCycles>(row);
        Assert.Equal(2, tooFew.Count);
        Assert.Equal(3, tooFew.Needed);
    }

    [Fact]
    public void EnoughTokenBearingCyclesButNotEnoughPricedIsTokensOnly()
    {
        var row = WindowEquivalence.Aggregate(
            declared: true, [Cycle(10, 1000, 0), Cycle(10, 1000, 0), Cycle(10, 1000, 0)]);
        var tokensOnly = Assert.IsType<WindowEquivalence.Row.TokensOnly>(row);
        Assert.Equal(1000, tokensOnly.TokensPerTenth); // 3000 / 30 * 10
        Assert.Equal(1, tokensOnly.ErrorPercent); // uniform cycles: 0 error, floored to 1
    }

    [Fact]
    public void EnoughPricedCyclesButNotEnoughTokenBearingIsCostOnly()
    {
        var row = WindowEquivalence.Aggregate(
            declared: true, [Cycle(10, 0, 5), Cycle(10, 0, 5), Cycle(10, 0, 5)]);
        var costOnly = Assert.IsType<WindowEquivalence.Row.CostOnly>(row);
        Assert.Equal(5.0, costOnly.CostPerTenth); // 15 / 30 * 10
        Assert.Equal(1, costOnly.ErrorPercent);
    }

    [Fact]
    public void NeitherEstimateHasEnoughCyclesIsTooFewCyclesNotTokensOrCostOnly()
    {
        // Admitted via tokens on two, via cost on a different two — four
        // admitted cycles, but priced.Count == 2 and tokenBearing.Count == 2.
        var row = WindowEquivalence.Aggregate(
            declared: true,
            [
                Cycle(10, 1000, 0),
                Cycle(10, 1000, 0),
                Cycle(10, 0, 5),
                Cycle(10, 0, 5),
            ]);
        var tooFew = Assert.IsType<WindowEquivalence.Row.TooFewCycles>(row);
        Assert.Equal(2, tooFew.Count);
    }

    [Fact]
    public void ConsistentCyclesProduceARatioNotASpread()
    {
        var row = WindowEquivalence.Aggregate(
            declared: true,
            [Cycle(10, 1000, 5), Cycle(10, 1000, 5), Cycle(10, 1000, 5)]);
        var ratio = Assert.IsType<WindowEquivalence.Row.Ratio>(row);
        Assert.Equal(1000, ratio.TokensPerTenth);
        Assert.Equal(5.0, ratio.CostPerTenth);
        Assert.Equal(1, ratio.ErrorPercent);
    }

    // The row this project exists to avoid faking a point estimate for: three
    // admitted cycles whose cost-per-point disagrees far past tolerance.
    [Fact]
    public void DisagreeingCyclesProduceASpreadNotAnAveragedRatio()
    {
        var row = WindowEquivalence.Aggregate(
            declared: true,
            [
                Cycle(10, 1000, 1),
                Cycle(10, 1000, 5),
                Cycle(10, 1000, 9),
            ]);
        var spread = Assert.IsType<WindowEquivalence.Row.Spread>(row);
        Assert.Equal(1000, spread.LowPerTenth);
        Assert.Equal(1000, spread.HighPerTenth);
        Assert.Equal(1.0, spread.LowCostPerTenth);
        Assert.Equal(9.0, spread.HighCostPerTenth);
    }

    // Cycles below minimumObservedFraction are excluded from admission even
    // when their delta clears minimumDelta — the "barely witnessed" gate is
    // independent of the "too coarse to measure" one.
    [Fact]
    public void PoorlyObservedCycleIsNotAdmittedDespiteALargeDelta()
    {
        var row = WindowEquivalence.Aggregate(
            declared: true,
            [
                Cycle(50, 1000, 10, observed: 0.1),
                Cycle(50, 1000, 10, observed: 0.1),
                Cycle(50, 1000, 10, observed: 0.1),
            ]);
        // None admitted, but real evidence exists, so this is Insufficient —
        // not Unaccounted and not a silently-produced Ratio.
        Assert.IsType<WindowEquivalence.Row.Insufficient>(row);
    }

    // ── Text ─────────────────────────────────────────────────────────────

    [Fact]
    public void TextRendersRatio()
    {
        var text = WindowEquivalence.Text(
            new WindowEquivalence.Row.Ratio(1_000_000, 12.5, 3),
            tokens => tokens.ToString(),
            money => "$" + money.ToString("F2"));
        Assert.Equal("10% of quota ~ 1000000 · $12.50 API-equivalent, ±3%", text);
    }

    [Fact]
    public void TextRendersUndeclared()
    {
        var text = WindowEquivalence.Text(
            new WindowEquivalence.Row.Undeclared(), t => t.ToString(), m => m.ToString());
        Assert.Equal("Classify your usage in Settings to see what this window is worth", text);
    }

    [Fact]
    public void TextRendersTooFewCyclesInArgumentOrder()
    {
        var text = WindowEquivalence.Text(
            new WindowEquivalence.Row.TooFewCycles(2, 3), t => t.ToString(), m => m.ToString());
        Assert.Equal("2 of 3 windows recorded — the estimate needs that many", text);
    }

    [Fact]
    public void TextRendersSpread()
    {
        var text = WindowEquivalence.Text(
            new WindowEquivalence.Row.Spread(100, 900, 1.0, 9.0),
            t => t.ToString(),
            m => "$" + m.ToString("F2"));
        Assert.Equal("10% of quota ~ 100-900 · $1.00-$9.00 API-equivalent", text);
    }
}

using TokenBar.App;
using TokenBar.Core;

namespace TokenBar.Core.Tests;

/// <summary>
/// The 每日・依訂閱 card's state choice and copy. The defect these exist to
/// prevent lives in the branch order: a range that holds real usage can still
/// draw nothing under one metric, and announcing "no usage recorded" there states
/// the opposite of the truth.
/// </summary>
public class SubscriptionTrendTextTests
{
    /// <summary>One day carrying exactly the given totals against one target.</summary>
    private static SubscriptionTrend Trend(long tokens, double cost, string target = "claude")
    {
        var day = new SubscriptionTrend.Day(
            "2026-08-30",
            new Dictionary<string, SubscriptionTrend.Bucket>
            {
                [target] = new(tokens, cost),
            },
            tokens,
            cost);
        return new SubscriptionTrend([day], [target], [target], cost, tokens);
    }

    // ---- The four states -------------------------------------------------

    [Fact]
    public void APublishedRangeWithSpendUnderBothMetricsDrawsTheChart()
    {
        var trend = Trend(tokens: 100, cost: 1.5);
        Assert.Equal(SubscriptionTrendState.Chart,
            SubscriptionTrendText.State(trend, ChartMetric.Cost));
        Assert.Equal(SubscriptionTrendState.Chart,
            SubscriptionTrendText.State(trend, ChartMetric.Tokens));
    }

    // The acceptance this card was written for. Usage IS recorded; it simply
    // carries no token counts, and under Tokens that is a different fact from
    // an empty range.
    [Fact]
    public void RealUsageWithZeroTokensIsNoTokenCountsUnderTokensAndAChartUnderPrice()
    {
        var trend = Trend(tokens: 0, cost: 4.25);

        Assert.Equal(SubscriptionTrendState.MetricUnavailable,
            SubscriptionTrendText.State(trend, ChartMetric.Tokens));
        Assert.Equal(
            "Usage recorded, but it carries no token counts.",
            SubscriptionTrendText.EmptyBody(
                SubscriptionTrendState.MetricUnavailable, ChartMetric.Tokens));
        Assert.Equal(SubscriptionTrendState.Chart,
            SubscriptionTrendText.State(trend, ChartMetric.Cost));
    }

    // The mirror image: unpriced models are a real range with nothing to draw
    // under Price.
    [Fact]
    public void RealUsageWithZeroCostIsUnpricedUnderPriceAndAChartUnderTokens()
    {
        var trend = Trend(tokens: 900_000, cost: 0);

        Assert.Equal(SubscriptionTrendState.MetricUnavailable,
            SubscriptionTrendText.State(trend, ChartMetric.Cost));
        Assert.Equal(
            "Usage recorded, but none of it is priced.",
            SubscriptionTrendText.EmptyBody(
                SubscriptionTrendState.MetricUnavailable, ChartMetric.Cost));
        Assert.Equal(SubscriptionTrendState.Chart,
            SubscriptionTrendText.State(trend, ChartMetric.Tokens));
    }

    // Only when BOTH metrics are empty is the range itself empty, and only then
    // may the card say so.
    [Fact]
    public void AnEmptyRangeIsNoUsageUnderEitherMetric()
    {
        var trend = Trend(tokens: 0, cost: 0);
        foreach (var metric in new[] { ChartMetric.Cost, ChartMetric.Tokens })
        {
            Assert.Equal(SubscriptionTrendState.NoUsage,
                SubscriptionTrendText.State(trend, metric));
            Assert.Equal(
                "No usage recorded in this range.",
                SubscriptionTrendText.EmptyBody(SubscriptionTrendState.NoUsage, metric));
        }

        Assert.Equal(SubscriptionTrendState.NoUsage,
            SubscriptionTrendText.State(SubscriptionTrend.Empty, ChartMetric.Cost));
    }

    // Not published yet is not an empty range: the card must not announce "no
    // usage recorded" while its own data is still on the way.
    [Fact]
    public void ANullTrendIsLoadingRatherThanEmpty()
    {
        Assert.Equal(SubscriptionTrendState.Loading,
            SubscriptionTrendText.State(null, ChartMetric.Cost));
        Assert.Equal(
            "Reading daily usage…",
            SubscriptionTrendText.EmptyBody(SubscriptionTrendState.Loading, ChartMetric.Cost));
        Assert.Null(SubscriptionTrendText.Subtitle(null));
    }

    // ---- Copy and ordering -----------------------------------------------

    // The subtitle names the first day the fold actually covered, so it and the
    // axis cannot disagree about the range.
    [Fact]
    public void TheSubtitleNamesTheFirstDayTheFoldCovered()
    {
        var trend = SubscriptionTrendFold.Build([], "2026-08-30", SubscriptionTrendText.Window);

        Assert.Equal(SubscriptionTrendText.Window, trend.Days.Count);
        Assert.Equal("2026-08-17", trend.Days[0].Date);
        Assert.Equal("2026-08-30", trend.Days[^1].Date);
        Assert.Equal($"since {Format.MonthDay("2026-08-17")}", SubscriptionTrendText.Subtitle(trend));
    }

    [Fact]
    public void TheHintAppearsOnlyWhenTheSingleBandIsTheUnclassifiedOne()
    {
        Assert.NotNull(SubscriptionTrendText.UndeclaredHint(
            Trend(10, 1, SubscriptionTrendFold.UnassignedTarget)));
        Assert.Null(SubscriptionTrendText.UndeclaredHint(Trend(10, 1, "claude")));

        var mixed = new SubscriptionTrend(
            [new SubscriptionTrend.Day("2026-08-30", new Dictionary<string, SubscriptionTrend.Bucket>(), 0, 0)],
            [SubscriptionTrendFold.UnassignedTarget, "claude"],
            [SubscriptionTrendFold.UnassignedTarget, "claude"],
            1,
            1);
        Assert.Null(SubscriptionTrendText.UndeclaredHint(mixed));
    }

    // Unclassified usage is named and coloured as itself: grey, not a brand, and
    // not the id the fold uses as a key.
    [Fact]
    public void TheUnclassifiedBandIsNamedAndColouredAsItself()
    {
        Assert.Equal("Unclassified",
            SubscriptionTrendText.TargetName(SubscriptionTrendFold.UnassignedTarget));
        Assert.Equal("#8e8e93",
            SubscriptionTrendText.TargetColor(SubscriptionTrendFold.UnassignedTarget));
        Assert.Equal(ClientRegistry.Style("claude").DisplayName,
            SubscriptionTrendText.TargetName("claude"));
        Assert.Equal(ClientRegistry.Style("claude").Color,
            SubscriptionTrendText.TargetColor("claude"));
    }

    // The stacking loop, the tooltip list and the legend all read this one
    // resolution, so they cannot disagree about which order they are in.
    [Fact]
    public void TheSelectedMetricChoosesTheOrder()
    {
        var trend = new SubscriptionTrend([], ["codex", "claude"], ["claude", "codex"], 1, 1);

        Assert.Equal(["codex", "claude"],
            SubscriptionTrendText.Ordered(trend, ChartMetric.Cost));
        Assert.Equal(["claude", "codex"],
            SubscriptionTrendText.Ordered(trend, ChartMetric.Tokens));
    }

    [Fact]
    public void TheMetricChoosesWhichNumberIsPlotted()
    {
        var bucket = new SubscriptionTrend.Bucket(120, 3.5);
        Assert.Equal(3.5, SubscriptionTrendText.Value(bucket, ChartMetric.Cost));
        Assert.Equal(120, SubscriptionTrendText.Value(bucket, ChartMetric.Tokens));

        var trend = Trend(tokens: 120, cost: 3.5);
        Assert.Equal(3.5, SubscriptionTrendText.Peak(trend, ChartMetric.Cost));
        Assert.Equal(120, SubscriptionTrendText.Peak(trend, ChartMetric.Tokens));
        Assert.Equal(3.5, SubscriptionTrendText.DayTotal(trend.Days[0], ChartMetric.Cost));
        Assert.Equal(120, SubscriptionTrendText.DayTotal(trend.Days[0], ChartMetric.Tokens));
    }

    // Both metrics carry both figures: flipping the toggle must not turn a
    // measured number into a missing one for the same row.
    [Fact]
    public void TheTooltipAmountLeadsWithTheSelectedMetricAndKeepsTheOther()
    {
        Assert.Equal("$3.50 · 120", SubscriptionTrendText.Amount(120, 3.5, ChartMetric.Cost));
        Assert.Equal("120 · $3.50", SubscriptionTrendText.Amount(120, 3.5, ChartMetric.Tokens));
    }

    [Fact]
    public void TheToggleUsesThePriceLabelTheOverviewChartUses()
    {
        Assert.Equal("Price", SubscriptionTrendText.MetricLabel(ChartMetric.Cost));
        Assert.Equal("Tokens", SubscriptionTrendText.MetricLabel(ChartMetric.Tokens));
    }

    // ---- i18n ------------------------------------------------------------
    //
    // Against the *shipped* strings-zh-Hant.json, for the reason QuotaLensText's
    // own i18n test records: the failure guarded against is a Localized() call
    // site whose key was never added to that file, and the card shows one state
    // at a time, so only driving each branch can prove the others have entries.
    [Fact]
    public void EveryStringTheCardCanShowHasATableEntry()
    {
        var trend = Trend(10, 1, SubscriptionTrendFold.UnassignedTarget);
        Func<string?>[] surfaces =
        [
            SubscriptionTrendText.Title,
            () => SubscriptionTrendText.Subtitle(trend),
            () => SubscriptionTrendText.EmptyBody(SubscriptionTrendState.NoUsage, ChartMetric.Cost),
            () => SubscriptionTrendText.EmptyBody(
                SubscriptionTrendState.MetricUnavailable, ChartMetric.Cost),
            () => SubscriptionTrendText.EmptyBody(
                SubscriptionTrendState.MetricUnavailable, ChartMetric.Tokens),
            () => SubscriptionTrendText.EmptyBody(SubscriptionTrendState.Loading, ChartMetric.Cost),
            () => SubscriptionTrendText.UndeclaredHint(trend),
            () => SubscriptionTrendText.MetricLabel(ChartMetric.Cost),
            () => SubscriptionTrendText.MetricLabel(ChartMetric.Tokens),
            SubscriptionTrendText.NoUsageThisDay,
            SubscriptionTrendText.TotalLabel,
            () => SubscriptionTrendText.TargetName(SubscriptionTrendFold.UnassignedTarget),
        ];

        var english = surfaces.Select(surface => surface()).ToList();
        Localization.Load("zh-Hant", AppContext.BaseDirectory);
        try
        {
            for (var i = 0; i < surfaces.Length; i++)
            {
                Assert.NotEqual(english[i], surfaces[i]());
            }
        }
        finally
        {
            Localization.Load("en", AppContext.BaseDirectory);
        }
    }
}

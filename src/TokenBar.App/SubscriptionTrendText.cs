using TokenBar.Core;

namespace TokenBar.App;

/// <summary>Which of the card's four states applies.</summary>
public enum SubscriptionTrendState
{
    /// <summary>The stacked columns, the axis and the legend.</summary>
    Chart,

    /// <summary>Usage WAS recorded — the selected metric simply has none of it.
    /// A range can hold real spend and still draw nothing under Tokens (rows
    /// priced but carrying no token counts) or under Price (unpriced models), and
    /// calling that "no usage" states the opposite of the truth.</summary>
    MetricUnavailable,

    /// <summary>The range really is empty, under either metric.</summary>
    NoUsage,

    /// <summary>The series has not been published yet, which is not the same as
    /// an empty range.</summary>
    Loading,

    /// <summary>A past year is selected in the dashboard's year filter. This
    /// card always follows the most recent <see cref="SubscriptionTrendText.Window"/>
    /// days ending today (macOS's own <c>SubscriptionTrendCard.swift</c> anchors
    /// the same way, with no year-filter awareness at all) — a year filter change
    /// never moves that window, so it can never overlap a past year. "No usage
    /// recorded in this range" would be false: the graph plainly has data for
    /// that year, this card simply never looks at it.</summary>
    PastYear,
}

/// <summary>
/// Every decision the 每日・依訂閱 card makes, and the copy it makes them with
/// (port of the non-drawing half of <c>SubscriptionTrendCard.swift</c>).
/// <para>
/// Pulled into TokenBar.Core.Tests via &lt;Compile Include&gt;, the same way
/// <see cref="QuotaLensText"/> and <see cref="UsageAttributionPage"/> are and for
/// the same reason: <c>DashboardView.Quota.cs</c> is compiled by no test project,
/// so a branch left inside it would not be asserted at all.
/// </para>
/// </summary>
public static class SubscriptionTrendText
{
    /// <summary>Today and the thirteen days before it. Fourteen because the macOS
    /// card's own column-gap note is written about "fourteen filled bars"
    /// (<c>SubscriptionTrendCard.swift:61-63</c>); the card's caller is not in the
    /// snapshot, so that comment is the evidence for the width.</summary>
    public const int Window = 14;

    /// <summary>Cost or tokens, persisted under macOS's own key
    /// (<c>SubscriptionTrendCard.swift:22</c>).</summary>
    public const string MetricKey = "tokenbar.trend.metric";

    /// <summary>Unclassified usage is deliberately grey rather than given a brand:
    /// it does not belong to anyone yet, and colouring it like a subscription
    /// would assert what the user has not declared. Solid, not an alpha of the
    /// text colour — the fold keeps this band so the totals agree, and hiding it
    /// by alpha undoes that.</summary>
    public const string UnclassifiedColor = "#8e8e93";

    public static string Title() => "Daily by subscription".Localized();

    /// <summary>Names the first day the fold actually covered, so the subtitle and
    /// the axis cannot disagree about the range.</summary>
    public static string? Subtitle(SubscriptionTrend? trend) =>
        trend?.Days.FirstOrDefault() is { } first
            ? "since {0}".Localized(Format.MonthDay(first.Date))
            : null;

    /// <summary>Which of the four things this card can show.
    /// <para>The range is a fact about the data; only the copy is a fact about the
    /// toggle. Testing the selected metric's peak alone reported the whole range
    /// as empty whenever the other metric held everything.</para>
    /// <para><see cref="SubscriptionTrendState.Loading"/> is unreachable from the
    /// Quota lens today: <c>RenderContent</c> returns before building anything
    /// while <c>_snapshot</c> is null, so <c>BuildQuota</c> always has a graph.
    /// Kept because the state is a property of the card, not of its one caller,
    /// and a second caller with a lazier feed would otherwise reintroduce the
    /// "no usage recorded" lie during a fetch.</para></summary>
    public static SubscriptionTrendState State(
        SubscriptionTrend? trend, ChartMetric metric, bool pastYearSelected = false)
    {
        // Checked before Loading, too: a past year picks a window this card
        // structurally cannot show data for, whether or not the fetch for
        // TODAY's window has landed yet.
        if (pastYearSelected)
        {
            return SubscriptionTrendState.PastYear;
        }

        if (trend is null)
        {
            return SubscriptionTrendState.Loading;
        }

        if (trend.Days.Count == 0 || (trend.PeakCost <= 0 && trend.PeakTokens <= 0))
        {
            return SubscriptionTrendState.NoUsage;
        }

        return Peak(trend, metric) > 0
            ? SubscriptionTrendState.Chart
            : SubscriptionTrendState.MetricUnavailable;
    }

    /// <summary>The line for a state that draws no chart. Three different facts,
    /// not one: which of them applies is what <see cref="State"/> decides, and the
    /// metric only chooses between the two halves of
    /// <see cref="SubscriptionTrendState.MetricUnavailable"/>.</summary>
    public static string EmptyBody(SubscriptionTrendState state, ChartMetric metric) => state switch
    {
        SubscriptionTrendState.MetricUnavailable => metric == ChartMetric.Cost
            ? "Usage recorded, but none of it is priced.".Localized()
            : "Usage recorded, but it carries no token counts.".Localized(),
        SubscriptionTrendState.NoUsage => "No usage recorded in this range.".Localized(),
        SubscriptionTrendState.PastYear =>
            "Always shows the most recent 14 days, not the selected year.".Localized(),
        _ => "Reading daily usage…".Localized(),
    };

    /// <summary>Shown when every band is the unclassified one. The chart is not
    /// wrong in that state — the totals are real — but it is answering "how much
    /// did you spend" with a single grey block while claiming to answer "on which
    /// subscription".</summary>
    public static string? UndeclaredHint(SubscriptionTrend trend) =>
        trend.Targets.Count == 1 && trend.Targets[0] == SubscriptionTrendFold.UnassignedTarget
            ? ("Nothing is classified yet. Settings › Usage attribution splits this "
                + "by subscription.").Localized()
            : null;

    public static string MetricLabel(ChartMetric metric) =>
        // "Price", matching the Overview chart's toggle: two cards in the same
        // scroll calling one concept by two names is a name the reader has to
        // reconcile.
        (metric == ChartMetric.Cost ? "Price" : "Tokens").Localized();

    public static string NoUsageThisDay() => "No usage this day".Localized();

    public static string TotalLabel() => "Total".Localized();

    public static string TargetName(string target) =>
        target == SubscriptionTrendFold.UnassignedTarget
            ? "Unclassified".Localized()
            : ClientRegistry.Style(target).DisplayName;

    public static string TargetColor(string target) =>
        target == SubscriptionTrendFold.UnassignedTarget
            ? UnclassifiedColor
            : ClientRegistry.Style(target).Color;

    public static IReadOnlyList<string> Ordered(SubscriptionTrend trend, ChartMetric metric) =>
        trend.TargetsFor(metric == ChartMetric.Tokens);

    public static double Value(SubscriptionTrend.Bucket bucket, ChartMetric metric) =>
        metric == ChartMetric.Cost ? bucket.Cost : bucket.Tokens;

    public static double DayTotal(SubscriptionTrend.Day day, ChartMetric metric) =>
        metric == ChartMetric.Cost ? day.TotalCost : day.TotalTokens;

    public static double Peak(SubscriptionTrend trend, ChartMetric metric) =>
        metric == ChartMetric.Cost ? trend.PeakCost : trend.PeakTokens;

    /// <summary>The tooltip's per-band and total figure. Compact tokens plus the
    /// price, in the order the selected metric asks for.</summary>
    public static string Amount(long tokens, double cost, ChartMetric metric) =>
        metric == ChartMetric.Cost
            ? $"{Format.Usd(cost)} · {Format.CompactTokens(tokens)}"
            : $"{Format.CompactTokens(tokens)} · {Format.Usd(cost)}";
}

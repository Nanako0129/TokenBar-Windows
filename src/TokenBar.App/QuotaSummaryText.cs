using TokenBar.Core;

namespace TokenBar.App;

/// <summary>Text composition for the Overview quota summary card (port of the
/// string-building half of TokenBarCore/QuotaSummaryLine.swift; the WinUI
/// grid layout lives in DashboardView.xaml.cs since WinUI elements cannot be
/// unit tested from TokenBar.Core.Tests). Pulled into TokenBar.Core.Tests via
/// &lt;Compile Include&gt;, the same way DrillDownSummary is.</summary>
/// <summary>Which of the card's three empty states applies. summary == null
/// alone cannot tell "still asking" from "asked and nothing reported a
/// window" — Attempted, from whether the quota lane has produced a payload
/// yet, is what tells them apart.</summary>
public enum QuotaSummaryState
{
    Ready,
    NoWindowReporting,
    Loading,
}

public static class QuotaSummaryText
{
    public static QuotaSummaryState State(QuotaSummary? summary, bool attempted) =>
        summary is not null ? QuotaSummaryState.Ready
        : attempted ? QuotaSummaryState.NoWindowReporting
        : QuotaSummaryState.Loading;


    /// <summary>Who a window belongs to, qualified by account when the
    /// client has more than one. Windows has no AccountIdentity/account-label
    /// surface yet (no multi-account support), so accountKey is always null
    /// today and this degrades to the bare client name; once accountKey stops
    /// being null this is where the qualified label is composed.</summary>
    private static string AccountQualifiedName(string clientId, string? accountKey)
    {
        var name = ClientRegistry.Style(clientId).DisplayName;
        return accountKey is null ? name : $"{name} {accountKey}";
    }

    public static string TightestName(QuotaSummary summary) =>
        AccountQualifiedName(summary.TightestClient, summary.TightestAccountKey);

    public static string BurnName(BurnWarning burning) =>
        AccountQualifiedName(burning.ClientId, burning.AccountKey);

    public static string TightestHeadline(QuotaSummary summary) =>
        $"{TightestName(summary)} · {summary.TightestLabel.Localized()}";

    /// <summary>"<paramref name="summary"/>% left · <reset>". Reset comes
    /// from UsagePace.ResetText, the same helper the quota cards use, so the
    /// countdown here cannot drift from the one shown next to the bar.</summary>
    public static string TightestDetail(QuotaSummary summary, DateTimeOffset now)
    {
        var left = "{0}% left".Localized(
            (int)Math.Round(summary.RemainingPercent, MidpointRounding.AwayFromZero));
        var reset = summary.ResetsAt is { } resetsAt ? UsagePace.ResetText(resetsAt, now) : null;
        return reset is null ? left : $"{left} · {reset}";
    }

    /// <summary>"N other windows, all above X%" or "N of M other windows are
    /// below X%" — two distinct forms, never rendered the same, per
    /// QuotaSummary.OthersComfortable's doc comment. Caller must check
    /// summary.OtherWindows &gt; 0 first.</summary>
    public static string OthersText(QuotaSummary summary) =>
        summary.AllOthersComfortable
            ? "{0} other windows, all above {1}%".Localized(
                summary.OtherWindows, (int)QuotaSummaryFold.ComfortablePercent)
            : "{0} of {1} other windows are below {2}%".Localized(
                summary.OtherWindows - summary.OthersComfortable,
                summary.OtherWindows,
                (int)QuotaSummaryFold.ComfortablePercent);

    /// <summary>"Every measured window is under its expected pace" — gated
    /// by the caller on PaceCheckedWindows &gt; 0. "Every measured", never
    /// "every": with four windows and one measurable, the wider wording
    /// would vouch for three nobody looked at.</summary>
    public static string PaceReassurance() => "Every measured window is under its expected pace".Localized();

    public static string BurnHeadline(BurnWarning burning) =>
        $"{BurnName(burning)} · {burning.Label.Localized()}";

    /// <summary>"N% ahead of pace · <risk-or-eta>". Risk text takes
    /// precedence over the ETA the same way UsagePace.Presentation
    /// prioritizes it for the Agent-limits card.</summary>
    public static string BurnDetail(BurnWarning burning)
    {
        var ahead = "{0}% ahead of pace".Localized(
            (int)Math.Round(burning.AheadPercent, MidpointRounding.AwayFromZero));
        var tail = burning.RiskText ?? burning.EtaText;
        return tail is null ? ahead : $"{ahead} · {tail}";
    }

    /// <summary>"1.2M tokens · $3.40". The cost half goes through
    /// CostSurfaceProjection.CostText — not a plain currency formatter — so a
    /// day of entirely unpriced models reads "Checking" instead of a
    /// fabricated "$0.00".</summary>
    public static string TodayText(long tokens, double cost, bool authoritative) =>
        "{0} tokens · {1}".Localized(
            Format.CompactTokens(tokens), CostSurfaceProjection.CostText(cost, authoritative));

    public static string NoWindowReporting() =>
        "No subscription is reporting a usage window right now.".Localized();

    public static string CheckingLimits() => "Checking agent limits…".Localized();
}

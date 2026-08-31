using TokenBar.App;
using TokenBar.Core;
using Xunit;

namespace TokenBar.Core.Tests;

// The Overview quota summary card's string composition (port of the display
// half of TokenBarCore/QuotaSummaryLine.swift). DashboardView.xaml.cs is
// WinUI and no test project compiles it, so this — and the layout it feeds —
// is where these strings are asserted.
public class QuotaSummaryTextTests
{
    public QuotaSummaryTextTests() => Localization.Load("en", AppContext.BaseDirectory);

    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_750_000_000);

    private static QuotaSummary Summary(
        int otherWindows = 0, int othersComfortable = 0,
        BurnWarning? burning = null, int paceChecked = 0, string? resetsAt = null) =>
        new(
            TightestClient: "claude",
            TightestAccountKey: null,
            TightestLabel: "Weekly",
            RemainingPercent: 42,
            ResetsAt: resetsAt ?? Now.AddSeconds(2 * 86_400 + 2 * 3_600).UtcDateTime
                .ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"),
            OtherWindows: otherWindows,
            OthersComfortable: othersComfortable,
            Burning: burning,
            PaceCheckedWindows: paceChecked);

    // 7. Ready / NoWindowReporting / Loading are three distinct states — a
    // fetch that hasn't returned yet must never render the same as one that
    // returned and found nothing.
    [Fact]
    public void ThreeEmptyStatesAreDistinct()
    {
        Assert.Equal(QuotaSummaryState.Ready, QuotaSummaryText.State(Summary(), attempted: true));
        Assert.Equal(QuotaSummaryState.Ready, QuotaSummaryText.State(Summary(), attempted: false));
        Assert.Equal(
            QuotaSummaryState.NoWindowReporting, QuotaSummaryText.State(null, attempted: true));
        Assert.Equal(QuotaSummaryState.Loading, QuotaSummaryText.State(null, attempted: false));
    }

    [Fact]
    public void TightestHeadlineNamesClientAndWindow() =>
        Assert.Equal("Claude Code · Weekly", QuotaSummaryText.TightestHeadline(Summary()));

    // Reset text is produced by UsagePace.ResetText, the same helper the
    // quota cards use — asserting the exact composed string here is what
    // pins that reuse rather than a re-derived countdown.
    [Fact]
    public void TightestDetailUsesUsagePaceResetText()
    {
        var summary = Summary();
        var expectedReset = UsagePace.ResetText(summary.ResetsAt!, Now);
        Assert.Equal($"42% left · {expectedReset}", QuotaSummaryText.TightestDetail(summary, Now));
    }

    [Fact]
    public void TightestDetailOmitsResetWhenUnparseable()
    {
        var summary = Summary(resetsAt: "not-a-timestamp");
        Assert.Equal("42% left", QuotaSummaryText.TightestDetail(summary, Now));
    }

    // 5. othersText: two distinct shapes, never rendered the same.
    [Fact]
    public void OthersTextAllComfortable() =>
        Assert.Equal(
            "4 other windows, all above 60%",
            QuotaSummaryText.OthersText(Summary(otherWindows: 4, othersComfortable: 4)));

    [Fact]
    public void OthersTextSomeBelowThreshold() =>
        Assert.Equal(
            "2 of 14 other windows are below 60%",
            QuotaSummaryText.OthersText(Summary(otherWindows: 14, othersComfortable: 12)));

    [Fact]
    public void BurnHeadlineAndDetailPreferRiskOverEta()
    {
        var burn = new BurnWarning("codex", null, "Session", 18.4, "Projected empty in 2h", "≈ 30% run-out risk");
        Assert.Equal("Codex CLI · Session", QuotaSummaryText.BurnHeadline(burn));
        Assert.Equal("18% ahead of pace · ≈ 30% run-out risk", QuotaSummaryText.BurnDetail(burn));
    }

    [Fact]
    public void BurnDetailFallsBackToEtaWithoutRisk()
    {
        var burn = new BurnWarning("codex", null, "Session", 18.4, "Projected empty in 2h", null);
        Assert.Equal("18% ahead of pace · Projected empty in 2h", QuotaSummaryText.BurnDetail(burn));
    }

    // 6. Today's money goes through the tokens-and-cost formatter: an
    // unauthoritative day must read "Checking", never a fabricated "$0.00".
    [Fact]
    public void TodayTextShowsCheckingWhenCostIsNotAuthoritative() =>
        Assert.Equal(
            "0 tokens · Checking", QuotaSummaryText.TodayText(0, 0, authoritative: false));

    [Fact]
    public void TodayTextShowsMoneyWhenAuthoritative() =>
        Assert.Equal(
            "1.2M tokens · $3.40", QuotaSummaryText.TodayText(1_234_000, 3.4, authoritative: true));

    // ---- i18n ------------------------------------------------------------

    private static void InChinese(Action body)
    {
        Localization.Load("zh-Hant", AppContext.BaseDirectory);
        try
        {
            body();
        }
        finally
        {
            Localization.Load("en", AppContext.BaseDirectory);
        }
    }

    [Fact]
    public void EveryStringIsTranslated() => InChinese(() =>
    {
        var summary = Summary();
        Assert.Equal("Claude Code · 每週", QuotaSummaryText.TightestHeadline(summary));
        Assert.Equal("剩餘 42% · 2天2小時 後重置", QuotaSummaryText.TightestDetail(summary, Now));
        Assert.Equal(
            "另外 14 個時間窗有 2 個低於 60%",
            QuotaSummaryText.OthersText(Summary(otherWindows: 14, othersComfortable: 12)));
        Assert.Equal("已測量的時間窗都在預期步調之內", QuotaSummaryText.PaceReassurance());
        Assert.Equal("目前沒有訂閱回報使用時間窗。", QuotaSummaryText.NoWindowReporting());
        Assert.Equal("正在查詢 Agent 額度…", QuotaSummaryText.CheckingLimits());
    });

    // --- SecondRow ---------------------------------------------------------
    //
    // Added after the reassurance row went missing on a real machine and could
    // not be explained: the counts said it should render, the row helper was
    // sound, the translation was present, and the live data flipped to a burn
    // warning before the question could be settled. The decision was inline in
    // the view, which no test compiles, so "it appears when nothing is burning
    // and something was measured" was a property only observable by catching
    // the data in that state.

    [Fact]
    public void SecondRowIsTheBurnWarningWhenSomethingIsBurning() =>
        Assert.Equal(
            QuotaSummarySecondRow.Burning,
            QuotaSummaryText.SecondRow(SummaryWith(others: 7, paceChecked: 5, burning: true)));

    // The burn warning REPLACES the reassurance; they never both show.
    [Fact]
    public void BurnWarningWinsEvenWhenTheReassuranceWouldAlsoQualify() =>
        Assert.NotEqual(
            QuotaSummarySecondRow.Reassurance,
            QuotaSummaryText.SecondRow(SummaryWith(others: 7, paceChecked: 5, burning: true)));

    [Fact]
    public void SecondRowIsTheReassuranceWhenNothingIsBurningAndSomethingWasMeasured() =>
        Assert.Equal(
            QuotaSummarySecondRow.Reassurance,
            QuotaSummaryText.SecondRow(SummaryWith(others: 7, paceChecked: 5, burning: false)));

    // Nothing measured: Burning is null for want of asking, not for want of
    // anything to find, so the reassurance would vouch for a check never run.
    [Fact]
    public void NothingIsShownWhenNoWindowWasMeasured() =>
        Assert.Equal(
            QuotaSummarySecondRow.None,
            QuotaSummaryText.SecondRow(SummaryWith(others: 7, paceChecked: 0, burning: false)));

    // One window and nothing to compare it to.
    [Fact]
    public void NothingIsShownWhenThereAreNoOtherWindows() =>
        Assert.Equal(
            QuotaSummarySecondRow.None,
            QuotaSummaryText.SecondRow(SummaryWith(others: 0, paceChecked: 5, burning: false)));

    private static QuotaSummary SummaryWith(int others, int paceChecked, bool burning) =>
        new(
            TightestClient: "claude",
            TightestAccountKey: null,
            TightestLabel: "Weekly",
            RemainingPercent: 41,
            ResetsAt: null,
            OtherWindows: others,
            OthersComfortable: others,
            Burning: burning
                ? new BurnWarning("codex", null, "Weekly", 12, null, null)
                : null,
            PaceCheckedWindows: paceChecked);
}

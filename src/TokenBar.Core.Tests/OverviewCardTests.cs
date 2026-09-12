using TokenBar.App;

namespace TokenBar.Core.Tests;

// The Overview's card order is a parity surface, not a local preference: a
// Windows build that sequences the same cards differently is a gap the eye
// notices before any feature list does.
//
// This test exists because the mistake has now been made twice. macOS's own
// OverviewCard comment records that a commit claiming to restore the order had
// two cards reversed and said so in its message, and that its pinned order was
// what caught it. Windows then arrived at the same error independently — the
// quota summary was built directly above the limits card, which left the usage
// chart ahead of it.
public class OverviewCardTests
{
    // Transcribed from macOS Sources/TokenBarCore/OverviewCard.swift, where
    // declaration order is render order:
    //     case quotaSummary, chart, limits, trace, models, streaks
    [Fact]
    public void RenderOrderMatchesMacOS()
    {
        Assert.Equal(
            [
                OverviewCard.QuotaSummary,
                OverviewCard.Chart,
                OverviewCard.Limits,
                OverviewCard.Trace,
                OverviewCard.Models,
                OverviewCard.Streaks,
            ],
            OverviewCards.RenderOrder);
    }

    // Every declared card must be placed. A card added to the enum and left out
    // of the order would simply never render, with nothing to say so.
    [Fact]
    public void EveryCardIsPlacedExactlyOnce()
    {
        var all = Enum.GetValues<OverviewCard>();

        Assert.Equal(all.Length, OverviewCards.RenderOrder.Length);
        Assert.Equal(all.Length, OverviewCards.RenderOrder.Distinct().Count());
        Assert.Empty(all.Except(OverviewCards.RenderOrder));
    }

    // The summary opens the lens. Stated as its own case rather than left
    // implicit in the full-order assertion, because this is the specific
    // property that was wrong and the failure message should say so.
    [Fact]
    public void QuotaSummaryComesFirst() =>
        Assert.Equal(OverviewCard.QuotaSummary, OverviewCards.RenderOrder[0]);
}

// OverviewScope (item 3): selecting a single client's tab must scope the
// Overview lens to that client — the quota summary headline and the
// live-session card both answer "across everything right now", not what a
// single-client tab asked, and the Agent-limits card must narrow to that one
// client instead of showing every agent's bars underneath a tab naming one.
public class OverviewScopeTests
{
    [Fact]
    public void TheOverviewTabItselfIsNotASingleClient() =>
        Assert.Null(OverviewScope.SingleClient(ClientRegistry.OverviewTab));

    [Fact]
    public void AClientTabIsItsOwnSingleClient() =>
        Assert.Equal("gemini", OverviewScope.SingleClient("gemini"));

    [Fact]
    public void QuotaSummaryAndTraceShowOnlyOnTheOverviewTab()
    {
        Assert.True(OverviewScope.ShowsQuotaSummary(null));
        Assert.False(OverviewScope.ShowsQuotaSummary("gemini"));
        Assert.True(OverviewScope.ShowsTrace(null));
        Assert.False(OverviewScope.ShowsTrace("gemini"));
    }

    [Fact]
    public void LimitsAreUnrestrictedOnOverviewAndScopedOnAClientTab()
    {
        Assert.Null(OverviewScope.LimitsClientId(null));
        Assert.Equal("gemini", OverviewScope.LimitsClientId("gemini"));
    }

    // A client that spends another subscription's allowance reaches BuildLimits
    // under the OWNER, because that is how the quota payload keys it. Passing
    // the raw tab id through matched nothing and the card claimed there was no
    // quota data while the quota sat in the payload. Codex review found this on
    // PR #89; nothing pinned the rule, which is why it was missed.
    [Fact]
    public void LimitsClientIdResolvesTheQuotaOwnerRatherThanTheRawTabId()
    {
        Assert.Equal("antigravity", OverviewScope.LimitsClientId("antigravity-cli"));
    }

    [Fact]
    public void LimitsClientIdLeavesAClientThatOwnsItsOwnQuotaAlone()
    {
        Assert.Equal("claude", OverviewScope.LimitsClientId("claude"));
        Assert.Null(OverviewScope.LimitsClientId(null));
    }
}

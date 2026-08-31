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

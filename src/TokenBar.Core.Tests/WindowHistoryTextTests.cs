using TokenBar.App;
using TokenBar.Core;
using Xunit;

namespace TokenBar.Core.Tests;

// The 時間窗歷史 card's rows, its scale rule and its disclaimer.
public class WindowHistoryTextTests
{
    private const long Hour = 3_600_000;

    public WindowHistoryTextTests() => Localization.Load("en", AppContext.BaseDirectory);

    private static QuotaCycle Cycle(
        long resetAtMs, double usedPercent, double observed = 1.0) =>
        new(
            ResetAtMs: resetAtMs,
            StartMs: resetAtMs - (5 * Hour),
            UsedPercent: usedPercent,
            PeakUsedPercent: usedPercent,
            SampleCount: 4,
            ObservedFraction: observed,
            FirstSampleMs: resetAtMs - (5 * Hour) + 1,
            LastSampleMs: resetAtMs - 1);

    private static WindowEquivalence.Cycle Span(long tokens, double cost) =>
        new(DeltaPercent: 0, SpanTokens: tokens, SpanCost: cost, ObservedFraction: 1);

    [Fact]
    public void ARowCarriesTheStampThePercentAndTheMoney()
    {
        var rows = WindowHistoryText.Rows(
            [Cycle(10 * Hour, 42)], [Span(1_500_000, 3.25)]);

        var row = Assert.Single(rows);
        Assert.Equal(10 * Hour, row.ResetAtMs);
        Assert.Equal("42%", WindowHistoryText.Percent(row));
        Assert.Equal(1_500_000, row.Tokens);
        Assert.Equal(3.25, row.Cost);
        Assert.Equal(0.42, row.QuotaFraction, 6);
        Assert.Matches(@"^\d{2}-\d{2} \d{2}:\d{2}$", row.Stamp);
    }

    // Fixed 0…100 on the quota bar, like the window card above it: rescaling to
    // the largest row would make a 3% window and a 58% one look alike, and
    // comparing cycles to each other and to the ceiling is what the bar is for.
    [Fact]
    public void TheQuotaBarIsOnAFixedScaleAndTheUsageBarIsNot()
    {
        var rows = WindowHistoryText.Rows(
            [Cycle(20 * Hour, 3), Cycle(10 * Hour, 58)],
            [Span(1_000, 1), Span(4_000, 2)]);

        Assert.Equal(0.03, rows[0].QuotaFraction, 6);
        Assert.Equal(0.58, rows[1].QuotaFraction, 6);
        // The usage bar is relative to the heaviest row on screen.
        Assert.Equal(0.25, rows[0].UsageFraction, 6);
        Assert.Equal(1, rows[1].UsageFraction, 6);
    }

    // One statement of "the rows on screen". macOS records what three answers
    // cost: a hidden older cycle with the largest total set the scale and made
    // every visible bar short — precisely the comparison the bar claims to make.
    [Fact]
    public void TheUsageScaleIsTakenOverTheVisibleRowsOnly()
    {
        var cycles = Enumerable.Range(0, WindowHistoryText.VisibleRows + 1)
            .Select(i => Cycle((100 - i) * Hour, 10))
            .ToList();
        var spans = Enumerable.Range(0, cycles.Count)
            // The heaviest window is the one row past the cut.
            .Select(i => Span(i == WindowHistoryText.VisibleRows ? 10_000 : 1_000, 1))
            .ToList();

        var rows = WindowHistoryText.Rows(cycles, spans);

        Assert.Equal(WindowHistoryText.VisibleRows, rows.Count);
        Assert.All(rows, row => Assert.Equal(1, row.UsageFraction, 6));
    }

    // The cap on the visible rows must stay at or under the cap on the cycles
    // the folds consider, or the card draws fewer rows than it intends with
    // nothing saying so.
    [Fact]
    public void TheVisibleCapFitsInsideTheConsideredCap() =>
        Assert.True(WindowHistoryText.VisibleRows <= QuotaHistoryFold.ConsideredCycles);

    // A window nobody was watching for most of its length reports a floor, and
    // says so. Silently printing the figure would present a partial observation
    // as a measurement.
    [Fact]
    public void ABarelyObservedWindowIsMarkedRatherThanPresentedAsMeasured()
    {
        var rows = WindowHistoryText.Rows(
            [Cycle(10 * Hour, 12, observed: 0.2), Cycle(5 * Hour, 12, observed: 0.9)],
            [Span(1, 1), Span(1, 1)]);

        Assert.True(rows[0].ThinObservation);
        Assert.Equal(
            "TokenBar observed 20% of this window, so its usage figure is a floor.",
            WindowHistoryText.ThinObservationNote(rows[0]));
        Assert.False(rows[1].ThinObservation);
        Assert.Null(WindowHistoryText.ThinObservationNote(rows[1]));
    }

    // A scan that has not landed leaves no span for a cycle. The row still
    // draws its quota half rather than the whole card waiting.
    [Fact]
    public void ARowWithNoScannedUsageStillDrawsItsQuotaHalf()
    {
        var row = Assert.Single(WindowHistoryText.Rows([Cycle(10 * Hour, 42)], []));

        Assert.Equal(0.42, row.QuotaFraction, 6);
        Assert.Equal(0, row.Tokens);
        Assert.Equal(0, row.UsageFraction);
    }

    [Fact]
    public void EveryStateIsDistinct()
    {
        var rows = WindowHistoryText.Rows([Cycle(10 * Hour, 42)], [Span(1, 1)]);

        Assert.Equal(WindowHistoryState.Rows, WindowHistoryText.State(rows, attempted: true));
        // The pair that differs only in `attempted`: `cycles` derives from a
        // lazily-read store, so an empty list before the read has settled means
        // "still asking", not "nothing recorded".
        Assert.Equal(WindowHistoryState.NoHistory, WindowHistoryText.State([], attempted: true));
        Assert.Equal(WindowHistoryState.Loading, WindowHistoryText.State([], attempted: false));
        Assert.NotEqual(
            WindowHistoryText.EmptyBody(WindowHistoryState.Loading),
            WindowHistoryText.EmptyBody(WindowHistoryState.NoHistory));
    }

    // The money column is an API list-price equivalent for usage the user
    // themselves declared — not a bill. The line naming that is part of the
    // card, not noise to drop.
    [Fact]
    public void TheDisclaimerNamesTheSubscriptionAndDeniesBeingACharge()
    {
        var text = WindowHistoryText.Disclaimer("claude");

        Assert.Contains(ClientRegistry.Style("claude").DisplayName, text);
        Assert.Contains("not what the subscription charged", text);
    }

    [Fact]
    public void SubtitleCountsTheRowsShownAndIsAbsentWithNone()
    {
        Assert.Null(WindowHistoryText.Subtitle([]));
        Assert.Equal(
            "2 windows",
            WindowHistoryText.Subtitle(WindowHistoryText.Rows(
                [Cycle(10 * Hour, 1), Cycle(5 * Hour, 2)], [Span(1, 1), Span(1, 1)])));
    }

    // ---- i18n ------------------------------------------------------------
    [Fact]
    public void EveryStringTheCardCanShowHasATableEntry()
    {
        var rows = WindowHistoryText.Rows([Cycle(10 * Hour, 42, observed: 0.2)], [Span(1, 1)]);
        Func<string?>[] surfaces =
        [
            WindowHistoryText.Title,
            () => WindowHistoryText.Subtitle(rows),
            () => WindowHistoryText.EmptyBody(WindowHistoryState.Loading),
            () => WindowHistoryText.EmptyBody(WindowHistoryState.NoHistory),
            () => WindowHistoryText.ThinObservationNote(rows[0]),
            () => WindowHistoryText.Disclaimer("claude"),
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

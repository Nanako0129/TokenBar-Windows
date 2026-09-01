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
        var otherAssignedRow = HistoryRow(otherAssigned: true);
        var otherExcludedRow = HistoryRow(otherExcluded: true);
        var otherUnattributedRow = HistoryRow(otherUnattributed: true);
        var otherAssignedAndExcludedRow = HistoryRow(otherAssigned: true, otherExcluded: true);
        var otherAssignedAndUnattributedRow = HistoryRow(otherAssigned: true, otherUnattributed: true);
        Func<string?>[] surfaces =
        [
            WindowHistoryText.Title,
            () => WindowHistoryText.Subtitle(rows),
            () => WindowHistoryText.EmptyBody(WindowHistoryState.Loading),
            () => WindowHistoryText.EmptyBody(WindowHistoryState.NoHistory),
            () => WindowHistoryText.ThinObservationNote(rows[0]),
            () => WindowHistoryText.Disclaimer("claude"),
            WindowHistoryText.NothingChargedNote,
            () => WindowHistoryText.SameHoursLine(otherAssignedRow),
            () => WindowHistoryText.SameHoursLine(otherExcludedRow),
            () => WindowHistoryText.SameHoursLine(otherUnattributedRow),
            () => WindowHistoryText.SameHoursLine(otherAssignedAndExcludedRow),
            () => WindowHistoryText.SameHoursLine(otherAssignedAndUnattributedRow),
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

    // ---- the expanded row (PARITY-3b's deferred half) ---------------------

    private static QuotaHistoryRow HistoryRow(
        long mineTokens = 0, double mineCost = 0,
        long otherTokens = 100, double otherCost = 1.0,
        bool otherAssigned = false, bool otherExcluded = false, bool otherUnattributed = false,
        IReadOnlyList<QuotaHistoryModel>? models = null) =>
        new(
            Cycle: Cycle(10 * Hour, 42),
            MineTokens: mineTokens,
            MineTokensExCacheRead: mineTokens,
            MineCost: mineCost,
            SpanTokens: mineTokens,
            SpanCost: mineCost,
            OtherTokens: otherTokens,
            OtherCost: otherCost,
            OtherHasAssigned: otherAssigned,
            OtherHasExcluded: otherExcluded,
            OtherHasUnattributed: otherUnattributed,
            Models: models ?? []);

    // The five variants named in the fold's own comment, each selected by its
    // OWN combination of presence flags — not by comparing token counts, the
    // defect QuotaHistoryRow's doc comment records having paid for once.
    [Theory]
    [InlineData(true, false, false, "Other subscriptions in the same hours: 100 · $1.00")]
    [InlineData(false, true, false, "Excluded usage in the same hours: 100 · $1.00")]
    [InlineData(false, false, true, "Unclassified usage in the same hours: 100 · $1.00")]
    [InlineData(true, true, false, "Other and excluded usage in the same hours: 100 · $1.00")]
    [InlineData(true, false, true, "Other and unclassified usage in the same hours: 100 · $1.00")]
    public void EachPresenceFlagCombinationSelectsItsOwnLead(
        bool assigned, bool excluded, bool unattributed, string expected)
    {
        var row = HistoryRow(otherAssigned: assigned, otherExcluded: excluded, otherUnattributed: unattributed);
        Assert.Equal(expected, WindowHistoryText.SameHoursLine(row));
    }

    // Unclassified takes precedence in the lead over excluded when both are
    // present alongside "other" — the fold checks OtherHasUnattributed first.
    [Fact]
    public void UnattributedTakesPrecedenceOverExcludedInTheLead() =>
        Assert.Equal(
            "Other and unclassified usage in the same hours: 100 · $1.00",
            WindowHistoryText.SameHoursLine(
                HistoryRow(otherAssigned: true, otherExcluded: true, otherUnattributed: true)));

    // The exact defect the fold's comment names: a contribution that carries
    // cost and NO tokens must still produce a line — a token-only comparison
    // would have called this bucket empty.
    [Fact]
    public void ACostOnlyUnattributedContributionStillProducesALine() =>
        Assert.Equal(
            "Unclassified usage in the same hours: $7.50",
            WindowHistoryText.SameHoursLine(
                HistoryRow(otherTokens: 0, otherCost: 7.5, otherUnattributed: true)));

    // The mirror: tokens with no price prints the tokens alone.
    [Fact]
    public void ATokenOnlyContributionPrintsTokensAlone() =>
        Assert.Equal(
            "Other subscriptions in the same hours: 100",
            WindowHistoryText.SameHoursLine(HistoryRow(otherTokens: 100, otherCost: 0, otherAssigned: true)));

    // Nothing recorded in the same hours at all -> no line, not an empty one.
    [Fact]
    public void NoOtherEvidenceProducesNoLine() =>
        Assert.Null(WindowHistoryText.SameHoursLine(HistoryRow(otherTokens: 0, otherCost: 0)));

    private static QuotaHistoryModel Model(string modelId, long tokens, double cost) =>
        new("anthropic", modelId, tokens, cost);

    // "The heaviest four" — a fifth, smaller model does not appear.
    [Fact]
    public void TopModelsIsTheHeaviestFour()
    {
        var row = HistoryRow(models:
        [
            Model("m1", 500, 1),
            Model("m2", 400, 1),
            Model("m3", 300, 1),
            Model("m4", 200, 1),
            Model("m5", 100, 1),
        ]);

        Assert.Equal(["m1", "m2", "m3", "m4"], WindowHistoryText.TopModels(row).Select(m => m.ModelId).ToArray());
    }

    // The dash rule: a model attributed by cost alone carries no token count,
    // and the mirror for a model with tokens and no price.
    [Fact]
    public void AModelRowDashesTheMetricItDoesNotCarry()
    {
        Assert.Equal("·", WindowHistoryText.ModelTokens(Model("cost-only", 0, 5)));
        Assert.Equal("$5.00", WindowHistoryText.ModelCost(Model("cost-only", 0, 5)));
        Assert.Equal("1.5K", WindowHistoryText.ModelTokens(Model("token-only", 1_500, 0)));
        Assert.Equal("·", WindowHistoryText.ModelCost(Model("token-only", 1_500, 0)));
    }

    // Segments are proportioned by tokens against THIS ROW's own MineTokens,
    // and empty when nothing has been attributed at all — an empty usage bar
    // is a real answer, not a rendering failure.
    [Fact]
    public void SegmentsAreProportionedByTokensAgainstMineTokens()
    {
        var row = HistoryRow(mineTokens: 1000, models: [Model("a", 750, 1), Model("b", 250, 1)]);

        var colors = new ModelColorMap([]);
        var segments = WindowHistoryText.Segments(row, colors);

        Assert.Equal(2, segments.Count);
        Assert.Equal(0.75, segments[0].Fraction, 6);
        Assert.Equal(0.25, segments[1].Fraction, 6);
    }

    [Fact]
    public void NoMineTokensMeansNoSegments()
    {
        var row = HistoryRow(mineTokens: 0, models: [Model("a", 0, 5)]);
        Assert.Empty(WindowHistoryText.Segments(row, new ModelColorMap([])));
    }

    // The equivalence pools only the rows actually shown on screen: a wider
    // pool moves a number the reader has no rows on screen to check.
    [Fact]
    public void EquivalencePoolsOnlyTheShownRows()
    {
        // Three cycles at 20% each with matching span evidence clears
        // WindowEquivalence.MinimumCycles (3) and its MinimumDelta gate.
        var shown = Enumerable.Range(0, 3)
            .Select(i => HistoryRow(mineTokens: 1000, mineCost: 10) with
            {
                Cycle = Cycle((10 + i) * Hour, 20),
            })
            .ToList();

        var row = WindowHistoryText.Equivalence(shown, declared: true);

        Assert.True(row.IsRatio);
    }

    [Fact]
    public void EquivalenceIsUndeclaredWhenNothingHasBeenClassified() =>
        Assert.IsType<WindowEquivalence.Row.Undeclared>(
            WindowHistoryText.Equivalence([HistoryRow()], declared: false));
}

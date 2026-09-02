using TokenBar.App;
using TokenBar.Core;
using TokenBar.Interop;
using Xunit;

namespace TokenBar.Core.Tests;

/// <summary>
/// The Quota lens's seven assembly sites, folded into
/// <see cref="QuotaLensProjection"/> and asserted here for the first time —
/// <c>DashboardView.Quota.cs</c>, where they used to live, is compiled by no
/// test project. Four of these tests pin round 7's findings: one reading of
/// the fetch outcome instead of three (finding 1), the retained-stale-data
/// signal (finding 2), and the January cross-year window (finding 4);
/// finding 3 is a Rust-side fix asserted in <c>window_usage.rs</c>.
/// </summary>
public class QuotaLensProjectionTests
{
    private const long FiveHours = 5 * 3_600;

    public QuotaLensProjectionTests() => Localization.Load("en", AppContext.BaseDirectory);

    // ---- fixtures ---------------------------------------------------------

    private static QuotaHistorySample Sample(
        double usedPercent, long sampledAt, long resetAt, bool active = true, long duration = FiveHours) =>
        new(
            ResetAt: resetAt,
            DurationSeconds: duration,
            DurationSource: QuotaHistoryDurationSource.Provider,
            UsedPercent: usedPercent,
            SampledAt: sampledAt,
            Origin: QuotaHistorySampleOrigin.LiveV3,
            IsActiveGroup: active);

    private static QuotaHistorySeries Series(
        string providerId, string accountScope, string windowKey, params QuotaHistorySample[] samples) =>
        new(providerId, accountScope, windowKey, samples);

    private static WindowMessage Message(long timestampMs, string client, string provider, long tokens, double cost) =>
        new(timestampMs, client, provider, "some-model", tokens, 0, 0, 0, 0, cost, true);

    private static UsageAttribution.Table Confirmed(params UsageAttribution.Record[] records) =>
        new(records, IsWritable: true);

    private static UsagePayload EmptyGraph() =>
        new(
            new UsageMeta("g", "v", new DateRange("2026-01-01", "2026-01-01"),
                PricingMode.BestEffort, CostCoverage.Complete),
            new UsageSummary(0, 0, 0, 0, 0, 0, [], []),
            [],
            []);

    private static UsageWindow Window(string cardId, string label, string? windowKey) =>
        new(
            Label: label,
            UsedPercent: 10,
            RemainingPercent: 90,
            CardId: cardId,
            PaceStatus: windowKey is null
                ? new PaceStatus(UsagePaceState.Unavailable)
                : new PaceStatus(UsagePaceState.Available, WindowKey: windowKey));

    private static AgentUsagePayload Quota(string clientId, params UsageWindow[] windows) =>
        new("2026-01-01T00:00:00Z",
            [new AgentUsageSnapshot(clientId, "source", "2026-01-01T00:00:00Z", windows)]);

    // Two completed (single-sample) cycles, plus a running cycle carrying TWO
    // active samples — WindowEquivalence.LiveRow needs a non-empty
    // [first, last] span to admit a message as "declared" at all before it
    // ever looks at the fetch outcome, so a single active sample (whose span
    // is a single instant) cannot exercise that branch. These tests only
    // assert WHICH branch ran, never a computed ratio.
    private static QuotaHistorySeries TwoCycleSeries(string providerId, string scope, string windowKey) =>
        Series(
            providerId, scope, windowKey,
            Sample(40, 1_500, 2_000, active: false),
            Sample(70, 3_500, 4_000, active: false),
            Sample(10, 5_000, 6_000, active: true),
            Sample(15, 5_900, 6_000, active: true));

    // Inside the running cycle's own sample span (5_000_000..5_900_000ms, per
    // TwoCycleSeries) so WindowEquivalence.LiveRow can see it as evidence.
    private const long InActiveSpanMs = 5_200_000;

    // ---- finding 1: one reading of the fetch outcome, not three -----------

    // Round 7's first finding: the overview path used to gate its equivalence
    // fold on the plain WindowUsageAttempted bool while the live and history
    // cards already switched on WindowUsageOutcome. `quotaHistoryAttempted`
    // is deliberately TRUE here while `windowUsageOutcome` is Failed — the
    // exact combination the boolean gate could not tell apart from a genuine
    // success. If site 1 still read the boolean, this dictionary would come
    // back populated.
    [Fact]
    public void OverviewEquivalencesAreEmptyWhenTheFetchFailedEvenThoughHistoryHasAttempted()
    {
        var history = new[] { TwoCycleSeries("codex", "primary", "weekly.v1") };
        var messages = new[] { Message(1_800, "codex", "openai", 1000, 5.0) };

        var model = QuotaLensProjection.Build(
            history,
            quota: null,
            EmptyGraph(),
            windowUsage: new WindowUsage(messages, 0, 0),
            windowUsageOutcome: WindowEquivalence.FetchOutcome.Failed,
            quotaHistoryAttempted: true,
            Confirmed(new UsageAttribution.Record("codex", "openai", UsageAttribution.State.Assigned("codex"))),
            year: null,
            new QuotaLensProjection.Selection(ClientRegistry.OverviewTab, string.Empty));

        Assert.Empty(model.Overview.Equivalences);
    }

    [Fact]
    public void OverviewEquivalencesArePopulatedOnceTheFetchSucceeds()
    {
        var history = new[] { TwoCycleSeries("codex", "primary", "weekly.v1") };
        var messages = new[] { Message(1_800, "codex", "openai", 1000, 5.0) };

        var model = QuotaLensProjection.Build(
            history,
            quota: null,
            EmptyGraph(),
            windowUsage: new WindowUsage(messages, 0, 0),
            windowUsageOutcome: WindowEquivalence.FetchOutcome.Succeeded,
            quotaHistoryAttempted: true,
            Confirmed(new UsageAttribution.Record("codex", "openai", UsageAttribution.State.Assigned("codex"))),
            year: null,
            new QuotaLensProjection.Selection(ClientRegistry.OverviewTab, string.Empty));

        // QuotaEquivalenceFold.Build inserts one row per series regardless of
        // how much evidence it carries — the row's own shape (Ratio,
        // Unavailable, ...) is QuotaEquivalenceFold's own contract, already
        // asserted in QuotaEquivalenceFoldTests. What this test pins is that
        // the projection actually calls it once the outcome is Succeeded.
        Assert.Single(model.Overview.Equivalences);
    }

    // ---- finding 2: retained-stale data is not a fresh success ------------

    // The three call sites this finding touches, all fed the SAME messages
    // (standing in for WindowUsage retained from an earlier successful fetch)
    // under a Failed outcome: none of them may read those messages as
    // current evidence, because DashboardModel retains stale WindowUsage
    // across a failing refresh (see Snapshot.WindowUsageFetchFailed's own
    // doc comment) and an empty/messages-bearing list under Failed means "we
    // do not know", not "here is the answer".
    [Fact]
    public void FailedOutcomeIsNotReadAsSuccessAtAnyOfTheThreeSitesEvenWithMessagesRetained()
    {
        var history = new[] { TwoCycleSeries("codex", "primary", "weekly.v1") };
        // Stale messages: present, and would otherwise produce a real ratio —
        // proving the branch below is not simply "no messages, so no line".
        var staleMessages = new[]
        {
            Message(InActiveSpanMs, "codex", "openai", 5_000, 25.0),
        };
        var confirmed = Confirmed(new UsageAttribution.Record("codex", "openai", UsageAttribution.State.Assigned("codex")));
        // A live window for "codex" so the client lens resolves an active
        // tab and site 4 (the live card) has something to test at all.
        var quota = Quota("codex", Window("codex|weekly.v1", "Weekly", "weekly.v1"));

        var failed = QuotaLensProjection.Build(
            history, quota, EmptyGraph(),
            windowUsage: new WindowUsage(staleMessages, 0, 0),
            windowUsageOutcome: WindowEquivalence.FetchOutcome.Failed,
            quotaHistoryAttempted: true, confirmed, year: null,
            new QuotaLensProjection.Selection("codex", string.Empty));

        // Site 1 (overview): no key at all, not a row computed from stale data.
        Assert.Empty(failed.Overview.Equivalences);

        // Site 4 (live card): ScanFailed, not a ratio computed from the stale
        // messages that are still sitting on the active cycle's own span.
        Assert.NotNull(failed.Client);
        Assert.IsType<WindowEquivalence.Row.ScanFailed>(failed.Client!.LiveEquivalence);

        // Site 6 (history card): ScanFailed, for the same reason.
        Assert.IsType<WindowEquivalence.Row.ScanFailed>(failed.Client.History.Equivalence);

        // The fresh-success control: the identical inputs, outcome flipped to
        // Succeeded, must NOT report ScanFailed at either of the two
        // per-client sites — the distinction this finding exists to draw.
        var succeeded = QuotaLensProjection.Build(
            history, quota, EmptyGraph(),
            windowUsage: new WindowUsage(staleMessages, 0, 0),
            windowUsageOutcome: WindowEquivalence.FetchOutcome.Succeeded,
            quotaHistoryAttempted: true, confirmed, year: null,
            new QuotaLensProjection.Selection("codex", string.Empty));

        Assert.NotEmpty(succeeded.Overview.Equivalences);
        Assert.IsNotType<WindowEquivalence.Row.ScanFailed>(succeeded.Client!.LiveEquivalence);
        Assert.IsNotType<WindowEquivalence.Row.ScanFailed>(succeeded.Client.History.Equivalence);
    }

    // ---- finding 4: the trend window's own range, not the calendar year ---

    [Fact]
    public void CurrentYearSelectedEarlyInJanuaryIsFlaggedBecauseTheWindowCrossesIntoDecember()
    {
        // The 14-day window ending 2026-01-05 is [2025-12-23, 2026-01-05] —
        // it reaches into the year before, which "2026" selected does not
        // cover. The old check (selectedYear != today's calendar year) missed
        // this exactly because "2026" IS today's calendar year.
        Assert.True(QuotaLensProjection.PastYearSelected("2026-01-05", "2026"));
    }

    [Fact]
    public void CurrentYearSelectedMidYearIsNotFlagged()
    {
        // The window [2026-02-19, 2026-03-05] does not cross a year boundary.
        Assert.False(QuotaLensProjection.PastYearSelected("2026-03-05", "2026"));
    }

    [Fact]
    public void AllTimeSelectionIsNeverFlagged()
    {
        Assert.False(QuotaLensProjection.PastYearSelected("2026-01-05", year: null));
    }

    [Fact]
    public void AGenuinelyPastYearIsStillFlagged()
    {
        // The window is entirely inside 2026, which never overlaps "2024" —
        // the case the old check already handled; still true under the new
        // range-based check.
        Assert.True(QuotaLensProjection.PastYearSelected("2026-03-05", "2024"));
    }

    // ---- the seven sites, generally ---------------------------------------

    [Fact]
    public void OverviewTabBuildsNoClientLens()
    {
        var model = QuotaLensProjection.Build(
            [], quota: null, EmptyGraph(), windowUsage: null,
            WindowEquivalence.FetchOutcome.NotAttempted, quotaHistoryAttempted: false,
            UsageAttribution.Table.Empty, year: null,
            new QuotaLensProjection.Selection(ClientRegistry.OverviewTab, string.Empty));

        Assert.Null(model.Client);
    }

    // Site 2: the owner-keyed lookup (antigravity-cli spends the antigravity
    // subscription) and the persisted tab selection both land in the same
    // place, so a future card cannot read one without the other.
    [Fact]
    public void ClientTabResolvesToItsQuotaOwnerAndHonoursThePersistedTabSelection()
    {
        var weekly = TwoCycleSeries("antigravity", "primary", "weekly.v1");
        var session = TwoCycleSeries("antigravity", "primary", "session.v1");
        var quota = Quota(
            "antigravity",
            Window("antigravity|weekly.v1", "Weekly", "weekly.v1"),
            Window("antigravity|session.v1", "Session", "session.v1"));

        var model = QuotaLensProjection.Build(
            [weekly, session], quota, EmptyGraph(), windowUsage: null,
            WindowEquivalence.FetchOutcome.NotAttempted, quotaHistoryAttempted: true,
            UsageAttribution.Table.Empty, year: null,
            // antigravity-cli, the raw client id — ClientRegistry.QuotaOwner
            // maps it to "antigravity", which is what Tabs() must be called
            // with or the store-side series above never match.
            new QuotaLensProjection.Selection("antigravity-cli", "antigravity|primary|session.v1"));

        Assert.NotNull(model.Client);
        Assert.Equal("antigravity", model.Client!.Owner);
        Assert.Equal(2, model.Client.Tabs.Count);
        Assert.Equal("session.v1", model.Client.Selected?.Id.WindowKey);
    }

    // Site 5: the undated note's own raw part, threaded through rather than
    // read straight off the snapshot in the view.
    [Fact]
    public void UndatedCountIsCarriedFromTheWindowUsagePayload()
    {
        var model = QuotaLensProjection.Build(
            [TwoCycleSeries("codex", "primary", "weekly.v1")], quota: null, EmptyGraph(),
            windowUsage: new WindowUsage([], UndatedCount: 7, 0),
            WindowEquivalence.FetchOutcome.Succeeded, quotaHistoryAttempted: true,
            UsageAttribution.Table.Empty, year: null,
            new QuotaLensProjection.Selection("codex", string.Empty));

        Assert.Equal(7, model.Client!.UndatedCount);
    }

    // Site 3/4: no active cycle selected (a client with only completed
    // history) draws no live equivalence line at all — the same guard the
    // view used to apply by checking WindowCardState == Chart before
    // touching declared/equivalence.
    [Fact]
    public void NoLiveEquivalenceWhenNoTabHasAPlacedActiveCycle()
    {
        var idleOnly = Series("codex", "primary", "weekly.v1", Sample(40, 1_500, 2_000, active: false));
        var quota = Quota("codex", Window("codex|weekly.v1", "Weekly", "weekly.v1"));

        var model = QuotaLensProjection.Build(
            [idleOnly], quota, EmptyGraph(), windowUsage: null,
            WindowEquivalence.FetchOutcome.Succeeded, quotaHistoryAttempted: true,
            UsageAttribution.Table.Empty, year: null,
            new QuotaLensProjection.Selection("codex", string.Empty));

        Assert.NotNull(model.Client!.Selected);
        Assert.Null(model.Client.LiveEquivalence);
    }

    // ---- round 8 finding 2: the collapsed row must show the WHOLE-WINDOW
    // total, not the sample-span-restricted one -----------------------------

    // A single-sample completed cycle: FirstSampleMs == LastSampleMs, so the
    // span QuotaHistoryFold.SpanTotals bounds is empty by construction — the
    // same "one-sample cycle" case WindowEquivalence's own comment on
    // MinimumObservedFraction/MinimumCycles notes computes to exactly zero.
    // A message stamped between the cycle's own StartMs and its first quota
    // sample is inside [EvidenceStartMs, ResetAtMs) — Mine — but strictly
    // before FirstSampleMs, so it is outside the span. If the display row
    // were built from SpanTokens/SpanCost (the round 8 bug) this message
    // would vanish from the collapsed row and its bar scale while still
    // showing up in the row's own expanded model breakdown.
    [Fact]
    public void TheCollapsedHistoryRowShowsTheSameTotalAsItsOwnExpandedModelBreakdown()
    {
        // duration 5h = 18_000s, resetAt 6_000s -> StartMs = -12_000_000.
        // sampledAt 5_000s -> FirstSampleMs = LastSampleMs = 5_000_000.
        var series = Series(
            "codex", "primary", "session.v1",
            Sample(usedPercent: 40, sampledAt: 5_000, resetAt: 6_000, active: false, duration: 18_000));
        var quota = Quota("codex", Window("codex|session.v1", "Session", "session.v1"));
        // 1_000_000ms is inside [StartMs=-12_000_000, ResetAtMs=6_000_000) —
        // Mine — and NOT inside (FirstSampleMs=5_000_000, LastSampleMs] —
        // Span, which is empty here regardless.
        var messages = new[] { Message(1_000_000, "codex", "openai", tokens: 1_000, cost: 5.0) };
        var confirmed = Confirmed(
            new UsageAttribution.Record("codex", "openai", UsageAttribution.State.Assigned("codex")));

        var model = QuotaLensProjection.Build(
            [series], quota, EmptyGraph(),
            windowUsage: new WindowUsage(messages, 0, 0),
            WindowEquivalence.FetchOutcome.Succeeded, quotaHistoryAttempted: true,
            confirmed, year: null,
            new QuotaLensProjection.Selection("codex", string.Empty));

        var displayRow = Assert.Single(model.Client!.History.DisplayRows);
        var storedRow = model.Client.History.ByResetAt[displayRow.ResetAtMs];

        // The fixture actually exercises the Mine-vs-Span gap it claims to.
        Assert.Equal(1_000, storedRow.MineTokens);
        Assert.Equal(5.0, storedRow.MineCost);
        Assert.Equal(0, storedRow.SpanTokens);
        Assert.Equal(0, storedRow.SpanCost);

        // The collapsed row and its own expanded model breakdown must report
        // the same total — the whole-window one, not the empty span.
        var breakdownTokens = storedRow.Models.Sum(m => m.Tokens);
        var breakdownCost = storedRow.Models.Sum(m => m.Cost);
        Assert.Equal(1_000, breakdownTokens);
        Assert.Equal(5.0, breakdownCost);
        Assert.Equal(breakdownTokens, displayRow.Tokens);
        Assert.Equal(breakdownCost, displayRow.Cost);
    }
}

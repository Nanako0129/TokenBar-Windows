using TokenBar.App;
using TokenBar.Core;
using TokenBar.Interop;
using Xunit;

namespace TokenBar.Core.Tests;

// The Session-window card's state choice, its per-client filter, its hatched
// no-sample series and its copy. DashboardView.Quota.cs is WinUI and no test
// project compiles it, so this is where those decisions are asserted.
public class WindowCardTextTests
{
    private const long FiveHours = 5 * 3_600;
    private const long ResetAt = 1_767_330_000;

    public WindowCardTextTests() => Localization.Load("en", AppContext.BaseDirectory);

    private static QuotaHistorySample Sample(
        double usedPercent, long sampledAt, bool active = true, long duration = FiveHours) =>
        new(
            ResetAt: ResetAt,
            DurationSeconds: duration,
            DurationSource: QuotaHistoryDurationSource.Provider,
            UsedPercent: usedPercent,
            SampledAt: sampledAt,
            Origin: QuotaHistorySampleOrigin.LiveV3,
            IsActiveGroup: active);

    private static QuotaHistorySeries Series(
        string clientId, string windowKey, params QuotaHistorySample[] samples) =>
        new(clientId, "primary", windowKey, samples);

    private static WindowMessage Message(
        string client, string provider, string model, long at = 1) =>
        new(at, client, provider, model, 0, 100, 0, 0, 0, 0.5, true);

    // `Tabs` now enumerates the LIVE side — one tab per window the client is
    // currently reporting — rather than the store side, so every fixture that
    // wants a tab has to supply the live window that tab comes from.
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

    // ---- the live-window enumeration -------------------------------------

    // The bug this fix removes: several stored series collapsing onto one
    // live window used to draw one tab PER SERIES, and a series with no
    // live-side match fell back to printing its raw store key. Four live
    // windows — one of them keyless, one of them fed by two stored series
    // that share a windowKey — must yield exactly four tabs, each carrying
    // the provider's own label and none of them a raw key.
    [Fact]
    public void FourLiveWindowsYieldFourTabsWithDistinctLabelsEvenWhenSeriesShareAWindowKey()
    {
        var quota = Quota(
            "codex",
            Window("codex|session.v1", "Session", "session.v1"),
            Window("codex|weekly.v1", "Weekly", "weekly.v1"),
            Window("codex|fable.v1", "Fable only", "weekly_scoped.fable.v1"),
            Window("codex|additional.d62616d2.primary.v1", "Extra usage", windowKey: null));

        var tabs = WindowCardText.Tabs(
            [
                Series("codex", "session.v1", Sample(40, ResetAt - 600)),
                // A second stored series landing on the SAME windowKey — a
                // rename or a re-carded provider revision. Must not draw a
                // second tab for the one live window it maps to.
                Series("codex", "session.v1", Sample(41, ResetAt - 600)),
                Series("codex", "weekly_scoped.fable.v1", Sample(12, ResetAt - 600)),
            ],
            quota,
            clientId: "codex");

        Assert.Equal(4, tabs.Count);
        Assert.Equal(
            new[] { "Session", "Weekly", "Fable only", "Extra usage" }.OrderBy(l => l),
            tabs.Select(tab => tab.Label).OrderBy(l => l));
        // No tab label is ever a raw store key — neither the dotted window
        // key nor the hashed card id it used to fall back to.
        Assert.All(tabs, tab => Assert.False(string.IsNullOrWhiteSpace(tab.Label)));
        Assert.DoesNotContain(tabs, tab => tab.Label == "weekly_scoped.fable.v1");
        Assert.DoesNotContain(tabs, tab => tab.Label!.Contains("d62616d2"));
    }

    // A live window the store has no series for still gets a tab — it just
    // has no history to draw, which is `NoQuotaHistory`, not an absent tab.
    [Fact]
    public void ALiveWindowWithNoStoredSeriesStillGetsATab()
    {
        var quota = Quota("codex", Window("codex|weekly.v1", "Weekly", "weekly.v1"));

        var tabs = WindowCardText.Tabs([], quota, "codex");

        Assert.Single(tabs);
        Assert.Equal("Weekly", tabs[0].Label);
        Assert.Equal(WindowCardState.NoQuotaHistory, WindowCardText.State(tabs[0], attempted: true));
        Assert.Equal(WindowCardState.Loading, WindowCardText.State(tabs[0], attempted: false));
    }

    // A stored series with no live window (the provider stopped reporting
    // it) produces no tab: macOS cannot show a window its own live payload
    // no longer offers either.
    [Fact]
    public void AStoredSeriesWithNoLiveWindowProducesNoTab()
    {
        var quota = Quota("codex", Window("codex|session.v1", "Session", "session.v1"));

        var tabs = WindowCardText.Tabs(
            [
                Series("codex", "session.v1", Sample(40, ResetAt - 600)),
                Series("codex", "retired.window.v1", Sample(99, ResetAt - 600)),
            ],
            quota,
            "codex");

        Assert.Single(tabs);
        Assert.Equal("Session", tabs[0].Label);
    }

    // The window key is the store's own, and ProviderId is already a registered
    // CLIENT id (QuotaEquivalenceFold states this and relies on it), so the
    // filter is that equality and nothing else.
    [Fact]
    public void TabsSelectOnlyTheSelectedClientsWindows()
    {
        var tabs = WindowCardText.Tabs(
            [
                Series("claude", "session.v1", Sample(40, ResetAt - 600)),
                Series("claude", "weekly.v1", Sample(12, ResetAt - 600)),
                Series("codex", "main.weekly.v1", Sample(70, ResetAt - 600)),
            ],
            Quota(
                "claude",
                Window("claude|session.v1", "Session", "session.v1"),
                Window("claude|weekly.v1", "Weekly", "weekly.v1")),
            clientId: "claude");

        Assert.Equal(2, tabs.Count);
        Assert.All(tabs, tab => Assert.Equal("claude", tab.Id.ProviderId));
    }

    // A running window leads: on a client with a session and a weekly window
    // the weekly one is routinely the idle half, and it must not be what the
    // card opens on.
    [Fact]
    public void ARunningWindowLeadsTheTabs()
    {
        var tabs = WindowCardText.Tabs(
            [
                Series("claude", "weekly.v1", Sample(12, ResetAt - 600, active: false)),
                Series("claude", "session.v1", Sample(40, ResetAt - 600)),
            ],
            Quota(
                "claude",
                Window("claude|weekly.v1", "Weekly", "weekly.v1"),
                Window("claude|session.v1", "Session", "session.v1")),
            clientId: "claude");

        Assert.Equal("Session", tabs[0].Label);
    }

    // Bars under the quota line are a claim about which subscription paid, so
    // only the user's own confirmed classification admits a message.
    [Fact]
    public void MineAdmitsOnlyMessagesTheUserAssignedToThisClient()
    {
        UsageAttribution.Record[] confirmed =
        [
            new("claude-code", "anthropic", null,
                new UsageAttribution.State(UsageAttribution.StateKind.Assigned, "claude")),
            new("codex-cli", "openai", null,
                new UsageAttribution.State(UsageAttribution.StateKind.Assigned, "codex")),
        ];

        var mine = WindowCardText.Mine(
            [
                Message("claude-code", "anthropic", "sonnet"),
                Message("codex-cli", "openai", "gpt-5"),
                Message("other", "other", "model"),
            ],
            "claude",
            confirmed);

        Assert.Single(mine);
        Assert.Equal("claude-code", mine[0].Client);
    }

    [Fact]
    public void MineAdmitsNothingWhileNothingIsDeclared() =>
        Assert.Empty(WindowCardText.Mine(
            [Message("claude-code", "anthropic", "sonnet")], "claude", []));

    // Round-3 P1: antigravity-cli spends the "antigravity" subscription
    // (ClientRegistry.QuotaOwner), so `Tabs` and `Mine` — every
    // subscription-facing lookup on the per-client lens — must be keyed by
    // that owner, not the raw client id, or the whole card renders empty for
    // that client even though it consumes the subscription.
    [Fact]
    public void TabsAndMineResolveAgainstTheOwnerForAClientWhoseOwnerDiffersFromItsId()
    {
        var owner = ClientRegistry.QuotaOwner("antigravity-cli");
        Assert.Equal("antigravity", owner);

        var quota = Quota(owner, Window("antigravity|session.v1", "Session", "session.v1"));
        var byOwner = WindowCardText.Tabs(
            [Series(owner, "session.v1", Sample(40, ResetAt - 600))], quota, owner);
        Assert.Single(byOwner);
        Assert.Equal("Session", byOwner[0].Label);

        // The bug: the raw id finds neither the live agent nor the stored
        // series, so the tab list — and with it the whole card — is empty.
        var byRawId = WindowCardText.Tabs(
            [Series(owner, "session.v1", Sample(40, ResetAt - 600))], quota, "antigravity-cli");
        Assert.Empty(byRawId);

        UsageAttribution.Record[] confirmed =
        [
            new("antigravity-cli", "antigravity", null,
                new UsageAttribution.State(UsageAttribution.StateKind.Assigned, owner)),
        ];
        var messages = new[] { Message("antigravity-cli", "antigravity", "model") };
        Assert.Single(WindowCardText.Mine(messages, owner, confirmed));
        Assert.Empty(WindowCardText.Mine(messages, "antigravity-cli", confirmed));
    }

    // A client whose owner equals its own id must behave identically either
    // way — the fix must not introduce a second id space for clients that
    // never had one.
    [Fact]
    public void TabsAndMineAreUnaffectedWhenTheOwnerEqualsTheRawClientId()
    {
        var owner = ClientRegistry.QuotaOwner("codex");
        Assert.Equal("codex", owner);

        var quota = Quota("codex", Window("codex|session.v1", "Session", "session.v1"));
        var byOwner = WindowCardText.Tabs(
            [Series("codex", "session.v1", Sample(40, ResetAt - 600))], quota, owner);
        var byRawId = WindowCardText.Tabs(
            [Series("codex", "session.v1", Sample(40, ResetAt - 600))], quota, "codex");
        Assert.Single(byOwner);
        Assert.Single(byRawId);
        Assert.Equal(byOwner[0].Label, byRawId[0].Label);
    }

    // ---- the no-sample series ------------------------------------------

    // "無樣本" is a rendered series, not an absence. The stretch with no reading
    // is produced as its own region so the chart can DRAW it: a gap would say
    // "nothing happened here" while the fact is "we did not look here".
    [Fact]
    public void TheNoSampleStretchesAreProducedAsRegionsRatherThanLeftAsGaps()
    {
        var start = 1_000_000L;
        var end = start + (FiveHours * 1000);
        var chart = WindowCardGeometry.Chart(
            start, end, start + 3_600_000,
            [new QuotaSample(start + 1_200_000, 10), new QuotaSample(start + 2_400_000, 20)],
            [],
            QuotaMetric.Used);

        var regions = WindowCardText.NoSampleRegions(chart);

        // Two: the stretch before the first reading, and the future.
        Assert.Equal(2, regions.Count);
        Assert.Equal(0, regions[0].From);
        Assert.Equal(chart.FirstSampleX, regions[0].To);
        Assert.Equal(chart.NowX, regions[1].From);
        Assert.Equal(1, regions[1].To);
    }

    // A window sampled from its first millisecond has no leading region, and a
    // zero-width hatch is a rendering artefact rather than a statement.
    [Fact]
    public void AZeroWidthNoSampleStretchIsNotDrawn()
    {
        var start = 1_000_000L;
        var end = start + (FiveHours * 1000);
        var chart = WindowCardGeometry.Chart(
            start, end, end,
            [new QuotaSample(start, 5), new QuotaSample(end, 60)],
            [],
            QuotaMetric.Used);

        Assert.Empty(WindowCardText.NoSampleRegions(chart));
    }

    // No reading at all leaves the WHOLE box hatched, not half of it: the app
    // was not running to sample any of this window.
    [Fact]
    public void AWindowWithNoReadingIsHatchedEndToEnd()
    {
        var start = 1_000_000L;
        var end = start + (FiveHours * 1000);
        var chart = WindowCardGeometry.Chart(start, end, start + 600_000, [], [], QuotaMetric.Used);

        // Two abutting regions rather than one — the leading stretch runs up to
        // `now`, where the future takes over — and between them they cover the
        // whole box with no gap. The count is an implementation detail; the
        // coverage is the claim.
        var regions = WindowCardText.NoSampleRegions(chart);
        Assert.Equal(0, regions[0].From);
        Assert.Equal(1, regions[^1].To);
        for (var i = 1; i < regions.Count; i++)
        {
            Assert.Equal(regions[i - 1].To, regions[i].From);
        }
    }

    // ---- states ---------------------------------------------------------

    [Fact]
    public void EveryStateIsDistinct()
    {
        var running = WindowCardText.Tabs(
            [Series("claude", "session.v1", Sample(40, ResetAt - 600))],
            Quota("claude", Window("claude|session.v1", "Session", "session.v1")),
            "claude")[0];
        var idle = WindowCardText.Tabs(
            [Series("claude", "session.v1", Sample(40, ResetAt - 600, active: false))],
            Quota("claude", Window("claude|session.v1", "Session", "session.v1")),
            "claude")[0];
        var unplaceable = WindowCardText.Tabs(
            [Series("claude", "session.v1", Sample(40, ResetAt - 600, duration: 0))],
            Quota("claude", Window("claude|session.v1", "Session", "session.v1")),
            "claude")[0];
        var noHistory = WindowCardText.Tabs(
            [], Quota("claude", Window("claude|session.v1", "Session", "session.v1")), "claude")[0];

        Assert.Equal(WindowCardState.Chart, WindowCardText.State(running, attempted: true));
        Assert.Equal(WindowCardState.Idle, WindowCardText.State(idle, attempted: true));
        Assert.Equal(
            WindowCardState.Unplaceable, WindowCardText.State(unplaceable, attempted: true));
        // The pair that differs only in `attempted`, which is the whole point:
        // a lazy lens fetches on first visit, so no tab before the read has
        // settled is the first paint of every cold start.
        Assert.Equal(WindowCardState.NoQuotaHistory, WindowCardText.State(null, attempted: true));
        Assert.Equal(WindowCardState.Loading, WindowCardText.State(null, attempted: false));
        // A live window with a tab but no matched series reads the same as no
        // tab at all — `HasHistory` is what carries the distinction from
        // `Idle`, not the tab's mere presence.
        Assert.Equal(WindowCardState.NoQuotaHistory, WindowCardText.State(noHistory, attempted: true));
    }

    // Each empty state has its own sentence. "The window ended" and "the
    // provider gave no reset time" are different facts, and one shared line
    // would report the second as the first — a claim that the user stopped
    // working.
    [Fact]
    public void EveryEmptyStateSaysSomethingDifferent()
    {
        var bodies = new[]
        {
            WindowCardState.Loading,
            WindowCardState.NoQuotaHistory,
            WindowCardState.Idle,
            WindowCardState.Unplaceable,
        }.Select(WindowCardText.EmptyBody).ToList();

        Assert.Equal(bodies.Count, bodies.Distinct().Count());
    }

    [Fact]
    public void TheHeadlineReadsTheLastSampleInTheWindowAndNamesItsDirection()
    {
        var start = 1_000_000L;
        var end = start + (FiveHours * 1000);
        QuotaSample[] samples = [new(start + 600_000, 10), new(start + 1_200_000, 42)];

        var used = WindowCardText.Headline(
            WindowCardGeometry.Chart(
                start, end, start + 1_800_000, samples, [], QuotaMetric.Used),
            QuotaMetric.Used);
        Assert.Equal("42%", used.Percent);
        Assert.Equal("used", used.Caption);

        var remaining = WindowCardText.Headline(
            WindowCardGeometry.Chart(
                start, end, start + 1_800_000, samples, [], QuotaMetric.Remaining),
            QuotaMetric.Remaining);
        Assert.Equal("58%", remaining.Percent);
        Assert.Equal("remaining", remaining.Caption);
    }

    // A window with a placed axis and no reading in it still draws — and says
    // why the big number is missing rather than printing a zero nobody
    // measured.
    [Fact]
    public void NoReadingInTheWindowHasNoPercentAndSaysSo()
    {
        var start = 1_000_000L;
        var chart = WindowCardGeometry.Chart(
            start, start + (FiveHours * 1000), start + 600_000, [], [], QuotaMetric.Used);

        var headline = WindowCardText.Headline(chart, QuotaMetric.Used);
        Assert.Null(headline.Percent);
        Assert.Equal("No quota reading in this window", headline.Caption);
    }

    // A zone in the hatch has no closing reading, and must say that rather than
    // show nothing.
    [Fact]
    public void AZoneWithNoClosingReadingSaysThereWasNoReading()
    {
        var start = 1_000_000L;
        var zones = WindowCardGeometry.Zones(
            start, start + (FiveHours * 1000), start + 1_800_000,
            [new QuotaSample(start + 600_000, 10)]);

        Assert.Equal("Quota 10% used", WindowCardText.ZoneQuota(zones[0], QuotaMetric.Used));
        Assert.Equal(
            "No quota reading in this interval",
            WindowCardText.ZoneQuota(zones[^1], QuotaMetric.Used));
        // Absent rather than zero: an interval nobody measured is not an
        // interval that consumed nothing.
        Assert.Null(WindowCardText.ZoneConsumed(zones[0], QuotaMetric.Used));
    }

    [Fact]
    public void AZoneWithNoMessagesSaysSoRatherThanPrintingZeroTokens()
    {
        var start = 1_000_000L;
        var zone = WindowCardGeometry.Zones(
            start, start + (FiveHours * 1000), start + 600_000, [])[0];

        var empty = WindowCardText.ZoneUsage(WindowCardText.InZone([], zone));
        Assert.Equal("No usage in this interval", empty.Empty);
        Assert.Null(empty.Tokens);

        var used = WindowCardText.ZoneUsage(
            WindowCardText.InZone([Message("claude-code", "anthropic", "sonnet", start)], zone));
        Assert.Null(used.Empty);
        Assert.Equal("100 tokens", used.Tokens);
    }

    // Zone 0 owns its own lower bound, matching the bar built from the same
    // message — otherwise the bar and the tooltip explaining it disagree about
    // whether the message is in the interval.
    [Fact]
    public void TheTooltipAndTheBarAgreeAboutAMessageOnTheWindowStart()
    {
        var start = 1_000_000L;
        var zones = WindowCardGeometry.Zones(
            start, start + (FiveHours * 1000), start + 1_800_000,
            [new QuotaSample(start + 600_000, 10)]);

        Assert.Single(WindowCardText.InZone(
            [Message("claude-code", "anthropic", "sonnet", start)], zones[0]));
        Assert.Empty(WindowCardText.InZone(
            [Message("claude-code", "anthropic", "sonnet", start)], zones[1]));
    }

    [Fact]
    public void UndatedRowsAreStatedRatherThanSwallowed()
    {
        Assert.Null(WindowCardText.UndatedNote(0));
        Assert.Contains("3", WindowCardText.UndatedNote(3));
    }

    // ---- i18n ------------------------------------------------------------
    //
    // Against the *shipped* strings-zh-Hant.json, for the reason QuotaLensText's
    // own i18n test states: the failure guarded against is a Localized() call
    // site whose key was never added to that file, and each card shows one state
    // at a time, so only driving every branch can prove the entries exist.
    [Fact]
    public void EveryStringTheCardCanShowHasATableEntry()
    {
        var tab = WindowCardText.Tabs(
            [Series("claude", "session.v1", Sample(40, ResetAt - 600))],
            Quota("claude", Window("claude|session.v1", "Session", "session.v1")),
            "claude")[0];
        var start = 1_000_000L;
        var chart = WindowCardGeometry.Chart(
            start, start + (FiveHours * 1000), start + 1_800_000,
            [new QuotaSample(start + 600_000, 10)], [], QuotaMetric.Used);
        var zones = chart.Hits;

        Func<string?>[] surfaces =
        [
            () => WindowCardText.Title(null),
            () => WindowCardText.Title(tab),
            () => WindowCardText.Subtitle(WindowCardState.Loading, null, DateTimeOffset.Now),
            () => WindowCardText.Subtitle(WindowCardState.NoQuotaHistory, null, DateTimeOffset.Now),
            () => WindowCardText.Subtitle(WindowCardState.Idle, null, DateTimeOffset.Now),
            () => WindowCardText.Subtitle(WindowCardState.Unplaceable, null, DateTimeOffset.Now),
            () => WindowCardText.EmptyBody(WindowCardState.Loading),
            () => WindowCardText.EmptyBody(WindowCardState.NoQuotaHistory),
            () => WindowCardText.EmptyBody(WindowCardState.Idle),
            () => WindowCardText.EmptyBody(WindowCardState.Unplaceable),
            () => WindowCardText.Headline(chart, QuotaMetric.Used).Caption,
            () => WindowCardText.Headline(chart, QuotaMetric.Remaining).Caption,
            () => WindowCardText.Headline(
                WindowCardGeometry.Chart(
                    start, start + (FiveHours * 1000), start + 600_000, [], [], QuotaMetric.Used),
                QuotaMetric.Used).Caption,
            WindowCardText.QuotaKey,
            WindowCardText.UsageKey,
            WindowCardText.NoSampleKey,
            () => WindowCardText.Readings(4),
            () => WindowCardText.MetricLabel(QuotaMetric.Used),
            () => WindowCardText.MetricLabel(QuotaMetric.Remaining),
            () => WindowCardText.UndatedNote(3),
            () => WindowCardText.ZoneQuota(zones[^1], QuotaMetric.Used),
            () => WindowCardText.ZoneQuota(zones[0], QuotaMetric.Used),
            () => WindowCardText.ZoneConsumed(
                new HitZone(1, 0, 1, 0, 0.1,
                    new QuotaSample(1, 30), new QuotaSample(0, 10)), QuotaMetric.Used),
            () => WindowCardText.ZoneUsage([]).Empty,
        ];

        var english = surfaces.Select(surface => surface()).ToList();
        Localization.Load("zh-Hant", AppContext.BaseDirectory);
        try
        {
            for (var i = 0; i < surfaces.Length; i++)
            {
                Assert.NotEqual(english[i], surfaces[i]());
            }

            // Deliberately outside the loop above: the shipped zh-Hant value for
            // "%@ tokens" is "{0} tokens" — the unit is not translated — so an
            // inequality assertion would fail on a correct entry. Asserted as a
            // lookup that RESOLVES instead, which is the property that matters.
            Assert.Equal("100 tokens", WindowCardText.ZoneUsage(
                [Message("claude-code", "anthropic", "sonnet", 5)]).Tokens);
        }
        finally
        {
            Localization.Load("en", AppContext.BaseDirectory);
        }
    }
}

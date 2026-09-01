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

    // ---- the per-client filter -----------------------------------------

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
            quota: null,
            clientId: "claude");

        Assert.Equal(2, tabs.Count);
        Assert.All(tabs, tab => Assert.Equal("claude", tab.Id.ProviderId));
        Assert.Equal(
            ["session.v1", "weekly.v1"],
            tabs.Select(tab => tab.Id.WindowKey).OrderBy(key => key));
    }

    // Two accounts of one client hold the same window key, and the identity
    // keeps them apart — a filter that dropped the scope would fold two
    // subscriptions' curves into one tab.
    [Fact]
    public void TabsKeepTwoScopesOfOneWindowApart()
    {
        var tabs = WindowCardText.Tabs(
            [
                new QuotaHistorySeries("claude", "a", "session.v1", [Sample(40, ResetAt - 600)]),
                new QuotaHistorySeries("claude", "b", "session.v1", [Sample(10, ResetAt - 600)]),
            ],
            quota: null,
            clientId: "claude");

        Assert.Equal(2, tabs.Count);
        Assert.Equal(["a", "b"], tabs.Select(tab => tab.Id.AccountScope).OrderBy(s => s));
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
            quota: null,
            clientId: "claude");

        Assert.Equal("session.v1", tabs[0].Id.WindowKey);
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
            [Series("claude", "session.v1", Sample(40, ResetAt - 600))], null, "claude")[0];
        var idle = WindowCardText.Tabs(
            [Series("claude", "session.v1", Sample(40, ResetAt - 600, active: false))],
            null, "claude")[0];
        var unplaceable = WindowCardText.Tabs(
            [Series("claude", "session.v1", Sample(40, ResetAt - 600, duration: 0))],
            null, "claude")[0];

        Assert.Equal(WindowCardState.Chart, WindowCardText.State(running, attempted: true));
        Assert.Equal(WindowCardState.Idle, WindowCardText.State(idle, attempted: true));
        Assert.Equal(
            WindowCardState.Unplaceable, WindowCardText.State(unplaceable, attempted: true));
        // The pair that differs only in `attempted`, which is the whole point:
        // a lazy lens fetches on first visit, so no tab before the read has
        // settled is the first paint of every cold start.
        Assert.Equal(WindowCardState.NoQuotaHistory, WindowCardText.State(null, attempted: true));
        Assert.Equal(WindowCardState.Loading, WindowCardText.State(null, attempted: false));
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
            [Series("claude", "session.v1", Sample(40, ResetAt - 600))], null, "claude")[0];
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

    // ---- tab-label collisions -------------------------------------------
    //
    // Found on the user's own screen: a Claude tab row showing 工作階段 three
    // times. The identity is the store's triple, but the label joins on
    // (clientId, windowKey), so two accounts of one client resolve to one
    // label. Tolerable on the strip card, where the rows sit side by side;
    // unusable on sub-tabs, where one has to be picked.

    private static WindowCardTab Tab(string scope, string window, string? label) =>
        new(new QuotaWindowIdentity("claude", scope, window), label, null);

    [Fact]
    public void TabsSharingALabelAcrossAccountsAreQualifiedByScope()
    {
        var tabs = WindowCardText.Disambiguate(
            [Tab("acct-a", "session.v1", "Session"), Tab("acct-b", "session.v1", "Session")]);

        Assert.Equal(["Session · acct-a", "Session · acct-b"], tabs.Select(t => t.Label));
    }

    // The first fix shipped the raw value and it was wrong on screen: a
    // 43-character account scope pushed the card's own title out of view.
    [Fact]
    public void AQualifierIsShortEnoughToSitOnATab()
    {
        var scope = "7M08lffPHce2VRneAFYQ-TZ35jRfP7AFM7SuWxoff2s";
        var tabs = WindowCardText.Disambiguate(
            [Tab(scope, "session.v1", "Session"), Tab("other-scope", "session.v1", "Session")]);

        Assert.All(tabs, tab => Assert.True(tab.Label!.Length <= "Session · ".Length + 10));
        Assert.StartsWith("Session · 7M08lffPHc", tabs[0].Label);
    }

    [Fact]
    public void AQualifierKeepsTheWordBeforeTheHashWhenThereIsOne()
    {
        Assert.Equal("additional", WindowCardText.Qualifier("additional.d62616d234d82d5e7e4593f3112e1"));
        Assert.Equal("session", WindowCardText.Qualifier("session.v1"));
    }

    [Fact]
    public void TabsSharingALabelWithinOneAccountAreQualifiedByWindowKey()
    {
        var tabs = WindowCardText.Disambiguate(
            [Tab("acct-a", "session.v1", "Session"), Tab("acct-a", "fable.v1", "Session")]);

        Assert.Equal(["Session · session", "Session · fable"], tabs.Select(t => t.Label));
    }

    [Fact]
    public void ALabelNothingElseSharesIsLeftAlone()
    {
        var tabs = WindowCardText.Disambiguate(
            [Tab("acct-a", "session.v1", "Session"), Tab("acct-a", "weekly.v1", "Weekly")]);

        Assert.Equal(["Session", "Weekly"], tabs.Select(t => t.Label));
    }

    [Fact]
    public void AnUnjoinedLabelStaysNullSoTheKeyFallbackStillApplies()
    {
        var tabs = WindowCardText.Disambiguate(
            [Tab("acct-a", "session.v1", null), Tab("acct-b", "session.v1", null)]);

        Assert.All(tabs, tab => Assert.Null(tab.Label));
    }
}

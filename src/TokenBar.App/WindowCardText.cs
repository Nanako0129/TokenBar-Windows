using TokenBar.Core;
using TokenBar.Interop;

namespace TokenBar.App;

/// <summary>Which of the Session-window card's states applies. Ported from
/// <c>WindowUsageCard.swift</c>'s five-case <c>WindowCardState</c>, minus the
/// two-stage split macOS needs for a scan that lands after the quota half:
/// Windows fetches both lanes through <c>DashboardModel</c>'s snapshot, so a
/// chart with no messages yet simply draws no bars.</summary>
public enum WindowCardState
{
    /// <summary>The persisted-curve read has not settled. Distinct from having
    /// no history, and it is the first paint of every cold start.</summary>
    Loading,

    /// <summary>Nothing recorded for this window at all, so there is no line to
    /// draw.</summary>
    NoQuotaHistory,

    /// <summary>The last window ended and nothing has been used since.</summary>
    Idle,

    /// <summary>A window IS running, and the subscription reported no usable
    /// reset time to place it with. Not <see cref="Idle"/>: that one says the
    /// user stopped working, and this one says the provider stopped
    /// answering.</summary>
    Unplaceable,

    Chart,
}

/// <summary>One sub-tab of the Session-window card: a window of the selected
/// client, and the cycle running inside it.</summary>
public sealed record WindowCardTab(
    QuotaWindowIdentity Id, string? Label, QuotaActiveCycle? Active);

/// <summary>
/// Every state choice and every string on the Session-window card (port of
/// <c>WindowUsageCard.swift</c>; the WinUI layout lives in
/// <c>DashboardView.Quota.cs</c>).
/// <para>
/// Pulled into <c>TokenBar.Core.Tests</c> via &lt;Compile Include&gt; for the
/// same reason as <see cref="QuotaLensText"/> and
/// <see cref="WindowEquivalenceText"/>: <c>DashboardView.Quota.cs</c> is
/// compiled by no test project, so a branch decided there is untested by
/// construction.
/// </para>
/// </summary>
public static class WindowCardText
{
    /// <summary>The used/remaining direction is one preference shared with the
    /// Agent-limits card, the same <c>@AppStorage("tokenbar.limits.asUsed")</c>
    /// macOS reads on both. Two toggles over two keys would let one card count
    /// up while the card below it counted down.</summary>
    public const string AsUsedKey = "tokenbar.limits.asUsed";

    /// <summary>Which window of this client the card is showing. Its own key,
    /// not the heatmap's: those are different lists (the heatmap drops windows
    /// with no movement) and one stored value would name a window the other
    /// picker does not offer.</summary>
    public const string TabKey = "tokenbar.windowcard.window";

    /// <summary>
    /// The windows of one client, each with its running cycle.
    /// <para>
    /// <see cref="QuotaHistorySeries.ProviderId"/> is — despite the field name
    /// inherited from the wire — already a registered CLIENT id, the
    /// quota-tracked subscription owner, which is what
    /// <see cref="QuotaEquivalenceFold"/> already relies on. So the per-client
    /// filter is that equality and nothing else; no join table, and no
    /// second id space to drift.
    /// </para>
    /// </summary>
    public static IReadOnlyList<WindowCardTab> Tabs(
        IReadOnlyList<QuotaHistorySeries>? history,
        AgentUsagePayload? quota,
        string clientId)
    {
        var labels = new Dictionary<(string Client, string Window), string>();
        foreach (var agent in quota?.Agents ?? [])
        {
            foreach (var window in agent.UniqueCardWindows)
            {
                if (window.PaceStatus.WindowKey is { } key)
                {
                    labels.TryAdd((agent.ClientId, key), window.Label);
                }
            }
        }

        var tabs = new List<WindowCardTab>();
        foreach (var series in history ?? [])
        {
            if (series.ProviderId != clientId)
            {
                continue;
            }

            tabs.Add(new WindowCardTab(
                new QuotaWindowIdentity(series.ProviderId, series.AccountScope, series.WindowKey),
                labels.GetValueOrDefault((series.ProviderId, series.WindowKey)),
                QuotaHistoryFold.Active(series.Samples)));
        }

        // A running window leads: it is the one the card exists to draw, and on
        // a client with a session and a weekly window the weekly one is
        // routinely the idle half.
        return Disambiguate([.. tabs.OrderByDescending(tab => tab.Active is not null)]);
    }

    /// <summary>Make every tab label distinct within one client.
    ///
    /// <para>Two windows can arrive with the same label. The identity is the
    /// store's triple, but the label is joined on <c>(clientId, windowKey)</c>
    /// alone — 3b's contract, because the live payload carries no account
    /// scope — so two accounts of one client resolve to one label. The provider
    /// can also name two different windows alike.</para>
    ///
    /// <para>On the strip card two rows that read alike are tolerable: they sit
    /// side by side and behave correctly. **On a row of sub-tabs they are not**,
    /// because the user has to pick one and nothing on screen says which. That
    /// distinction is why this exists here and not there — the same data, made
    /// unusable by a different control.</para>
    ///
    /// <para>Qualified by the first field that actually differs: the account
    /// scope when the collision spans accounts, the window key when it does
    /// not. The scope is an opaque hash and reads poorly; it is a stand-in
    /// until Windows has the account label macOS shows (<c>.claude-work</c>),
    /// and it is still the honest answer, because the alternative is three
    /// controls a user cannot tell apart.</para></summary>
    internal static IReadOnlyList<WindowCardTab> Disambiguate(
        IReadOnlyList<WindowCardTab> tabs) =>
        [.. tabs.Select(tab =>
        {
            var label = tab.Label;
            if (string.IsNullOrWhiteSpace(label))
            {
                return tab;
            }

            var clash = tabs.Where(other =>
                string.Equals(other.Label, label, StringComparison.Ordinal)).ToList();
            if (clash.Count < 2)
            {
                return tab;
            }

            var scopesDiffer = clash
                .Select(other => other.Id.AccountScope)
                .Distinct(StringComparer.Ordinal).Count() > 1;
            var qualifier = scopesDiffer ? tab.Id.AccountScope : tab.Id.WindowKey;
            return tab with { Label = $"{label} · {qualifier}" };
        })];

    /// <summary>Messages this window's own subscription is answerable for.
    /// Same rule as <see cref="QuotaEquivalenceFold.Cycles"/>: a message is
    /// this window's evidence precisely when the user's confirmed
    /// classification resolves it to the window's own client id. An
    /// unclassified machine therefore draws no bars — deliberately, because
    /// bars under this line are a claim about which subscription paid.</summary>
    public static IReadOnlyList<WindowMessage> Mine(
        IReadOnlyList<WindowMessage> messages,
        string clientId,
        IReadOnlyList<UsageAttribution.Record> confirmed) =>
        [.. messages.Where(message =>
        {
            var state = UsageAttribution.Resolve(
                message.Client, message.ProviderId, message.ModelId, confirmed);
            return state.Kind == UsageAttribution.StateKind.Assigned
                && state.Target == clientId;
        })];

    public static WindowCardState State(WindowCardTab? tab, bool attempted)
    {
        if (tab is null)
        {
            return attempted ? WindowCardState.NoQuotaHistory : WindowCardState.Loading;
        }

        return tab.Active switch
        {
            null => WindowCardState.Idle,
            { IsPlaced: false } => WindowCardState.Unplaceable,
            _ => WindowCardState.Chart,
        };
    }

    public static string Title(WindowCardTab? tab) =>
        tab is null
            ? "Session window".Localized()
            : "{0} window".Localized(
                string.IsNullOrWhiteSpace(tab.Label) ? tab.Id.WindowKey : tab.Label!.Localized());

    /// <summary>The line under the title. Every state names itself, so the
    /// subtitle can never say "waiting" while the body says it gave up.</summary>
    public static string Subtitle(WindowCardState state, WindowCardTab? tab, DateTimeOffset now) =>
        state switch
        {
            WindowCardState.Loading => "Waiting for quota".Localized(),
            WindowCardState.NoQuotaHistory => "No quota history".Localized(),
            WindowCardState.Idle => "No window running".Localized(),
            WindowCardState.Unplaceable => "Window unavailable".Localized(),
            _ => "Resets in {0}".Localized(UsagePace.DurationText(
                Math.Max(0, (tab!.Active!.ResetAtMs!.Value / 1000.0) - now.ToUnixTimeSeconds()))),
        };

    /// <summary>The body copy for every state that draws no chart. Each one is
    /// its own sentence: "no history at all", "the window ended", and "the
    /// provider gave no reset time" are three different facts, and one shared
    /// line would report two of them as the third.</summary>
    public static string EmptyBody(WindowCardState state) => state switch
    {
        WindowCardState.Loading => "Waiting for quota…".Localized(),
        WindowCardState.NoQuotaHistory =>
            "This window has no recorded quota history, so there is no line to draw.".Localized(),
        WindowCardState.Idle =>
            "The last window ended and nothing has been used since — no window is running."
                .Localized(),
        _ => "This subscription did not report a usable reset time, so the window cannot be placed."
            .Localized(),
    };

    /// <summary>The big number, or the reason there is none. Read off the last
    /// sample INSIDE the window rather than off the live payload, so it agrees
    /// with the dot the curve ends on.</summary>
    public static (string? Percent, string Caption) Headline(
        ChartGeometry geometry, QuotaMetric metric)
    {
        if (geometry.SamplePoints.Count == 0)
        {
            return (null, "No quota reading in this window".Localized());
        }

        var latest = geometry.SamplePoints[^1].Y;
        return (
            ((int)Math.Round(latest, MidpointRounding.AwayFromZero))
                .ToString(System.Globalization.CultureInfo.CurrentCulture) + "%",
            (metric == QuotaMetric.Used ? "used" : "remaining").Localized());
    }

    /// <summary>
    /// The stretches with no quota sample, as a series the chart DRAWS rather
    /// than as the space it leaves between two things it drew.
    /// <para>
    /// Hatch means one thing: no quota reading here, so no line. Both the
    /// pre-sampling stretch and the future qualify, so both get one weight;
    /// whether usage data exists is answered by the bars drawn over it. The
    /// distinction the region carries is "we did not look here", which is not
    /// "nothing happened here" — a gap says the second while meaning the
    /// first.
    /// </para>
    /// <para>Empty pairs are dropped: a window sampled from its first
    /// millisecond has no leading region, and a zero-width hatch is a
    /// rendering artefact rather than a statement.</para>
    /// </summary>
    public static IReadOnlyList<(double From, double To)> NoSampleRegions(ChartGeometry geometry) =>
        [.. new[] { (From: 0.0, To: geometry.FirstSampleX), (From: geometry.NowX, To: 1.0) }
            .Where(region => region.To > region.From)];

    public static string QuotaKey() => "Quota".Localized();

    public static string UsageKey() => "Usage".Localized();

    public static string NoSampleKey() => "No sample".Localized();

    public static string Readings(int count) =>
        "{0} readings".Localized(count);

    public static string MetricLabel(QuotaMetric metric) =>
        (metric == QuotaMetric.Used ? "Used" : "Remaining").Localized();

    /// <summary>Stated, not swallowed. The engine counts rows it could not
    /// place in time precisely so a consumer cannot present a window total as
    /// definitive while omitting them.
    /// <para>Worded as a fact about the SCAN, not about this card's totals: an
    /// undated row has no timestamp, so its window membership does not exist to
    /// be recovered, and "not in these totals" would claim this card had lost a
    /// row that may belong to another subscription entirely.</para></summary>
    public static string? UndatedNote(int undatedCount) =>
        undatedCount > 0
            ? "{0} scanned rows have no usable timestamp, so no window can count them"
                .Localized(undatedCount)
            : null;

    // ── Hover ────────────────────────────────────────────────────────────

    /// <summary><c>HH:mm – HH:mm</c> in the viewer's own zone. macOS's
    /// <c>Format.clockRange</c> is not in this slice's snapshot, so this is the
    /// shape the interval needs rather than a transcription of that
    /// function.</summary>
    public static string ClockRange(long fromMs, long toMs) =>
        $"{Clock(fromMs)} – {Clock(toMs)}";

    private static string Clock(long ms) =>
        DateTimeOffset.FromUnixTimeMilliseconds(ms).ToLocalTime().ToString(
            "HH:mm", System.Globalization.CultureInfo.CurrentCulture);

    /// <summary>The quota row of the hover. A zone with no closing sample is
    /// the hatched stretch, and must say so rather than show nothing.</summary>
    public static string ZoneQuota(HitZone zone, QuotaMetric metric) =>
        zone.ClosingSample is { } sample
            ? "Quota {0}% {1}".Localized(
                (int)Math.Round(metric.Value(sample.UsedPercent), MidpointRounding.AwayFromZero),
                (metric == QuotaMetric.Used ? "used" : "remaining").Localized())
            : "No quota reading in this interval".Localized();

    /// <summary>What this interval cost, signed the way the card is currently
    /// read. Null rather than zero when an end has no reading — an interval
    /// nobody measured is not an interval that consumed nothing.</summary>
    public static string? ZoneConsumed(HitZone zone, QuotaMetric metric)
    {
        if (zone.Consumed(metric) is not { } delta)
        {
            return null;
        }

        var rounded = Math.Round(delta, 2, MidpointRounding.AwayFromZero);
        return "{0}{1}% this interval".Localized(
            rounded > 0 ? "+" : string.Empty,
            rounded.ToString("0.##", System.Globalization.CultureInfo.CurrentCulture));
    }

    /// <summary>The tokens and money a zone's own messages carry, or the line
    /// that says it carries none.</summary>
    public static (string? Tokens, string? Money, string? Empty) ZoneUsage(
        IReadOnlyList<WindowMessage> messages)
    {
        if (messages.Count == 0)
        {
            return (null, null, "No usage in this interval".Localized());
        }

        long tokens = 0;
        var cost = 0.0;
        foreach (var message in messages)
        {
            tokens = tokens.SaturatingAdd(message.Tokens);
            cost += message.Cost;
        }

        return ("{0} tokens".Localized(Format.CompactTokens(tokens)), Format.Usd(cost), null);
    }

    /// <summary>The messages inside one zone. Zone 0 owns its own lower bound,
    /// matching <see cref="WindowCardGeometry.UsageGeometry"/> — without this
    /// the bar drawn from a window-start message and the tooltip explaining
    /// that bar disagree about whether the message is in it.</summary>
    public static IReadOnlyList<WindowMessage> InZone(
        IReadOnlyList<WindowMessage> messages, HitZone zone) =>
        [.. messages.Where(message =>
            (zone.Index == 0
                ? message.Timestamp >= zone.LoMs
                : message.Timestamp > zone.LoMs)
            && message.Timestamp <= zone.HiMs)];
}

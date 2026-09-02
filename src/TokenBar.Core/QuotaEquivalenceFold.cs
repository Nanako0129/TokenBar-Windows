using TokenBar.Interop;

namespace TokenBar.Core;

/// <summary>
/// Feeds <see cref="WindowEquivalence.Cycle"/>'s <c>SpanTokens</c>/<c>SpanCost</c>
/// from the 5d-1 export and folds every window's admitted cycles into its
/// <see cref="WindowEquivalence.Row"/>.
/// <para>
/// No macOS file is snapshotted for this join — TokenBarCore's DashboardModel
/// wires it inline and was not part of this slice's snapshot. This is built
/// from the two pieces that are: <see cref="AttributedDailySeries.Points"/>'s
/// attribution join (<c>UsageAttribution.Resolve(client, provider, model,
/// confirmed)</c>), and <see cref="QuotaHistoryFold"/>'s own comments on the
/// span a cycle's delta describes and the <see cref="QuotaHistoryFold.Considered"/>
/// cap it names as belonging to "the admitted set behind the equivalence".
/// </para>
/// <para>
/// A message counts toward a window without any extra join table, because
/// <see cref="QuotaHistorySeries.ProviderId"/> is — despite the field name
/// inherited from the wire — already a registered CLIENT id: the
/// quota-tracked subscription owner (<c>QuotaHistoryFoldTests</c> decodes
/// series with <c>providerId: "codex"</c>). <see cref="UsageAttribution.Resolve"/>
/// resolves each message into that exact same id space via its
/// <c>Assigned(target)</c> case, so a message is this window's evidence
/// precisely when its resolved target equals the window's own
/// <see cref="QuotaHistorySeries.ProviderId"/>.
/// </para>
/// </summary>
public static class QuotaEquivalenceFold
{
    /// <summary>Each cycle's span restricted to messages attributed to
    /// <paramref name="providerId"/> — the window's own subscription-owner
    /// client id — and falling inside that cycle's own
    /// <see cref="QuotaCycle.FirstSampleMs"/>…<see cref="QuotaCycle.LastSampleMs"/>
    /// span, matching <see cref="WindowEquivalence.LiveRow"/>'s
    /// <c>timestamp &gt; first &amp;&amp; timestamp &lt;= last</c> rule.</summary>
    public static IReadOnlyList<WindowEquivalence.Cycle> Cycles(
        IReadOnlyList<QuotaCycle> cycles,
        string providerId,
        IReadOnlyList<WindowMessage> messages,
        IReadOnlyList<UsageAttribution.Record> confirmed)
    {
        var result = new List<WindowEquivalence.Cycle>(cycles.Count);
        foreach (var cycle in cycles)
        {
            long tokens = 0;
            var cost = 0.0;
            foreach (var message in messages)
            {
                if (message.Timestamp <= cycle.FirstSampleMs || message.Timestamp > cycle.LastSampleMs)
                {
                    continue;
                }

                var state = UsageAttribution.Resolve(
                    message.Client, message.ProviderId, message.ModelId, confirmed);
                if (state.Kind != UsageAttribution.StateKind.Assigned || state.Target != providerId)
                {
                    continue;
                }

                tokens = tokens.SaturatingAdd(WindowEquivalence.RatioTokens(message));
                cost += message.Cost;
            }

            result.Add(new WindowEquivalence.Cycle(
                DeltaPercent: cycle.UsedPercent,
                SpanTokens: tokens,
                SpanCost: cost,
                ObservedFraction: cycle.ObservedFraction));
        }

        return result;
    }

    /// <summary>Every window's equivalence row, keyed the same way the strip
    /// and heatmap cards already key their own data
    /// (<see cref="QuotaWindowIdentity"/>). <paramref name="messages"/> is the
    /// whole 5d-1 export for the fetched range — unfiltered by window, since
    /// <see cref="Cycles"/> does that per window from the attribution
    /// join.</summary>
    public static IReadOnlyDictionary<QuotaWindowIdentity, WindowEquivalence.Row> Build(
        IReadOnlyList<QuotaHistorySeries> history,
        IReadOnlyList<WindowMessage> messages,
        UsageAttribution.Table confirmed)
    {
        var result = new Dictionary<QuotaWindowIdentity, WindowEquivalence.Row>();
        foreach (var series in history)
        {
            var id = new QuotaWindowIdentity(series.ProviderId, series.AccountScope, series.WindowKey);
            // The same cap the history card's message scan bounds itself by
            // (QuotaHistoryFold.Considered's own doc comment): the oldest
            // cycles beyond it are not part of "the admitted set behind the
            // equivalence" either.
            var considered = QuotaHistoryFold.Considered(QuotaHistoryFold.Cycles(series.Samples));
            var spanCycles = Cycles(considered, series.ProviderId, messages, confirmed.Records);
            // Per window, not per app: a user who classified their Codex
            // usage but never touched this window's own messages has still
            // declared nothing about THIS subscription's evidence, so a zero
            // span here means "unclassified", not "recorded as zero".
            // Checked against every message the window's own cycles cover
            // (any resolved state, not just ones assigned to this provider) —
            // the same population `Cycles` scans above — so a second
            // subscription's declaration cannot stand in for this one's.
            var declared = Declared(considered, messages, confirmed.Records);
            result[id] = WindowEquivalence.Aggregate(declared, spanCycles);
        }

        return result;
    }

    /// <summary>Whether at least one message inside this window's own admitted
    /// cycles has been classified at all (assigned OR excluded — anything but
    /// the untouched default). Distinct from <see cref="Cycles"/>'s filter,
    /// which additionally requires the classification to point AT this
    /// window: a message assigned to a different subscription still proves
    /// the user reached this window's evidence and chose to route it
    /// elsewhere, which is not "undeclared".</summary>
    public static bool Declared(
        IReadOnlyList<QuotaCycle> cycles,
        IReadOnlyList<WindowMessage> messages,
        IReadOnlyList<UsageAttribution.Record> confirmed)
    {
        foreach (var cycle in cycles)
        {
            foreach (var message in messages)
            {
                if (message.Timestamp <= cycle.FirstSampleMs || message.Timestamp > cycle.LastSampleMs)
                {
                    continue;
                }

                var state = UsageAttribution.Resolve(
                    message.Client, message.ProviderId, message.ModelId, confirmed);
                if (state.Kind != UsageAttribution.StateKind.Unassigned)
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>The earliest instant any window's admitted cycles need
    /// messages for — the lower bound a caller should pass to
    /// <c>TbCore.WindowUsage</c>, so the export is not asked to scan further
    /// back than any card can use.
    /// <para>
    /// <see cref="QuotaCycle.EvidenceStartMs"/>, not
    /// <see cref="QuotaCycle.FirstSampleMs"/>: the same "evidence reaches back
    /// to whichever is earlier" rule <see cref="QuotaHistoryFold"/> already
    /// states for a provider that shortened its reported window mid-cycle.
    /// </para>
    /// <para>
    /// Also considers each series' placed running cycle
    /// (<see cref="QuotaHistoryFold.Active"/>), applying the same
    /// earlier-of-start-or-first-reading rule. <see cref="QuotaHistoryFold.Cycles"/>
    /// deliberately excludes the running cycle, so a series holding only its
    /// active cycle — a fresh install, or any window whose first cycle has not
    /// completed — has an empty <see cref="Considered"/> set; without this, the
    /// scan bound falls back to <paramref name="fallbackMs"/> (the caller's
    /// "now"), the requested window collapses to <c>[now, now)</c>, and
    /// <c>BuildWindowCard</c> — which draws the active cycle's usage from
    /// exactly these messages — shows no usage for a cycle that is plainly
    /// running. An unplaced active cycle (<see cref="QuotaActiveCycle.IsPlaced"/>
    /// false) contributes nothing: there is no start to bound a scan by.
    /// </para></summary>
    public static long BoundFromMs(IReadOnlyList<QuotaHistorySeries> history, long fallbackMs)
    {
        long? earliest = null;
        void Consider(long candidateMs)
        {
            if (earliest is null || candidateMs < earliest)
            {
                earliest = candidateMs;
            }
        }

        foreach (var series in history)
        {
            foreach (var cycle in QuotaHistoryFold.Considered(QuotaHistoryFold.Cycles(series.Samples)))
            {
                Consider(cycle.EvidenceStartMs);
            }

            if (QuotaHistoryFold.Active(series.Samples) is { IsPlaced: true } active)
            {
                var firstSampleMs = active.Samples[0].AtMs;
                Consider(Math.Min(active.StartMs!.Value, firstSampleMs));
            }
        }

        return earliest ?? fallbackMs;
    }
}

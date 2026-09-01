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
        var declared = confirmed.Records.Count > 0;
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
            result[id] = WindowEquivalence.Aggregate(declared, spanCycles);
        }

        return result;
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
    /// </para></summary>
    public static long BoundFromMs(IReadOnlyList<QuotaHistorySeries> history, long fallbackMs)
    {
        long? earliest = null;
        foreach (var series in history)
        {
            foreach (var cycle in QuotaHistoryFold.Considered(QuotaHistoryFold.Cycles(series.Samples)))
            {
                if (earliest is null || cycle.EvidenceStartMs < earliest)
                {
                    earliest = cycle.EvidenceStartMs;
                }
            }
        }

        return earliest ?? fallbackMs;
    }
}

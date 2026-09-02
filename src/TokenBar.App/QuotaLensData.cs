using TokenBar.Core;
using TokenBar.Interop;

namespace TokenBar.App;

/// <summary>
/// The Quota lens's two cards, assembled from one pass over the persisted
/// series.
/// <para>
/// Pulled out of <c>DashboardView</c> and into the test project's
/// &lt;Compile Include&gt; list for the same reason as
/// <see cref="QuotaLensText"/>: the label join can miss, and what a miss leaves
/// behind is the rule this slice was most at risk of collapsing.
/// </para>
/// </summary>
public static class QuotaLensData
{
    /// <summary>
    /// Summaries, picker windows and grids, keyed by the store's own triple.
    /// <para>
    /// The picker's windows and the strip's summaries are enumerated
    /// independently from the same export, never derived from one another: a
    /// window whose only movement is in the cycle now running has a grid and no
    /// summary, and keying the picker on summaries made that grid unreachable
    /// until the first cycle completed — days, on a weekly window.
    /// </para>
    /// </summary>
    public static (
        IReadOnlyList<QuotaWindowSummary> Summaries,
        IReadOnlyList<QuotaHeatmapWindow> Windows,
        IReadOnlyDictionary<QuotaWindowIdentity, QuotaHeatmap> Grids)
        Build(IReadOnlyList<QuotaHistorySeries>? history, AgentUsagePayload? quota)
    {
        var labels = WindowLabels(quota);
        var forSummaries =
            new List<(QuotaWindowIdentity Id, string? Label, IReadOnlyList<QuotaCycle> Cycles)>();
        var forWindows = new List<(QuotaWindowIdentity Id, string? Label, QuotaHeatmap Grid)>();
        // Keyed by the identity record, not by "client|window": two accounts of
        // one client can hold the same window, and a key that dropped the scope
        // would let one of their grids overwrite the other's.
        var grids = new Dictionary<QuotaWindowIdentity, QuotaHeatmap>();
        foreach (var series in history ?? [])
        {
            var id = new QuotaWindowIdentity(
                series.ProviderId, series.AccountScope, series.WindowKey);
            // Null when the join found no live window — NOT pre-filled with the
            // WindowKey here. The fallback belongs to QuotaLabels, where "never
            // a trailing separator" is stated and asserted; applying it at this
            // seam would collapse "no label" into "labelled with its own key"
            // in the data layer, where nothing downstream could tell them apart
            // again.
            var label = labels.GetValueOrDefault((series.ProviderId, series.WindowKey));
            var grid = QuotaHeatmapFold.Build(series.Samples);
            grids[id] = grid;
            forWindows.Add((id, label, grid));
            forSummaries.Add((id, label, QuotaHistoryFold.Cycles(series.Samples)));
        }

        return (
            QuotaOverviewFold.Summaries(forSummaries),
            QuotaOverviewFold.HeatmapWindows(forWindows),
            grids);
    }

    /// <summary>The label join PARITY-3b established: <c>(clientId,
    /// PaceStatus.WindowKey)</c> against the live agent-usage payload. A series
    /// with no matching live window keeps its identity and loses only its label,
    /// so a miss leaves null rather than dropping the row.</summary>
    private static Dictionary<(string Client, string Window), string> WindowLabels(
        AgentUsagePayload? quota)
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

        return labels;
    }
}

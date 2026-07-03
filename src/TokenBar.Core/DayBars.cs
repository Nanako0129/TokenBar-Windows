using TokenBar.Interop;

namespace TokenBar.Core;

// Stacked-bar series for the Token Usage chart, port of
// TokenBarCore/DayBars.swift (originally the grouping logic in
// UsageBarGraph2D.tsx). UI-free so the builder is unit-testable; the chart
// just renders the result.

public enum StackBy
{
    Model,
    Agent,
}

/// <summary>Whether bar length encodes token count or spend.</summary>
public enum ChartMetric
{
    Tokens,
    Cost,
}

// Deliberately a class, not a record: the builder and legend accumulate into
// Tokens/Cost, and a mutable record's synthesized value equality/hash would be
// a trap the moment one lands in a set or gets diffed.
public sealed class DaySegment(string key, string label, string color)
{
    public string Key { get; } = key;
    public string Label { get; } = label;
    /// <summary>Hex color (provider shade for model stacking, brand color for
    /// agent).</summary>
    public string Color { get; } = color;
    public long Tokens { get; set; }
    public double Cost { get; set; }
}

public sealed record DayBar(string Date, IReadOnlyList<DaySegment> Segments)
{
    public long TotalTokens => Segments.Sum(s => s.Tokens);
    public double TotalCost => Segments.Sum(s => s.Cost);
    public bool IsEmpty => Segments.Count == 0;
}

public static class DayBars
{
    public const int Window = 30;

    /// <summary>Build the trailing Window-day series ending at the payload's
    /// range end (endFallback — today — when absent). Days outside the data
    /// render as empty bars.</summary>
    public static IReadOnlyList<DayBar> Build(
        UsagePayload payload,
        IReadOnlyList<string> clientIds,
        StackBy stackBy,
        ModelColorMap colors,
        string endFallback)
    {
        var allowed = new HashSet<string>(clientIds);
        var byDate = new Dictionary<string, DayBar>();
        foreach (var contribution in payload.Contributions)
        {
            var day = BuildDayBar(contribution, allowed, stackBy, colors);
            if (day.TotalTokens > 0 || day.TotalCost > 0)
            {
                byDate[day.Date] = day;
            }
        }

        var end = payload.Meta.DateRange.End.Length == 0 ? endFallback : payload.Meta.DateRange.End;
        // A malformed (non-empty but unparseable) range end shouldn't blank the
        // whole chart — fall back to today's key, which is always parseable.
        var endDay = ISODay.Parse(end) ?? ISODay.Parse(endFallback);
        if (endDay is null)
        {
            return [];
        }

        return Enumerable.Range(0, Window)
            .Select(i =>
            {
                var date = new ISODay(endDay.Value.Number - (Window - 1) + i).Iso;
                return byDate.GetValueOrDefault(date) ?? new DayBar(date, []);
            })
            .ToList();
    }

    internal static DayBar BuildDayBar(
        Contribution contribution, IReadOnlySet<string> allowed, StackBy stackBy,
        ModelColorMap colors)
    {
        // Group each day either by model (tokscale-style provider shades) or
        // by agent/client (brand colors). Color + label follow the mode.
        var grouped = new Dictionary<string, DaySegment>();
        foreach (var client in contribution.Clients)
        {
            if (!allowed.Contains(client.Client))
            {
                continue;
            }

            var t = client.Tokens;
            var tokens = t.Input + t.Output + t.CacheRead + t.CacheWrite + t.Reasoning;
            if (tokens <= 0 && client.Cost <= 0)
            {
                continue;
            }

            var model = client.ModelId.Length == 0 ? "unknown" : client.ModelId;
            var key = stackBy == StackBy.Model ? model : client.Client;
            if (!grouped.TryGetValue(key, out var slot))
            {
                slot = stackBy == StackBy.Model
                    ? new DaySegment(key, model, colors.Color(client.ProviderId, model))
                    : new DaySegment(
                        key,
                        ClientRegistry.ShortName(client.Client),
                        ClientRegistry.Style(client.Client).Color);
                grouped[key] = slot;
            }

            slot.Tokens += tokens;
            slot.Cost += client.Cost;
        }

        // Stable stacking order across days: sort by key.
        return new DayBar(
            contribution.Date,
            [.. grouped.Values.OrderBy(s => s.Key, StringComparer.Ordinal)]);
    }

    /// <summary>Aggregate every segment across the visible window for the
    /// legend, heaviest-first by the active metric.</summary>
    public static IReadOnlyList<DaySegment> Legend(IReadOnlyList<DayBar> bars, ChartMetric metric)
    {
        var agg = new Dictionary<string, DaySegment>();
        foreach (var seg in bars.SelectMany(b => b.Segments))
        {
            if (!agg.TryGetValue(seg.Key, out var slot))
            {
                slot = new DaySegment(seg.Key, seg.Label, seg.Color);
                agg[seg.Key] = slot;
            }

            slot.Tokens += seg.Tokens;
            slot.Cost += seg.Cost;
        }

        // Key tiebreak: the Swift original inherits Dictionary iteration order
        // on metric ties (random per process); a deterministic order is a
        // strict improvement and worth upstreaming.
        return metric == ChartMetric.Cost
            ? [.. agg.Values.OrderByDescending(s => s.Cost).ThenBy(s => s.Key, StringComparer.Ordinal)]
            : [.. agg.Values.OrderByDescending(s => s.Tokens).ThenBy(s => s.Key, StringComparer.Ordinal)];
    }
}

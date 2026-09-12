using System.Globalization;
using TokenBar.Interop;

namespace TokenBar.Core;

/// <summary>
/// Daily usage stacked by the subscription the user declared it against. Port of
/// TokenBarCore/SubscriptionTrend.swift.
/// <para>
/// <see cref="AttributedDailySeries"/> already folds the graph's client rows into
/// (date, attribution state, model). This regroups that by subscription and fills
/// the calendar, which is what a trend chart needs and a total does not.
/// </para>
/// </summary>
public sealed record SubscriptionTrend(
    IReadOnlyList<SubscriptionTrend.Day> Days,
    IReadOnlyList<string> Targets,
    IReadOnlyList<string> TargetsByTokens,
    double PeakCost,
    long PeakTokens)
{
    public readonly record struct Bucket(long Tokens, double Cost);

    /// <summary>One calendar day. An absent target means zero for that day:
    /// callers must treat a missing key as zero rather than as "no data", because
    /// the day is present precisely because the range covers it.</summary>
    public sealed record Day(
        string Date,
        IReadOnlyDictionary<string, Bucket> ByTarget,
        long TotalTokens,
        double TotalCost)
    {
        public bool IsEmpty => TotalTokens == 0 && TotalCost == 0;
    }

    public static SubscriptionTrend Empty { get; } = new([], [], [], 0, 0);

    /// <summary>The order to stack and to list under the selected metric.
    /// <para>Cost and tokens do not rank the same subscriptions the same way — an
    /// unpriced or partly priced source can be the largest by tokens and near-last
    /// by cost. Resolved once so the stacking loop, the tooltip list and the
    /// legend cannot disagree about which order they are in.</para></summary>
    public IReadOnlyList<string> TargetsFor(bool byTokens) =>
        byTokens ? TargetsByTokens : Targets;
}

public static class SubscriptionTrendFold
{
    /// <summary>Usage the user has not classified yet. Kept as its own stack
    /// segment rather than dropped: it is real spend, and hiding it would make the
    /// chart's total disagree with every other total in the app.</summary>
    public const string UnassignedTarget = "__unassigned";

    /// <summary><paramref name="days"/> counts calendar days back from
    /// <paramref name="today"/> inclusive, so 14 means today and the thirteen
    /// before it.
    /// <para>Declared-excluded usage is dropped. That is the one omission the user
    /// asked for by declaring it, and it is not the same as unassigned.</para></summary>
    public static SubscriptionTrend Build(
        IReadOnlyList<AttributedDailySeries.Point> points, string today, int days)
    {
        if (days <= 0 || CalendarRange(today, days) is not { } range)
        {
            return SubscriptionTrend.Empty;
        }

        var first = range[0];
        var byDate = new Dictionary<string, Dictionary<string, SubscriptionTrend.Bucket>>(
            StringComparer.Ordinal);
        var totals = new Dictionary<string, double>(StringComparer.Ordinal);
        var tokenTotals = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var point in points)
        {
            if (string.CompareOrdinal(point.Date, first) < 0
                || string.CompareOrdinal(point.Date, today) > 0)
            {
                continue;
            }

            string target;
            switch (point.State.Kind)
            {
                case UsageAttribution.StateKind.Assigned:
                    target = point.State.Target!;
                    break;
                case UsageAttribution.StateKind.Excluded:
                    continue;
                default:
                    target = UnassignedTarget;
                    break;
            }

            if (!byDate.TryGetValue(point.Date, out var day))
            {
                day = new Dictionary<string, SubscriptionTrend.Bucket>(StringComparer.Ordinal);
                byDate[point.Date] = day;
            }

            // Saturating for the reason AttributedDailySeries already saturates
            // the buckets this regroups: the counters originate in local
            // transcripts this app does not write.
            var bucket = day.GetValueOrDefault(target);
            day[target] = new SubscriptionTrend.Bucket(
                bucket.Tokens.SaturatingAdd(point.Tokens), bucket.Cost + point.Cost);
            totals[target] = totals.GetValueOrDefault(target) + point.Cost;
            tokenTotals[target] = tokenTotals.GetValueOrDefault(target).SaturatingAdd(point.Tokens);
        }

        var built = range.Select(date =>
        {
            var buckets = byDate.TryGetValue(date, out var found)
                ? found
                : new Dictionary<string, SubscriptionTrend.Bucket>(StringComparer.Ordinal);
            return new SubscriptionTrend.Day(
                date,
                buckets,
                buckets.Values.Aggregate(0L, (acc, bucket) => acc.SaturatingAdd(bucket.Tokens)),
                buckets.Values.Sum(bucket => bucket.Cost));
        }).ToList();

        return new SubscriptionTrend(
            built,
            Ranked(totals),
            Ranked(tokenTotals),
            built.Count == 0 ? 0 : built.Max(day => day.TotalCost),
            built.Count == 0 ? 0 : built.Max(day => day.TotalTokens));
    }

    /// <summary>Largest first, ties broken by name so the stacking order is stable
    /// across refreshes; an unstable order makes the chart reshuffle its bands for
    /// no reason the user can see.</summary>
    private static IReadOnlyList<string> Ranked<T>(Dictionary<string, T> totals)
        where T : IComparable<T> =>
        [.. totals.Keys
            .OrderByDescending(key => totals[key])
            .ThenBy(key => key, StringComparer.Ordinal)];

    /// <summary><paramref name="count"/> consecutive <c>yyyy-MM-dd</c> keys ending
    /// at <paramref name="endingAt"/>, oldest first. Null when the anchor is not a
    /// date.
    /// <para>Walked as calendar days (<see cref="DateOnly"/>) rather than by
    /// subtracting 86,400 seconds: a DST transition makes one of those days 23 or
    /// 25 hours long, and the arithmetic version silently repeats or skips a date
    /// twice a year.</para></summary>
    public static IReadOnlyList<string>? CalendarRange(string endingAt, int count)
    {
        if (count <= 0
            || !DateOnly.TryParseExact(
                endingAt, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var end))
        {
            return null;
        }

        return [.. Enumerable.Range(0, count)
            .Reverse()
            .Select(back => end.AddDays(-back).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))];
    }
}

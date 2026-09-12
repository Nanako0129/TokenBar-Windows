using TokenBar.Interop;

namespace TokenBar.Core;

// Port of TokenBarCore/WindowCardGeometry.swift, comments included: they record
// the defect each rule exists to prevent.
//
// Pure geometry for the in-window usage card. Everything the card draws is
// derived here so it can be asserted without a view: the WinUI layer only
// strokes and fills what these functions return.
//
// The load-bearing property is that `Bars` and `Hits` never see `metric`.
// A flow has no "remaining" version, so flipping used/remaining must leave the
// usage geometry bit-identical.

/// <summary>Which direction the card counts in.</summary>
public enum QuotaMetric
{
    Used,
    Remaining,
}

public static class QuotaMetricExtensions
{
    /// <summary>The provider always reports used%; remaining is the
    /// complement.</summary>
    public static double Value(this QuotaMetric metric, double fromUsedPercent) =>
        metric == QuotaMetric.Used ? fromUsedPercent : 100 - fromUsedPercent;
}

/// <param name="AtMs">The reading's own instant, in ms.</param>
public sealed record QuotaSample(long AtMs, double UsedPercent);

/// <summary>x and width are fractions of the window; height is a fraction of
/// the tallest bar. Nothing here is in pixels — the view owns those.</summary>
/// <param name="IsEmpty">True when the interval held no usage at all. Drawn as
/// a baseline tick so "nothing was spent" stays distinguishable from "no data
/// here".</param>
public sealed record BarRect(double X, double Width, double Height, bool IsEmpty);

/// <param name="Y">Already through <see cref="QuotaMetricExtensions.Value"/>,
/// so 0…100 in the displayed sense.</param>
public sealed record CurvePoint(double X, double Y);

/// <summary>One hit target. Zones tile <c>[windowStart, now]</c> exactly — see
/// <see cref="WindowCardGeometry.Zones"/>.</summary>
/// <param name="ClosingSample">Absent for the region before the first sample
/// and after the last one: usage happened, but no quota reading closes the
/// interval.</param>
/// <param name="OpeningSample">The reading the interval opened on. Present with
/// <paramref name="ClosingSample"/> it gives the quota this interval actually
/// consumed — the number the bars are supposed to explain.</param>
public sealed record HitZone(
    int Index,
    long LoMs,
    long HiMs,
    double X,
    double Width,
    QuotaSample? ClosingSample,
    QuotaSample? OpeningSample)
{
    /// <summary>How much quota this interval consumed, in the direction the
    /// card is currently read: positive when counting up, negative when
    /// counting down. Null when either end has no reading, because then nothing
    /// was measured.</summary>
    public double? Consumed(QuotaMetric metric) =>
        OpeningSample is { } a && ClosingSample is { } b
            ? metric.Value(b.UsedPercent) - metric.Value(a.UsedPercent)
            : null;
}

/// <param name="NowX">Where <c>now</c> falls in the drawn window, 0…1.
/// Everything to its right has not happened: no bars, no line, and no hit
/// zones.</param>
/// <param name="FirstSampleX">Where the first quota sample falls, or
/// <paramref name="NowX"/> when there is none. Left of it the line is not drawn
/// — the app was not running to sample.</param>
/// <param name="SamplePoints">Sample positions, drawn as dots. The curve
/// between them is interpolation.</param>
/// <param name="Curve">The interpolated polyline. Empty when fewer than two
/// samples fall inside.</param>
public sealed record ChartGeometry(
    double NowX,
    double FirstSampleX,
    IReadOnlyList<BarRect> Bars,
    IReadOnlyList<HitZone> Hits,
    IReadOnlyList<CurvePoint> SamplePoints,
    IReadOnlyList<CurvePoint> Curve);

public static class WindowCardGeometry
{
    /// <summary>How many interpolated points per segment. Purely visual
    /// smoothness; the shape is fixed by <see cref="MonotoneCurve"/> regardless
    /// of this value.</summary>
    public const int CurveResolution = 8;

    /// <summary>
    /// Boundaries are <c>{windowStart} ∪ {sample times} ∪ {now}</c>,
    /// deduplicated and clamped, so the zones tile <c>[windowStart, now]</c>
    /// with no gap, no overlap, and <b>no minimum width</b>.
    /// <para>
    /// A minimum width is what pushes the last zone past <c>now</c>, and the
    /// future must be unhittable by construction rather than by intent. A zone
    /// too narrow to hit means too many buckets, not a wider zone.
    /// <paramref name="windowEndMs"/> only sets the horizontal scale: zones
    /// still stop at <c>now</c>.
    /// </para>
    /// </summary>
    public static IReadOnlyList<HitZone> Zones(
        long windowStartMs, long windowEndMs, long nowMs, IReadOnlyList<QuotaSample> samples)
    {
        if (nowMs <= windowStartMs || windowEndMs <= windowStartMs)
        {
            return [];
        }

        var edges = new List<long> { windowStartMs };
        foreach (var sample in samples)
        {
            if (sample.AtMs > windowStartMs && sample.AtMs < nowMs && sample.AtMs != edges[^1])
            {
                edges.Add(sample.AtMs);
            }
        }

        edges.Add(nowMs);

        // Fractions are of the whole window, not of the elapsed part, so the
        // future region keeps its share of the width.
        var span = (double)(windowEndMs - windowStartMs);
        var zones = new List<HitZone>(edges.Count - 1);
        for (var i = 0; i < edges.Count - 1; i++)
        {
            var lo = edges[i];
            var hi = edges[i + 1];
            zones.Add(new HitZone(
                Index: i,
                LoMs: lo,
                HiMs: hi,
                X: (lo - windowStartMs) / span,
                Width: (hi - lo) / span,
                ClosingSample: samples.FirstOrDefault(s => s.AtMs == hi),
                OpeningSample: samples.FirstOrDefault(s => s.AtMs == lo)));
        }

        return zones;
    }

    /// <summary>The metric-free half: bars and hit zones. Split out because a
    /// function that cannot see the metric cannot leak it into the usage
    /// geometry.
    /// <para>
    /// Round 12's P2 finding: a comment here used to claim "one pass over the
    /// messages", but the loop it described was O(zones × messages) — for
    /// every message it walked every zone in order until one matched. The
    /// <paramref name="messages"/> list is the full bounded history, not just
    /// the active window, so an older client with a large attributed history
    /// and dozens of active quota readings could put this on the UI thread
    /// (the Quota lens rebuilds on the 10-second fast-lane tick,
    /// QuotaLensProjection.Build -&gt; ... -&gt; <c>Chart</c> -&gt; here) doing
    /// millions of comparisons.
    /// </para>
    /// <para>
    /// First attempt followed <c>QuotaEquivalenceFold.CyclesCore</c>'s shape
    /// (a3ea946) literally: sort <paramref name="messages"/> once, then slice
    /// each zone out of the sorted array by binary search. Measured it
    /// end-to-end (scratchpad/bench, git-stash A/B, 100k messages / 40
    /// samples / 5 warmed-up calls) and it was SLOWER than the code it
    /// replaced — 13-14 ms/call against the naive loop's 3-4 ms/call — because
    /// the shapes are inverted. <c>CyclesCore</c> amortizes ONE sort of the
    /// message list across every series' cycles in the same <c>Build()</c>
    /// call (8 series × 32 cycles in that benchmark); here there is only ever
    /// one zone list per call, so the O(m log m) sort of the LARGE side
    /// (messages, ~10^5) paid for a binary search over the SMALL side (zones,
    /// tens) and never earned its cost back.
    /// </para>
    /// <para>
    /// Fixed instead by inverting which side gets sorted: the zones' own
    /// upper bounds (one per zone — tens of entries) are collected into
    /// <c>upperBounds</c>, and each message binary-searches THAT array
    /// (<see cref="LowerBound"/>, O(log zones)) instead of walking it
    /// linearly. No message list is copied or sorted. Zones already tile
    /// <c>[windowStart, now]</c> with no gap and no overlap (<see cref="Zones"/>'s
    /// own doc comment), so "first upperBound ≥ timestamp" is exactly the
    /// zone that owns it, INCLUDING the <c>(lo, hi]</c> vs zone-0's own
    /// <c>[lo, hi]</c> distinction the old loop special-cased: a timestamp
    /// sitting exactly on the boundary between two zones equals the earlier
    /// zone's own <c>HiMs</c>, so it resolves to that zone with no extra
    /// check, and zone 0's <c>LoMs</c> is never any zone's boundary value, so
    /// it is never at risk of resolving to a "previous" zone that does not
    /// exist. This is a genuine single pass over <paramref name="messages"/>
    /// (each message costs one <c>O(log zones)</c> lookup, not one sort),
    /// re-measured at 0.4-0.5 ms/call on the same payload — faster than both
    /// the code this fixes AND the sort-based first attempt.
    /// </para></summary>
    public static (IReadOnlyList<BarRect> Bars, IReadOnlyList<HitZone> Hits) UsageGeometry(
        long windowStartMs,
        long windowEndMs,
        long nowMs,
        IReadOnlyList<QuotaSample> samples,
        IReadOnlyList<WindowMessage> messages)
    {
        var hits = Zones(windowStartMs, windowEndMs, nowMs, samples);
        // Bars are sized by every token class except cache read. Cache read is
        // 200x the volume at a tenth the price, so including it decouples the
        // bars from the line; cache WRITE costs 1.25x base input and must stay.
        var weights = new long[hits.Count];
        if (hits.Count > 0)
        {
            var upperBounds = new long[hits.Count];
            for (var i = 0; i < hits.Count; i++)
            {
                upperBounds[i] = hits[i].HiMs;
            }

            var windowStart = hits[0].LoMs;
            foreach (var message in messages)
            {
                var timestamp = message.Timestamp;
                if (timestamp < windowStart)
                {
                    continue;
                }

                var index = LowerBound(upperBounds, timestamp);
                if (index >= hits.Count)
                {
                    continue;
                }

                weights[index] = weights[index].SaturatingAdd(message.TokensExCacheRead);
            }
        }

        var tallest = Math.Max(weights.Length == 0 ? 0 : weights.Max(), 1);
        var bars = new List<BarRect>(hits.Count);
        for (var i = 0; i < hits.Count; i++)
        {
            bars.Add(new BarRect(
                hits[i].X, hits[i].Width, (double)weights[i] / tallest, weights[i] == 0));
        }

        return (bars, hits);
    }

    /// <summary>First index whose value is &gt;= <paramref name="value"/>. Own
    /// copy of <c>QuotaEquivalenceFold</c>'s private helper of the same name
    /// and shape (itself a copy of <c>QuotaHistoryFold</c>'s) — neither is
    /// reachable from here, and each fold over a sorted list owns this the
    /// same way. <see cref="UsageGeometry"/> searches it over a zone's own
    /// upper bounds rather than over the message list — see that method's own
    /// doc comment for why the two shapes are not interchangeable.</summary>
    private static int LowerBound(IReadOnlyList<long> values, long value)
    {
        var low = 0;
        var high = values.Count;
        while (low < high)
        {
            var mid = (low + high) / 2;
            if (values[mid] < value)
            {
                low = mid + 1;
            }
            else
            {
                high = mid;
            }
        }

        return low;
    }

    /// <summary>The metric-dependent half: cheap, O(samples).</summary>
    public static (
        IReadOnlyList<CurvePoint> SamplePoints,
        IReadOnlyList<CurvePoint> Curve,
        double NowX,
        double FirstSampleX) QuotaGeometry(
        long windowStartMs,
        long windowEndMs,
        long nowMs,
        IReadOnlyList<QuotaSample> samples,
        QuotaMetric metric)
    {
        var span = (double)Math.Max(windowEndMs - windowStartMs, 1);
        var points = samples
            .Where(sample => sample.AtMs >= windowStartMs && sample.AtMs <= nowMs)
            .Select(sample => new CurvePoint(
                (sample.AtMs - windowStartMs) / span, metric.Value(sample.UsedPercent)))
            .ToList();
        var nowX = (nowMs - windowStartMs) / span;
        return (points, MonotoneCurve(points), nowX, points.Count > 0 ? points[0].X : nowX);
    }

    /// <summary>The metric reaches the curve and nothing else.</summary>
    public static ChartGeometry Chart(
        long windowStartMs,
        long windowEndMs,
        long nowMs,
        IReadOnlyList<QuotaSample> samples,
        IReadOnlyList<WindowMessage> messages,
        QuotaMetric metric)
    {
        var usage = UsageGeometry(windowStartMs, windowEndMs, nowMs, samples, messages);
        var quota = QuotaGeometry(windowStartMs, windowEndMs, nowMs, samples, metric);
        return new ChartGeometry(
            quota.NowX, quota.FirstSampleX,
            usage.Bars, usage.Hits,
            quota.SamplePoints, quota.Curve);
    }

    /// <summary>
    /// Fritsch-Carlson monotone cubic, sampled into a polyline.
    /// <para>
    /// Not Catmull-Rom: quota is monotone, and an overshoot between two rising
    /// samples draws a refill that never happened. The <c>α²+β²&gt;9</c> clamp
    /// is what guarantees every interpolated point stays between its two
    /// endpoints — it is not a smoothing nicety.
    /// </para>
    /// </summary>
    public static IReadOnlyList<CurvePoint> MonotoneCurve(IReadOnlyList<CurvePoint> p)
    {
        var n = p.Count;
        if (n < 2)
        {
            return [];
        }

        var dx = new double[n - 1];
        var d = new double[n - 1];
        for (var i = 0; i < n - 1; i++)
        {
            var h = p[i + 1].X - p[i].X;
            dx[i] = h;
            d[i] = h == 0 ? 0 : (p[i + 1].Y - p[i].Y) / h;
        }

        var m = new double[n];
        m[0] = d[0];
        for (var i = 1; i < n - 1; i++)
        {
            // Zero at a turn. Averaging adjacent secants unconditionally leaves
            // a nonzero tangent at a local extremum, and the `α²+β²>9` clamp
            // below bounds a tangent's MAGNITUDE without touching its sign — so
            // a provider correction that reverses direction (80 → 90 → 89)
            // interpolated above 90 between the last two points and drew quota
            // levels nobody observed. This is the sign half of the monotone
            // condition; the clamp is the magnitude half.
            m[i] = d[i - 1] * d[i] <= 0 ? 0 : (d[i - 1] + d[i]) / 2;
        }

        m[n - 1] = d[n - 2];
        for (var i = 0; i < n - 1; i++)
        {
            if (d[i] == 0)
            {
                m[i] = 0;
                m[i + 1] = 0;
                continue;
            }

            var a = m[i] / d[i];
            var b = m[i + 1] / d[i];
            var s = (a * a) + (b * b);
            if (s > 9)
            {
                var t = 3 / Math.Sqrt(s);
                m[i] = t * a * d[i];
                m[i + 1] = t * b * d[i];
            }
        }

        var output = new List<CurvePoint> { p[0] };
        for (var i = 0; i < n - 1; i++)
        {
            var h = dx[i];
            for (var step = 1; step <= CurveResolution; step++)
            {
                var t = (double)step / CurveResolution;
                var t2 = t * t;
                var t3 = t2 * t;
                // Cubic Hermite basis.
                var y = (((2 * t3) - (3 * t2) + 1) * p[i].Y)
                    + ((t3 - (2 * t2) + t) * h * m[i])
                    + (((-2 * t3) + (3 * t2)) * p[i + 1].Y)
                    + ((t3 - t2) * h * m[i + 1]);
                output.Add(new CurvePoint(p[i].X + (t * h), y));
            }
        }

        return output;
    }
}

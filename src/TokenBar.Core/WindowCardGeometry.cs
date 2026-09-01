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

    /// <summary>The metric-free half: bars and hit zones. Split out because it
    /// is the expensive half — O(zones × messages) — and because a function
    /// that cannot see the metric cannot leak it into the usage
    /// geometry.</summary>
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
        //
        // One pass over the messages instead of one per zone. Each zone is
        // closed by the sample that ends it, hence `(lo, hi]` — except the
        // first, which owns its own lower bound: for an inferred window the
        // message landing exactly on the window start IS the start, and leaving
        // zone 0 exclusive put it in the card's totals and in no bar at all.
        var weights = new long[hits.Count];
        foreach (var message in messages)
        {
            for (var i = 0; i < hits.Count; i++)
            {
                var zone = hits[i];
                var insideStart = i == 0
                    ? message.Timestamp >= zone.LoMs
                    : message.Timestamp > zone.LoMs;
                if (insideStart && message.Timestamp <= zone.HiMs)
                {
                    weights[i] = weights[i].SaturatingAdd(message.TokensExCacheRead);
                    break;
                }
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

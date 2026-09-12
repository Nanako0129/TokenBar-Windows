namespace TokenBar.Core;

// Port of TokenBarCore/QuotaTrend.swift, comments included: they record the
// measurement behind each constant.

/// <summary>
/// Per-row recent-trend indicator for a quota window: which way the curve is
/// moving right now, and where that rate lands the window by reset.
/// <para>
/// Deliberately NOT a cross-window ranking. A 5-hour window and a 31-day window
/// have no shared rate scale, and every normalisation that would put them on
/// one needs a parameter chosen by taste. A per-row indicator needs no
/// cross-window comparison, so none of those parameters exist here, and it
/// works for any vendor that reports a window duration with no hardcoded list.
/// </para>
/// </summary>
/// <param name="ProjectedUsedPercent">What percentage of the allowance this
/// window will have consumed at reset if the recent rate continues.
/// Deliberately the same 0…100(+) scale every window already shows — not a
/// ratio, an index, or a per-hour rate. A raw %/hour was rejected because it is
/// biased by window length: a 5h window must burn ~20%/h to use its allowance,
/// a 7d window only ~0.6%/h, so %/hour always names the shortest window as
/// "burning fastest" regardless of actual behaviour. May exceed 100 — that is
/// the one actionable state here.</param>
/// <param name="ProjectedDeltaPercent">Percentage points of the allowance the
/// recent rate will consume between now and reset —
/// <paramref name="ProjectedUsedPercent"/> minus the reading it was projected
/// from. A different quantity from the pace delta beside it: pace compares the
/// level against the window's usual pattern <em>so far</em>, this says what the
/// current slope costs <em>from here on</em>. On live data the two disagreed on
/// 3 of 7 windows with both correct, so they must not be printed as if
/// interchangeable.</param>
public sealed record QuotaTrend(
    QuotaTrendDirection Direction,
    double ProjectedUsedPercent,
    double ProjectedDeltaPercent)
{
    /// <summary>Whether the recent rate spends the whole allowance before the
    /// window resets.
    /// <para>Lives here rather than in the row that draws it because it decides
    /// what that row may say: past this point the delta is larger than the axis
    /// has room for, and printing it produces a drop bigger than the amount
    /// that exists. The row names the state instead.</para></summary>
    public bool RunsOutEarly => ProjectedUsedPercent > 100;
}

public enum QuotaTrendDirection
{
    Rising,
    Falling,
    Flat,
}

public static class QuotaTrendFold
{
    /// <summary>
    /// Trailing fraction of the window's own duration used to measure recent
    /// slope, back from the newest sample.
    /// <para>
    /// Measured 2026-08-17 on live data, normalised slope over the trailing
    /// 25%: codex main.weekly.v1 (4 samples) 0.45, claude session.v1 (48) 0.69,
    /// claude weekly.v1 (34) 0.47, grok billing.weekly (42) 0.00 — used 63%,
    /// elapsed 88%, flat recently.
    /// </para>
    /// <para>
    /// 25% produced a usable slope for every window that had a curve at all. A
    /// shorter lookback was tried and rejected: at 10% the answer for
    /// <c>claude weekly.v1</c> flipped relative to <c>claude session.v1</c>
    /// purely because of sampling density, and sparsely-sampled windows fall
    /// below the two-sample floor entirely at that width.
    /// </para>
    /// </summary>
    public const double LookbackFraction = 0.25;

    /// <summary>Below this many samples inside the lookback span, a slope would
    /// be invented from a single point (or nothing). No indicator, not a
    /// zero.</summary>
    public const int MinimumSamples = 2;

    /// <summary>Slope magnitude at or under this reads as "not moving recently"
    /// rather than a direction — this is the grok case: lifetime-average ratio
    /// 0.72 (the highest of the four measured windows above) while the recent
    /// slope is 0.00, because it stopped being used days ago. Any implementation
    /// whose grok row reads as "burning" is wrong. The threshold sits an order
    /// of magnitude below the smallest genuine measured slope (0.45) so a real
    /// burn is never muted, and comfortably above float noise from two adjacent
    /// quota readings.</summary>
    public const double FlatThreshold = 0.05;

    /// <summary>
    /// Samples, window bounds and <c>now</c> in → slope-derived direction and
    /// projection, or null when there is not enough recent data to say
    /// anything.
    /// <para>
    /// <paramref name="usedPercent"/> is the window's own current reading (not
    /// necessarily the last curve sample — the two can lag each other by a poll
    /// interval), because the projection is defined relative to the value the
    /// rest of the row already displays.
    /// </para>
    /// </summary>
    public static QuotaTrend? Trend(
        double usedPercent,
        long windowStartMs,
        long windowEndMs,
        long nowMs,
        IReadOnlyList<QuotaSample> samples)
    {
        var durationMs = windowEndMs - windowStartMs;
        if (durationMs <= 0 || nowMs <= windowStartMs)
        {
            return null;
        }

        var inside = samples
            .Where(sample => sample.AtMs >= windowStartMs && sample.AtMs <= nowMs)
            .OrderBy(sample => sample.AtMs)
            .ToList();
        if (inside.Count == 0)
        {
            return null;
        }

        var newest = inside[^1];
        var spanStartMs = newest.AtMs
            - (long)Math.Round(durationMs * LookbackFraction, MidpointRounding.AwayFromZero);
        var span = inside.Where(sample => sample.AtMs >= spanStartMs).ToList();
        if (span.Count < MinimumSamples || span[^1].AtMs <= span[0].AtMs)
        {
            return null;
        }

        // Normalised: usedPercent moved per 100 percentage-points of the WINDOW
        // elapsed (not per unit of wall time), so a 5h window and a 31d window
        // read on the same scale here even though this value never leaves the
        // fold.
        var elapsedFraction = (double)(span[^1].AtMs - span[0].AtMs) / durationMs;
        var recentSlope = (span[^1].UsedPercent - span[0].UsedPercent) / (elapsedFraction * 100);

        var windowElapsedFraction =
            Math.Min(1, Math.Max(0, (double)(nowMs - windowStartMs) / durationMs));
        var rawDelta = recentSlope * (1 - windowElapsedFraction) * 100;
        // Floor only, and deliberately not a ceiling. A provider correction can
        // make the recent samples fall steeply enough that `rawDelta` is more
        // negative than the meter has to give, projecting a window to "-190%
        // used" and a drop larger than the amount that exists — the mirror of
        // the overshoot `RunsOutEarly` exists for, with no state to name.
        //
        // The ceiling is NOT clamped, because `RunsOutEarly` is exactly
        // `ProjectedUsedPercent > 100`: capping there would delete the signal
        // the row uses to stop printing an impossible delta. The asymmetry is
        // the point — one saturation has a name, the other does not.
        //
        // The delta is recomputed from the clamped projection rather than kept
        // raw, so the two cannot disagree: a reader adding the delta to the
        // current reading must land on the projection beside it.
        var projected = Math.Max(0, usedPercent + rawDelta);
        var direction = Math.Abs(recentSlope) <= FlatThreshold
            ? QuotaTrendDirection.Flat
            : recentSlope > 0 ? QuotaTrendDirection.Rising : QuotaTrendDirection.Falling;

        return new QuotaTrend(direction, projected, projected - usedPercent);
    }
}

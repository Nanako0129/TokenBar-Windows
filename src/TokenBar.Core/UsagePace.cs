using System.Globalization;
using TokenBar.Interop;

namespace TokenBar.Core;

// Usage pace — port of TokenBarCore/UsagePace.swift (originally
// src/lib/usagePace.ts, itself ported from codexbar's UsagePace).
//
// Given a rate-limit window's length and reset time, work out how much you'd
// be *expected* to have used if you paced evenly, compare it to actual usage,
// and classify the gap. Positive delta = ahead of pace ("in deficit", burning
// fast); negative = behind pace ("in reserve"). Also projects when the window
// empties at the current burn rate.

/// <summary>How the pace marker is derived (PaceMode in settings).</summary>
public enum PaceMode
{
    Historical,
    Linear,
    Off,
}

public enum PaceStage
{
    OnTrack,
    SlightlyAhead,
    Ahead,
    FarAhead,
    SlightlyBehind,
    Behind,
    FarBehind,
}

public static class PaceStageExtensions
{
    public static bool IsDeficit(this PaceStage stage) =>
        stage is PaceStage.SlightlyAhead or PaceStage.Ahead or PaceStage.FarAhead;
}

public sealed record UsagePace(
    PaceStage Stage,
    // actual − expected, in percentage points (>0 = ahead/deficit).
    double DeltaPercent,
    double ExpectedUsedPercent,
    double ActualUsedPercent,
    // Seconds until the window empties at the current rate, if before reset.
    double? EtaSeconds,
    // True if the current rate lasts past the reset (won't run out).
    bool WillLastToReset)
{
    /// <summary>Short left-hand label: "On pace" / "12% in deficit" /
    /// "8% in reserve".</summary>
    public string Label
    {
        get
        {
            if (Stage == PaceStage.OnTrack)
            {
                return "On pace";
            }

            var d = (int)Math.Round(Math.Abs(DeltaPercent), MidpointRounding.AwayFromZero);
            return Stage.IsDeficit() ? $"{d}% in deficit" : $"{d}% in reserve";
        }
    }

    /// <summary>Right-hand projection: "Lasts until reset" /
    /// "Projected empty in 2h 10m".</summary>
    public string? EtaText
    {
        get
        {
            if (WillLastToReset)
            {
                return "Lasts until reset";
            }

            if (EtaSeconds is not { } eta)
            {
                return null;
            }

            var t = DurationText(eta);
            return t == "now" ? "Projected empty now" : $"Projected empty in {t}";
        }
    }

    public static string DurationText(double seconds)
    {
        var m = (int)Math.Round(seconds / 60, MidpointRounding.AwayFromZero);
        if (m < 1) { return "now"; }
        if (m < 60) { return $"{m}m"; }
        var h = m / 60;
        var rem = m % 60;
        if (h < 24) { return rem > 0 ? $"{h}h {rem}m" : $"{h}h"; }
        var days = h / 24;
        var hr = h % 24;
        return hr > 0 ? $"{days}d {hr}h" : $"{days}d";
    }

    /// <summary>Compute *linear* pace for a window, or null if it can't be
    /// derived yet.</summary>
    public static UsagePace? Compute(UsageWindow window, DateTimeOffset now) =>
        ComputeCore(window, now, expectedOverride: null);

    /// <summary>Compute pace under the user's chosen mode:
    /// Off → null (no pace marker). Historical → use the backend's historical
    /// expected-percent if present, otherwise transparently fall back to
    /// linear. Linear → naive elapsed/duration pace.</summary>
    public static UsagePace? Compute(UsageWindow window, PaceMode mode, DateTimeOffset now)
    {
        if (mode == PaceMode.Off)
        {
            return null;
        }

        double? expectedOverride = mode == PaceMode.Historical && window.HistoricalExpectedPercent is { } h
            ? Clamp(h, 0, 100)
            : null;
        var pace = ComputeCore(window, now, expectedOverride);
        if (pace is null)
        {
            return null;
        }

        // In historical mode the run-out *probability* (share of past weeks
        // that hit the cap) is a better lasts/empty signal than the naive
        // linear burn rate — otherwise the card could read "in reserve ·
        // Projected empty" at once. If most past weeks lasted, project "Lasts
        // until reset"; codexbar does the same.
        if (expectedOverride is not null && window.RunOutProbability is { } probability)
        {
            var lasts = probability < 0.5;
            return pace with
            {
                EtaSeconds = lasts ? null : pace.EtaSeconds,
                WillLastToReset = lasts,
            };
        }

        return pace;
    }

    private static UsagePace? ComputeCore(
        UsageWindow window, DateTimeOffset now, double? expectedOverride)
    {
        if (window.ResetsAt is not { } resetsAtRaw ||
            window.WindowMinutes is not { } windowMinutes || windowMinutes <= 0 ||
            ParseRfc3339(resetsAtRaw) is not { } resetsAt)
        {
            return null;
        }

        var duration = windowMinutes * 60.0;
        var timeUntilReset = (resetsAt - now).TotalSeconds;
        if (timeUntilReset <= 0 || timeUntilReset > duration)
        {
            return null;
        }

        var elapsed = Clamp(duration - timeUntilReset, 0, duration);
        // Expected used-percent: historical override when available, else the
        // naive linear elapsed/duration. The rest (delta/stage/ETA) is
        // identical either way.
        var expected = expectedOverride ?? Clamp(elapsed / duration * 100, 0, 100);
        var actual = Clamp(window.UsedPercent, 0, 100);
        if (elapsed == 0 && actual > 0)
        {
            return null;
        }

        var delta = actual - expected;

        double? etaSeconds = null;
        var willLastToReset = false;
        if (elapsed > 0 && actual > 0)
        {
            var rate = actual / elapsed; // percentage points per second
            if (rate > 0)
            {
                var remaining = Math.Max(0, 100 - actual);
                var candidate = remaining / rate;
                if (candidate >= timeUntilReset)
                {
                    willLastToReset = true;
                }
                else
                {
                    etaSeconds = candidate;
                }
            }
        }
        else if (elapsed > 0 && actual == 0)
        {
            willLastToReset = true;
        }

        return new UsagePace(
            StageFor(delta), delta, expected, actual, etaSeconds, willLastToReset);
    }

    private static PaceStage StageFor(double delta)
    {
        var a = Math.Abs(delta);
        if (a <= 2) { return PaceStage.OnTrack; }
        if (a <= 6) { return delta >= 0 ? PaceStage.SlightlyAhead : PaceStage.SlightlyBehind; }
        if (a <= 12) { return delta >= 0 ? PaceStage.Ahead : PaceStage.Behind; }
        return delta >= 0 ? PaceStage.FarAhead : PaceStage.FarBehind;
    }

    private static readonly string[] Rfc3339Formats =
    [
        "yyyy-MM-dd'T'HH:mm:ssK",
        "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK",
    ];

    /// <summary>RFC3339 parser tolerating fractional seconds (the backend
    /// emits both). TryParseExact keeps Swift ISO8601DateFormatter's
    /// strictness — a lenient TryParse would accept non-RFC3339 strings and
    /// show a pace the macOS app would suppress.</summary>
    internal static DateTimeOffset? ParseRfc3339(string s) =>
        DateTimeOffset.TryParseExact(
            s, Rfc3339Formats, CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal, out var parsed)
            ? parsed
            : null;

    private static double Clamp(double v, double lo, double hi) => Math.Min(hi, Math.Max(lo, v));

    /// <summary>codexbar-style historical run-out risk, e.g.
    /// "≈ 30% run-out risk", or null.</summary>
    public static string? RunOutRiskLabel(UsageWindow window)
    {
        if (window.RunOutProbability is not { } probability)
        {
            return null;
        }

        var pct = (int)Math.Round(Clamp(probability, 0, 1) * 100, MidpointRounding.AwayFromZero);
        return pct <= 0 ? null : $"≈ {pct}% run-out risk";
    }
}

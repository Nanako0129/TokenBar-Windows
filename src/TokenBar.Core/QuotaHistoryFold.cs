using TokenBar.Interop;

namespace TokenBar.Core;

// Port of TokenBarCore/QuotaHistory.swift's cycle fold, comments included: they
// record the defect each rule exists to prevent.
//
// Deliberately NOT ported: `rows`, `spans`, `spanTotals` and `inScope`. That
// half is the message join and depends on WindowMessage, UsageAttribution,
// WindowEquivalence and ModelScope, none of which exist on Windows yet. Nor
// QuotaOverviewFold.summaries, where the strip card's "ran out / never ran out"
// lives; this fold ships the PeakUsedPercent it will read.

/// <summary>One recorded reset cycle, derived from the persisted samples alone.</summary>
/// <param name="ResetAtMs">Reset instant in ms. Doubles as the cycle's identity —
/// the engine groups its samples by exactly this value.</param>
/// <param name="UsedPercent">
/// How much of the allowance this cycle consumed: the span between the lowest
/// and highest reading in it, NOT the highest reading alone.
/// <para>
/// The distinction is not cosmetic. Sampling starts whenever the app happens to
/// be running, so a cycle first observed at 40% used would report 40% as its
/// consumption when the app only witnessed the last few points of it. The span
/// is what this app can honestly claim to have seen.
/// </para>
/// <para>
/// Note the floor this leaves: the store rejects <c>usedPercent == 0</c>, so a
/// cycle's first reading is always above zero and the span necessarily omits
/// whatever was consumed before it. That understates, and understating is the
/// right direction — the alternative assumes the app witnessed a window it may
/// have joined late.
/// </para>
/// </param>
/// <param name="PeakUsedPercent">
/// The highest absolute reading seen in this cycle.
/// <para>
/// Separate from <paramref name="UsedPercent"/>, which is the observed SPAN. The
/// two answer different questions and only coincide when the app watched the
/// cycle from zero: a cycle first seen at 40% and last seen at 100% consumed 60
/// points as far as this machine can tell, and reached the ceiling. Deriving
/// "never ran out" from the span called that cycle a quiet one.
/// </para>
/// </param>
/// <param name="FirstSampleMs">First reading's instant. Carried because a ratio
/// is only meaningful when its numerator and denominator cover the same
/// interval, and the denominator is a span between two readings — not the whole
/// window. Usage from a stretch the app was not running for would otherwise be
/// counted against quota movement nobody observed.</param>
/// <param name="LastSampleMs">Last reading's instant, same reason.</param>
/// <param name="ObservedFraction">Fraction of the window the samples actually
/// cover, 0…1. A cycle observed for eight minutes of five hours is not evidence
/// about that cycle, and the UI has to be able to say so.</param>
public sealed record QuotaCycle(
    long ResetAtMs,
    long StartMs,
    double UsedPercent,
    double PeakUsedPercent,
    int SampleCount,
    double ObservedFraction,
    long FirstSampleMs,
    long LastSampleMs)
{
    /// <summary>
    /// How far back this cycle's evidence reaches — the earlier of where the
    /// window is computed to have started and where sampling actually began.
    /// <para>
    /// Normally <see cref="StartMs"/>, since the first reading lands after the
    /// window opens. They invert when the provider SHORTENS its reported
    /// duration mid-cycle: <see cref="StartMs"/> is derived from the newest
    /// sample's duration, so a window that went from seven days to five moves
    /// its own start forward, past readings already taken. Bounding a message
    /// scan at <see cref="StartMs"/> would then drop usage from before it while
    /// <see cref="UsedPercent"/> — the span between the first and last reading —
    /// still counts the movement those readings showed. Numerator short,
    /// denominator whole.
    /// </para>
    /// </summary>
    public long EvidenceStartMs => Math.Min(StartMs, FirstSampleMs);

    public long DurationMs => ResetAtMs - StartMs;
}

/// <summary>
/// The running cycle: where it sits on the clock, and every reading taken
/// inside it. See <see cref="QuotaHistoryFold.Active"/>.
/// <para>
/// <see cref="ResetAtMs"/> and <see cref="StartMs"/> are nullable together, and
/// that is the third outcome this type exists to carry: a window that IS
/// running but whose newest reading reports no usable duration cannot be
/// placed on an axis. "Nothing is running" and "something is running that we
/// cannot place" are different sentences on the card, and a record that could
/// only say "no cycle" would have merged them into the first — which is the
/// one that claims the user has stopped working.
/// </para>
/// </summary>
public sealed record QuotaActiveCycle(
    long? ResetAtMs, long? StartMs, IReadOnlyList<QuotaSample> Samples)
{
    /// <summary>True when the window has both ends and the geometry can be
    /// asked for a chart.</summary>
    public bool IsPlaced => ResetAtMs is not null && StartMs is not null;
}

public static class QuotaHistoryFold
{
    /// <summary>
    /// Groups samples into cycles, newest first.
    /// <para>
    /// <c>DurationSeconds</c> is per sample rather than per cycle, so the
    /// cycle's length is taken from its own newest sample: a window whose
    /// provider changed its reported duration mid-cycle should be placed by what
    /// it reports now, not by what it reported when sampling began.
    /// </para>
    /// <para>
    /// Completed cycles only. The running cycle is excluded, and each sample says
    /// for itself whether it is in it. Folding it in put the running cycle in a
    /// strip captioned "past windows", let a partially observed span stand beside
    /// completed ones, and let it count toward the three-cycle threshold the
    /// equivalence needs — an estimate that would then change under the reader as
    /// the cycle filled.
    /// </para>
    /// <para>
    /// The first version took the series' <c>activeResetAt</c> and compared it to
    /// each sample's <c>resetAt</c>. Those are not comparable values: the engine
    /// publishes the RAW provider reset there while every stored <c>resetAt</c>
    /// has been through <c>normalize_sample_reset</c>, so the exclusion silently
    /// did nothing whenever the provider's reset was off the quantum — which is
    /// most of the time. <c>IsActiveGroup</c> is the producer's own answer, so
    /// there is no longer a parameter a caller can omit and no rule stated twice
    /// in two languages.
    /// </para>
    /// <para>
    /// Returns EVERY recorded cycle. The cap that bounds the message scan is
    /// <see cref="Considered"/>, applied by the consumers that pay for a scan —
    /// not here. Putting it here looked like the tidier place ("bound it once, at
    /// the source") and was wrong: the overview fold derives LIFETIME facts from
    /// this list — peak percent, never-exhausted, cycle count — and costs no scan
    /// at all. A capped fold made a window that ran out thirty-three cycles ago
    /// report that it never had.
    /// </para>
    /// </summary>
    public static IReadOnlyList<QuotaCycle> Cycles(IReadOnlyList<QuotaHistorySample> samples)
    {
        var grouped = new Dictionary<long, List<QuotaHistorySample>>();
        foreach (var sample in samples)
        {
            if (sample.IsActiveGroup)
            {
                continue;
            }

            if (!grouped.TryGetValue(sample.ResetAt, out var group))
            {
                grouped[sample.ResetAt] = group = [];
            }

            group.Add(sample);
        }

        var cycles = new List<QuotaCycle>(grouped.Count);
        foreach (var (resetAt, raw) in grouped)
        {
            var sorted = raw.OrderBy(sample => sample.SampledAt).ToList();
            var first = sorted[0];
            var last = sorted[^1];
            if (last.DurationSeconds <= 0)
            {
                continue;
            }

            var maximum = sorted.Max(sample => sample.UsedPercent);
            var minimum = sorted.Min(sample => sample.UsedPercent);
            cycles.Add(new QuotaCycle(
                ResetAtMs: resetAt * 1000,
                StartMs: (resetAt - last.DurationSeconds) * 1000,
                UsedPercent: maximum - minimum,
                PeakUsedPercent: maximum,
                SampleCount: sorted.Count,
                ObservedFraction: Math.Clamp(
                    (double)(last.SampledAt - first.SampledAt) / last.DurationSeconds, 0, 1),
                FirstSampleMs: first.SampledAt * 1000,
                LastSampleMs: last.SampledAt * 1000));
        }

        return cycles.OrderByDescending(cycle => cycle.ResetAtMs).ToList();
    }

    /// <summary>
    /// The cycle still running, and the readings taken inside it — the half
    /// <see cref="Cycles"/> deliberately excludes.
    /// <para>
    /// The Session-window card draws exactly this: a line through the samples
    /// of the window the user is in right now. It is the same
    /// <c>IsActiveGroup</c> bit that keeps the running cycle out of the strip's
    /// "past windows", read the other way round, so the two surfaces cannot
    /// disagree about which cycle is current.
    /// </para>
    /// <para>
    /// Placement comes from the NEWEST active sample's own
    /// <c>ResetAt</c>/<c>DurationSeconds</c>, the same rule
    /// <see cref="Cycles"/> applies per cycle: a provider that changes its
    /// reported duration mid-window should have the window placed by what it
    /// reports now. Every active sample is carried, including any taken before
    /// such a shift — the geometry clips to the window bounds, and dropping a
    /// reading here would delete evidence the card is meant to show.
    /// </para>
    /// <para>Null only when nothing is running. A running window whose newest
    /// sample reports no usable duration comes back with its readings and no
    /// placement (<see cref="QuotaActiveCycle.IsPlaced"/> false), because an
    /// unplaceable window is not an idle one and the card has a different
    /// sentence for each.</para>
    /// </summary>
    public static QuotaActiveCycle? Active(IReadOnlyList<QuotaHistorySample> samples)
    {
        var active = samples
            .Where(sample => sample.IsActiveGroup)
            .OrderBy(sample => sample.SampledAt)
            .ToList();
        if (active.Count == 0)
        {
            return null;
        }

        var newest = active[^1];
        var placed = newest.DurationSeconds > 0;
        return new QuotaActiveCycle(
            ResetAtMs: placed ? newest.ResetAt * 1000 : null,
            StartMs: placed ? (newest.ResetAt - newest.DurationSeconds) * 1000 : null,
            Samples: active
                .Select(sample => new QuotaSample(sample.SampledAt * 1000, sample.UsedPercent))
                .ToList());
    }

    /// <summary>
    /// The newest cycles a scan-paying surface may look at. Applied by the
    /// consumers whose cost grows with the answer: the history card's cycle list,
    /// which bounds the union scan through its oldest entry's
    /// <see cref="QuotaCycle.EvidenceStartMs"/>, and the admitted set behind the
    /// equivalence. Lifetime summaries deliberately do not call this.
    /// </summary>
    public static IReadOnlyList<QuotaCycle> Considered(IReadOnlyList<QuotaCycle> cycles) =>
        cycles.Take(ConsideredCycles).ToList();

    /// <summary>
    /// How far back any cycle-derived surface reaches, in cycles.
    /// <para>
    /// The engine retains 128 cycles per series and this fold used to return all
    /// of them, but nothing downstream wants that many: the history card draws 12
    /// rows and the overview strip 16. The cost of the extra ones is not the
    /// list, it is that the OLDEST cycle sets where the message scan starts —
    /// <c>min(windowStart, cycles.last.startMs)</c> — so a 5-hour session window
    /// at 128 cycles asked for a 26-day scan to render twelve rows, and a weekly
    /// window walked that start backwards for ever.
    /// </para>
    /// <para>
    /// 32, not the 16 the issue proposed. A sweep over real history on
    /// 2026-08-21 put 16 on the cliff edge: two runs an hour apart, differing by
    /// one newly completed cycle, landed on opposite sides of "here is the
    /// number" and "we cannot say". 32 does not bite on any window there today
    /// (the widest has 27), which is the point: it bounds the growth without
    /// moving a number anyone reads.
    /// </para>
    /// </summary>
    public const int ConsideredCycles = 32;
}

using TokenBar.Interop;

namespace TokenBar.Core;

// Port of TokenBarCore/QuotaHeatmap.swift, comments included.

/// <summary>
/// When a window's allowance actually gets consumed, as a weekday-by-hour grid.
/// <para>
/// Built from the persisted samples alone — no message scan — so it costs the
/// same read the rest of this lens already pays. The question it answers is not
/// "when do I work", which the usage chart already covers, but "when does the
/// allowance move", and those differ: a long cheap session and a short expensive
/// one look the same in a message count and nothing alike here.
/// </para>
/// </summary>
/// <param name="Cells"><c>[weekday][hour]</c> in percentage points of the
/// window's allowance. Weekday 0 = Monday, hour 0…23. Always 7 x 24; an idle
/// slot is 0, not absent.</param>
/// <param name="Peak">The heaviest slot, and therefore the top of the colour
/// ramp. Zero means nothing was placed, which the card must read as "no data"
/// rather than dividing by it.</param>
/// <param name="Total">Everything the grid accounts for.</param>
/// <param name="UnplacedPercent">Consumption that was measured but could not be
/// placed in time, because the gap between the two readings that bracket it is
/// longer than <see cref="QuotaHeatmapFold.MaximumGapSeconds"/>. Reported rather
/// than dropped silently: a large value means the grid is understating, and the
/// card says so.</param>
/// <param name="ObservedDays">Distinct local calendar days carrying at least one
/// reading. The honest denominator for "is this enough to read a weekly rhythm" —
/// a 168-slot grid over four days is mostly white space.</param>
public sealed record QuotaHeatmap(
    IReadOnlyList<IReadOnlyList<double>> Cells,
    double Peak,
    double Total,
    double UnplacedPercent,
    int ObservedDays)
{
    public static QuotaHeatmap Empty { get; } = new(
        Cells: Enumerable.Range(0, 7).Select(_ => (IReadOnlyList<double>)new double[24]).ToList(),
        Peak: 0, Total: 0, UnplacedPercent: 0, ObservedDays: 0);

    public bool IsEmpty => Total <= 0;

    /// <summary>
    /// Whether the allowance moved at all, placed or not.
    /// <para>
    /// NOT <c>!IsEmpty</c>. <see cref="Total"/> counts only what the grid could
    /// place, so a window whose every reading pair straddles more than
    /// <see cref="QuotaHeatmapFold.MaximumGapSeconds"/> has <c>Total == 0</c>
    /// while having consumed real allowance. Treating that as nothing dropped the
    /// window from the picker and made the card report "no allowance movement
    /// recorded yet" — the opposite of what happened, with the one line that
    /// explains it unreachable behind the same condition.
    /// </para>
    /// <para>
    /// Any positive value, not a full point. Readings are arbitrary finite
    /// percentages, so a pair of readings seven hours apart moving 0.5 points is
    /// movement this fold recorded and a <c>&gt;= 1</c> test would discard. The
    /// one-point threshold belongs to the footnote, which is a question about
    /// whether a line is worth drawing, not about whether anything happened.
    /// </para>
    /// </summary>
    public bool HasMovement => !IsEmpty || UnplacedPercent > 0;
}

public static class QuotaHeatmapFold
{
    /// <summary>
    /// Longer than this between two readings and the consumption between them is
    /// not placed at all.
    /// <para>
    /// The provider reports a level, not an event, so all we ever know is that
    /// some amount was spent between two readings. Spreading that across a
    /// three-day gap would draw burn at 04:00 on days the machine was asleep — an
    /// invented pattern, and the grid exists to show a pattern. Six hours is long
    /// enough to cover a normal poll gap on a sparsely-sampled weekly window and
    /// short enough that a spread stays inside one working session.
    /// </para>
    /// </summary>
    public const long MaximumGapSeconds = 6 * 3_600;

    /// <summary>
    /// Consumption per weekday-hour slot.
    /// <para>
    /// Deltas are taken WITHIN a reset cycle, never across one: a reset drops the
    /// reading back to near zero, and the difference across that boundary is the
    /// whole previous cycle inverted, not consumption. Cycles are keyed by
    /// <c>ResetAt</c>, which is what <see cref="QuotaHistoryFold"/> groups on too.
    /// </para>
    /// <para>
    /// A delta is spread across the hours its interval covers, weighted by the
    /// time in each, rather than charged to the reading that observed it. On a
    /// window polled every minute the two are identical; on one polled hourly,
    /// charging the observing reading would pile a whole hour of work onto the
    /// minute it happened to be noticed.
    /// </para>
    /// </summary>
    public static QuotaHeatmap Build(
        IReadOnlyList<QuotaHistorySample> samples,
        TimeZoneInfo? timeZone = null)
    {
        var zone = timeZone ?? TimeZoneInfo.Local;
        var cells = new double[7][];
        for (var weekday = 0; weekday < 7; weekday++)
        {
            cells[weekday] = new double[24];
        }

        var unplaced = 0.0;
        // Local days, not `SampledAt / 86_400`. That divides UTC seconds into UTC
        // dates, while every cell below is placed on the LOCAL weekday and hour —
        // so away from UTC the denominator counted a different calendar from the
        // grid it describes, splitting one local day either side of UTC midnight
        // in two and merging two into one.
        var days = new HashSet<long>();

        foreach (var cycle in samples.GroupBy(sample => sample.ResetAt))
        {
            var sorted = cycle.OrderBy(sample => sample.SampledAt).ToList();
            foreach (var sample in sorted)
            {
                days.Add(FloorDiv(LocalSeconds(sample.SampledAt, zone), 86_400));
            }

            for (var index = 1; index < sorted.Count; index++)
            {
                var previous = sorted[index - 1];
                var current = sorted[index];
                var delta = current.UsedPercent - previous.UsedPercent;
                // Negative means the reading went backwards inside one cycle — a
                // refill, or a provider correction. Not consumption, and not
                // something to subtract from a neighbouring slot either.
                if (delta <= 0)
                {
                    continue;
                }

                var span = current.SampledAt - previous.SampledAt;
                if (span <= 0)
                {
                    continue;
                }

                if (span > MaximumGapSeconds)
                {
                    unplaced += delta;
                    continue;
                }

                var cursor = previous.SampledAt;
                while (cursor < current.SampledAt)
                {
                    var local = LocalSeconds(cursor, zone);
                    var intoHour = local - FloorDiv(local, 3_600) * 3_600;
                    // A zone offset that cannot advance the cursor would spin
                    // forever; step at least one second and keep going.
                    var segmentEnd = Math.Min(
                        Math.Max(cursor + (3_600 - intoHour), cursor + 1),
                        current.SampledAt);
                    var day = FloorDiv(local, 86_400);
                    // Weekday 0 = Monday. Unix day 0 was a Thursday, so +3 lands
                    // it at index 3: a grid that starts on Sunday puts the two
                    // quietest days at opposite ends and hides the weekend as a
                    // block.
                    var weekday = (int)(((day + 3) % 7 + 7) % 7);
                    var hour = (int)(local - day * 86_400) / 3_600;
                    cells[weekday][hour] += delta * (segmentEnd - cursor) / span;
                    cursor = segmentEnd;
                }
            }
        }

        var flat = cells.SelectMany(row => row).ToList();
        return new QuotaHeatmap(
            Cells: cells,
            Peak: flat.Count == 0 ? 0 : flat.Max(),
            Total: flat.Sum(),
            UnplacedPercent: unplaced,
            ObservedDays: days.Count);
    }

    private static long LocalSeconds(long unixSeconds, TimeZoneInfo zone) =>
        unixSeconds + (long)zone
            .GetUtcOffset(DateTimeOffset.FromUnixTimeSeconds(unixSeconds))
            .TotalSeconds;

    private static long FloorDiv(long value, long divisor)
    {
        var quotient = Math.DivRem(value, divisor, out var remainder);
        return remainder < 0 ? quotient - 1 : quotient;
    }
}

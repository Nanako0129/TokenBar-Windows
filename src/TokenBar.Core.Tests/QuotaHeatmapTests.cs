using TokenBar.Interop;
using Xunit;

namespace TokenBar.Core.Tests;

// Expectations transcribed from TokenBarCore/QuotaHeatmap.swift, not invented.
public class QuotaHeatmapTests
{
    private const long FiveHours = 5 * 3_600;
    private const long ResetAt = 1_767_330_000;

    // A fixed +08:00 with no DST, so the local-versus-UTC assertions below say
    // what they mean wherever the suite runs.
    private static readonly TimeZoneInfo PlusEight = TimeZoneInfo.CreateCustomTimeZone(
        "test-plus-eight", TimeSpan.FromHours(8), "test-plus-eight", "test-plus-eight");

    private static QuotaHistorySample Sample(long resetAt, long sampledAt, double usedPercent) =>
        new(
            ResetAt: resetAt,
            DurationSeconds: FiveHours,
            DurationSource: QuotaHistoryDurationSource.Provider,
            UsedPercent: usedPercent,
            SampledAt: sampledAt,
            Origin: QuotaHistorySampleOrigin.LiveV3,
            IsActiveGroup: false);

    // "A delta is spread across the hours its interval covers, weighted by the
    // time in each" — and the days are LOCAL days: 23:30 and 00:30 UTC are two
    // UTC dates and one local one at +08:00.
    [Fact]
    public void DeltasAreSpreadOverLocalHoursAndCountedAsOneLocalDay()
    {
        // 2026-01-01T23:30Z and 2026-01-02T00:30Z = 07:30 and 08:30 local, Friday.
        var grid = QuotaHeatmapFold.Build(
            [Sample(ResetAt, 1_767_310_200, 10), Sample(ResetAt, 1_767_313_800, 16)],
            PlusEight);

        const int friday = 4;
        Assert.Equal(3, grid.Cells[friday][7], 9);
        Assert.Equal(3, grid.Cells[friday][8], 9);
        Assert.Equal(6, grid.Total, 9);
        Assert.Equal(3, grid.Peak, 9);
        Assert.Equal(0, grid.UnplacedPercent);
        Assert.Equal(1, grid.ObservedDays);
    }

    // "Longer than this between two readings and the consumption between them is
    // not placed at all."
    [Fact]
    public void APairStraddlingMoreThanSixHoursIsUnplaced()
    {
        var start = ResetAt - FiveHours;

        var grid = QuotaHeatmapFold.Build(
            [Sample(ResetAt, start, 10), Sample(ResetAt, start + QuotaHeatmapFold.MaximumGapSeconds + 1, 15)],
            PlusEight);

        Assert.Equal(0, grid.Total);
        Assert.Equal(5, grid.UnplacedPercent, 9);
    }

    [Fact]
    public void APairExactlySixHoursApartIsStillPlaced()
    {
        var start = ResetAt - FiveHours;

        var grid = QuotaHeatmapFold.Build(
            [Sample(ResetAt, start, 10), Sample(ResetAt, start + QuotaHeatmapFold.MaximumGapSeconds, 15)],
            PlusEight);

        Assert.Equal(5, grid.Total, 9);
        Assert.Equal(0, grid.UnplacedPercent);
    }

    // "NOT !isEmpty. `total` counts only what the grid could place, so a window
    // whose every reading pair straddles more than `maximumGapSeconds` has
    // `total == 0` while having consumed real allowance."
    [Fact]
    public void HasMovementIsNotTotalAboveZero()
    {
        var start = ResetAt - FiveHours;
        var unplacedOnly = QuotaHeatmapFold.Build(
            [Sample(ResetAt, start, 10), Sample(ResetAt, start + QuotaHeatmapFold.MaximumGapSeconds + 1, 10.5)],
            PlusEight);

        Assert.True(unplacedOnly.IsEmpty);
        Assert.Equal(0, unplacedOnly.Total);
        // "Any positive value, not a full point."
        Assert.True(unplacedOnly.HasMovement);

        // A single reading moves nothing, so neither question is true of it.
        var oneReading = QuotaHeatmapFold.Build([Sample(ResetAt, start, 10)], PlusEight);
        Assert.True(oneReading.IsEmpty);
        Assert.False(oneReading.HasMovement);
        Assert.Equal(1, oneReading.ObservedDays);

        // And a window with no observations at all.
        var nothing = QuotaHeatmapFold.Build([], PlusEight);
        Assert.False(nothing.HasMovement);
        Assert.Equal(0, nothing.ObservedDays);
        Assert.Equal(7, nothing.Cells.Count);
        Assert.All(nothing.Cells, row =>
        {
            Assert.Equal(24, row.Count);
            Assert.All(row, cell => Assert.Equal(0, cell));
        });
    }

    // "Deltas are taken WITHIN a reset cycle, never across one: a reset drops the
    // reading back to near zero, and the difference across that boundary is the
    // whole previous cycle inverted, not consumption."
    [Fact]
    public void NoDeltaIsTakenAcrossAReset()
    {
        var older = ResetAt - FiveHours;

        var grid = QuotaHeatmapFold.Build(
        [
            Sample(older, older - 3_600, 80),
            Sample(ResetAt, older + 3_600, 5),
            Sample(ResetAt, older + 7_200, 9),
        ], PlusEight);

        // Only the 5 -> 9 pair inside the newer cycle is consumption.
        Assert.Equal(4, grid.Total, 9);
    }

    // "Negative means the reading went backwards inside one cycle — a refill, or
    // a provider correction. Not consumption."
    [Fact]
    public void ABackwardsReadingIsNotConsumption()
    {
        var start = ResetAt - FiveHours;

        var grid = QuotaHeatmapFold.Build(
            [Sample(ResetAt, start, 40), Sample(ResetAt, start + 3_600, 10)],
            PlusEight);

        Assert.Equal(0, grid.Total);
        Assert.False(grid.HasMovement);
    }

    // The running cycle is NOT excluded here: its consumption is real
    // consumption, and the grid answers "when does the allowance move".
    [Fact]
    public void TheRunningCycleStillContributesToTheGrid()
    {
        var start = ResetAt - FiveHours;
        var active = new[]
        {
            Sample(ResetAt, start, 10) with { IsActiveGroup = true },
            Sample(ResetAt, start + 3_600, 17) with { IsActiveGroup = true },
        };

        Assert.Equal(7, QuotaHeatmapFold.Build(active, PlusEight).Total, 9);
        Assert.Empty(QuotaHistoryFold.Cycles(active));
    }
}

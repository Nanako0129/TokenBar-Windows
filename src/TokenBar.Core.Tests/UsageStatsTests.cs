using Xunit;

namespace TokenBar.Core.Tests;

// Ported from SelfTest.swift's ISODay / streaks / grid blocks.
public class UsageStatsTests
{
    private static Dictionary<string, PerDay> PerDayOf(params string[] dates) =>
        dates.ToDictionary(d => d, d => new PerDay(d, Tokens: 10, Cost: 1, Intensity: 1));

    [Fact]
    public void EpochDayNumberIsZero() => Assert.Equal(0, ISODay.Parse("1970-01-01")!.Value.Number);

    [Fact]
    public void IsoRoundTrips() => Assert.Equal("2026-06-10", ISODay.Parse("2026-06-10")!.Value.Iso);

    [Fact]
    public void InvalidIsoRejected() => Assert.Null(ISODay.Parse("garbage"));

    [Fact]
    public void EpochDayIsThursdayAndJun7IsSunday()
    {
        Assert.Equal(4, ISODay.Parse("1970-01-01")!.Value.Weekday);
        Assert.Equal(0, ISODay.Parse("2026-06-07")!.Value.Weekday);
    }

    [Fact]
    public void StreaksLongestAndCurrent()
    {
        var s = Streaks.Compute(
            PerDayOf("2026-06-01", "2026-06-02", "2026-06-03", "2026-06-05", "2026-06-06"),
            "2026-06-01", "2026-06-06");
        Assert.Equal(3, s.Longest);
        Assert.Equal(2, s.Current);
    }

    [Fact]
    public void BrokenCurrentStreakIsZero()
    {
        var s = Streaks.Compute(PerDayOf("2026-06-01"), "2026-06-01", "2026-06-03");
        Assert.Equal(1, s.Longest);
        Assert.Equal(0, s.Current);
    }

    [Fact]
    public void InvertedRangeIsEmpty()
    {
        var s = Streaks.Compute(new Dictionary<string, PerDay>(), "2026-06-10", "2026-06-01");
        Assert.Equal(0, s.Longest);
        Assert.Equal(0, s.Current);
    }

    [Fact]
    public void GridLayoutMatchesGitHubShape()
    {
        var grid = Grid.Build("2026", new Dictionary<string, PerDay>
        {
            ["2026-01-01"] = new("2026-01-01", 500, 1, 1),
            ["2025-12-29"] = new("2025-12-29", 900, 1, 1),
        });

        Assert.Equal(7, grid.Rows);
        Assert.True(grid.Cols >= 53);
        Assert.Equal(grid.Cols * 7, grid.Cells.Count);
        // Starts on the Sunday before Jan 1.
        Assert.Equal("2025-12-28", grid.Cells[0].Date);
        Assert.False(grid.Cells[0].InYear);
        // Jan 1 2026 is a Thursday → col 0 row 4, active.
        var jan1 = grid.Cells.First(c => c.Date == "2026-01-01");
        Assert.Equal((0, 4, true), (jan1.Col, jan1.Row, jan1.Active));
        // Out-of-year tokens don't drive max and out-of-year cells are inactive.
        Assert.Equal(500, grid.MaxTokens);
        Assert.False(grid.Cells.First(c => c.Date == "2025-12-29").Active);
    }
}

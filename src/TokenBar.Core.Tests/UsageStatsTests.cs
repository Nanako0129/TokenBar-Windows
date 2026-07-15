using TokenBar.Interop;
using Xunit;

namespace TokenBar.Core.Tests;

// Ported from SelfTest.swift's ISODay / streaks / grid blocks, plus the
// hidden-client filtered-range and visibility-helper delta.
public class UsageStatsTests
{
    private static Dictionary<string, PerDay> PerDayOf(params string[] dates) =>
        dates.ToDictionary(d => d, d => new PerDay(d, Tokens: 10, Cost: 1, Intensity: 1));

    private static ContributionClient Client(string id, long tokens, double cost = 1) =>
        new(id, "m", "anthropic", new TokenBreakdown(tokens, 0, 0, 0, 0), cost, Messages: 1);

    private static Contribution Day(string date, params ContributionClient[] clients) =>
        new(date, new ContributionTotals(0, 0, 0), 1, new TokenBreakdown(0, 0, 0, 0, 0), clients);

    private static UsagePayload Payload(string metaStart, string metaEnd, params Contribution[] days) =>
        new(
            new UsageMeta("g", "v", new DateRange(metaStart, metaEnd)),
            new UsageSummary(0, 0, 0, 0, 0, 0, [], []),
            [],
            days);

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

    // --- Filtered-range & visibility delta ---

    [Fact]
    public void FilteredStatsDeriveRangeFromSelectedClients()
    {
        // meta range spans to 2026-06-20 (a hidden client's later activity), but
        // the selected client (claude) last acted on 2026-06-05.
        var payload = Payload(
            "2026-06-01", "2026-06-20",
            Day("2026-06-05", Client("claude", 100)),
            Day("2026-06-20", Client("gemini", 200)));

        var stats = new UsageStats(payload, new HashSet<string> { "claude" });
        // Range shrinks to the selected client's own span.
        Assert.Equal("2026-06-05", stats.DateRange.Start);
        Assert.Equal("2026-06-05", stats.DateRange.End);
    }

    [Fact]
    public void UnfilteredStatsKeepMetaRange()
    {
        var payload = Payload(
            "2026-06-01", "2026-06-20",
            Day("2026-06-05", Client("claude", 100)),
            Day("2026-06-20", Client("gemini", 200)));

        // All present clients selected → nothing filtered → meta range verbatim.
        var stats = new UsageStats(payload, new HashSet<string> { "claude", "gemini" });
        Assert.Equal("2026-06-01", stats.DateRange.Start);
        Assert.Equal("2026-06-20", stats.DateRange.End);
    }

    [Fact]
    public void TotalsSaturateOnCorruptLane()
    {
        var payload = Payload(
            "2026-06-01", "2026-06-02",
            Day("2026-06-01", Client("claude", long.MaxValue)),
            Day("2026-06-02", Client("claude", long.MaxValue)));

        var stats = new UsageStats(payload, new HashSet<string> { "claude" });
        Assert.Equal(long.MaxValue, stats.TotalTokens);
    }

    [Fact]
    public void YearsWithVisibleActivityDropsHiddenOnlyYears()
    {
        var contributions = new[]
        {
            Day("2025-05-01", Client("gemini", 50)),   // only hidden client
            Day("2026-05-01", Client("claude", 10)),   // visible
            Day("2026-06-01", Client("gemini", 20)),   // hidden, same year as visible
        };

        var years = UsageStatsVisibility.YearsWithVisibleActivity(
            contributions, new HashSet<string> { "gemini" });
        Assert.Equal(new HashSet<string> { "2026" }, years);
    }

    [Fact]
    public void HasVisibleActivityReflectsNonHiddenPresence()
    {
        var onlyHidden = new[] { Day("2026-05-01", Client("gemini", 50)) };
        Assert.False(UsageStatsVisibility.HasVisibleActivity(
            onlyHidden, new HashSet<string> { "gemini" }));
        Assert.True(UsageStatsVisibility.HasVisibleActivity(
            onlyHidden, new HashSet<string>()));
    }
}

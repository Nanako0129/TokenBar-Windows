using TokenBar.Interop;
using Xunit;

namespace TokenBar.Core.Tests;

public class MonthlyRowsTests
{
    private static ContributionClient Client(
        string id, string model = "m", long tokens = 0, double cost = 0,
        int messages = 0) =>
        new(id, model, "anthropic", new TokenBreakdown(tokens, 0, 0, 0, 0), cost, messages);

    private static Contribution Day(
        string date,
        IReadOnlyList<ContributionClient> clients,
        IReadOnlyDictionary<string, long>? turns = null) =>
        new(date, new ContributionTotals(0, 0, 0), 0,
            new TokenBreakdown(0, 0, 0, 0, 0), clients, turns);

    private static UsagePayload Payload(params Contribution[] days) =>
        new(
            new UsageMeta("g", "v", new DateRange("2026-05-01", "2026-06-30"),
                PricingMode.BestEffort, CostCoverage.Complete),
            new UsageSummary(0, 0, 0, 0, 0, 0, [], []), [], days);

    [Fact]
    public void DaysFoldIntoMonthsMostRecentFirst()
    {
        var rows = MonthlyRows.Build(
            Payload(
                Day("2026-05-30", [Client("claude", tokens: 100, cost: 1, messages: 2)]),
                Day("2026-06-01", [Client("claude", tokens: 10, cost: 0.5, messages: 1)]),
                Day("2026-06-15", [Client("claude", tokens: 5, cost: 0.25, messages: 3)])),
            ["claude"]);

        Assert.Equal(["2026-06", "2026-05"], rows.Select(r => r.Month));
        Assert.Equal(15L, rows[0].Tokens);
        Assert.Equal(0.75, rows[0].Cost, 6);
        Assert.Equal(4L, rows[0].Messages);
        Assert.Equal(100L, rows[1].Tokens);
    }

    // Daily merges stripes inside one contribution; Monthly has to fold them
    // across every day in the month, which is the behaviour that has no
    // equivalent in DailyRows.
    [Fact]
    public void ModelStripesMergeAcrossDaysWithinAMonth()
    {
        var rows = MonthlyRows.Build(
            Payload(
                Day("2026-06-01", [Client("claude", "opus", tokens: 10, cost: 1)]),
                Day("2026-06-02", [Client("claude", "opus", tokens: 5, cost: 2)]),
                Day("2026-06-03", [Client("claude", "haiku", tokens: 7, cost: 0.5)])),
            ["claude"]);

        var row = Assert.Single(rows);
        Assert.Equal(2, row.Clients.Count);
        var opus = row.Clients.Single(c => c.ModelId == "opus");
        Assert.Equal(15L, opus.Tokens.Total);
        Assert.Equal(3.0, opus.Cost, 6);
        // Ordered by cost, so the merged stripe outranks the cheaper model.
        Assert.Equal("opus", row.Clients[0].ModelId);
    }

    [Fact]
    public void SameModelFromDifferentClientsStaysSeparate()
    {
        var rows = MonthlyRows.Build(
            Payload(
                Day("2026-06-01", [Client("claude", "shared", tokens: 10)]),
                Day("2026-06-02", [Client("codex", "shared", tokens: 4)])),
            ["claude", "codex"]);

        var row = Assert.Single(rows);
        Assert.Equal(2, row.Clients.Count);
        Assert.Equal([10L, 4L], row.Clients.Select(c => c.Tokens.Total).OrderByDescending(t => t));
    }

    // Turn data is per-client and only some clients report it, so a month with
    // one turn-bearing day must keep that count rather than being flattened to
    // zero by the days around it.
    [Fact]
    public void TurnsAccumulateOnlyFromDaysThatHaveThem()
    {
        var rows = MonthlyRows.Build(
            Payload(
                Day("2026-06-01", [Client("claude", messages: 1)],
                    new Dictionary<string, long> { ["claude"] = 3 }),
                Day("2026-06-02", [Client("claude", messages: 1)])),
            ["claude"]);

        var row = Assert.Single(rows);
        Assert.Equal(3L, row.Turns);
        Assert.Equal(["claude"], row.TurnClients);
    }

    [Fact]
    public void MonthWithNoTurnDataReportsNullRatherThanZero()
    {
        var rows = MonthlyRows.Build(
            Payload(Day("2026-06-01", [Client("opencode", messages: 1)])),
            ["opencode"]);

        Assert.Null(Assert.Single(rows).Turns);
    }

    [Fact]
    public void DatesTooShortToBucketAreDropped()
    {
        var rows = MonthlyRows.Build(
            Payload(
                Day("2026-6", [Client("claude", messages: 1)]),
                Day("2026-06-02", [Client("claude", messages: 1)])),
            ["claude"]);

        Assert.Equal(["2026-06"], rows.Select(r => r.Month));
    }
}

using TokenBar.Interop;
using Xunit;

namespace TokenBar.Core.Tests;

public class DailyRowsTests
{
    private static ContributionClient Client(
        string id, long tokens = 0, double cost = 0, int messages = 0) =>
        new(id, "m", "anthropic", new TokenBreakdown(tokens, 0, 0, 0, 0), cost, messages);

    private static Contribution Day(
        string date,
        IReadOnlyList<ContributionClient> clients,
        IReadOnlyDictionary<string, long>? turns = null) =>
        new(date, new ContributionTotals(0, 0, 0), 0,
            new TokenBreakdown(0, 0, 0, 0, 0), clients, turns);

    private static UsagePayload Payload(params Contribution[] days) =>
        new(
            new UsageMeta("g", "v", new DateRange("2026-06-01", "2026-06-30")),
            new UsageSummary(0, 0, 0, 0, 0, 0, [], []), [], days);

    [Fact]
    public void MessageAndCostOnlyRowsAreActiveAndOrdinalDescending()
    {
        var rows = DailyRows.Build(
            Payload(
                Day("2026-06-01", [Client("claude", messages: 2)]),
                Day("2026-06-03", [Client("claude", cost: 1)]),
                Day("2026-06-02", [Client("claude")])),
            ["claude"]);

        Assert.Equal(["2026-06-03", "2026-06-01"], rows.Select(row => row.Date));
        Assert.Equal(1.0, rows[0].Cost);
        Assert.Equal(2L, rows[1].Messages);
        Assert.All(rows, row => Assert.NotEmpty(row.Clients));
    }

    [Fact]
    public void CanonicalMembershipExcludesUnselectedAndKeepsRawDrilldown()
    {
        var rows = DailyRows.Build(
            Payload(Day(
                "2026-06-04",
                [Client("claude-code", tokens: 10), Client("gemini", tokens: 90)])),
            ["claude"]);

        var row = Assert.Single(rows);
        var client = Assert.Single(row.Clients);
        Assert.Equal("claude-code", client.Client);
        Assert.Equal(10L, row.Tokens);
        Assert.Equal(["claude-code"], row.Clients.Select(c => c.Client));
    }

    [Fact]
    public void SingleSupportedTurnScopeQueriesExactRawKey()
    {
        var rows = DailyRows.Build(
            Payload(Day(
                "2026-06-05",
                [Client("claude-code", messages: 1)],
                new Dictionary<string, long>
                {
                    ["claude-code"] = 5,
                    ["claude"] = 99,
                    ["map-only"] = 100,
                })),
            ["claude"]);

        var row = Assert.Single(rows);
        Assert.Equal(["claude"], row.TurnClients);
        Assert.Equal(5L, row.Turns!.Value);
    }

    [Fact]
    public void DualSupportedScopeOrdersCanonicalClientsAndDedupesRawKeys()
    {
        var rows = DailyRows.Build(
            Payload(Day(
                "2026-06-06",
                [
                    Client("claude-code", messages: 1),
                    Client("claude-code", messages: 1),
                    Client("claude", messages: 1),
                    Client("codex-cli", messages: 1),
                    Client("gemini", messages: 1),
                ],
                new Dictionary<string, long>
                {
                    ["claude-code"] = 2,
                    ["claude"] = 3,
                    ["codex-cli"] = 4,
                    ["codex"] = 100,
                    ["gemini"] = 200,
                })),
            ["codex", "claude", "gemini"]);

        var row = Assert.Single(rows);
        Assert.Equal(["codex", "claude"], row.TurnClients);
        Assert.Equal(9L, row.Turns!.Value);
    }

    [Fact]
    public void UnsupportedOnlyScopeIsNullAndMixedScopeIgnoresUnsupportedMapKeys()
    {
        var rows = DailyRows.Build(
            Payload(
                Day(
                    "2026-06-07",
                    [Client("gemini", messages: 1)],
                    new Dictionary<string, long> { ["gemini"] = 100 }),
                Day(
                    "2026-06-08",
                    [Client("claude", messages: 1), Client("gemini", messages: 1)],
                    new Dictionary<string, long> { ["gemini"] = 100 })),
            ["gemini", "claude"]);

        var mixed = Assert.Single(rows.Where(row => row.Date == "2026-06-08"));
        Assert.Equal(["claude"], mixed.TurnClients);
        Assert.Equal(0L, mixed.Turns!.Value);
        var unsupported = Assert.Single(rows.Where(row => row.Date == "2026-06-07"));
        Assert.Null(unsupported.Turns);
        Assert.Empty(unsupported.TurnClients);
    }

    [Fact]
    public void NullAndEmptyMapsProduceZeroWhenSupportedScopeExists()
    {
        var rows = DailyRows.Build(
            Payload(
                Day("2026-06-09", [Client("codex", messages: 1)]),
                Day("2026-06-10", [Client("claude", messages: 1)], new Dictionary<string, long>())),
            ["codex", "claude"]);

        Assert.All(rows, row => Assert.Equal(0L, row.Turns!.Value));
        Assert.Equal(["claude"], rows.Single(row => row.Date == "2026-06-10").TurnClients);
        Assert.Equal(["codex"], rows.Single(row => row.Date == "2026-06-09").TurnClients);
    }

    [Fact]
    public void TurnsDoNotMakeAnInactiveDay()
    {
        var rows = DailyRows.Build(
            Payload(Day(
                "2026-06-11",
                [Client("codex")],
                new Dictionary<string, long> { ["codex"] = 5 })),
            ["codex"]);

        Assert.Empty(rows);
    }

    [Fact]
    public void TurnTotalsSaturateIntegerOverflow()
    {
        var rows = DailyRows.Build(
            Payload(Day(
                "2026-06-12",
                [Client("claude", messages: 1), Client("claude-code", messages: 1)],
                new Dictionary<string, long>
                {
                    ["claude"] = long.MaxValue,
                    ["claude-code"] = long.MaxValue,
                })),
            ["claude"]);

        Assert.Equal(long.MaxValue, Assert.Single(rows).Turns!.Value);
    }
}

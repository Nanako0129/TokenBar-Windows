using TokenBar.App;
using TokenBar.Core;
using TokenBar.Interop;

namespace TokenBar.Core.Tests;

public class CostSurfaceProjectionTests
{
    private static readonly ModelReportEntry ExpensiveFewTokens =
        new("claude", "model-a", "anthropic", 1, 0, 0, 0, 0, 1, 1, 100);

    private static readonly ModelReportEntry CheapManyTokens =
        new("claude", "model-b", "anthropic", 100, 0, 0, 0, 0, 100, 1, 1);

    private static readonly AgentReportEntry ExpensiveAgent =
        new("agent-a", [], 1, 0, 0, 0, 0, 1, 100, 1);

    private static readonly AgentReportEntry TokenAgent =
        new("agent-b", [], 100, 0, 0, 0, 0, 100, 1, 1);

    private static UsagePayload Payload(
        PricingMode pricing = PricingMode.BestEffort,
        CostCoverage coverage = CostCoverage.Complete) =>
        new(
            new UsageMeta(
                "generated",
                "test",
                new DateRange("2026-01-01", "2026-01-02"),
                pricing,
                coverage),
            new UsageSummary(100, 100, 1, 1, 100, 100, ["claude"], ["model-a"]),
            [],
            [new Contribution(
                "2026-01-02",
                new ContributionTotals(100, 100, 1),
                1,
                new TokenBreakdown(100, 0, 0, 0, 0),
                [new ContributionClient(
                    "claude", "model-a", "anthropic",
                    new TokenBreakdown(100, 0, 0, 0, 0), 100, 1)])]);

    [Theory]
    [InlineData(PricingMode.LocalOnly, CostCoverage.Complete, false)]
    [InlineData(PricingMode.LocalOnly, CostCoverage.Partial, false)]
    [InlineData(PricingMode.LocalOnly, CostCoverage.None, false)]
    [InlineData(PricingMode.BestEffort, CostCoverage.Partial, false)]
    [InlineData(PricingMode.BestEffort, CostCoverage.None, false)]
    [InlineData(PricingMode.BestEffort, CostCoverage.Complete, true)]
    public void MetadataMatrixIsTheOnlyCostAuthority(
        PricingMode pricing, CostCoverage coverage, bool expected)
    {
        Assert.Equal(expected, CostSurfaceProjection.IsAuthoritative(
            Payload(pricing, coverage)));
    }

    [Fact]
    public void CheckingPolicyUsesTokenFallbacksAcrossNamedSurfaces()
    {
        var entries = new[] { ExpensiveFewTokens, CheapManyTokens };
        var agents = new[] { ExpensiveAgent, TokenAgent };
        var payload = Payload();
        var stats = new UsageStats(payload, new HashSet<string> { "claude" });

        Assert.Equal("Checking · 1 active days",
            CostSurfaceProjection.HeaderCostLine(stats, false));
        Assert.Equal(ChartMetric.Tokens,
            CostSurfaceProjection.EffectiveMetric(false, ChartMetric.Cost));
        Assert.Equal("Price · Checking",
            CostSurfaceProjection.ChartCostLabel(false));
        Assert.True(CostSurfaceProjection.IsChartCostChecking(
            false, ChartMetric.Cost));
        Assert.False(CostSurfaceProjection.IsChartCostChecking(
            false, ChartMetric.Tokens));
        Assert.False(CostSurfaceProjection.IsChartCostChecking(
            true, ChartMetric.Cost));
        Assert.Equal("Checking cost…", CostSurfaceProjection.ChartChecking);
        Assert.Equal("2 models · Checking",
            CostSurfaceProjection.ModelsSubtitle(entries, false));
        Assert.Equal("Checking",
            CostSurfaceProjection.CostText(100, false));
        Assert.Equal("Checking",
            CostSurfaceProjection.DayTipCost(100, false));
        Assert.Equal("Checking",
            CostSurfaceProjection.HourlyCost(100, false));
        Assert.Equal("Checking",
            CostSurfaceProjection.ModelTipCost(100, false));
        Assert.Equal("Checking",
            CostSurfaceProjection.BestDayText(stats.BestDay, false));
        Assert.Equal("Agents by tokens",
            CostSurfaceProjection.AgentsTitle(false));
        Assert.Equal("Checking",
            CostSurfaceProjection.TrayTitle(
                TrayMode.TodayCost,
                new TrayTotals(1, 100, 1, 100),
                null,
                null,
                false));
        Assert.Equal("Checking",
            CostSurfaceProjection.TrayTitle(
                TrayMode.TotalCost,
                new TrayTotals(1, 100, 1, 100),
                null,
                null,
                false));

        Assert.Equal("model-b",
            CostSurfaceProjection.OrderModels(entries, false).First().Model);
        var segments = new[]
        {
            new DaySegment("a", "a", "#000000") { Tokens = 1, Cost = 100 },
            new DaySegment("b", "b", "#000000") { Tokens = 100, Cost = 1 },
        };
        Assert.Equal("b",
            CostSurfaceProjection.OrderDaySegments(
                segments, false, ChartMetric.Cost).First().Key);
        Assert.Equal("agent-b",
            CostSurfaceProjection.OrderAgents(agents, false).First().Agent);
        Assert.Equal(100,
            CostSurfaceProjection.AgentBarValue(TokenAgent, false));
        Assert.Equal(100, CostSurfaceProjection.AgentScale(agents, false));

        var colors = new ModelColorMap(entries.Select(e =>
            (e.Provider, e.Model, e.Cost)), costAuthoritative: false);
        Assert.Equal("#da7756", colors.Color("anthropic", "model-a"));
        Assert.NotEqual("#da7756", colors.Color("anthropic", "model-b"));

        var clients = new[]
        {
            new ContributionClient(
                "claude", "model-a", "anthropic",
                new TokenBreakdown(1, 0, 0, 0, 0), 100, 1),
            new ContributionClient(
                "claude", "model-b", "anthropic",
                new TokenBreakdown(100, 0, 0, 0, 0), 1, 1),
        };
        Assert.Equal("model-b",
            CostSurfaceProjection.OrderContributionClients(clients, false).First().ModelId);
    }

    [Fact]
    public void ExactCompleteRestoresCostOrderScaleAndFormatting()
    {
        var entries = new[] { ExpensiveFewTokens, CheapManyTokens };
        var agents = new[] { ExpensiveAgent, TokenAgent };
        var payload = Payload();
        var stats = new UsageStats(payload, new HashSet<string> { "claude" });

        Assert.Equal("$100.00 today · $100.00 all time · 1 active days",
            CostSurfaceProjection.HeaderCostLine(100, stats, true));
        Assert.Equal(ChartMetric.Cost,
            CostSurfaceProjection.EffectiveMetric(true, ChartMetric.Cost));
        Assert.Equal("2 models · $101.00",
            CostSurfaceProjection.ModelsSubtitle(entries, true));
        Assert.Equal("$100.00", CostSurfaceProjection.CostText(100, true));
        Assert.Equal("model-a",
            CostSurfaceProjection.OrderModels(entries, true).First().Model);
        Assert.Equal("agent-a",
            CostSurfaceProjection.OrderAgents(agents, true).First().Agent);
        Assert.Equal(100, CostSurfaceProjection.AgentScale(agents, true));
        Assert.Equal("Agents by cost", CostSurfaceProjection.AgentsTitle(true));
        Assert.Equal("$100.00",
            CostSurfaceProjection.TrayTitle(
                TrayMode.TodayCost,
                new TrayTotals(1, 100, 1, 100),
                null,
                null,
                true));
    }

    [Fact]
    public void DelayedOldGenerationCannotRestoreCostAuthority()
    {
        var state = new GraphConsumerState();
        var old = new GraphRequestId(GraphQuery.Normalize(null), 1);
        var current = new GraphRequestId(GraphQuery.Normalize(null), 2);
        var query2 = new GraphRequestId(GraphQuery.Normalize("2025"), 3);

        state.Begin(old);
        Assert.True(state.TryAcceptGraph(
            old, Payload(), GraphPublicationStage.Richer));
        Assert.True(state.CostAuthoritative);

        state.Begin(current);
        Assert.False(state.CostAuthoritative);
        Assert.False(state.TryAcceptGraph(
            old, Payload(), GraphPublicationStage.Richer));
        Assert.Equal("Checking", CostSurfaceProjection.CostText(100, false));

        state.Begin(query2);
        Assert.False(state.TryAcceptGraph(
            current, Payload(), GraphPublicationStage.Richer));
        Assert.False(state.CostAuthoritative);

        // A blocked/failed local stage leaves the new query checking until the
        // exact current richer result is accepted.
        Assert.True(state.TryAcceptGraph(
            query2, Payload(), GraphPublicationStage.Richer));
        Assert.True(state.CostAuthoritative);
    }
}

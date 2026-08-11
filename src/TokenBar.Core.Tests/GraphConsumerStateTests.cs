using TokenBar.App;
using TokenBar.Interop;

namespace TokenBar.Core.Tests;

public class GraphConsumerStateTests
{
    private static UsagePayload Payload(
        PricingMode pricing = PricingMode.BestEffort,
        CostCoverage coverage = CostCoverage.Complete) =>
        new(
            new UsageMeta(
                "generated",
                "test",
                new DateRange("2026-01-01", "2026-12-31"),
                pricing,
                coverage),
            new UsageSummary(1, 2, 1, 1, 2, 2, [], []),
            [],
            []);

    private static GraphRequestId Id(string? year, long generation) =>
        new(GraphQuery.Normalize(year), generation);

    [Fact]
    public void BeginRevokesOldGraphAndRejectsDelayedCallback()
    {
        var state = new GraphConsumerState();
        var first = Id("2026", 1);
        var second = Id("2026", 2);
        var querySwitch = Id("2025", 3);

        Assert.True(state.Begin(first));
        Assert.True(state.TryAcceptGraph(
            first, Payload(), GraphPublicationStage.Richer));
        Assert.True(state.CostAuthoritative);

        Assert.True(state.Begin(second));
        Assert.False(state.LocalAccepted);
        Assert.False(state.CostAuthoritative);
        Assert.Null(state.AcceptedGraph);
        Assert.False(state.TryAcceptGraph(
            first, Payload(), GraphPublicationStage.Richer));

        Assert.True(state.Begin(querySwitch));
        Assert.Null(state.AcceptedGraph);
        Assert.False(state.TryAcceptGraph(
            second, Payload(), GraphPublicationStage.Richer));
    }

    [Fact]
    public void BeginRejectsOlderSameQueryButAllowsOlderDifferentQuery()
    {
        var state = new GraphConsumerState();
        var current = Id(null, 9);
        var olderSameQuery = Id(null, 8);
        var olderDifferentQuery = Id("2025", 1);

        Assert.True(state.Begin(current));
        Assert.True(state.TryAcceptGraph(
            current, Payload(), GraphPublicationStage.Richer));
        Assert.False(state.Begin(olderSameQuery));
        Assert.Equal(current, state.DesiredId);
        Assert.True(state.CostAuthoritative);

        Assert.True(state.Begin(olderDifferentQuery));
        Assert.Equal(olderDifferentQuery, state.DesiredId);
        Assert.False(state.CostAuthoritative);
    }

    [Fact]
    public void RetainedLocalCannotRegressSameGenerationRicherPublication()
    {
        var state = new GraphConsumerState();
        var id = Id(null, 1);
        var richer = Payload();
        var retainedLocal = Payload(PricingMode.LocalOnly, CostCoverage.None);
        state.Begin(id);

        Assert.True(state.TryAcceptGraph(
            id, richer, GraphPublicationStage.Richer));
        Assert.False(state.TryAcceptGraph(
            id, retainedLocal, GraphPublicationStage.LocalFirst));

        Assert.Same(richer, state.AcceptedGraph);
        Assert.Equal(GraphPublicationStage.Richer, state.AcceptedStage);
        Assert.True(state.LocalAccepted);
        Assert.True(state.CostAuthoritative);
    }

    [Theory]
    [InlineData(PricingMode.LocalOnly, CostCoverage.Complete)]
    [InlineData(PricingMode.LocalOnly, CostCoverage.Partial)]
    [InlineData(PricingMode.LocalOnly, CostCoverage.None)]
    [InlineData(PricingMode.BestEffort, CostCoverage.Partial)]
    [InlineData(PricingMode.BestEffort, CostCoverage.None)]
    public void NonAuthoritativeMetadataStaysChecking(
        PricingMode pricing, CostCoverage coverage)
    {
        var state = new GraphConsumerState();
        var id = Id(null, 1);
        state.Begin(id);

        Assert.True(state.TryAcceptGraph(
            id, Payload(pricing, coverage), GraphPublicationStage.LocalFirst));
        Assert.True(state.LocalAccepted);
        Assert.False(state.CostAuthoritative);
    }

    [Fact]
    public void BlockedOrFailedLocalLeavesLocalAcceptedFalse()
    {
        var state = new GraphConsumerState();
        var id = Id(null, 1);
        state.Begin(id);

        Assert.False(state.LocalAccepted);
        Assert.False(state.TryBeginModel(id));
        Assert.False(state.CostAuthoritative);
    }

    [Fact]
    public void ExactCurrentCompleteRestoresAuthorityAndModelStartsOnce()
    {
        var state = new GraphConsumerState();
        var id = Id(null, 4);
        state.Begin(id);

        // A retained richer publication is enough for a late observer to prove
        // the local stage and start ModelReport exactly once.
        Assert.True(state.TryAcceptGraph(
            id, Payload(), GraphPublicationStage.Richer));
        Assert.True(state.TryBeginModel(id));
        Assert.False(state.TryBeginModel(id));
        Assert.True(state.TryAcceptModel(id, new ModelReport([], 0, 0, 0, 0, 0, 0)));
        Assert.True(state.ModelAccepted);

        var newer = Id(null, 5);
        Assert.True(state.Begin(newer));
        Assert.False(state.CostAuthoritative);
        Assert.False(state.TryAcceptModel(
            id, new ModelReport([], 0, 0, 0, 0, 0, 0)));
        Assert.True(state.TryAcceptGraph(
            newer, Payload(), GraphPublicationStage.Richer));
        Assert.True(state.CostAuthoritative);
    }

    [Fact]
    public void DisposeFencesGraphAndModelTransitions()
    {
        var state = new GraphConsumerState();
        var id = Id(null, 1);
        state.Begin(id);
        state.Dispose();

        Assert.True(state.IsDisposed);
        Assert.False(state.Begin(Id(null, 2)));
        Assert.False(state.TryAcceptGraph(
            id, Payload(), GraphPublicationStage.Richer));
        Assert.False(state.TryBeginModel(id));
        Assert.False(state.TryAcceptModel(
            id, new ModelReport([], 0, 0, 0, 0, 0, 0)));
    }
}

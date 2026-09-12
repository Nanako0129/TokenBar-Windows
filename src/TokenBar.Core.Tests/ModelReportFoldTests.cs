using TokenBar.Interop;
using Xunit;

namespace TokenBar.Core.Tests;

// ModelReportFold.ModelLevelEntries (item 2): tokscale groups usage by
// (client, provider, model), so one model reached through two providers (or a
// provider name that changed mid-history) arrives as two entries. Left
// unfolded, a model shows twice in the Models lens, "N models" overcounts, and
// the larger provider-split component — not the model — wins "Favorite model"
// on Stats.
public class ModelReportFoldTests
{
    private static ModelReportEntry Entry(
        string client, string provider, string model,
        long input, long output, double cost, int messageCount = 1) =>
        new(client, model, provider, input, output, 0, 0, 0,
            input + output, messageCount, cost, MsPer1kTokens: 12.5);

    [Fact]
    public void SameClientAndModelAcrossTwoProvidersFoldsIntoOneRow()
    {
        var report = new ModelReport(
            Entries:
            [
                Entry("codex", "openai", "gpt-5", input: 100, output: 50, cost: 1.0),
                Entry("codex", "azure", "gpt-5", input: 40, output: 10, cost: 0.5),
            ],
            TotalInput: 140, TotalOutput: 60, TotalCacheRead: 0, TotalCacheWrite: 0,
            TotalMessages: 2, TotalCost: 1.5);

        var folded = report.ModelLevelEntries();

        var row = Assert.Single(folded);
        Assert.Equal("codex", row.Client);
        Assert.Equal("gpt-5", row.Model);
        Assert.Equal("azure, openai", row.Provider); // merged, sorted, deduped
        Assert.Equal(140, row.Input);
        Assert.Equal(60, row.Output);
        Assert.Equal(200, row.Total);
        Assert.Equal(2, row.MessageCount);
        Assert.Equal(1.5, row.Cost, 6);
        // Throughput is dropped, not averaged or carried from one component:
        // tokscale only computes it honestly over the complete rollup.
        Assert.Null(row.MsPer1kTokens);
    }

    [Fact]
    public void DifferentClientsOrModelsStayDistinctRows()
    {
        var report = new ModelReport(
            Entries:
            [
                Entry("codex", "openai", "gpt-5", 100, 50, 1.0),
                Entry("claude", "anthropic", "gpt-5", 100, 50, 1.0), // same model, different client
                Entry("codex", "openai", "gpt-5-mini", 100, 50, 1.0), // same client, different model
            ],
            TotalInput: 300, TotalOutput: 150, TotalCacheRead: 0, TotalCacheWrite: 0,
            TotalMessages: 3, TotalCost: 3.0);

        Assert.Equal(3, report.ModelLevelEntries().Count);
    }

    [Fact]
    public void MergeOrderIsStableFirstOccurrenceWins() =>
        Assert.Equal(
            ["gpt-5", "o1"],
            new ModelReport(
                Entries:
                [
                    Entry("codex", "openai", "gpt-5", 1, 1, 0.1),
                    Entry("codex", "openai", "o1", 1, 1, 0.1),
                    Entry("codex", "azure", "gpt-5", 1, 1, 0.1),
                ],
                TotalInput: 3, TotalOutput: 3, TotalCacheRead: 0, TotalCacheWrite: 0,
                TotalMessages: 3, TotalCost: 0.3)
                .ModelLevelEntries()
                .Select(e => e.Model));
}

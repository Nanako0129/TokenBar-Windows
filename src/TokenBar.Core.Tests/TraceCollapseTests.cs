using TokenBar.Interop;
using Xunit;

namespace TokenBar.Core.Tests;

// Ported from SelfTest.swift's trace-collapse block.
public class TraceCollapseTests
{
    private static TraceBucket Bucket(string client, string agent, string model, long tokens) =>
        new(client, agent, model, tokens, Messages: 1, TokensPerMin: tokens);

    [Fact]
    public void GroupsPerClientAndSortsByTokens()
    {
        var collapsed = TraceCollapse.CollapseByClient(
        [
            Bucket("claude-code", "Main", "claude-opus-4-8", 100),
            Bucket("claude-code", "Subagent", "unknown", 50),
            Bucket("codex-cli", "Main", "gpt-5.5", 400),
        ]);

        Assert.Equal(2, collapsed.Count);
        Assert.Equal("codex-cli", collapsed[0].Client);
        Assert.Equal(150, collapsed[1].Tokens);
        Assert.Equal(150, collapsed[1].TokensPerMin);
        Assert.Equal("Main, Subagent", collapsed[1].Agent);
        Assert.Equal("claude-opus-4-8", collapsed[1].Model); // unknown dropped among named
    }

    [Fact]
    public void KeepsALoneUnknownModel()
    {
        var collapsed = TraceCollapse.CollapseByClient([Bucket("amp", "Main", "unknown", 5)]);
        Assert.Equal("unknown", Assert.Single(collapsed).Model);
    }

    [Fact]
    public void CollapseSaturatesTokenFold()
    {
        var collapsed = TraceCollapse.CollapseByClient(
        [
            Bucket("claude-code", "Main", "m", long.MaxValue),
            Bucket("claude-code", "Sub", "m", long.MaxValue),
        ]);
        Assert.Equal(long.MaxValue, Assert.Single(collapsed).Tokens);
    }

    [Fact]
    public void FilterByClientsRunsBeforeRowCapAndCanonicalizesIds()
    {
        var buckets = Enumerable.Range(0, 5)
            .Select(i => Bucket($"hidden-{i}", "Main", "m", 100 - i))
            .Append(Bucket("claude-code", "Main", "selected", 1));

        var rows = TraceCollapse.FilterByClients(
                buckets, new HashSet<string> { "claude" })
            .Take(5)
            .ToList();

        var row = Assert.Single(rows);
        Assert.Equal("claude", row.Client);
        Assert.Equal("selected", row.Model);
    }

    [Fact]
    public void TotalRateNormalizesLiveIdsBeforeHidingThem()
    {
        var buckets = new[]
        {
            new TraceBucket("claude-code", "Main", "m", 0, 0, TokensPerMin: 30),
            new TraceBucket("codex-cli", "Main", "m", 0, 0, TokensPerMin: 12),
        };

        // hidden holds the canonical short id "claude"; the row's raw
        // "claude-code" must normalize to it and drop out of the rate.
        Assert.Equal(12, TraceCollapse.TotalRate(buckets, new HashSet<string> { "claude" }));
        // Nothing hidden → full sum.
        Assert.Equal(42, TraceCollapse.TotalRate(buckets, new HashSet<string>()));
    }
}

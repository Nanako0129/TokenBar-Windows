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
}

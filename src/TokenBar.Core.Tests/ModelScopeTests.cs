namespace TokenBar.Core.Tests;

/// <summary>Port check for TokenBarCore/ModelScope.swift: the join between the
/// provider's display-name slug and the transcript's canonical model id.</summary>
public class ModelScopeTests
{
    [Theory]
    // Subset, not equality: the provider says "Fable", the transcript says
    // claude-fable-5.
    [InlineData("Fable", "claude-fable-5", true)]
    [InlineData("fable", "claude-fable-5", true)]
    [InlineData("Claude Fable 5", "claude-fable-5", true)]
    // Subset, not substring: a token the id does not carry fails.
    [InlineData("Fable Max", "claude-fable-5", false)]
    [InlineData("abl", "claude-fable-5", false)]
    // An empty scope selects nothing rather than everything.
    [InlineData("", "claude-fable-5", false)]
    [InlineData("---", "claude-fable-5", false)]
    public void CoversIsATokenSubsetTest(string scope, string modelId, bool expected) =>
        Assert.Equal(expected, ModelScope.Covers(scope, modelId));
}

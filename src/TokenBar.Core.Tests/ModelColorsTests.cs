using Xunit;

namespace TokenBar.Core.Tests;

// Ported from SelfTest.swift's ModelColors block.
public class ModelColorsTests
{
    [Theory]
    [InlineData("claude-sonnet-4-6", "anthropic")]
    [InlineData("gpt-5.5", "openai")]
    [InlineData("o3-mini", "openai")]
    [InlineData("gemini-3-pro", "google")]
    [InlineData("auto", "cursor")]
    [InlineData("mystery", "unknown")]
    public void ProviderFromModel(string model, string expected) =>
        Assert.Equal(expected, ModelColors.ProviderFromModel(model));

    [Fact]
    public void MergedProviderIdFallsBackToModel() =>
        Assert.Equal("openai", ModelColors.ProviderColorKey("litellm, openai", "gpt-5.5"));

    [Fact]
    public void ProviderIdAliasMatches() =>
        Assert.Equal("anthropic", ModelColors.ProviderColorKey("Anthropic", "whatever"));

    [Fact]
    public void ShadeRank0IsBase() =>
        Assert.Equal("#da7756", ModelColors.ShadeFromBase("#da7756", 0));

    [Fact]
    public void ShadeRank1Lerps() =>
        // factor 0.11: 59→81 (0x51), 130→144 (0x90), 246→247 (0xf7)
        Assert.Equal("#5190f7", ModelColors.ShadeFromBase("#3b82f6", 1));

    [Fact]
    public void CostRankingDrivesShades()
    {
        var map = new ModelColorMap(
        [
            ("anthropic", "claude-opus-4-8", 100.0),
            ("anthropic", "claude-haiku-4-5", 1.0),
        ]);

        Assert.Equal("#da7756", map.Color("anthropic", "claude-opus-4-8")); // priciest = base
        Assert.NotEqual("#da7756", map.Color("anthropic", "claude-haiku-4-5")); // cheaper = tinted
        Assert.Equal("#06b6d4", map.Color(null, "gemini-3-pro")); // unseen → provider base
    }

    [Fact]
    public void CheckingRankingUsesLexicalModelOrder()
    {
        var map = new ModelColorMap(
        [
            ("anthropic", "model-z", 100.0),
            ("anthropic", "model-a", 1.0),
        ], costAuthoritative: false);

        Assert.Equal("#da7756", map.Color("anthropic", "model-a"));
        Assert.NotEqual("#da7756", map.Color("anthropic", "model-z"));
    }
}

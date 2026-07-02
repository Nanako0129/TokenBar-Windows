using Xunit;

namespace TokenBar.Core.Tests;

public class ClientRegistryTests
{
    [Fact]
    public void KnownClientHasBrandStyle()
    {
        var style = ClientRegistry.Style("claude");
        Assert.Equal("Claude Code", style.DisplayName);
        Assert.Equal("#d97706", style.Color);
    }

    [Fact]
    public void UnknownClientTitleCasesWithGreyDisc()
    {
        var style = ClientRegistry.Style("mystery");
        Assert.Equal("Mystery", style.DisplayName);
        Assert.Equal("#6b7280", style.Color);
    }

    [Theory]
    [InlineData("claude", "Claude")]       // " Code" dropped
    [InlineData("codex", "Codex")]         // " CLI" dropped
    [InlineData("cursor", "Cursor")]       // " IDE" dropped
    [InlineData("amp", "Amp")]             // no suffix
    [InlineData("antigravity-cli", "Antigravity CLI")] // base collides with the IDE client
    public void ShortNameDropsFormFactorSafely(string id, string expected) =>
        Assert.Equal(expected, ClientRegistry.ShortName(id));
}

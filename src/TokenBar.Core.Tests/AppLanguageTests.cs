using TokenBar.App;
using Xunit;

namespace TokenBar.Core.Tests;

public class AppLanguageTests
{
    [Theory]
    [InlineData("zh-Hant", "zh-Hant")]
    [InlineData("zh-Hant-TW", "zh-Hant")]
    [InlineData("zh-TW", "zh-Hant")]
    [InlineData("zh-HK", "zh-Hant")]
    [InlineData("en", "en")]
    [InlineData("en-US", "en")]
    [InlineData("ja-JP", "en")]
    [InlineData("de", "en")]
    public void ExplicitChoiceResolvesToAShippedTable(string stored, string expected) =>
        Assert.Equal(expected, AppLanguage.Resolve(stored));

    // Re-selecting the current value, or a value we do not ship, must not
    // prompt the user to relaunch for nothing.
    [Theory]
    [InlineData("system", "en", true)]
    [InlineData("en", "zh-Hant", true)]
    [InlineData("en", "en", false)]
    [InlineData("system", "system", false)]
    [InlineData("en", "fr", false)]
    [InlineData("en", "", false)]
    public void RelaunchIsPromptedOnlyForARealChange(
        string current, string next, bool expected) =>
        Assert.Equal(expected, AppLanguage.RequiresRelaunch(current, next));

    [Fact]
    public void OptionsNameEachLanguageInItsOwnLanguage()
    {
        Assert.Equal("English", AppLanguage.Options.Single(o => o.Value == "en").Label);
        Assert.Equal("繁體中文",
            AppLanguage.Options.Single(o => o.Value == "zh-Hant").Label);
    }
}

public class LocalizationTests
{
    // The English source text is the key, so a call site that has been wrapped
    // before its translation exists renders exactly as it did before.
    [Fact]
    public void MissingTableRendersTheEnglishSource()
    {
        Localization.Load("zh-Hant", Path.Combine(Path.GetTempPath(), "no-such-dir"));
        Assert.Equal("Menu bar", "Menu bar".Localized());
    }

    [Fact]
    public void EnglishNeedsNoTable()
    {
        Localization.Load("en", Path.GetTempPath());
        Assert.Equal("Dashboard", "Dashboard".Localized());
        Assert.Equal("en", Localization.CurrentTag);
    }

    [Fact]
    public void MalformedTableFailsSoftToEnglish()
    {
        var dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "strings.zh-Hant.json"), "{ not json");
        try
        {
            Localization.Load("zh-Hant", dir);
            Assert.Equal("Startup", "Startup".Localized());
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void PresentEntryIsUsedAndBlankEntryFallsBack()
    {
        var dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        File.WriteAllText(
            Path.Combine(dir, "strings.zh-Hant.json"),
            """{"Menu bar":"選單列","Dashboard":""}""");
        try
        {
            Localization.Load("zh-Hant", dir);
            Assert.Equal("選單列", "Menu bar".Localized());
            Assert.Equal("Dashboard", "Dashboard".Localized());
        }
        finally
        {
            Localization.Load("en", dir);
            Directory.Delete(dir, recursive: true);
        }
    }
}

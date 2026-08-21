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

    // The notice is a standing state indicator, not a one-shot event: it must
    // read correctly when the page is rebuilt, and clear when the user selects
    // their way back to whatever this process actually loaded.
    [Theory]
    [InlineData("zh-Hant", "en", true)]
    [InlineData("en", "zh-Hant", true)]
    [InlineData("zh-TW", "en", true)]
    [InlineData("en", "en", false)]
    [InlineData("zh-Hant", "zh-Hant", false)]
    [InlineData("zh-HK", "zh-Hant", false)]
    [InlineData("fr", "en", false)]
    public void RelaunchIsNeededOnlyWhenTheResolvedTagDiffers(
        string stored, string activeTag, bool expected) =>
        Assert.Equal(expected, AppLanguage.NeedsRelaunch(stored, activeTag));

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

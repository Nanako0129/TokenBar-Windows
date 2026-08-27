using System.Diagnostics;
using TokenBar.App;
using Xunit;

namespace TokenBar.Core.Tests;

/// <summary>Acceptance items 1, 2 and 5 for the Sparkle-style update dialog:
/// the skip rule, and the copy the dialog puts on screen. Items 6 and 7 are
/// manual — no test project compiles a XAML window.</summary>
public class UpdateSkipRuleTests
{
    // Item 1. Both sides go through UpdateCandidate.Version as it travelled
    // through PublishUpdate; the dialog's display text is never read back.
    [Fact]
    public void SkipSuppressesExactlyOneVersion()
    {
        Assert.False(PendingUpdateAction.ShouldOffer("0.2.3", "0.2.3"));
        Assert.True(PendingUpdateAction.ShouldOffer("0.2.4", "0.2.3"));
        Assert.True(PendingUpdateAction.ShouldOffer("0.2.2", "0.2.3"));
        Assert.True(PendingUpdateAction.ShouldOffer("0.10.0", "0.9.0"));
    }

    // Item 2. Anything that is not a well-formed version resolves to *offer*:
    // a corrupt or hand-edited key must fail towards showing the update, never
    // towards hiding it.
    //
    // "999.0.0" is the case that pins the rule to equality. It is well-formed,
    // so it survives validation; under a "<=" ordering rule it would
    // permanently and silently suppress every future update, and this repo has
    // already shipped one release whose failure mode was "reports you are up
    // to date while an update exists".
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("*")]
    [InlineData("999.0.0")]
    [InlineData("0.2.3-beta")]
    [InlineData("0.2.3+abc")]
    [InlineData("not a version")]
    [InlineData("0.2.3\t")]
    [InlineData("0.2.3\r\n")]
    public void BadOrUnrelatedStoredValuesStillOffer(string? stored)
    {
        Assert.True(PendingUpdateAction.ShouldOffer("0.2.3", stored));
    }

    [Fact]
    public void OverLongStoredValueStillOffers()
    {
        var stored = new string('9', PendingUpdateAction.MaxVersionLength + 1);
        Assert.True(PendingUpdateAction.ShouldOffer("0.2.3", stored));
        Assert.False(PendingUpdateAction.TryValidateVersion(stored));
    }

    // The rule returns a bool for every input rather than throwing: it runs on
    // the update-check path, where an exception disappears into a catch and
    // takes the whole offer with it.
    [Fact]
    public void TheRuleNeverThrows()
    {
        foreach (var stored in new string?[]
        {
            null, "", " ", "*", "\u202E0.2.3", "0.2.3", "9".PadLeft(4096, '9'),
        })
        {
            _ = PendingUpdateAction.ShouldOffer("0.2.3", stored);
            _ = PendingUpdateAction.TryValidateVersion(stored);
        }
    }
}

/// <summary>Item 5. The version line names both versions, so its translation
/// entry is a two-argument format string — <c>Localized(a, b, c)</c> goes
/// through <c>string.Format</c>, and a stray placeholder in
/// <c>strings-zh-Hant.json</c> would throw <c>FormatException</c> at dialog
/// construction, on the UI thread, inside a modal window.
///
/// <para>Rendered against the <em>shipped</em> table (copied to the output by
/// the csproj), not a fixture: the failure being guarded against is a call site
/// whose key was never added to that file, which a fixture written beside the
/// test cannot reproduce.</para></summary>
public class UpdateDialogTextTests
{
    private static void InChinese(Action body)
    {
        Localization.Load("zh-Hant", AppContext.BaseDirectory);
        try
        {
            body();
        }
        finally
        {
            Localization.Load("en", AppContext.BaseDirectory);
        }
    }

    [Fact]
    public void EnglishRendersBothVersionsAndSubstitutesEveryPlaceholder()
    {
        Localization.Load("en", AppContext.BaseDirectory);
        Assert.Equal(
            "A new version of Syrtis is available!",
            UpdateDialogText.Headline("Syrtis"));
        Assert.Equal(
            "Syrtis 0.2.3 is now available — you have 0.2.2. "
            + "Would you like to download it now?",
            UpdateDialogText.VersionLine("Syrtis", "0.2.3", "0.2.2"));
    }

    [Fact]
    public void EveryDialogStringHasAShippedTranslationThatFormats()
    {
        var english = new List<(string Name, Func<string> Render)>
        {
            ("Title", UpdateDialogText.Title),
            ("Headline", () => UpdateDialogText.Headline("Syrtis")),
            ("VersionLine", () => UpdateDialogText.VersionLine("Syrtis", "0.2.3", "0.2.2")),
            ("NoNotes", UpdateDialogText.NoNotes),
            ("Skip", UpdateDialogText.Skip),
            ("Later", UpdateDialogText.Later),
            ("Install", UpdateDialogText.Install),
        };

        Localization.Load("en", AppContext.BaseDirectory);
        var before = english.Select(item => item.Render()).ToList();

        InChinese(() =>
        {
            for (var i = 0; i < english.Count; i++)
            {
                var rendered = english[i].Render();
                Assert.False(
                    string.IsNullOrWhiteSpace(rendered),
                    $"{english[i].Name} rendered empty in zh-Hant.");
                // Comparing against the English *key* would not work for the
                // version line: a missing entry renders the substituted key,
                // which is not equal to the key either. Comparing the two
                // renderings is what catches a key that was never translated.
                Assert.True(
                    rendered != before[i],
                    $"{english[i].Name} has no entry in strings-zh-Hant.json.");
            }
        });

        InChinese(() =>
        {
            var line = UpdateDialogText.VersionLine("Syrtis", "0.2.3", "0.2.2");
            Assert.Contains("Syrtis", line, StringComparison.Ordinal);
            Assert.Contains("0.2.3", line, StringComparison.Ordinal);
            Assert.Contains("0.2.2", line, StringComparison.Ordinal);
            Assert.DoesNotContain("{0}", line, StringComparison.Ordinal);
            Assert.DoesNotContain("{1}", line, StringComparison.Ordinal);
            Assert.DoesNotContain("{2}", line, StringComparison.Ordinal);
        });
    }
}

/// <summary>Items 3 and 4: what the Markdown subset renders, and the bounds
/// that keep it from hanging the UI thread it runs on.</summary>
public class ReleaseNotesMarkdownTests
{
    private static string Plain(IReadOnlyList<NotesBlock> blocks) =>
        string.Join("\n", blocks.Select(block => block.PlainText));

    private static NotesBlock Single(string markdown)
    {
        var blocks = ReleaseNotesMarkdown.Parse(markdown);
        return Assert.Single(blocks);
    }

    // ---- item 3: degradation ------------------------------------------

    [Theory]
    [InlineData("# Heading")]
    [InlineData("## Heading")]
    [InlineData("###### Heading")]
    public void HashSpaceIsAHeading(string markdown)
    {
        var block = Single(markdown);
        Assert.Equal(NotesBlockKind.Heading, block.Kind);
        Assert.Equal("Heading", block.PlainText);
    }

    // The space is required. #39 is an issue reference — the real v0.2.2 body
    // contains one — and must never become a heading, wherever it appears.
    [Theory]
    [InlineData("#39 is still open", "#39 is still open")]
    [InlineData("#nospace", "#nospace")]
    [InlineData("####### seven hashes", "####### seven hashes")]
    public void HashWithoutASpaceIsNotAHeading(string markdown, string expected)
    {
        var block = Single(markdown);
        Assert.Equal(NotesBlockKind.Paragraph, block.Kind);
        Assert.Equal(expected, block.PlainText);
    }

    [Theory]
    [InlineData("- item")]
    [InlineData("* item")]
    [InlineData("+ item")]
    public void UnorderedListsAreBullets(string markdown)
    {
        var block = Single(markdown);
        Assert.Equal(NotesBlockKind.Bullet, block.Kind);
        Assert.Equal("item", block.PlainText);
    }

    // Accepted degradation: an ordered list renders as bullets and the
    // numbering is lost.
    [Theory]
    [InlineData("1. item")]
    [InlineData("27) item")]
    public void OrderedListsAreBulletsWithoutNumbering(string markdown)
    {
        var block = Single(markdown);
        Assert.Equal(NotesBlockKind.Bullet, block.Kind);
        Assert.Equal("item", block.PlainText);
    }

    // Bold must be an inline run, not a whole-line style: the real body is full
    // of "**Unsigned.** Windows SmartScreen…", bold followed by plain text on
    // one line.
    [Fact]
    public void BoldIsInlineAndTheRestOfTheLineIsNot()
    {
        var block = Single("**Unsigned.** Windows SmartScreen will warn.");
        Assert.Collection(
            block.Runs,
            run =>
            {
                Assert.Equal("Unsigned.", run.Text);
                Assert.True(run.Bold);
            },
            run =>
            {
                Assert.Equal(" Windows SmartScreen will warn.", run.Text);
                Assert.False(run.Bold);
            });
    }

    [Fact]
    public void ItalicAndCodeAreInlineRuns()
    {
        var block = Single("Choose *More info* then run `where.exe`.");
        Assert.Equal("Choose More info then run where.exe.", block.PlainText);
        Assert.Contains(block.Runs, run => run.Text == "More info" && run.Italic);
        Assert.Contains(block.Runs, run => run.Text == "where.exe" && run.Code);
    }

    // Text only, URL discarded. NEVER "text (url)": that is the common
    // "helpful" variant and it is strictly worse — it puts an unvisitable,
    // attacker-chosen string in front of the user as though it were
    // information.
    [Fact]
    public void LinksKeepTheirTextAndLoseTheUrl()
    {
        var block = Single("See [the notes](https://example.invalid/x) for more.");
        Assert.Equal("See the notes for more.", block.PlainText);
        Assert.DoesNotContain("example.invalid", block.PlainText, StringComparison.Ordinal);
        Assert.DoesNotContain("(", block.PlainText, StringComparison.Ordinal);
    }

    [Fact]
    public void EmphasisInsideLinkTextStillRenders()
    {
        var block = Single("[**bold link**](https://example.invalid/x)");
        Assert.Equal("bold link", block.PlainText);
        Assert.True(Assert.Single(block.Runs).Bold);
    }

    [Fact]
    public void TablesDegradeToPlainText()
    {
        var block = Single("| Machine | File |\n|---|---|\n| x64 | Setup.exe |");
        Assert.Equal(NotesBlockKind.Paragraph, block.Kind);
        Assert.Contains("Machine", block.PlainText, StringComparison.Ordinal);
        Assert.Contains("Setup.exe", block.PlainText, StringComparison.Ordinal);
    }

    // Markdown's soft wrap. Without this a hard-wrapped changelog renders every
    // fragment of a sentence as its own spaced paragraph, which is what the
    // first visual check of this dialog showed.
    [Fact]
    public void SoftWrappedLinesJoinIntoOneParagraph()
    {
        var blocks = ReleaseNotesMarkdown.Parse(
            "one line\nand its continuation\n\na second paragraph");
        Assert.Collection(
            blocks,
            block => Assert.Equal("one line and its continuation", block.PlainText),
            block => Assert.Equal("a second paragraph", block.PlainText));
    }

    // A heading or a bullet ends the paragraph it follows, blank line or not.
    [Fact]
    public void HeadingsAndBulletsBreakAParagraph()
    {
        var blocks = ReleaseNotesMarkdown.Parse("prose\n## Heading\n- item\nmore prose");
        Assert.Collection(
            blocks,
            block => Assert.Equal(NotesBlockKind.Paragraph, block.Kind),
            block => Assert.Equal(NotesBlockKind.Heading, block.Kind),
            block => Assert.Equal(NotesBlockKind.Bullet, block.Kind),
            block => Assert.Equal(NotesBlockKind.Paragraph, block.Kind));
    }

    // An opening marker with no closer stays literal rather than swallowing
    // the rest of the line.
    [Theory]
    [InlineData("2 * 3 = 6 and nothing else", "2 * 3 = 6 and nothing else")]
    [InlineData("an unclosed `backtick", "an unclosed `backtick")]
    [InlineData("a [bracket with no link", "a [bracket with no link")]
    public void UnbalancedMarkersStayLiteral(string markdown, string expected) =>
        Assert.Equal(expected, Single(markdown).PlainText);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\n\n\n")]
    public void NothingToShowIsAnEmptyListNotAnError(string? markdown) =>
        Assert.Empty(ReleaseNotesMarkdown.Parse(markdown));

    /// <summary>Every release body this repository actually publishes, parsed
    /// whole. The per-construct cases above prove each rule; this proves the
    /// rules together leave no markup on screen.</summary>
    [Fact]
    public void RealReleaseBodiesLeaveNoMarkupBehind()
    {
        var directory = Path.Combine(
            AppContext.BaseDirectory, "Fixtures", "release-notes");
        var files = Directory.GetFiles(directory, "*.md");
        Assert.NotEmpty(files);

        foreach (var file in files)
        {
            var blocks = ReleaseNotesMarkdown.Parse(File.ReadAllText(file));
            var name = Path.GetFileName(file);
            Assert.NotEmpty(blocks);

            var text = Plain(blocks);
            Assert.DoesNotContain("`", text, StringComparison.Ordinal);
            Assert.DoesNotContain("*", text, StringComparison.Ordinal);
            Assert.All(blocks, block => Assert.False(
                block.Kind != NotesBlockKind.Heading
                    && block.PlainText.StartsWith("# ", StringComparison.Ordinal),
                $"{name} left a line-initial heading marker in place."));
            // The headings are recognised as headings rather than surviving as
            // literal text, and the bold-led paragraphs keep their bold.
            Assert.Contains(blocks, block => block.Kind == NotesBlockKind.Heading);
            Assert.Contains(blocks, block => block.Runs.Any(run => run.Bold));
            Assert.Contains(blocks, block => block.Kind == NotesBlockKind.Bullet);
        }
    }

    // ---- item 4: bounds -----------------------------------------------
    //
    // The parser runs on the UI thread during construction of a modal dialog,
    // on a string that arrived over the network. Two of these are deliberately
    // independent of the byte cap, because the byte cap does not bound either.

    /// <summary>Bound 1. Checked before parsing, not after reading it all.
    /// </summary>
    [Fact]
    public void InputBeyondTheByteCapIsNotParsed()
    {
        var markdown = new string('x', ReleaseNotesMarkdown.MaxInputChars) + "\ncanary";
        var blocks = ReleaseNotesMarkdown.Parse(markdown);
        Assert.DoesNotContain("canary", Plain(blocks), StringComparison.Ordinal);
    }

    /// <summary>Bound 2, and the one that matters most. Thousands of
    /// Paragraphs on a RichTextBlock hang the UI thread rather than throwing —
    /// no try/catch reaches that, and the dialog is modal. A SMALL input, so
    /// the byte cap cannot be what stops it.</summary>
    [Fact]
    public void ParagraphCapHoldsIndependentlyOfTheByteCap()
    {
        // Bullets, not bare lines: consecutive plain lines soft-wrap into one
        // paragraph, which would make this input prove nothing about the cap.
        var markdown = string.Join("\n", Enumerable.Repeat("- a", 5_000));
        Assert.True(markdown.Length < ReleaseNotesMarkdown.MaxInputChars);

        var blocks = ReleaseNotesMarkdown.Parse(markdown);

        Assert.Equal(ReleaseNotesMarkdown.MaxBlocks, blocks.Count);
    }

    /// <summary>Bound 2's other half: the paragraph cap alone does not bound
    /// the runs inside one paragraph.</summary>
    [Fact]
    public void RunCapHoldsAcrossTheWholeDocument()
    {
        var line = "- " + string.Concat(Enumerable.Repeat("**a**b", 300));
        var markdown = string.Join("\n", Enumerable.Repeat(line, 20));

        var blocks = ReleaseNotesMarkdown.Parse(markdown);

        Assert.True(
            blocks.Sum(block => block.Runs.Count) <= ReleaseNotesMarkdown.MaxRuns,
            "the total run count escaped MaxRuns.");
    }

    /// <summary>Bound 3. A 64 KB line with wrapping on is one enormous measure
    /// pass.</summary>
    [Fact]
    public void OverLongLinesAreTruncatedWithAnEllipsis()
    {
        var block = Single(new string('y', 10_000));
        Assert.Equal(ReleaseNotesMarkdown.MaxLineChars, block.PlainText.Length);
        Assert.EndsWith("…", block.PlainText, StringComparison.Ordinal);
    }

    /// <summary>Bounds 4 and 5: a flat single pass with no recursion and no
    /// regex, so there is neither a stack to blow nor a backtracking path to
    /// burn a second of UI thread on. The timing assertion is deliberately
    /// loose — a backtracking regex on these inputs takes minutes, not
    /// milliseconds.</summary>
    [Fact]
    public void PathologicalMarkupNeitherThrowsNorHangs()
    {
        var inputs = new[]
        {
            new string('[', 40_000),
            new string('*', 40_000),
            new string('`', 40_000),
            string.Concat(Enumerable.Repeat("[a](", 10_000)),
            string.Concat(Enumerable.Repeat("*a**", 10_000)),
            new string('\n', ReleaseNotesMarkdown.MaxInputChars),
            // 16 000 soft-wrapped lines: one paragraph accumulator holding the
            // whole document before the cap applies to it.
            string.Concat(Enumerable.Repeat("a b\n", 16_000)),
        };

        var stopwatch = Stopwatch.StartNew();
        foreach (var input in inputs)
        {
            var blocks = ReleaseNotesMarkdown.Parse(input);
            Assert.True(blocks.Count <= ReleaseNotesMarkdown.MaxBlocks);
        }

        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(5),
            $"parsing pathological markup took {stopwatch.Elapsed}.");
    }

    /// <summary>Bound 6. char.IsControl covers only Cc; the Trojan Source
    /// class is Cf and returns false there, and in a changelog those can
    /// reverse or hide a line. Stripped rather than rejected, so one stray
    /// character cannot erase the whole changelog — and U+200D ZWJ is stripped
    /// too, accepting that emoji sequences break in changelogs.</summary>
    // Escapes, not literals: these characters are invisible in a source file,
    // which is exactly the property that makes them worth a test.
    [Theory]
    [InlineData("\u202A")] // LEFT-TO-RIGHT EMBEDDING
    [InlineData("\u202B")] // RIGHT-TO-LEFT EMBEDDING
    [InlineData("\u202C")] // POP DIRECTIONAL FORMATTING
    [InlineData("\u202D")] // LEFT-TO-RIGHT OVERRIDE
    [InlineData("\u202E")] // RIGHT-TO-LEFT OVERRIDE
    [InlineData("\u2066")] // LEFT-TO-RIGHT ISOLATE
    [InlineData("\u2067")] // RIGHT-TO-LEFT ISOLATE
    [InlineData("\u2068")] // FIRST STRONG ISOLATE
    [InlineData("\u2069")] // POP DIRECTIONAL ISOLATE
    [InlineData("\u200E")] // LEFT-TO-RIGHT MARK
    [InlineData("\u200F")] // RIGHT-TO-LEFT MARK
    [InlineData("\u061C")] // ARABIC LETTER MARK
    [InlineData("\u200B")] // ZERO WIDTH SPACE
    [InlineData("\u200D")] // ZERO WIDTH JOINER, deliberately included
    [InlineData("\uFEFF")] // ZERO WIDTH NO-BREAK SPACE
    [InlineData("\u0007")] // Cc, which char.IsControl does catch
    public void FormattingAndControlCharactersAreStrippedNotRejected(string hidden)
    {
        var block = Single($"safe{hidden}text");
        Assert.Equal("safetext", block.PlainText);
    }

    // A bidi override must not be able to hide the marker that decides the
    // block kind, so stripping runs before classification.
    [Fact]
    public void StrippingRunsBeforeClassification()
    {
        Assert.Equal(NotesBlockKind.Heading, Single("\u202E# Heading").Kind);
        Assert.Equal(NotesBlockKind.Bullet, Single("-\u200B item").Kind);
    }

    [Fact]
    public void TabsBecomeSpacesRatherThanVanishing() =>
        Assert.Equal("a b", Single("a\tb").PlainText);
}

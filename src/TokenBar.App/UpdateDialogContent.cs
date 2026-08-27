using System.Globalization;
using System.Text;
using TokenBar.Core;

namespace TokenBar.App;

/// <summary>Everything the update dialog displays, with no XAML in it, so the
/// whole content layer is reachable from TokenBar.Core.Tests. The dialog
/// itself (<see cref="UpdateDialog"/>) only turns these values into controls.
/// </summary>
internal sealed record UpdateOffer(
    string Version,
    string InstalledVersion,
    string? NotesMarkdown);

internal enum NotesBlockKind
{
    Paragraph,
    Heading,
    Bullet,
    TableRow,
}

/// <summary>One styled span of a line. Styles are flags rather than a nested
/// tree because the block model is deliberately flat (bound 4).</summary>
internal readonly record struct NotesRun(
    string Text,
    bool Bold,
    bool Italic,
    bool Code);

/// <summary>A block of the changelog. <paramref name="Runs"/> is the whole
/// block's text in order, which is what every kind but
/// <see cref="NotesBlockKind.TableRow"/> renders; a table row additionally
/// carries <paramref name="Cells"/>, the same runs split at the column
/// boundaries. Keeping both means the flat model still describes a table
/// without a nested block tree, and <see cref="PlainText"/> stays meaningful
/// for every kind.</summary>
internal sealed record NotesBlock(
    NotesBlockKind Kind,
    IReadOnlyList<NotesRun> Runs,
    IReadOnlyList<IReadOnlyList<NotesRun>>? Cells = null)
{
    internal string PlainText =>
        string.Concat(Runs.Select(run => run.Text));
}

/// <summary>A hand-written Markdown subset, rendered by the update dialog into
/// a <c>RichTextBlock</c>.
///
/// <para>This runs on the UI thread during dialog construction, on a string
/// that arrived over the network with the update feed — release notes are the
/// only part of the feed that reaches the user before any download, and
/// neither <c>ValidateTarget</c>'s package checks nor <c>ValidateNuspec</c>
/// covers them. So it is <b>total</b>: every input returns a block list and
/// none throws.</para>
///
/// <para>WebView2 (<c>NotesHTML</c>) was rejected for this: it hands a script
/// engine, <c>fetch()</c> and a navigation surface to a modal dialog. Native
/// text controls have no executable semantics at all, which is the whole
/// reason this exists. <c>NotesMarkdown</c> is not "cleaner" than
/// <c>NotesHTML</c> — both come from the same file and Velopack sanitizes
/// neither; the safety comes from what renders them.</para>
///
/// <para>The bounds below are load-bearing, not tidiness. The paragraph cap in
/// particular: thousands of <c>Paragraph</c>s on a <c>RichTextBlock</c> hang
/// the UI thread rather than throwing, so no <c>try</c>/<c>catch</c> reaches
/// it and the dialog is modal.</para>
/// </summary>
internal static class ReleaseNotesMarkdown
{
    /// <summary>Bound 1. Checked before parsing (and again, earlier, in
    /// <c>UpdateFlow.ValidateTarget</c>, which drops over-long notes and keeps
    /// the update). Real notes are ~4 KB.</summary>
    internal const int MaxInputChars = 65_536;

    /// <summary>Bound 2, and the one that matters most: 64 KB of newlines is
    /// 65 536 empty paragraphs. Independent of <see cref="MaxInputChars"/>.
    /// </summary>
    internal const int MaxBlocks = 500;

    /// <summary>Bound 2's other half: one line of alternating <c>**</c> is
    /// thousands of <c>Run</c>s inside a single paragraph, which the paragraph
    /// cap alone does not catch. Counted across the whole document.</summary>
    internal const int MaxRuns = 2_000;

    /// <summary>Bound 3. A 64 KB line with wrapping on is one enormous measure
    /// pass.</summary>
    internal const int MaxLineChars = 2_000;

    private static readonly IReadOnlyList<NotesBlock> None = Array.Empty<NotesBlock>();

    /// <summary>Parse release-notes markdown into a flat block list. Never
    /// throws; an empty list means "no release notes", which is a first-class
    /// state — every version published so far has none.</summary>
    internal static IReadOnlyList<NotesBlock> Parse(string? markdown)
    {
        if (string.IsNullOrEmpty(markdown))
        {
            return None;
        }

        // Bound 1, before any per-line work.
        var text = markdown.Length > MaxInputChars
            ? markdown.AsSpan(0, MaxInputChars)
            : markdown.AsSpan();

        var blocks = new List<NotesBlock>();
        var runBudget = MaxRuns;
        // Markdown's soft wrap: consecutive plain lines are one paragraph, and
        // a hard-wrapped changelog would otherwise render every fragment of a
        // sentence as its own spaced block. Bounded by MaxInputChars, which the
        // whole document already is, and capped again at flush.
        var paragraph = new StringBuilder();
        var start = 0;
        // Bound 4: one linear pass over lines, no recursion anywhere. A future
        // nested list adds an explicit depth counter rather than a call.
        while (start <= text.Length && blocks.Count < MaxBlocks && runBudget > 0)
        {
            var end = text[start..].IndexOf('\n');
            var line = end < 0 ? text[start..] : text.Slice(start, end);
            start = end < 0 ? text.Length + 1 : start + end + 1;

            var cleaned = Clean(line);
            if (cleaned.Length == 0)
            {
                FlushParagraph(paragraph, blocks, ref runBudget);
                continue;
            }

            if (cleaned[0] == '|')
            {
                // A table interrupts a soft-wrapped paragraph rather than
                // continuing it, so flush before the row is emitted.
                FlushParagraph(paragraph, blocks, ref runBudget);
                AppendTableRow(cleaned, blocks, ref runBudget);
                continue;
            }

            var kind = Classify(ref cleaned);
            if (kind == NotesBlockKind.Paragraph)
            {
                if (paragraph.Length > 0)
                {
                    paragraph.Append(' ');
                }

                paragraph.Append(cleaned);
                continue;
            }

            FlushParagraph(paragraph, blocks, ref runBudget);
            if (cleaned.Length == 0 || blocks.Count >= MaxBlocks || runBudget <= 0)
            {
                continue;
            }

            var runs = new List<NotesRun>();
            ScanInline(cleaned, runs, ref runBudget);
            if (runs.Count > 0)
            {
                blocks.Add(new NotesBlock(kind, runs));
            }
        }

        FlushParagraph(paragraph, blocks, ref runBudget);
        return blocks.Count == 0 ? None : blocks;
    }

    /// <summary>Bound 7: a pathological row of thousands of pipes is thousands
    /// of Grid columns, which the run budget alone does not catch — an empty
    /// cell costs no runs. Real tables are two or three columns wide.</summary>
    internal const int MaxCells = 12;

    /// <summary>A pipe-delimited row becomes one <see cref="NotesBlockKind.TableRow"/>
    /// block carrying its cells; the <c>|---|---|</c> alignment row is dropped
    /// entirely, so the renderer can treat the first row of a run of table rows
    /// as its header without looking for a marker.
    ///
    /// <para>Known ceiling: <c>\|</c> is not an escape here, so a literal pipe
    /// inside a cell splits it. GitHub's own tables allow it; a changelog that
    /// uses one gets an extra column rather than a wrong render.</para>
    /// </summary>
    private static void AppendTableRow(
        string line, List<NotesBlock> blocks, ref int budget)
    {
        var fields = line.Split('|');
        var cells = new List<IReadOnlyList<NotesRun>>();
        var runs = new List<NotesRun>();
        var separator = true;
        var empty = true;
        // Skip index 0: the text before the leading pipe, always empty. A
        // trailing pipe likewise yields a final empty field, which is kept only
        // if the row genuinely ends without one.
        var last = fields.Length - 1;
        if (last > 0 && fields[last].Trim().Length == 0)
        {
            last--;
        }

        for (var i = 1; i <= last && cells.Count < MaxCells && budget > 0; i++)
        {
            var cell = fields[i].Trim();
            if (cell.Length > 0)
            {
                empty = false;
                separator &= IsAlignmentCell(cell);
            }
            else
            {
                separator = false;
            }

            runs.Clear();
            ScanInline(cell, runs, ref budget);
            cells.Add(runs.ToArray());
        }

        if (empty || separator || cells.Count == 0
            || blocks.Count >= MaxBlocks)
        {
            return;
        }

        // The flat run list is what PlainText and every non-table consumer
        // reads, so the column boundaries survive in it as spaces rather than
        // running "Machine" and "File" together.
        var flat = new List<NotesRun>();
        foreach (var cell in cells)
        {
            if (flat.Count > 0)
            {
                flat.Add(new NotesRun(" ", false, false, false));
            }

            flat.AddRange(cell);
        }

        blocks.Add(new NotesBlock(NotesBlockKind.TableRow, flat, cells));
    }

    private static bool IsAlignmentCell(string cell)
    {
        var dashes = 0;
        foreach (var c in cell)
        {
            if (c == '-')
            {
                dashes++;
            }
            else if (c != ':')
            {
                return false;
            }
        }

        return dashes > 0;
    }

    private static void FlushParagraph(
        StringBuilder pending, List<NotesBlock> blocks, ref int budget)
    {
        if (pending.Length == 0)
        {
            return;
        }

        var text = Cap(pending.ToString());
        pending.Clear();
        if (blocks.Count >= MaxBlocks || budget <= 0)
        {
            return;
        }

        var runs = new List<NotesRun>();
        ScanInline(text, runs, ref budget);
        if (runs.Count > 0)
        {
            blocks.Add(new NotesBlock(NotesBlockKind.Paragraph, runs));
        }
    }

    /// <summary>Strip what must never survive into the rendered text, then
    /// discard link syntax and apply the line-length cap.
    ///
    /// <para>Bound 6: <c>char.IsControl</c> covers only <c>Cc</c>. The Trojan
    /// Source class — U+202A–202E, U+2066–2069, U+200E/200F, U+061C — is
    /// <c>Cf</c> and returns false there, and in a changelog those can reverse
    /// or hide a line. Every <c>Cf</c> character is stripped, which
    /// deliberately includes U+200D ZWJ: emoji sequences such as 👨‍👩‍👧 break
    /// in changelogs, and that is the accepted price. Stripping rather than
    /// rejecting, so one stray character cannot erase the whole
    /// changelog.</para>
    ///
    /// <para>Known ceiling: this is a UTF-16 code-unit scan, so the handful of
    /// non-BMP format characters (U+110BD, U+1D173–1D17A) arrive as surrogate
    /// pairs and are not stripped. They are not in the bidirectional-override
    /// class this guards.</para></summary>
    private static string Clean(ReadOnlySpan<char> line)
    {
        var builder = new StringBuilder(Math.Min(line.Length, MaxLineChars) + 1);
        foreach (var c in line)
        {
            if (char.IsControl(c))
            {
                // Tabs are the only control character with a rendering intent;
                // \r is the other half of a CRLF and must simply go.
                if (c == '\t')
                {
                    builder.Append(' ');
                }

                continue;
            }

            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.Format)
            {
                continue;
            }

            builder.Append(c);
        }

        // Cap BEFORE the link pass and before the inline scanner, not after:
        // both of those do an IndexOf per candidate marker, so on a 64 KB line
        // of unclosed '[' they would be quadratic over 64 KB rather than over
        // 2 000 characters. The cap is what makes every later scan cheap.
        return StripLinks(Cap(builder.ToString().Trim()));
    }

    /// <summary>Bound 3, applied per source line and again to a soft-wrapped
    /// paragraph after its lines are joined.</summary>
    private static string Cap(string text) =>
        text.Length > MaxLineChars
            ? string.Concat(text.AsSpan(0, MaxLineChars - 1), "…")
            : text;

    /// <summary><c>[text](url)</c> renders as the text with the URL discarded.
    /// Never <c>text (url)</c> — the common "helpful" variant, and strictly
    /// worse: it puts an unvisitable attacker-chosen string in front of the
    /// user as if it were information.
    ///
    /// <para>A separate pass rather than a case inside the inline scanner, so
    /// emphasis inside link text is still scanned without recursing.</para>
    /// </summary>
    private static string StripLinks(string line)
    {
        if (line.IndexOf('[') < 0)
        {
            return line;
        }

        var builder = new StringBuilder(line.Length);
        var i = 0;
        while (i < line.Length)
        {
            var c = line[i];
            if (c != '[')
            {
                builder.Append(c);
                i++;
                continue;
            }

            var close = line.IndexOf(']', i + 1);
            if (close < 0
                || close + 1 >= line.Length
                || line[close + 1] != '(')
            {
                builder.Append(c);
                i++;
                continue;
            }

            var paren = line.IndexOf(')', close + 2);
            if (paren < 0)
            {
                builder.Append(c);
                i++;
                continue;
            }

            builder.Append(line, i + 1, close - i - 1);
            i = paren + 1;
        }

        return builder.ToString();
    }

    /// <summary>Block kind from the line prefix, consuming the marker.
    ///
    /// <para>The space after <c>#</c> is required, so <c>#39</c> — an issue
    /// reference, which the real release body contains — is not a heading
    /// wherever it appears. Ordered lists render as bullets with the numbering
    /// lost; that is an accepted degradation, not an oversight.</para>
    /// </summary>
    private static NotesBlockKind Classify(ref string line)
    {
        var hashes = 0;
        while (hashes < line.Length && line[hashes] == '#')
        {
            hashes++;
        }

        if (hashes is > 0 and <= 6 && hashes < line.Length && line[hashes] == ' ')
        {
            line = line[(hashes + 1)..].Trim();
            return NotesBlockKind.Heading;
        }

        if (line.Length > 1
            && line[0] is '-' or '*' or '+'
            && line[1] == ' ')
        {
            line = line[2..].Trim();
            return NotesBlockKind.Bullet;
        }

        var digits = 0;
        while (digits < line.Length && char.IsAsciiDigit(line[digits]))
        {
            digits++;
        }

        if (digits > 0
            && digits + 1 < line.Length
            && line[digits] is '.' or ')'
            && line[digits + 1] == ' ')
        {
            line = line[(digits + 2)..].Trim();
            return NotesBlockKind.Bullet;
        }

        return NotesBlockKind.Paragraph;
    }

    /// <summary>Bound 5: a character loop, not a regex. Shorter than getting
    /// the regex right for this subset, and with no backtracking to bound.
    ///
    /// <para>An opening marker with no closer on the same line stays literal,
    /// so an unbalanced <c>*</c> renders as itself rather than swallowing the
    /// rest of the line.</para></summary>
    private static void ScanInline(string line, List<NotesRun> runs, ref int budget)
    {
        var pending = new StringBuilder();
        var bold = false;
        var italic = false;
        var i = 0;

        while (i < line.Length && budget > 0)
        {
            var c = line[i];
            if (c == '`')
            {
                var close = line.IndexOf('`', i + 1);
                if (close < 0)
                {
                    pending.Append(c);
                    i++;
                    continue;
                }

                Flush(pending, runs, bold, italic, code: false, ref budget);
                if (close > i + 1)
                {
                    Emit(line[(i + 1)..close], runs, bold, italic, code: true, ref budget);
                }

                i = close + 1;
                continue;
            }

            if (c == '*' && i + 1 < line.Length && line[i + 1] == '*')
            {
                if (!bold && line.IndexOf("**", i + 2, StringComparison.Ordinal) < 0)
                {
                    pending.Append("**");
                    i += 2;
                    continue;
                }

                Flush(pending, runs, bold, italic, code: false, ref budget);
                bold = !bold;
                i += 2;
                continue;
            }

            if (c == '*')
            {
                if (!italic && line.IndexOf('*', i + 1) < 0)
                {
                    pending.Append(c);
                    i++;
                    continue;
                }

                Flush(pending, runs, bold, italic, code: false, ref budget);
                italic = !italic;
                i++;
                continue;
            }

            pending.Append(c);
            i++;
        }

        Flush(pending, runs, bold, italic, code: false, ref budget);
    }

    private static void Flush(
        StringBuilder pending,
        List<NotesRun> runs,
        bool bold,
        bool italic,
        bool code,
        ref int budget)
    {
        if (pending.Length == 0)
        {
            return;
        }

        Emit(pending.ToString(), runs, bold, italic, code, ref budget);
        pending.Clear();
    }

    private static void Emit(
        string text,
        List<NotesRun> runs,
        bool bold,
        bool italic,
        bool code,
        ref int budget)
    {
        if (text.Length == 0 || budget <= 0)
        {
            return;
        }

        runs.Add(new NotesRun(text, bold, italic, code));
        budget--;
    }
}

/// <summary>The dialog's own copy. Here rather than in <c>UpdateDialog.cs</c>
/// so that the shipped translation table can be exercised for every line: no
/// test project compiles a XAML window, and a two-placeholder entry is a
/// format string — a stray <c>{3}</c> in <c>strings-zh-Hant.json</c> would
/// throw <c>FormatException</c> at dialog construction.</summary>
internal static class UpdateDialogText
{
    internal static string Title() => "Software Update".Localized();

    internal static string Headline(string product) =>
        "A new version of {0} is available!".Localized(product);

    internal static string VersionLine(
        string product, string version, string installedVersion) =>
        "{0} {1} is now available — you have {2}. Would you like to download it now?"
            .Localized(product, version, installedVersion);

    internal static string NoNotes() => "This update has no release notes.".Localized();

    internal static string Skip() => "Skip This Version".Localized();

    internal static string Later() => "Remind Me Later".Localized();

    internal static string Install() => "Install Update".Localized();

    // Progress copy. The phases are what this app can actually report: the
    // download reports percent, verification is brief and indeterminate, and
    // the restart happens after the process exits, so all it can do is say so
    // in advance.
    internal static string Downloading() => "Downloading update…".Localized();

    internal static string Verifying() => "Verifying update…".Localized();

    internal static string Restarting() => "Installing update…".Localized();

    internal static string Percent(int percent) =>
        "{0}%".Localized(Math.Clamp(percent, 0, 100));

    internal static string RestartNotice() =>
        "{0} will close and reopen.".Localized(ProductIdentity.Name);
}

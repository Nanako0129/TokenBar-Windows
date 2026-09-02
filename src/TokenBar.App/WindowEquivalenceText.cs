using TokenBar.Core;

namespace TokenBar.App;

/// <summary>
/// The strip card's <c>≈</c> line and the heatmap hover's equivalence lines
/// (port of the string half of <c>WindowEquivalence.text</c>'s call site in
/// <c>QuotaHistoryStripCard.swift</c> and the <c>equivalent(percent:row:)</c> /
/// <c>noFigureReason(_:)</c> pair in <c>QuotaHeatmapCard.swift</c>; the WinUI
/// layout lives in <c>DashboardView.Quota.cs</c>).
/// <para>
/// Pulled into <c>TokenBar.Core.Tests</c> via &lt;Compile Include&gt;, the same
/// way <see cref="QuotaLensText"/> is and for the same reason: left inside
/// <c>DashboardView.Quota.cs</c> this file's branches would sit in a file no
/// test project compiles.
/// </para>
/// </summary>
public static class WindowEquivalenceText
{
    /// <summary>The strip card's single line, tokens and money formatted the
    /// way the rest of the app already does.</summary>
    public static string Line(WindowEquivalence.Row row) =>
        WindowEquivalence.Text(row, Format.CompactTokens, Format.Usd);

    /// <summary>The heatmap hover's equivalence lines for one slot.
    /// <para><see cref="Primary"/> is either the <c>≈ token</c> figure (never
    /// localized — <c>"≈ "</c> is a literal prefix, matching macOS's
    /// <c>Text(verbatim:)</c> for that line) or, when the row carries no
    /// figure at all, the sole reason line from <see cref="NoFigureReason"/>.
    /// <see cref="Secondary"/> is the localized money/error line, null only in
    /// the no-figure case.</para></summary>
    public readonly record struct SlotEquivalent(string Primary, string? Secondary);

    /// <summary>
    /// Converted from the window's own equivalence rather than measured.
    /// <para>
    /// The measured route would need a message scan over the whole recorded
    /// history, which this project has already paid 60 seconds for once. The
    /// equivalence is derived from attributed spend against quota movement,
    /// so the figures below are attributed too — they are an estimate, and
    /// they carry the same error the strip card's line does.
    /// </para>
    /// <para><paramref name="percent"/> is the slot's own allowance-points
    /// value; <paramref name="row"/> is null when the window has no
    /// equivalence at all (absent from the fetched dictionary, not merely an
    /// unproductive row) — that case reads the same as any other with no
    /// figure, via <see cref="NoFigureReason"/>'s default arm.</para>
    /// </summary>
    public static SlotEquivalent Slot(double percent, WindowEquivalence.Row? row)
    {
        var share = percent / 10;
        switch (row)
        {
            case WindowEquivalence.Row.TokensOnly r:
                // The fold produces this deliberately when the models carry
                // no price; matching only `.Ratio` sent it to "not enough
                // history", which is false — the history is sufficient and
                // the token estimate is the part of the answer that exists.
                return new SlotEquivalent(
                    "≈ " + Format.CompactTokens(ScaledTokens(r.TokensPerTenth, share)),
                    "unpriced models, ±{0}%".Localized(r.ErrorPercent));

            case WindowEquivalence.Row.CostOnly r:
                // The mirror of `.TokensOnly`, and it fell through to "not
                // enough history" for the same wrong reason: the history IS
                // sufficient, and the money estimate is the part of the
                // answer that exists.
                return new SlotEquivalent(
                    "~ " + Format.Usd(r.CostPerTenth * share),
                    "tokens unavailable, ±{0}%".Localized(r.ErrorPercent));

            case WindowEquivalence.Row.Ratio r:
                return new SlotEquivalent(
                    "≈ " + Format.CompactTokens(ScaledTokens(r.TokensPerTenth, share)),
                    "~ {0} API-equivalent, ±{1}%".Localized(Format.Usd(r.CostPerTenth * share), r.ErrorPercent));

            case WindowEquivalence.Row.Spread r:
                // The third sibling of the two branches above, missed when
                // they were written. `.Spread` is a measured answer — the
                // cycles disagree, so the fold reports a range instead of a
                // point — and the strip card prints exactly that range for
                // the same row. Falling through made one card say "not
                // enough history" about a window the card beside it was
                // quantifying.
                return new SlotEquivalent(
                    "≈ " + Format.CompactTokens(ScaledTokens(r.LowPerTenth, share))
                        + "–" + Format.CompactTokens(ScaledTokens(r.HighPerTenth, share)),
                    "~ {0} – {1} API-equivalent".Localized(
                        Format.Usd(r.LowCostPerTenth * share), Format.Usd(r.HighCostPerTenth * share)));

            default:
                return new SlotEquivalent(NoFigureReason(row), null);
        }
    }

    /// <summary>Clamped: the ratio itself can already be saturated, and a
    /// cell above 10% scales it further, past what a bare cast would
    /// accept.</summary>
    private static long ScaledTokens(long perTenth, double share) =>
        WindowEquivalence.Clamped(Math.Round(perTenth * share, MidpointRounding.AwayFromZero));

    /// <summary>
    /// Why there is no figure, stated per case rather than blamed on history.
    /// <para>
    /// "Not enough history" used to cover everything that reached here, and
    /// it is a false sentence for three of them: <c>Insufficient</c>,
    /// <c>NotMoved</c> and <c>Unaccounted</c> all have sufficient history and
    /// fail for a different reason each.
    /// </para>
    /// </summary>
    public static string NoFigureReason(WindowEquivalence.Row? row) => row switch
    {
        WindowEquivalence.Row.Undeclared =>
            "Classify your usage in Settings to see what this window is worth".Localized(),
        WindowEquivalence.Row.NotMoved => "The allowance did not move in this window".Localized(),
        WindowEquivalence.Row.Insufficient => "The allowance moved too little to convert reliably".Localized(),
        WindowEquivalence.Row.Unaccounted =>
            "The allowance moved, but no usage was recorded on this machine".Localized(),
        _ => "Not enough history to convert this window to a figure".Localized(),
    };
}

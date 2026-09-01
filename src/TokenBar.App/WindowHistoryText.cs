using TokenBar.Core;

namespace TokenBar.App;

/// <summary>Which of the window-history card's states applies.</summary>
public enum WindowHistoryState
{
    /// <summary>The persisted-curve read has not settled. On every cold start
    /// an empty list means "still asking", not "nothing recorded" — without
    /// this the card announced no history while the card above it was still
    /// showing its own spinner.</summary>
    Loading,

    NoHistory,
    Rows,
}

/// <summary>One earlier window, as the card draws it.</summary>
/// <param name="QuotaFraction">The quota bar's fill, 0…1 on a FIXED 0…100
/// scale. Rescaling to the largest row would make a 3% window and a 58% one
/// look alike, and comparing cycles to each other and to the ceiling is what
/// the bar is for.</param>
/// <param name="UsageFraction">The usage bar's fill, relative to the heaviest
/// window ON SCREEN. Zero is a real answer: this window consumed allowance and
/// nothing in it was declared as this subscription's.</param>
/// <param name="ThinObservation">The app was running for less than half of
/// this window, so its consumption figure is a floor rather than a
/// measurement. Shown, but marked.</param>
public sealed record WindowHistoryRow(
    long ResetAtMs,
    string Stamp,
    string Range,
    double UsedPercent,
    double QuotaFraction,
    long Tokens,
    double Cost,
    double UsageFraction,
    double ObservedFraction,
    bool ThinObservation);

/// <summary>
/// The 時間窗歷史 card's rows and copy (port of <c>QuotaHistoryCard.swift</c>;
/// the WinUI layout lives in <c>DashboardView.Quota.cs</c>).
/// <para>
/// Pulled into <c>TokenBar.Core.Tests</c> via &lt;Compile Include&gt; for the
/// same reason as its neighbours: the row fold's scale rule and the disclaimer
/// would otherwise sit in a file no test project compiles.
/// </para>
/// </summary>
public static class WindowHistoryText
{
    /// <summary>Enough to read a trend without turning the lens into a scroll
    /// marathon. The engine retains 128 cycles, so this is a display choice,
    /// not a storage one — and <see cref="QuotaHistoryFold.ConsideredCycles"/>
    /// has to stay at or above it, which a test asserts: a cap below this
    /// number would draw fewer rows than this card intends with nothing saying
    /// so.</summary>
    public const int VisibleRows = 12;

    /// <summary>Below this the cycle was barely witnessed and its consumption
    /// figure is not evidence about the window — the app simply was not running
    /// for most of it.</summary>
    public const double ThinObservation = 0.5;

    public static WindowHistoryState State(IReadOnlyList<WindowHistoryRow> rows, bool attempted) =>
        rows.Count > 0 ? WindowHistoryState.Rows
            : attempted ? WindowHistoryState.NoHistory
            : WindowHistoryState.Loading;

    /// <summary>
    /// The visible rows, newest first.
    /// <para>
    /// <paramref name="spans"/> is <see cref="QuotaEquivalenceFold.Cycles"/>'s
    /// output for the same list and in the same order, which is where the
    /// attributed tokens and cost per cycle already live; asking a second fold
    /// for them would be a second answer free to disagree with the ≈ line the
    /// strip card prints from the first.
    /// </para>
    /// <para>
    /// The usage bar's scale is taken over the rows ACTUALLY SHOWN. macOS
    /// states the same rule and records what breaking it cost: a hidden older
    /// cycle with the largest total set the scale and made every visible bar
    /// short, which is precisely the comparison the bar claims to be making.
    /// </para>
    /// </summary>
    public static IReadOnlyList<WindowHistoryRow> Rows(
        IReadOnlyList<QuotaCycle> cycles, IReadOnlyList<WindowEquivalence.Cycle> spans)
    {
        var shown = Math.Min(cycles.Count, VisibleRows);
        var peak = 0L;
        for (var i = 0; i < shown && i < spans.Count; i++)
        {
            peak = Math.Max(peak, spans[i].SpanTokens);
        }

        var rows = new List<WindowHistoryRow>(shown);
        for (var i = 0; i < shown; i++)
        {
            var cycle = cycles[i];
            var span = i < spans.Count ? spans[i] : null;
            var tokens = span?.SpanTokens ?? 0;
            rows.Add(new WindowHistoryRow(
                ResetAtMs: cycle.ResetAtMs,
                Stamp: Stamp(cycle.StartMs),
                Range: WindowCardText.ClockRange(cycle.StartMs, cycle.ResetAtMs),
                UsedPercent: cycle.UsedPercent,
                QuotaFraction: Math.Min(1, cycle.UsedPercent / 100),
                Tokens: tokens,
                Cost: span?.SpanCost ?? 0,
                UsageFraction: peak > 0 ? Math.Min(1, (double)tokens / peak) : 0,
                ObservedFraction: cycle.ObservedFraction,
                ThinObservation: cycle.ObservedFraction < ThinObservation));
        }

        return rows;
    }

    /// <summary><c>MM-DD HH:mm</c> in the viewer's own zone: the window's start,
    /// which is what identifies a row.</summary>
    public static string Stamp(long ms) =>
        DateTimeOffset.FromUnixTimeMilliseconds(ms).ToLocalTime().ToString(
            "MM-dd HH:mm", System.Globalization.CultureInfo.CurrentCulture);

    public static string Title() => "Window history".Localized();

    public static string? Subtitle(IReadOnlyList<WindowHistoryRow> rows) =>
        rows.Count == 0 ? null : "{0} windows".Localized(rows.Count);

    public static string EmptyBody(WindowHistoryState state) =>
        state == WindowHistoryState.Loading
            ? "Reading quota history…".Localized()
            : "No earlier windows recorded yet. They accumulate as TokenBar runs.".Localized();

    public static string? ThinObservationNote(WindowHistoryRow row) =>
        row.ThinObservation
            ? "TokenBar observed {0}% of this window, so its usage figure is a floor.".Localized(
                (int)Math.Round(row.ObservedFraction * 100, MidpointRounding.AwayFromZero))
            : null;

    /// <summary>The line that keeps the money column honest: these are API
    /// list-price equivalents for usage the user themselves declared as this
    /// subscription's, not what the subscription charged. Not noise — without
    /// it the column reads as a bill.</summary>
    public static string Disclaimer(string clientId) =>
        "Amounts are API list-price equivalents for usage you declared as {0} — not what the subscription charged."
            .Localized(ClientRegistry.Style(clientId).DisplayName);

    /// <summary>The row's own numbers, formatted the way the rest of the app
    /// already does.</summary>
    public static string Tokens(WindowHistoryRow row) => Format.CompactTokens(row.Tokens);

    public static string Cost(WindowHistoryRow row) => Format.Usd(row.Cost);

    public static string Percent(WindowHistoryRow row) =>
        ((int)Math.Round(row.UsedPercent, MidpointRounding.AwayFromZero))
            .ToString(System.Globalization.CultureInfo.CurrentCulture) + "%";
}

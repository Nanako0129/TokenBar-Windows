using System.Globalization;
using TokenBar.Interop;

namespace TokenBar.Core;

/// <summary>Small display formatters shared by the tray and the flyout (port
/// of Sources/TokenBar/Format.swift — output must match it character for
/// character, hence the invariant/en-US formatting throughout).</summary>
public static class Format
{
    /// <summary>Compact token count: 999 → "999", 12_345 → "12.3K",
    /// 1_234_567 → "1.2M".</summary>
    public static string CompactTokens(long count)
    {
        var value = (double)count;
        double scaled;
        string suffix;
        // Tier boundaries sit at the ROUNDING boundary, not at the unit: the
        // >= 100 arm below prints F0, so 999_500_000 scales to 999.5 and
        // carries to "1000M" — a mantissa that has left its own tier. Promoting
        // at 999_500 / 999_500_000 renders those bands as "1M" / "1B" instead.
        // Every value outside the two half-unit bands picks the same tier it
        // always did. (The top of the B tier has nowhere to promote to, so
        // 999.5B still prints "1000B".)
        if (value >= 999_500_000) { (scaled, suffix) = (value / 1_000_000_000, "B"); }
        else if (value >= 999_500) { (scaled, suffix) = (value / 1_000_000, "M"); }
        else if (value >= 1_000) { (scaled, suffix) = (value / 1_000, "K"); }
        else { return count.ToString(CultureInfo.InvariantCulture); }

        // Bare F0/F1 IS printf %.0f/%.1f: .NET Core formatting is IEEE-correct
        // from the full binary value (1.05 → "1.1" because the double is
        // 1.0500…0444, above the half; exact ties like 1.25 go to even →
        // "1.2"). A Math.Round pre-round would re-quantize near-half values
        // to exact halves and diverge — the fixture cross-check caught that.
        var text = scaled >= 100
            ? scaled.ToString("F0", CultureInfo.InvariantCulture)
            : scaled.ToString("F1", CultureInfo.InvariantCulture);
        if (text.EndsWith(".0", StringComparison.Ordinal))
        {
            text = text[..^2];
        }

        return text + suffix;
    }

    // "$" prepended outside the numeric format so a negative amount renders
    // "$-1.50" like Swift's "$%.2f", not .NET's "-$1.50". Bare F2 is printf
    // %.2f (IEEE-correct from the binary value — see CompactTokens).
    public static string Usd(double amount) =>
        "$" + amount.ToString("F2", CultureInfo.InvariantCulture);

    /// <summary>Today's contribution-graph day key. tokscale-core buckets days
    /// in the local timezone as %Y-%m-%d, so this must match exactly.</summary>
    public static string TodayKey(DateTime? now = null) =>
        (now ?? DateTime.Now).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>Total tokens recorded today in the graph (0 when today has no
    /// entry). Contributions are date-sorted; today, if present, is at the tail.</summary>
    public static long TodayTokens(UsagePayload graph, DateTime? now = null)
    {
        var today = TodayKey(now);
        return graph.Contributions.LastOrDefault(c => c.Date == today)?.Totals.Tokens ?? 0;
    }

    /// <summary>Today's cost in the graph (0 when today has no entry).</summary>
    public static double TodayCost(UsagePayload graph, DateTime? now = null)
    {
        var today = TodayKey(now);
        return graph.Contributions.LastOrDefault(c => c.Date == today)?.Totals.Cost ?? 0;
    }

    /// <summary>English month names, which double as their own lookup keys.</summary>
    private static readonly string[] MonthsShort =
    [
        "Jan", "Feb", "Mar", "Apr", "May", "Jun",
        "Jul", "Aug", "Sep", "Oct", "Nov", "Dec",
    ];

    /// <summary>"2026-06-10" → "Jun 10". RemoveEmptyEntries + all-three-parsed
    /// mirrors Swift's `split(separator:)` + `compactMap { Int($0) }`.
    ///
    /// The field order comes from the table, not from this code: zh-Hant
    /// renders "6月10日", and format.monthYear goes further and *swaps* its two
    /// arguments ("2026年6月"). Translating only the month names would produce
    /// "6月 2026". Ported from Format.swift, which keys the same two templates.</summary>
    public static string MonthDay(string iso)
    {
        var parts = iso.Split('-', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 3 ||
            !int.TryParse(parts[0], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _) ||
            !int.TryParse(parts[1], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var month) ||
            !int.TryParse(parts[2], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var day) ||
            month is < 1 or > 12)
        {
            return iso;
        }

        return "format.monthDay".LocalizedKey(
            "{0} {1}", MonthsShort[month - 1].Localized(), day);
    }

    /// <summary>"2026-06" → "Jun 2026". Same shape as <see cref="MonthDay"/>:
    /// anything that does not parse as year-month comes back unchanged rather
    /// than being guessed at.</summary>
    public static string MonthYear(string ym)
    {
        var parts = ym.Split('-', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2 ||
            !int.TryParse(parts[0], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var year) ||
            !int.TryParse(parts[1], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var month) ||
            month is < 1 or > 12)
        {
            return ym;
        }

        return "format.monthYear".LocalizedKey(
            "{0} {1}", MonthsShort[month - 1].Localized(), year);
    }

    /// <summary>"2026-06-10" → "06/10".</summary>
    public static string Mmdd(string iso)
    {
        var parts = iso.Split('-', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 3 ? $"{parts[1]}/{parts[2]}" : iso;
    }

    /// <summary>Exact token count with thousands separators ("1,234,567").</summary>
    public static string ExactTokens(long count) =>
        count.ToString("N0", CultureInfo.GetCultureInfo("en-US"));

    /// <summary>Compact "time ago" from a Unix-seconds timestamp: "just now",
    /// "5m ago", "3h ago", "2d ago". Used for the pricing freshness hint.</summary>
    public static string RelativeTime(ulong epochSecs, DateTimeOffset? now = null)
    {
        var nowSecs = (now ?? DateTimeOffset.Now).ToUnixTimeSeconds();
        var diff = Math.Max(0, nowSecs - (long)epochSecs);
        if (diff < 60) { return "just now".Localized(); }
        if (diff < 3600) { return "{0}m ago".Localized(diff / 60); }
        if (diff < 86400) { return "{0}h ago".Localized(diff / 3600); }
        return "{0}d ago".Localized(diff / 86400);
    }
}

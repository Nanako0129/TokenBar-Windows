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
        if (value >= 1_000_000_000) { (scaled, suffix) = (value / 1_000_000_000, "B"); }
        else if (value >= 1_000_000) { (scaled, suffix) = (value / 1_000_000, "M"); }
        else if (value >= 1_000) { (scaled, suffix) = (value / 1_000, "K"); }
        else { return count.ToString(CultureInfo.InvariantCulture); }

        var text = scaled >= 100
            ? scaled.ToString("F0", CultureInfo.InvariantCulture)
            : scaled.ToString("F1", CultureInfo.InvariantCulture);
        if (text.EndsWith(".0", StringComparison.Ordinal))
        {
            text = text[..^2];
        }

        return text + suffix;
    }

    public static string Usd(double amount) =>
        amount.ToString("$0.00", CultureInfo.InvariantCulture);

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

    private static readonly string[] MonthsShort =
    [
        "Jan", "Feb", "Mar", "Apr", "May", "Jun",
        "Jul", "Aug", "Sep", "Oct", "Nov", "Dec",
    ];

    /// <summary>"2026-06-10" → "Jun 10".</summary>
    public static string MonthDay(string iso)
    {
        var parts = iso.Split('-');
        if (parts.Length != 3 ||
            !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var month) ||
            !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var day) ||
            month is < 1 or > 12)
        {
            return iso;
        }

        return $"{MonthsShort[month - 1]} {day}";
    }

    /// <summary>"2026-06-10" → "06/10".</summary>
    public static string Mmdd(string iso)
    {
        var parts = iso.Split('-');
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
        if (diff < 60) { return "just now"; }
        if (diff < 3600) { return $"{diff / 60}m ago"; }
        if (diff < 86400) { return $"{diff / 3600}h ago"; }
        return $"{diff / 86400}d ago";
    }
}

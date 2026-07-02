using TokenBar.Interop;

namespace TokenBar.Core;

/// <summary>What the tray shows, mirroring the macOS TrayMode (itself a port
/// of the Tauri computeTrayTitle). On Windows there is no menu-bar text, so
/// a non-hidden mode's title is drawn INTO the icon and the pictorial icon
/// styles take over only in Hidden ("icon only") mode.</summary>
public enum TrayMode
{
    TodayTokens,
    TodayCost,
    TotalTokens,
    TotalCost,
    TokensPerMin,
    QuotaLeft,
    Hidden,
}

public static class TrayModes
{
    public const string StorageKey = "tokenbar.tray.mode";

    public static readonly IReadOnlyList<TrayMode> All =
    [
        TrayMode.TodayTokens, TrayMode.TodayCost, TrayMode.TotalTokens,
        TrayMode.TotalCost, TrayMode.TokensPerMin, TrayMode.QuotaLeft,
        TrayMode.Hidden,
    ];

    public static TrayMode Parse(string? raw) => raw switch
    {
        "today_cost" => TrayMode.TodayCost,
        "total_tokens" => TrayMode.TotalTokens,
        "total_cost" => TrayMode.TotalCost,
        "tokens_per_min" => TrayMode.TokensPerMin,
        "quota_left" => TrayMode.QuotaLeft,
        "hidden" => TrayMode.Hidden,
        _ => TrayMode.TodayTokens,
    };

    public static string RawValue(this TrayMode mode) => mode switch
    {
        TrayMode.TodayCost => "today_cost",
        TrayMode.TotalTokens => "total_tokens",
        TrayMode.TotalCost => "total_cost",
        TrayMode.TokensPerMin => "tokens_per_min",
        TrayMode.QuotaLeft => "quota_left",
        TrayMode.Hidden => "hidden",
        _ => "today_tokens",
    };

    /// <summary>Settings-UI copy (macOS TrayMode.label).</summary>
    public static string Label(this TrayMode mode) => mode switch
    {
        TrayMode.TodayTokens => "Today's tokens (50M)",
        TrayMode.TodayCost => "Today's cost ($5.20)",
        TrayMode.TotalTokens => "Total tokens (1.5B)",
        TrayMode.TotalCost => "Total cost ($889)",
        TrayMode.TokensPerMin => "Tokens / min (12.4K/m)",
        TrayMode.QuotaLeft => "Quota left (57%)",
        _ => "Icon only",
    };

    /// <summary>Short mode name for the tray tooltip's "layer b" of the
    /// removed-native-text substitution.</summary>
    public static string ShortLabel(this TrayMode mode) => mode switch
    {
        TrayMode.TodayTokens => "Today's tokens",
        TrayMode.TodayCost => "Today's cost",
        TrayMode.TotalTokens => "Total tokens",
        TrayMode.TotalCost => "Total cost",
        TrayMode.TokensPerMin => "Tokens / min",
        TrayMode.QuotaLeft => "Quota left",
        _ => "TokenBar",
    };

    /// <summary>The icon-sized short form of a title (parity table #1's
    /// "truncated $5.20"): past six characters the icon drops precision —
    /// cents off a big cost, the decimal out of a rate — while the tooltip
    /// keeps the full string.</summary>
    public static string IconTitle(string title)
    {
        if (title.Length <= 6)
        {
            return title;
        }

        var dot = title.IndexOf('.');
        if (dot < 0)
        {
            return title;
        }

        var end = dot + 1;
        while (end < title.Length && char.IsAsciiDigit(title[end]))
        {
            end++;
        }

        return title.StartsWith('$')
            ? title[..dot] // $4637.49 → $4637
            : title[..dot] + title[end..]; // 12.4K/m → 12K/m
    }

    /// <summary>The tray title for this mode ("" = icon only). Faithful to
    /// macOS title(graph:tokensPerMin:quotaRemaining:), including Swift's
    /// away-from-zero rounding.</summary>
    public static string Title(
        this TrayMode mode, UsagePayload? graph, double? tokensPerMin,
        double? quotaRemaining = null)
    {
        if (mode == TrayMode.QuotaLeft)
        {
            return quotaRemaining is { } quota
                ? $"{(int)Math.Round(Math.Clamp(quota, 0, 100), MidpointRounding.AwayFromZero)}%"
                : "—%";
        }

        if (mode == TrayMode.Hidden || graph is null)
        {
            return "";
        }

        return mode switch
        {
            TrayMode.TodayTokens => Format.CompactTokens(Format.TodayTokens(graph)),
            TrayMode.TodayCost => Format.Usd(Format.TodayCost(graph)),
            TrayMode.TotalTokens => Format.CompactTokens(graph.Summary.TotalTokens),
            TrayMode.TotalCost => Format.Usd(graph.Summary.TotalCost),
            TrayMode.TokensPerMin => tokensPerMin is { } rate
                ? $"{Format.CompactTokens((long)Math.Round(Math.Max(0, rate), MidpointRounding.AwayFromZero))}/m"
                : "—/m",
            _ => "",
        };
    }
}

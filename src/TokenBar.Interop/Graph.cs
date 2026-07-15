namespace TokenBar.Interop;

// Contribution-graph payload (`UsagePayload` in the Tauri frontend's
// src/lib/types.ts; Swift port: TokenBarCore/Graph.swift). Wire keys are the
// Rust serde camelCase serialization; the Web JsonSerializerOptions in TbCore
// map them onto these PascalCase properties.

/// <summary>Saturating Int64 arithmetic, port of <c>Int64.saturatingAdding</c>
/// in TokenBarCore/Graph.swift. Addition that clamps to long.MaxValue/MinValue
/// instead of wrapping (C# unchecked) or throwing (C# checked) on overflow. The
/// Rust side clamps corrupt Antigravity token varints to Int64.max (M6 #766) and
/// saturates its fold arithmetic (#822/#823), so a legitimately-clamped lane can
/// carry long.MaxValue; the re-sums here (TokenBreakdown.Total, the tray totals)
/// run on the always-on menu bar and must not crash the app on such data. Normal
/// (non-overflowing) values are byte-identical to <c>+</c>.</summary>
public static class SaturatingArithmetic
{
    public static long SaturatingAdd(this long value, long other)
    {
        var result = unchecked(value + other);
        // Signed overflow iff the operands share a sign but the result's sign
        // differs from both.
        var overflow = ((value ^ result) & (other ^ result)) < 0;
        if (!overflow)
        {
            return result;
        }

        // Match Swift: key the clamp off the addend's sign, not the sum's.
        return other > 0 ? long.MaxValue : long.MinValue;
    }
}

public sealed record TokenBreakdown(
    long Input,
    long Output,
    long CacheRead,
    long CacheWrite,
    long Reasoning)
{
    /// <summary>Sum of every token lane — the single definition shared by the
    /// tray totals, DayBars, and UsageStats aggregations. Saturating so an
    /// long.MaxValue-clamped corrupt lane can't trap the always-on menu bar
    /// (matches the Rust-side saturation; see <see cref="SaturatingArithmetic"/>).</summary>
    public long Total =>
        Input
            .SaturatingAdd(Output)
            .SaturatingAdd(CacheRead)
            .SaturatingAdd(CacheWrite)
            .SaturatingAdd(Reasoning);
}

public sealed record ContributionClient(
    string Client,
    string ModelId,
    string ProviderId,
    TokenBreakdown Tokens,
    double Cost,
    int Messages);

public sealed record ContributionTotals(
    long Tokens,
    double Cost,
    int Messages);

public sealed record Contribution(
    string Date,
    ContributionTotals Totals,
    int Intensity,
    TokenBreakdown TokenBreakdown,
    IReadOnlyList<ContributionClient> Clients);

public sealed record DateRange(
    string Start,
    string End);

public sealed record YearMeta(
    string Year,
    long TotalTokens,
    double TotalCost,
    DateRange Range);

public sealed record UsageMeta(
    string GeneratedAt,
    string Version,
    DateRange DateRange);

public sealed record UsageSummary(
    long TotalTokens,
    double TotalCost,
    int TotalDays,
    int ActiveDays,
    double AveragePerDay,
    double MaxCostInSingleDay,
    IReadOnlyList<string> Clients,
    IReadOnlyList<string> Models);

public sealed record UsagePayload(
    UsageMeta Meta,
    UsageSummary Summary,
    IReadOnlyList<YearMeta> Years,
    IReadOnlyList<Contribution> Contributions);

/// <summary>Today/total token+cost figures for the menu-bar title, with the
/// user's hidden clients excluded (port of TrayTotals in Graph.swift).</summary>
public readonly record struct TrayTotals(
    long TodayTokens,
    double TodayCost,
    long TotalTokens,
    double TotalCost);

public static class UsagePayloadExtensions
{
    /// <summary>The four tray-title figures with <paramref name="hidden"/>
    /// client ids excluded. <paramref name="today"/> is the local-timezone
    /// YYYY-MM-DD day key (tokscale-core's bucketing).
    ///
    /// An empty hidden set takes a fast path that reads <c>Summary</c> and the
    /// today contribution totals directly, so the numbers are byte-identical to
    /// the pre-hide implementation (regression guard). With any client hidden,
    /// the figures are re-summed from the surviving per-client stripes.
    ///
    /// Clamp-granularity caveat: vendor/tokscale-core's aggregator clamps a
    /// day's totals.tokens with .max(0) at the aggregate level, while each
    /// per-client stripe lane clamps independently. With pathological negative
    /// token deltas the re-summed slow path can therefore differ slightly from
    /// <c>Summary</c> — the day-level clamp is not reproducible from the stripes
    /// alone, so we do NOT try to.</summary>
    public static TrayTotals TrayTotals(this UsagePayload payload, IReadOnlySet<string> hidden, string today)
    {
        if (hidden.Count == 0)
        {
            var todayEntry = payload.Contributions.LastOrDefault(c => c.Date == today);
            return new TrayTotals(
                todayEntry?.Totals.Tokens ?? 0,
                todayEntry?.Totals.Cost ?? 0,
                payload.Summary.TotalTokens,
                payload.Summary.TotalCost);
        }

        long totalTokens = 0;
        var totalCost = 0.0;
        long todayTokens = 0;
        var todayCost = 0.0;
        foreach (var c in payload.Contributions)
        {
            var isToday = c.Date == today;
            foreach (var cc in c.Clients)
            {
                if (hidden.Contains(cc.Client))
                {
                    continue;
                }

                var sum = cc.Tokens.Total;
                totalTokens = totalTokens.SaturatingAdd(sum);
                totalCost += cc.Cost;
                if (isToday)
                {
                    todayTokens = todayTokens.SaturatingAdd(sum);
                    todayCost += cc.Cost;
                }
            }
        }

        return new TrayTotals(todayTokens, todayCost, totalTokens, totalCost);
    }
}

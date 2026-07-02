using TokenBar.Interop;

namespace TokenBar.Core;

// Dashboard stats derived from a UsagePayload, port of
// TokenBarCore/UsageStats.swift (originally the Tauri app's src/lib/stats.ts
// + streaks.ts). Day keys are local-timezone YYYY-MM-DD strings
// (tokscale-core's bucketing); day arithmetic uses a civil-date day number so
// no calendar/timezone library is involved.

/// <summary>Days since 1970-01-01 (Howard Hinnant's days_from_civil).</summary>
public readonly record struct ISODay(int Number)
{
    public static ISODay? Parse(string iso)
    {
        // RemoveEmptyEntries + AllowLeadingSign + invariant matches Swift's
        // `split(separator:)` (omits empty subsequences) + `Int(_:)` (sign ok,
        // no whitespace). Out-of-range months normalize via the civil math,
        // same as the Swift original.
        var parts = iso.Split('-', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 3 ||
            !int.TryParse(parts[0], System.Globalization.NumberStyles.AllowLeadingSign,
                System.Globalization.CultureInfo.InvariantCulture, out var y) ||
            !int.TryParse(parts[1], System.Globalization.NumberStyles.AllowLeadingSign,
                System.Globalization.CultureInfo.InvariantCulture, out var m) ||
            !int.TryParse(parts[2], System.Globalization.NumberStyles.AllowLeadingSign,
                System.Globalization.CultureInfo.InvariantCulture, out var d))
        {
            return null;
        }

        var yy = m <= 2 ? y - 1 : y;
        var era = (yy >= 0 ? yy : yy - 399) / 400;
        var yoe = yy - era * 400;
        var doy = (153 * (m + (m > 2 ? -3 : 9)) + 2) / 5 + d - 1;
        var doe = yoe * 365 + yoe / 4 - yoe / 100 + doy;
        return new ISODay(era * 146097 + doe - 719468);
    }

    /// <summary>YYYY-MM-DD (civil_from_days inverse).</summary>
    public string Iso
    {
        get
        {
            var z = Number + 719468;
            var era = (z >= 0 ? z : z - 146096) / 146097;
            var doe = z - era * 146097;
            var yoe = (doe - doe / 1460 + doe / 36524 - doe / 146096) / 365;
            var doy = doe - (365 * yoe + yoe / 4 - yoe / 100);
            var mp = (5 * doy + 2) / 153;
            var d = doy - (153 * mp + 2) / 5 + 1;
            var m = mp + (mp < 10 ? 3 : -9);
            var y = yoe + era * 400 + (m <= 2 ? 1 : 0);
            return $"{y:D4}-{m:D2}-{d:D2}";
        }
    }

    /// <summary>Day of week, Sunday = 0. Day 0 (1970-01-01) was a Thursday.</summary>
    public int Weekday => ((Number + 4) % 7 + 7) % 7;
}

public sealed record PerDay(string Date, long Tokens, double Cost, int Intensity);

public sealed record Streaks(int Longest, int Current)
{
    /// <summary>Port of computeStreaks: walk every calendar day in the range;
    /// a day is active when it has tokens. Current counts back from the range
    /// end.</summary>
    public static Streaks Compute(
        IReadOnlyDictionary<string, PerDay> perDayMap, string rangeStart, string rangeEnd)
    {
        var start = ISODay.Parse(rangeStart);
        var end = ISODay.Parse(rangeEnd);
        if (start is null || end is null || end.Value.Number < start.Value.Number)
        {
            return new Streaks(0, 0);
        }

        bool Active(int n) =>
            perDayMap.TryGetValue(new ISODay(n).Iso, out var day) && day.Tokens > 0;

        var longest = 0;
        var run = 0;
        for (var n = start.Value.Number; n <= end.Value.Number; n++)
        {
            if (Active(n))
            {
                run++;
                longest = Math.Max(longest, run);
            }
            else
            {
                run = 0;
            }
        }

        var current = 0;
        for (var n = end.Value.Number; n >= start.Value.Number && Active(n); n--)
        {
            current++;
        }

        return new Streaks(longest, current);
    }
}

public sealed class UsageStats
{
    public long TotalTokens { get; }
    public double TotalCost { get; }
    public int ActiveDays { get; }
    public (string Date, double Cost)? BestDay { get; }
    public double AveragePerDay { get; }
    public DateRange DateRange { get; }
    public IReadOnlyList<PerDay> PerDay { get; }
    public IReadOnlyDictionary<string, PerDay> PerDayMap { get; }
    public Streaks Streaks { get; }
    public IReadOnlyList<string> PresentClients { get; }
    public long MaxTokens { get; }

    /// <summary>Port of computeStats: aggregate per-day totals over
    /// <paramref name="selectedClients"/> (empty set = nothing selected; pass
    /// all present clients for the all-agent view).</summary>
    public UsageStats(UsagePayload payload, IReadOnlySet<string> selectedClients)
    {
        var perDay = new List<PerDay>();
        var perDayMap = new Dictionary<string, PerDay>();
        var present = new SortedSet<string>(StringComparer.Ordinal);
        long totalTokens = 0;
        var totalCost = 0.0;
        (string Date, double Cost)? bestDay = null;
        long maxTokens = 0;

        foreach (var c in payload.Contributions)
        {
            long dayTokens = 0;
            var dayCost = 0.0;
            foreach (var cc in c.Clients)
            {
                present.Add(cc.Client);
                if (!selectedClients.Contains(cc.Client))
                {
                    continue;
                }

                var t = cc.Tokens;
                dayTokens += t.Input + t.Output + t.CacheRead + t.CacheWrite + t.Reasoning;
                dayCost += cc.Cost;
            }

            if (dayTokens == 0 && dayCost == 0)
            {
                continue;
            }

            var entry = new PerDay(c.Date, dayTokens, dayCost, c.Intensity);
            perDay.Add(entry);
            perDayMap[c.Date] = entry;
            totalTokens += dayTokens;
            totalCost += dayCost;
            maxTokens = Math.Max(maxTokens, dayTokens);
            if (bestDay is null || dayCost > bestDay.Value.Cost)
            {
                bestDay = (c.Date, dayCost);
            }
        }

        TotalTokens = totalTokens;
        TotalCost = totalCost;
        ActiveDays = perDay.Count;
        BestDay = bestDay;
        AveragePerDay = perDay.Count == 0 ? 0 : totalCost / perDay.Count;
        DateRange = payload.Meta.DateRange;
        PerDay = perDay;
        PerDayMap = perDayMap;
        Streaks = Streaks.Compute(perDayMap, DateRange.Start, DateRange.End);
        PresentClients = [.. present];
        MaxTokens = maxTokens;
    }
}

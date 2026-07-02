namespace TokenBar.Core;

// GitHub-style contribution grid layout, port of TokenBarCore/Grid.swift
// (originally src/lib/grid.ts). Pure civil-date math over ISODay — no
// calendar/timezone involvement, matching how tokscale-core buckets days.

public sealed record GridCell(
    int Col,
    // 0 = Sunday (top row).
    int Row,
    string Date,
    bool InYear,
    bool Active,
    long Tokens,
    double Cost);

public sealed record GridLayout(
    int Cols,
    int Rows, // 7
    IReadOnlyList<GridCell> Cells,
    long MaxTokens);

public static class Grid
{
    /// <summary>53+ columns x 7 rows; col 0 row 0 = the Sunday on or before
    /// Jan 1 of <paramref name="year"/>. Cells outside the year are
    /// InYear=false and never active.</summary>
    public static GridLayout Build(string year, IReadOnlyDictionary<string, PerDay> perDayMap)
    {
        var jan1 = ISODay.Parse($"{year}-01-01");
        var dec31 = ISODay.Parse($"{year}-12-31");
        if (jan1 is null || dec31 is null)
        {
            return new GridLayout(53, 7, [], 0);
        }

        var start = new ISODay(jan1.Value.Number - jan1.Value.Weekday);
        var lastCol = (dec31.Value.Number - start.Number) / 7;
        var cols = Math.Max(53, lastCol + 1);
        var yearPrefix = $"{year}-";

        var cells = new List<GridCell>(cols * 7);
        long maxTokens = 0;
        for (var col = 0; col < cols; col++)
        {
            for (var row = 0; row < 7; row++)
            {
                var date = new ISODay(start.Number + col * 7 + row).Iso;
                var inYear = date.StartsWith(yearPrefix, StringComparison.Ordinal);
                perDayMap.TryGetValue(date, out var entry);
                var tokens = entry?.Tokens ?? 0;
                var active = inYear && tokens > 0;
                if (active)
                {
                    maxTokens = Math.Max(maxTokens, tokens);
                }

                cells.Add(new GridCell(col, row, date, inYear, active, tokens, entry?.Cost ?? 0));
            }
        }

        return new GridLayout(cols, 7, cells, maxTokens);
    }
}

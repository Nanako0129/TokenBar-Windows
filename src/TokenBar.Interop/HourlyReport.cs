namespace TokenBar.Interop;

// Per-hour report (`HourlyReport` in types.ts; Swift port:
// TokenBarCore/HourlyReport.swift).

public sealed record HourlyReportEntry(
    // "YYYY-MM-DD HH:00" local-time slot.
    string Hour,
    IReadOnlyList<string> Clients,
    IReadOnlyList<string> Models,
    long Input,
    long Output,
    long CacheRead,
    long CacheWrite,
    long Reasoning,
    long Total,
    int MessageCount,
    int TurnCount,
    double Cost);

public sealed record HourlyReport(
    IReadOnlyList<HourlyReportEntry> Entries,
    double TotalCost);

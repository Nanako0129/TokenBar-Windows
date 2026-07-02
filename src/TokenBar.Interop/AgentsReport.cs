namespace TokenBar.Interop;

// Per-(sub-)agent report (`AgentsReport` in types.ts; Swift port:
// TokenBarCore/AgentsReport.swift).

public sealed record AgentReportEntry(
    string Agent,
    IReadOnlyList<string> Clients,
    long Input,
    long Output,
    long CacheRead,
    long CacheWrite,
    long Reasoning,
    long Total,
    double Cost,
    int Messages);

public sealed record AgentsReport(
    IReadOnlyList<AgentReportEntry> Entries,
    double TotalCost,
    int TotalMessages);

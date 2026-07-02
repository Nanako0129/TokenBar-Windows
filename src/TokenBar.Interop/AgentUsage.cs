namespace TokenBar.Interop;

// OAuth quota cards (`AgentUsagePayload` in the Tauri frontend's
// src/lib/agentUsage.ts; Swift port: TokenBarCore/AgentUsage.swift).

public sealed record AgentIdentity(
    string? Email,
    string? Plan);

public sealed record UsageWindow(
    string Label,
    double UsedPercent,
    double RemainingPercent,
    string? ResetsAt,
    string? ResetText,
    // Total window length in minutes; enables pace (expected vs actual).
    long? WindowMinutes,
    // Expected used-percent now from *historical* weekly samples (Codex weekly
    // only, once enough past weeks accrued). Absent → fall back to linear pace.
    double? HistoricalExpectedPercent,
    // 0..1 chance the window empties before reset at the historical burn rate.
    double? RunOutProbability);

public sealed record CreditsSnapshot(
    double? Remaining,
    bool Unlimited);

public sealed record AgentUsageSnapshot(
    string ClientId,
    string Source,
    string UpdatedAt,
    AgentIdentity? Identity,
    IReadOnlyList<UsageWindow> Windows,
    CreditsSnapshot? Credits,
    string? Error);

public sealed record AgentUsagePayload(
    string GeneratedAt,
    IReadOnlyList<AgentUsageSnapshot> Agents,
    // Subscription-type providers opencode is authed against (e.g. ["Codex"]).
    // Omitted from the JSON entirely when empty.
    IReadOnlyList<string>? OpencodeSubscriptions);

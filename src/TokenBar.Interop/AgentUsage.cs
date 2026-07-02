namespace TokenBar.Interop;

// OAuth quota cards (`AgentUsagePayload` in the Tauri frontend's
// src/lib/agentUsage.ts; Swift port: TokenBarCore/AgentUsage.swift).

// Nullable params carry `= null` defaults: TbCore's serializer enforces
// required constructor parameters (Swift-Decodable strictness), and these are
// the fields the wire legitimately omits.

public sealed record AgentIdentity(
    string? Email = null,
    string? Plan = null);

public sealed record UsageWindow(
    string Label,
    double UsedPercent,
    double RemainingPercent,
    string? ResetsAt = null,
    string? ResetText = null,
    // Total window length in minutes; enables pace (expected vs actual).
    long? WindowMinutes = null,
    // Expected used-percent now from *historical* weekly samples (Codex weekly
    // only, once enough past weeks accrued). Absent → fall back to linear pace.
    double? HistoricalExpectedPercent = null,
    // 0..1 chance the window empties before reset at the historical burn rate.
    double? RunOutProbability = null);

public sealed record CreditsSnapshot(
    bool Unlimited,
    double? Remaining = null);

public sealed record AgentUsageSnapshot(
    string ClientId,
    string Source,
    string UpdatedAt,
    IReadOnlyList<UsageWindow> Windows,
    AgentIdentity? Identity = null,
    CreditsSnapshot? Credits = null,
    string? Error = null);

public sealed record AgentUsagePayload(
    string GeneratedAt,
    IReadOnlyList<AgentUsageSnapshot> Agents,
    // Subscription-type providers opencode is authed against (e.g. ["Codex"]).
    // Omitted from the JSON entirely when empty.
    IReadOnlyList<string>? OpencodeSubscriptions = null);

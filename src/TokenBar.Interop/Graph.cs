namespace TokenBar.Interop;

// Contribution-graph payload (`UsagePayload` in the Tauri frontend's
// src/lib/types.ts; Swift port: TokenBarCore/Graph.swift). Wire keys are the
// Rust serde camelCase serialization; the Web JsonSerializerOptions in TbCore
// map them onto these PascalCase properties.

public sealed record TokenBreakdown(
    long Input,
    long Output,
    long CacheRead,
    long CacheWrite,
    long Reasoning);

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

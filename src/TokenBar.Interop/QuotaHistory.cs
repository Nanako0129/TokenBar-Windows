namespace TokenBar.Interop;

// Persisted quota-pace history as `tb_quota_history` exports it (Swift port:
// TokenBarCore/QuotaCurve.swift, whose `QuotaCurvePoint` is the same record
// reached through a different export). Keys match the Rust serde camelCase
// serialization exactly.

/// <summary>Where a sample's window length came from. Same Rust
/// <c>DurationSource</c> wire values as <see cref="UsagePaceDurationSource"/>;
/// a separate type only because the two payloads decode independently.</summary>
public enum QuotaHistoryDurationSource
{
    Provider,
    Contract,
    Observed,
}

/// <summary>Which writer recorded the sample. <c>ImportedV2</c> is the one-time
/// schema-2 migration lane; everything the v3 writer records is
/// <c>LiveV3</c>.</summary>
public enum QuotaHistorySampleOrigin
{
    LiveV3,
    ImportedV2,
}

/// <summary>
/// One stored quota reading. <c>SampledAt</c> is the real observation time,
/// never a grid position: phase-bucket admission decides <em>whether</em> a
/// reading is kept, not when it was taken, so samples are unevenly spaced by
/// construction.
/// </summary>
/// <param name="IsActiveGroup">
/// Whether this sample belongs to the cycle still running — answered by the
/// producer, never re-derived here.
/// <para>
/// The fold used to decide this by comparing <c>ResetAt</c> against the series'
/// <c>activeResetAt</c>. Those are not comparable: <c>activeResetAt</c> is the
/// RAW provider value and every stored <c>resetAt</c> has been through
/// <c>normalize_sample_reset</c>, so the comparison silently failed whenever the
/// provider's reset was not already on the quantum — the ordinary case, not the
/// exotic one (codex off by 62s, grok by 19s). The running cycle was then listed
/// under "past windows", stood beside completed spans as though comparable, and
/// counted toward the three-cycle equivalence threshold while still filling.
/// </para>
/// <para>
/// Required, not defaulted: Rust always emits it, so an absent key is ABI drift.
/// Defaulting it to false would render every sample as finished history — the
/// exact misreading this field exists to remove, arriving silently instead of as
/// a decode failure. <see cref="TbCore"/>'s
/// <c>RespectRequiredConstructorParameters</c> is what makes a parameter without
/// a default behave that way.
/// </para>
/// </param>
public sealed record QuotaHistorySample(
    long ResetAt,
    long DurationSeconds,
    QuotaHistoryDurationSource DurationSource,
    double UsedPercent,
    long SampledAt,
    QuotaHistorySampleOrigin Origin,
    bool IsActiveGroup);

/// <summary>
/// One stored series and its raw samples. Identity is the store's own triple;
/// the window's display label is not in the store and is joined consumer-side
/// on <c>(clientId, PaceStatus.WindowKey)</c> against the live agent-usage
/// payload. That label is a hint — a series with no matching live window keeps
/// its identity and loses only the label, and two series landing on one live
/// window stay separate.
/// </summary>
public sealed record QuotaHistorySeries(
    string ProviderId,
    string AccountScope,
    string WindowKey,
    IReadOnlyList<QuotaHistorySample> Samples);

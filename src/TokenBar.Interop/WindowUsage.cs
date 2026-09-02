namespace TokenBar.Interop;

// Per-message usage inside one quota window, as `tb_window_usage` exports it
// (Swift port: TokenBarCore/WindowUsage.swift). One row per message, not per
// bucket: a quota window is a small slice of history, so the curve's
// resolution stays a UI choice instead of a wire contract. Attribution is
// applied on this side, because it is the user's own declaration — the
// engine never sees it.

/// <summary>
/// One message inside the requested <c>[from_ms, until_ms)</c> window.
/// </summary>
public sealed record WindowMessage(
    long Timestamp,
    string Client,
    string ProviderId,
    string ModelId,
    long Input,
    long Output,
    long CacheRead,
    long CacheWrite,
    long Reasoning,
    double Cost,
    bool IsTurnStart)
{
    /// <summary>Saturating, like the graph and report totals that fold the same
    /// untrusted counters (<see cref="SaturatingArithmetic"/>). These come from
    /// local session files this app does not write; a corrupt row summing past
    /// <see cref="long.MaxValue"/> would trap, and a malformed line in someone's
    /// transcript must not be able to terminate the UI.
    /// Everything except cache reads, summed from the components rather than
    /// subtracted from the saturated total.
    ///
    /// <c>Tokens - CacheRead</c> is wrong twice over on a corrupt row: when both
    /// saturate it reports zero non-cache tokens although the input alone was
    /// enormous, and an overflowing subtraction can wrap negative, which then
    /// flows into a ratio as a negative numerator. Neither is a crash, which is
    /// what makes them worse than one.</summary>
    public long TokensExCacheRead =>
        Input.SaturatingAdd(Output).SaturatingAdd(CacheWrite).SaturatingAdd(Reasoning);

    public long Tokens =>
        Input
            .SaturatingAdd(Output)
            .SaturatingAdd(CacheRead)
            .SaturatingAdd(CacheWrite)
            .SaturatingAdd(Reasoning);
}

/// <param name="UndatedCount">Messages with no usable timestamp. Counted,
/// never silently dropped — a window total that quietly omits rows is worse
/// than one that says so.</param>
public sealed record WindowUsage(
    IReadOnlyList<WindowMessage> Messages,
    int UndatedCount,
    int ProcessingTimeMs);

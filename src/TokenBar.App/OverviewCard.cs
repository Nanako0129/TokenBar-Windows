namespace TokenBar.App;

/// <summary>The pieces the Overview lens shows, in render order.
///
/// <para><b>Declaration order is render order</b>, mirroring macOS's
/// <c>OverviewCard</c> so that "what appears where" has one source rather than
/// two that can disagree. The order itself is not arbitrary and is not ours to
/// choose freely: it is the shipped macOS arrangement, and a Windows build that
/// puts the same cards in a different sequence is a parity gap the eye notices
/// before any feature list does.</para>
///
/// <para>The order is pinned by a test, and that is worth explaining rather
/// than assuming. macOS got this exact sequence wrong once — its own comment
/// records that the commit which claimed to restore the order had two cards
/// the other way round and said so in its message, and that the pinned order
/// was what caught it. Windows arrived at the same mistake independently: the
/// quota summary was first built directly above the limits card, which put the
/// usage chart ahead of it. Repeating a mistake another platform has already
/// paid for and already guarded against is the avoidable kind.</para>
///
/// <para>Hiding individual cards is a separate macOS capability
/// (<c>tokenbar.overview.hidden</c>) that Windows does not have yet. This enum
/// is deliberately only an order, not a visibility model — the seat is here
/// when that slice arrives, but nothing speculative is built into it now.</para>
/// </summary>
internal enum OverviewCard
{
    QuotaSummary,
    Chart,
    Limits,
    Trace,
    Models,
    Streaks,
}

internal static class OverviewCards
{
    /// <summary>Render order, read by <c>DashboardView.BuildOverview</c> and
    /// asserted by <c>OverviewCardTests</c>. Enum declaration order is the
    /// source; this exists so the order can be enumerated and compared rather
    /// than only being implied by a sequence of Add calls that no test can
    /// see.</summary>
    internal static readonly OverviewCard[] RenderOrder =
    [
        OverviewCard.QuotaSummary,
        OverviewCard.Chart,
        OverviewCard.Limits,
        OverviewCard.Trace,
        OverviewCard.Models,
        OverviewCard.Streaks,
    ];
}

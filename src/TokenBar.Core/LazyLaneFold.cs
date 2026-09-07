namespace TokenBar.Core;

/// <summary>
/// The retain-on-failure fold every lazy Dashboard lane's own fetch pass must
/// apply before publishing: a failed read keeps whatever the snapshot already
/// had rather than replacing good, previously-fetched data with null, and
/// records its own attempted/failed facts independently of what ended up
/// retained — the same shape <c>DashboardModel.Snapshot.QuotaHistory</c> and
/// <c>WindowUsage</c> already carry (see those properties' own doc comments).
/// <para>
/// Round 9's finding: the Hourly and Agents lanes did not apply this fold at
/// all — <c>DashboardModel.FetchLazyWanted</c> published
/// <c>Hourly = hourly ? hourlyReport : s.Hourly</c>, so a transient failure
/// (<c>hourlyReport is null</c> because the fetch threw) overwrote a
/// previously-good report with null, and the view then showed "Loading
/// hourly data…" — indistinguishable from a cold start that has not fetched
/// yet, even though good data existed a moment before. Strictly worse than
/// the loading-vs-failed confusion rounds 7 and 8 already fixed on the other
/// two lanes.
/// </para>
/// <para>
/// Extracted here rather than left inline in <c>DashboardModel.Publish</c>,
/// because <c>DashboardModel.cs</c> is compiled by no test project (it opens
/// with <c>using Microsoft.UI.Dispatching;</c>) — this is the seam a test can
/// actually reach.
/// </para>
/// </summary>
public static class LazyLaneFold
{
    /// <summary>The three facts one lane's own Publish call needs, folded from
    /// this pass's own result and the snapshot's prior state.</summary>
    public readonly record struct Result<T>(T? Value, bool Attempted, bool FetchFailed) where T : class;

    /// <param name="requested">Whether THIS pass asked for the lane at all —
    /// the caller's own "wanted" flag, not derived from <paramref name="fetched"/>:
    /// <c>fetched is null</c> alone cannot tell "did not ask" from "asked and
    /// it threw", which is exactly the ambiguity this fold exists to
    /// resolve.</param>
    /// <param name="fetched">This pass's own fetch result — null when not
    /// requested, and also null when requested but the fetch threw
    /// (<c>TryFetch</c> collapses both to null, which is why
    /// <paramref name="requested"/> has to travel alongside it rather than
    /// being inferred from it).</param>
    /// <param name="previous">The snapshot's own already-published value,
    /// kept when this pass has nothing newer to report.</param>
    public static Result<T> Apply<T>(
        bool requested, T? fetched, T? previous, bool previousAttempted, bool previousFetchFailed)
        where T : class =>
        new(
            fetched ?? previous,
            requested || previousAttempted,
            requested ? fetched is null : previousFetchFailed);
}

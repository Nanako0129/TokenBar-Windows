namespace TokenBar.App;

/// <summary>
/// The Usage-attribution page's model-report fetch guard, split out of
/// <c>SettingsWindow</c> so it can be exercised by <c>dotnet test</c> without a
/// WinUI host — that file itself compiles into no test project.
/// <para>
/// <c>SettingsWindow</c> is a singleton reused across hide/show (macOS
/// <c>isReleasedWhenClosed=false</c> parity): closing it hides it rather than
/// destroying it, so a bare "fetch once per process" guard on the report
/// request meant a provider first observed after that one fetch could never
/// appear on the page — not until the app restarted. <see cref="Reset"/> is
/// the fix: called when the window hides, it clears the guard so the next
/// visit fetches again, while leaving whatever the caller already cached
/// alone. The already-fetched report stays in place across a reset, so the
/// page keeps showing it — real rows, not "unavailable" — for as long as the
/// fresh fetch this triggers is still in flight.
/// </para>
/// </summary>
public sealed class AttributionReportGate
{
    /// <summary>True once a fetch has been asked for and not yet reset. Named
    /// separately from <see cref="Settled"/>: a fetch can be requested and
    /// still in flight, which is exactly the state a reset must be able to
    /// happen in without losing track of the outstanding request.</summary>
    public bool Requested { get; private set; }

    /// <summary>True once a fetch has returned — successfully or not — so the
    /// page can tell "no report yet" apart from "asked and it failed".
    /// Deliberately NOT cleared by <see cref="Reset"/>: the point of a reset is
    /// to ask again, not to forget the answer the last ask produced.</summary>
    public bool Settled { get; private set; }

    /// <summary>Bumped every time <see cref="ShouldFetch"/> actually starts a
    /// fetch. <see cref="Requested"/> alone answers "may a fetch begin", never
    /// "which of possibly several outstanding fetches is the current one" — a
    /// <see cref="Reset"/> that happens before the fetch it is resetting has
    /// completed lets a second one start while the first is still in flight,
    /// and nothing about the latch says which of the two completions a caller
    /// should believe. The generation each fetch captured at start
    /// (<see cref="ShouldFetch"/>'s return value once true) is that answer: a
    /// completion is current only while it still equals <see cref="Generation"/>,
    /// checked with <see cref="IsCurrent"/>.</summary>
    public int Generation { get; private set; }

    /// <summary>The generation <see cref="Settle"/> was last called for, or
    /// -1 (never equal to a real <see cref="Generation"/>, which starts
    /// counting from 1) before any fetch has ever settled. Backs
    /// <see cref="IsLoading"/>: comparing this against <see cref="Generation"/>
    /// is what tells "settled for THIS request" apart from "settled for a
    /// stale one".</summary>
    private int _settledGeneration = -1;

    /// <summary>
    /// What a caller with no retained report should actually show: the
    /// spinner state, not <see cref="Settled"/> directly.
    /// <para>
    /// <see cref="Settled"/> alone cannot tell "failed, idle" apart from
    /// "failed, retrying" (round 13's finding): <see cref="Reset"/> leaves
    /// <see cref="Settled"/> untouched on purpose, so once a fetch has ever
    /// failed with nothing to retain, <see cref="Settled"/> stays true
    /// straight through the next hide/show — including the moment
    /// <c>SettingsWindow.Rebuild()</c> builds the attribution page, which
    /// happens BEFORE <c>ShowPage()</c> re-triggers the fetch, so no amount
    /// of reordering the fetch call fixes it: the expression has to be right
    /// even before the retry has started, and it has to stay right for the
    /// whole time the retry is in flight, not only at that first instant.
    /// </para>
    /// <para>
    /// Comparing <see cref="_settledGeneration"/> against <see cref="Generation"/>
    /// answers both halves at once, the same way <see cref="IsCurrent"/>
    /// already does for a fetch's own completion: <see cref="Reset"/> leaves
    /// <see cref="Generation"/> alone, so immediately after a reset the last
    /// settle (if any) still matches — <see cref="Requested"/> going false is
    /// what marks that answer stale, hence the first disjunct. Once the retry
    /// actually starts, <see cref="ShouldFetch"/> bumps <see cref="Generation"/>
    /// past <see cref="_settledGeneration"/>, and the mismatch alone reads as
    /// loading for as long as the retry is out — a plain <c>!Requested</c>
    /// check would have gone false the instant <see cref="ShouldFetch"/> ran,
    /// wrongly showing the stale answer again while the retry was still in
    /// flight (e.g. a settings write during that window forces a rebuild that
    /// reads this).
    /// </para>
    /// </summary>
    public bool IsLoading => !Requested || _settledGeneration != Generation;

    /// <summary>Call before starting a fetch. Returns true the first time this
    /// is called after construction or after <see cref="Reset"/>, and false on
    /// every call in between — the caller starts a fetch only when this
    /// returns true, exactly the shape the request-guard existed for. On a
    /// true return, <see cref="Generation"/> has already been bumped to the
    /// value this fetch's completion should capture and later check with
    /// <see cref="IsCurrent"/>.</summary>
    public bool ShouldFetch()
    {
        if (Requested)
        {
            return false;
        }

        Requested = true;
        Generation++;
        return true;
    }

    /// <summary>Whether <paramref name="generation"/> — captured by a prior
    /// <see cref="ShouldFetch"/> call — is still the current one. False means a
    /// later fetch has since started (via <see cref="Reset"/> and a fresh
    /// <see cref="ShouldFetch"/>), and this completion's result must be
    /// discarded rather than applied.</summary>
    public bool IsCurrent(int generation) => generation == Generation;

    /// <summary>Call when the fetch this gate guarded has returned.</summary>
    public void Settle()
    {
        Settled = true;
        _settledGeneration = Generation;
    }

    /// <summary>Whether a settled fetch's completion may call back into
    /// <c>SettingsWindow.ShowPage</c>. Round 14's finding: <see cref="Reset"/>
    /// clears only <see cref="Requested"/>, so if a completion calls
    /// <c>ShowPage</c> while the window is hidden, that call's own
    /// <c>EnsureAttributionReport</c> re-enters <see cref="ShouldFetch"/>,
    /// which sees <see cref="Requested"/> already false (from the hide's
    /// <see cref="Reset"/>) and starts a second fetch right there — consuming
    /// the very reset the hide performed. That second fetch then settles with
    /// <see cref="Requested"/> true again, so the next reopen's own
    /// <see cref="ShouldFetch"/> call finds the guard already armed and skips
    /// its refetch, serving a stale report until another close. A hidden
    /// completion must not call back in at all; this is the predicate the
    /// completion checks first.</summary>
    public static bool ShouldNotifyOnCompletion(bool isAttributionPageSelected, bool isWindowVisible) =>
        isAttributionPageSelected && isWindowVisible;

    /// <summary>Call when the settings window hides. Clears the guard so the
    /// next page visit fetches again; <see cref="Settled"/> and whatever report
    /// the caller cached are left untouched, so the page has real data to show
    /// while that fresh fetch is out.
    /// <para>
    /// Deliberately does NOT touch <see cref="Generation"/>: clearing the latch
    /// is what makes a second fetch possible while the first may still be in
    /// flight (hide, then show again before the first request lands), and it
    /// is exactly then that the two completions need telling apart. The
    /// caller must gate its own write of the fetched result behind
    /// <see cref="IsCurrent"/> using the generation <see cref="ShouldFetch"/>
    /// handed it, or whichever completion finishes last wins by simply
    /// overwriting the newer one's answer.
    /// </para>
    /// </summary>
    public void Reset() => Requested = false;
}

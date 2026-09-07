using TokenBar.App;

namespace TokenBar.Core.Tests;

/// <summary>
/// The attribution page's fetch guard. The defect this exists to prevent: the
/// settings window is a singleton reused across hide/show, so a guard that
/// only ever fires once per process left a provider first observed after that
/// one fetch unclassifiable until the app restarted — the acceptance case is
/// "a second visit refetches", and that is exactly what these pin.
/// </summary>
public class AttributionReportGateTests
{
    [Fact]
    public void TheFirstVisitFetches()
    {
        var gate = new AttributionReportGate();
        Assert.True(gate.ShouldFetch());
    }

    // The guard SettingsWindow's original bare bool existed for: switching
    // tabs and back within one open window must not re-ask every time.
    [Fact]
    public void ASecondCallBeforeAResetDoesNotFetchAgain()
    {
        var gate = new AttributionReportGate();
        Assert.True(gate.ShouldFetch());
        Assert.False(gate.ShouldFetch());
        Assert.False(gate.ShouldFetch());
    }

    // The acceptance case this P2 was filed for: reopening the settings window
    // (Reset, called from AppWindow.Closing) and revisiting the page fetches
    // again rather than serving the first fetch this process ever made.
    [Fact]
    public void RevisitingAfterAResetFetchesAgain()
    {
        var gate = new AttributionReportGate();
        Assert.True(gate.ShouldFetch());
        gate.Settle();

        gate.Reset();

        Assert.True(gate.ShouldFetch());
    }

    // Reset clears only the guard, not the cache: the point of a reset is to
    // ask again, not to forget the answer the last ask produced — a reset that
    // also cleared Settled would make the page flash "unavailable" (Settled
    // false + no report) instead of keeping the last real page up while the
    // fresh fetch is out.
    [Fact]
    public void ResetDoesNotClearSettled()
    {
        var gate = new AttributionReportGate();
        gate.ShouldFetch();
        gate.Settle();
        Assert.True(gate.Settled);

        gate.Reset();

        Assert.True(gate.Settled);
    }

    [Fact]
    public void ANewGateHasNotSettled()
    {
        Assert.False(new AttributionReportGate().Settled);
    }

    // Round 13's P2: SettingsWindow.cs:608 used to read `!Settled` directly,
    // so once a fetch had ever failed with nothing to retain, Settled stayed
    // true straight through the next hide/show (the fixture directly above
    // this one pins that Reset() must not clear it) — and Rebuild() builds
    // the attribution page BEFORE ShowPage() re-triggers the fetch, so the
    // page rendered "could not be loaded" for the whole in-flight retry.
    // IsLoading is the fix: it reads Requested too, and Reset() clears
    // Requested immediately, well before the next build — not only once the
    // retry's ShouldFetch() call actually lands.
    [Fact]
    public void IsLoadingIsTrueAfterResetEvenThoughSettledStaysTrue()
    {
        var gate = new AttributionReportGate();
        gate.ShouldFetch();
        gate.Settle();
        Assert.False(gate.IsLoading);

        gate.Reset();

        Assert.True(gate.Settled); // unchanged, per ResetDoesNotClearSettled
        Assert.True(gate.IsLoading); // but the retry state must read as loading
    }

    // Once the retry's own ShouldFetch() call actually lands (Requested true
    // again) and it settles, IsLoading must go back to false — otherwise a
    // second genuine failure would show "loading" forever instead of
    // "could not be loaded".
    [Fact]
    public void IsLoadingGoesFalseAgainOnceTheRetrySettles()
    {
        var gate = new AttributionReportGate();
        gate.ShouldFetch();
        gate.Settle();
        gate.Reset();

        gate.ShouldFetch();
        Assert.True(gate.IsLoading); // fetch started, not yet settled

        gate.Settle();
        Assert.False(gate.IsLoading);
    }

    [Fact]
    public void ANewGateIsLoading() =>
        Assert.True(new AttributionReportGate().IsLoading);

    // Round-3 P2 (a race the previous fix introduced): Reset() re-arms the
    // latch without knowing whether the fetch it is resetting has finished,
    // so a hide/show/hide can start a second fetch while the first is still
    // out. SettingsWindow.EnsureAttributionReport captures Generation before
    // starting each fetch and checks IsCurrent from the completion — this
    // pins that contract deterministically, with no Task/timing involved:
    // the OLDER fetch's captured generation must read as stale once a NEWER
    // one has started, regardless of which one's network call actually
    // returns first.
    [Fact]
    public void AnOlderGenerationIsNotCurrentOnceANewerFetchHasStarted()
    {
        var gate = new AttributionReportGate();

        Assert.True(gate.ShouldFetch());
        var firstGeneration = gate.Generation;
        Assert.True(gate.IsCurrent(firstGeneration));

        // Hide, then show again before the first request lands.
        gate.Reset();
        Assert.True(gate.ShouldFetch());
        var secondGeneration = gate.Generation;

        Assert.NotEqual(firstGeneration, secondGeneration);
        // The older fetch's completion — arriving after the newer one
        // started, whichever order the two network calls actually land in —
        // must not be able to claim it is still the current request.
        Assert.False(gate.IsCurrent(firstGeneration));
        Assert.True(gate.IsCurrent(secondGeneration));
    }

    // The mirror of the case above: if the OLDER fetch happens to land
    // first, only the newer generation may still write. A caller applying
    // this rule (`if (!IsCurrent(captured)) return;` before writing its
    // cache) can therefore never have a newer result overwritten by a
    // straggling older one, deterministically, without needing the two
    // completions to actually race in real time.
    [Fact]
    public void ApplyingOnlyCurrentGenerationCompletionsCannotLetAnOlderOneOverwriteANewerResult()
    {
        var gate = new AttributionReportGate();
        Assert.True(gate.ShouldFetch());
        var firstGeneration = gate.Generation;

        gate.Reset();
        Assert.True(gate.ShouldFetch());
        var secondGeneration = gate.Generation;

        string? cache = null;

        // The newer fetch's completion lands first and writes.
        if (gate.IsCurrent(secondGeneration))
        {
            cache = "second (fresh)";
        }

        // The older fetch's completion lands after — it must be refused.
        if (gate.IsCurrent(firstGeneration))
        {
            cache = "first (stale)";
        }

        Assert.Equal("second (fresh)", cache);
    }
}

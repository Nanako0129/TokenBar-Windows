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
}

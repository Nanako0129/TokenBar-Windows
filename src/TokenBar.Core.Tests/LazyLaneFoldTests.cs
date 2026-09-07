using TokenBar.Core;
using Xunit;

namespace TokenBar.Core.Tests;

// Round 9's third finding: the Hourly and Agents lanes had no retain-on-
// failure fold at all, so DashboardModel.FetchLazyWanted published
// `Hourly = hourly ? hourlyReport : s.Hourly` — a transient failure
// (hourlyReport is null because TryFetch swallowed a throw) overwrote a
// previously-good report with null. DashboardModel.cs itself is compiled by
// no test project (it opens with `using Microsoft.UI.Dispatching;`), so this
// is the seam that actually gets exercised.
//
// A third output, FetchFailed, and the previousFetchFailed input that fed it,
// were dropped later: every reader on the equivalence path that cared about
// "did the MOST RECENT attempt fail" already checked Value for retained data
// first, so once Value is non-null, deriving Failed-vs-Succeeded from
// Value is null alone reaches the same answer that output existed to correct
// — see DashboardModel.Snapshot.HourlyOutcome's own doc comment. The tests
// that existed only to pin that output are gone; the rest keep asserting the
// Value/Attempted behaviour that remains.
public class LazyLaneFoldTests
{
    private sealed record Report(int Value);

    // ---- the retention bug itself -----------------------------------------

    [Fact]
    public void AFailedFetchRetainsThePreviousValueRatherThanOverwritingItWithNull()
    {
        var previous = new Report(7);

        var result = LazyLaneFold.Apply(
            requested: true, fetched: null, previous, previousAttempted: true);

        // The bug this fold exists to fix: `requested ? fetched : previous`
        // (the old Hourly/Agents line) would have set Value to null here,
        // discarding `previous` outright. The mutant that flips `??` to `:`
        // for the Value line reproduces exactly that regression.
        Assert.Equal(previous, result.Value);
        Assert.True(result.Attempted);
    }

    [Fact]
    public void ASuccessfulFetchReplacesThePreviousValue()
    {
        var previous = new Report(7);
        var fresh = new Report(9);

        var result = LazyLaneFold.Apply(
            requested: true, fetched: fresh, previous, previousAttempted: true);

        Assert.Equal(fresh, result.Value);
        Assert.True(result.Attempted);
    }

    [Fact]
    public void APassThatDidNotAskLeavesTheValueAtWhateverThePriorPassLeftIt()
    {
        var previous = new Report(7);

        var result = LazyLaneFold.Apply(
            requested: false, fetched: null, previous, previousAttempted: true);

        Assert.Equal(previous, result.Value);
        Assert.True(result.Attempted);
    }

    // ---- attempted as a fact about THIS request, not the result -----------

    [Fact]
    public void NeverRequestedAndNothingRetainedIsNotAttempted()
    {
        var result = LazyLaneFold.Apply<Report>(
            requested: false, fetched: null, previous: null, previousAttempted: false);

        Assert.Null(result.Value);
        Assert.False(result.Attempted);
    }
}

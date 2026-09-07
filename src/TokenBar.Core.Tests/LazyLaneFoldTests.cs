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
public class LazyLaneFoldTests
{
    private sealed record Report(int Value);

    // ---- the retention bug itself -----------------------------------------

    [Fact]
    public void AFailedFetchRetainsThePreviousValueRatherThanOverwritingItWithNull()
    {
        var previous = new Report(7);

        var result = LazyLaneFold.Apply(
            requested: true, fetched: null, previous, previousAttempted: true, previousFetchFailed: false);

        // The bug this fold exists to fix: `requested ? fetched : previous`
        // (the old Hourly/Agents line) would have set Value to null here,
        // discarding `previous` outright. The mutant that flips `??` to `:`
        // for the Value line reproduces exactly that regression.
        Assert.Equal(previous, result.Value);
        Assert.True(result.Attempted);
        Assert.True(result.FetchFailed);
    }

    [Fact]
    public void ASuccessfulFetchReplacesThePreviousValue()
    {
        var previous = new Report(7);
        var fresh = new Report(9);

        var result = LazyLaneFold.Apply(
            requested: true, fetched: fresh, previous, previousAttempted: true, previousFetchFailed: true);

        Assert.Equal(fresh, result.Value);
        Assert.True(result.Attempted);
        Assert.False(result.FetchFailed);
    }

    [Fact]
    public void APassThatDidNotAskLeavesEverythingAtWhateverThePriorPassLeftIt()
    {
        var previous = new Report(7);

        var result = LazyLaneFold.Apply(
            requested: false, fetched: null, previous, previousAttempted: true, previousFetchFailed: true);

        Assert.Equal(previous, result.Value);
        Assert.True(result.Attempted);
        // Not derived from `fetched is null` when this pass never asked —
        // the prior pass's own FetchFailed carries forward unexamined.
        Assert.True(result.FetchFailed);
    }

    // ---- attempted/failed as facts about THIS request, not the result -----

    [Fact]
    public void NeverRequestedAndNothingRetainedIsNotAttempted()
    {
        var result = LazyLaneFold.Apply<Report>(
            requested: false, fetched: null, previous: null, previousAttempted: false, previousFetchFailed: false);

        Assert.Null(result.Value);
        Assert.False(result.Attempted);
        Assert.False(result.FetchFailed);
    }

    [Fact]
    public void ARequestedFirstFetchThatThrowsIsAttemptedAndFailedWithNoPriorDataToRetain()
    {
        var result = LazyLaneFold.Apply<Report>(
            requested: true, fetched: null, previous: null, previousAttempted: false, previousFetchFailed: false);

        Assert.Null(result.Value);
        Assert.True(result.Attempted);
        Assert.True(result.FetchFailed);
    }

    [Fact]
    public void ASuccessAfterAnEarlierFailureClearsFetchFailed()
    {
        var fresh = new Report(3);

        var result = LazyLaneFold.Apply(
            requested: true, fetched: fresh, previous: (Report?)null, previousAttempted: true, previousFetchFailed: true);

        Assert.Equal(fresh, result.Value);
        Assert.True(result.Attempted);
        Assert.False(result.FetchFailed);
    }
}

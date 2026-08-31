using TokenBar.Interop;
using Xunit;

namespace TokenBar.Core.Tests;

// Ported from TokenBarCore/QuotaSummary.swift's fold, covering the defects
// its comments record: identity by (clientId, cardId) rather than label, the
// burn check reaching the tightest window too, pace-checked gating the
// reassurance, and the two distinct othersText shapes.
public class QuotaSummaryTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_750_000_000);

    private static string ResetAt(double seconds) =>
        Now.AddSeconds(seconds).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");

    /// <summary>A window ready for Linear pace (duration + reset in the
    /// future), so a caller can force a deficit/on-track reading without
    /// needing HistoricalPace fixtures.</summary>
    private static UsageWindow PaceWindow(
        string cardId, string label, double remainingPercent,
        double usedPercent, double elapsedFraction = 0.5, double windowSeconds = 3_600)
    {
        var untilReset = windowSeconds * (1 - elapsedFraction);
        var status = new PaceStatus(
            State: UsagePaceState.Available,
            WindowKey: cardId,
            DurationSeconds: (long)windowSeconds,
            DurationSource: UsagePaceDurationSource.Contract,
            CompleteCycles: 5);
        return new UsageWindow(
            Label: label,
            UsedPercent: usedPercent,
            RemainingPercent: remainingPercent,
            ResetsAt: ResetAt(untilReset),
            CardId: cardId,
            PaceStatus: status);
    }

    private static UsageWindow PlainWindow(string cardId, string label, double remainingPercent) =>
        new(label, 100 - remainingPercent, remainingPercent, CardId: cardId);

    // 1. Identity is (clientId, cardId), not the label: two different clients
    // both naming a window "Weekly" must not collapse into one tally entry,
    // and only the tightest's own (clientId, cardId) is excluded from
    // "others" — a same-labelled window on a different client still counts.
    [Fact]
    public void IdentityIsClientAndCardIdNotLabel()
    {
        var payload = new AgentUsagePayload(
            "now",
            new[]
            {
                new AgentUsageSnapshot(
                    "codex", "fixture", "now",
                    new[] { PlainWindow("weekly.v1", "Weekly", remainingPercent: 20) }),
                new AgentUsageSnapshot(
                    "claude", "fixture", "now",
                    new[] { PlainWindow("weekly.v1", "Weekly", remainingPercent: 70) }),
            });

        var summary = QuotaSummaryFold.Build(payload, now: Now);
        Assert.NotNull(summary);
        // "codex" is tightest (20% left). Same cardId+label on "claude" must
        // still count as an "other" window rather than being folded away as
        // if it were the same identity as the tightest.
        Assert.Equal("codex", summary!.TightestClient);
        Assert.Equal(1, summary.OtherWindows);
    }

    // 2. The burn check covers every eligible window including the tightest:
    // when the tightest window is ALSO the fastest-burning one, it must
    // still surface as the BurnWarning rather than being skipped because it
    // was already claimed as "tightest".
    [Fact]
    public void BurnCheckCoversTheTightestWindowToo()
    {
        var payload = new AgentUsagePayload(
            "now",
            new[]
            {
                new AgentUsageSnapshot(
                    "codex", "fixture", "now",
                    new[] { PaceWindow("session.v1", "Session", remainingPercent: 10, usedPercent: 90, elapsedFraction: 0.3) }),
                new AgentUsageSnapshot(
                    "claude", "fixture", "now",
                    new[] { PaceWindow("weekly.v1", "Weekly", remainingPercent: 80, usedPercent: 20, elapsedFraction: 0.5) }),
            });

        var summary = QuotaSummaryFold.Build(payload, paceMode: PaceMode.Linear, now: Now);
        Assert.NotNull(summary);
        Assert.Equal("codex", summary!.TightestClient); // 10% left is tightest
        Assert.NotNull(summary.Burning);
        Assert.Equal("codex", summary.Burning!.ClientId); // and also the burn warning
        Assert.Equal("Session", summary.Burning.Label);
    }

    // 3. paceCheckedWindows gates the reassurance: with pace off, Compute
    // returns null for every window, so Burning is null AND PaceCheckedWindows
    // is 0 — nothing was asked, not "nothing was wrong".
    [Fact]
    public void PaceOffLeavesNothingChecked()
    {
        var payload = new AgentUsagePayload(
            "now",
            new[]
            {
                new AgentUsageSnapshot(
                    "codex", "fixture", "now",
                    new[] { PaceWindow("session.v1", "Session", remainingPercent: 10, usedPercent: 90, elapsedFraction: 0.3) }),
                new AgentUsageSnapshot(
                    "claude", "fixture", "now",
                    new[] { PaceWindow("weekly.v1", "Weekly", remainingPercent: 80, usedPercent: 20) }),
            });

        var summary = QuotaSummaryFold.Build(payload, paceMode: PaceMode.Off, now: Now);
        Assert.NotNull(summary);
        Assert.Null(summary!.Burning);
        Assert.Equal(0, summary.PaceCheckedWindows);
    }

    // 4. PaceCheckedWindows counts only the windows pace could actually be
    // computed for, never the full window count — "every measured window"
    // must stay scoped to what was measured. Mixing one pace-ready window
    // with one that has no duration (LegacyMissing) must report 1, not 2.
    [Fact]
    public void PaceCheckedWindowsCountsOnlyMeasuredWindows()
    {
        var payload = new AgentUsagePayload(
            "now",
            new[]
            {
                new AgentUsageSnapshot(
                    "codex", "fixture", "now",
                    new[] { PaceWindow("session.v1", "Session", remainingPercent: 40, usedPercent: 50) }),
                new AgentUsageSnapshot(
                    "claude", "fixture", "now",
                    new[] { PlainWindow("legacy.v1", "Legacy", remainingPercent: 20) }), // LegacyMissing pace state
            });

        var summary = QuotaSummaryFold.Build(payload, paceMode: PaceMode.Linear, now: Now);
        Assert.NotNull(summary);
        Assert.Equal(1, summary!.PaceCheckedWindows);
    }

    // 5. othersText has two distinct shapes — this asserts the underlying
    // counts a caller renders those two shapes from, so "all comfortable"
    // and "N of M below X%" can never collapse onto each other.
    [Fact]
    public void OthersComfortableCountDistinguishesTheTwoShapes()
    {
        var allComfortable = new AgentUsagePayload(
            "now",
            new[]
            {
                new AgentUsageSnapshot("a", "fixture", "now", new[] { PlainWindow("w.v1", "W", 5) }),
                new AgentUsageSnapshot("b", "fixture", "now", new[] { PlainWindow("w.v1", "W", 90) }),
                new AgentUsageSnapshot("c", "fixture", "now", new[] { PlainWindow("w.v1", "W", 75) }),
            });
        var allSummary = QuotaSummaryFold.Build(allComfortable, now: Now);
        Assert.NotNull(allSummary);
        Assert.True(allSummary!.AllOthersComfortable);
        Assert.Equal(2, allSummary.OtherWindows);
        Assert.Equal(2, allSummary.OthersComfortable);

        var mixed = new AgentUsagePayload(
            "now",
            new[]
            {
                new AgentUsageSnapshot("a", "fixture", "now", new[] { PlainWindow("w.v1", "W", 5) }),
                new AgentUsageSnapshot("b", "fixture", "now", new[] { PlainWindow("w.v1", "W", 90) }),
                new AgentUsageSnapshot("c", "fixture", "now", new[] { PlainWindow("w.v1", "W", 30) }),
            });
        var mixedSummary = QuotaSummaryFold.Build(mixed, now: Now);
        Assert.NotNull(mixedSummary);
        Assert.False(mixedSummary!.AllOthersComfortable);
        Assert.Equal(2, mixedSummary.OtherWindows);
        Assert.Equal(1, mixedSummary.OthersComfortable); // only the 90% window
    }

    [Fact]
    public void NullPayloadReturnsNull() =>
        Assert.Null(QuotaSummaryFold.Build(null, now: Now));

    // 7 (Core half). No payload and a payload with no eligible window both
    // fold to null from Build — the Ready/NoWindowReporting/Loading
    // distinction is a caller-side concern (QuotaSummaryText.State), covered
    // by QuotaSummaryTextTests, but Build itself must not fabricate a
    // summary out of an all-errored payload.
    [Fact]
    public void NoEligibleWindowReturnsNull()
    {
        var payload = new AgentUsagePayload(
            "now",
            new[]
            {
                new AgentUsageSnapshot(
                    "codex", "fixture", "now",
                    new[] { PlainWindow("session.v1", "Session", remainingPercent: 1) },
                    Error: "401"),
            });

        Assert.Null(QuotaSummaryFold.Build(payload, now: Now));
    }

    [Fact]
    public void ExcludingRemovesTheClientFromAutoAndFromOthers()
    {
        var payload = new AgentUsagePayload(
            "now",
            new[]
            {
                new AgentUsageSnapshot("codex", "fixture", "now", new[] { PlainWindow("w.v1", "W", 10) }),
                new AgentUsageSnapshot("claude", "fixture", "now", new[] { PlainWindow("w.v1", "W", 80) }),
            });

        var summary = QuotaSummaryFold.Build(payload, excluding: new HashSet<string> { "codex" }, now: Now);
        Assert.NotNull(summary);
        Assert.Equal("claude", summary!.TightestClient);
        Assert.Equal(0, summary.OtherWindows); // codex excluded entirely, not counted as "other"
    }
}

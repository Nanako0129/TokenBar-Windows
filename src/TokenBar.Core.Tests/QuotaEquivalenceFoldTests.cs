using TokenBar.Core;
using TokenBar.Interop;
using Xunit;

namespace TokenBar.Core.Tests;

// QuotaEquivalenceFold: the message-to-window join 5d-2 needed and no macOS
// file ports (see the class doc comment). Two things are pinned here that
// nothing else in this slice asserts: the observed-span restriction
// (QuotaCycle.FirstSampleMs/LastSampleMs, not the whole window), and that a
// message counts toward a window only when its attributed target matches the
// window's own QuotaHistorySeries.ProviderId.
public class QuotaEquivalenceFoldTests
{
    private static WindowMessage Message(
        long timestampMs, string client, string provider, long tokens, double cost) =>
        new(
            Timestamp: timestampMs,
            Client: client,
            ProviderId: provider,
            ModelId: "some-model",
            Input: tokens,
            Output: 0,
            CacheRead: 0,
            CacheWrite: 0,
            Reasoning: 0,
            Cost: cost,
            IsTurnStart: true);

    private static QuotaCycle Cycle(long firstMs, long lastMs, double usedPercent, double observedFraction = 1.0) =>
        new(
            ResetAtMs: lastMs + 1000,
            StartMs: firstMs - 1000,
            UsedPercent: usedPercent,
            PeakUsedPercent: usedPercent,
            SampleCount: 2,
            ObservedFraction: observedFraction,
            FirstSampleMs: firstMs,
            LastSampleMs: lastMs);

    // Only a message whose resolved target equals the window's own
    // (misnamed) ProviderId counts. A message logged by a different client
    // to the same underlying provider, but attributed to a DIFFERENT
    // subscription, must not leak into this window's span.
    [Fact]
    public void OnlyMessagesAttributedToThisWindowsOwnerCount()
    {
        var confirmed = new List<UsageAttribution.Record>
        {
            new("claude-code", "anthropic", UsageAttribution.State.Assigned("claude")),
            new("cursor", "anthropic", UsageAttribution.State.Assigned("cursor")),
        };
        var cycles = new[] { Cycle(1000, 2000, 40) };
        var messages = new[]
        {
            Message(1500, "claude-code", "anthropic", 1000, 5.0), // -> claude, matches
            Message(1500, "cursor", "anthropic", 999, 99.0), // -> cursor, does not match
        };

        var spans = QuotaEquivalenceFold.Cycles(cycles, "claude", messages, confirmed);

        var span = Assert.Single(spans);
        Assert.Equal(1000, span.SpanTokens);
        Assert.Equal(5.0, span.SpanCost);
    }

    // The observed-span restriction: a message inside the cycle's window but
    // outside its own FirstSampleMs..LastSampleMs must not count, because
    // that span is "the only interval the delta describes"
    // (WindowEquivalence.Cycle's own field comment).
    [Fact]
    public void MessagesOutsideTheCyclesOwnSampleSpanAreExcluded()
    {
        var confirmed = new List<UsageAttribution.Record>
        {
            new("claude-code", "anthropic", UsageAttribution.State.Assigned("claude")),
        };
        var cycles = new[] { Cycle(1000, 2000, 40) };
        var messages = new[]
        {
            Message(999, "claude-code", "anthropic", 1000, 5.0), // before first: excluded
            Message(1000, "claude-code", "anthropic", 1000, 5.0), // at first: excluded (not >)
            Message(1500, "claude-code", "anthropic", 1000, 5.0), // inside: included
            Message(2000, "claude-code", "anthropic", 1000, 5.0), // at last: included (<=)
            Message(2001, "claude-code", "anthropic", 1000, 5.0), // after last: excluded
        };

        var spans = QuotaEquivalenceFold.Cycles(cycles, "claude", messages, confirmed);

        var span = Assert.Single(spans);
        Assert.Equal(2000, span.SpanTokens); // two included messages, 1000 each
        Assert.Equal(10.0, span.SpanCost);
    }

    // An unassigned or excluded row contributes nothing, matching Resolve's
    // own contract — never counted as this window's evidence.
    [Fact]
    public void UnassignedAndExcludedMessagesContributeNothing()
    {
        var confirmed = new List<UsageAttribution.Record>
        {
            new("cursor", "anthropic", UsageAttribution.State.Excluded),
        };
        var cycles = new[] { Cycle(1000, 2000, 40) };
        var messages = new[]
        {
            Message(1500, "cursor", "anthropic", 1000, 5.0), // excluded
            Message(1500, "unknown-client", "anthropic", 1000, 5.0), // unassigned
        };

        var spans = QuotaEquivalenceFold.Cycles(cycles, "claude", messages, confirmed);

        var span = Assert.Single(spans);
        Assert.Equal(0, span.SpanTokens);
        Assert.Equal(0.0, span.SpanCost);
    }

    // End to end: Build folds the per-window span cycles into an
    // Aggregate row, keyed the same way the strip/heatmap cards key their
    // own data.
    [Fact]
    public void BuildKeysByTheStoresOwnWindowIdentity()
    {
        var confirmed = UsageAttribution.Table.Empty with
        {
            Records = [new UsageAttribution.Record("claude-code", "anthropic", UsageAttribution.State.Assigned("claude"))],
        };
        var samples = new[]
        {
            new QuotaHistorySample(
                ResetAt: 2, DurationSeconds: 2, DurationSource: QuotaHistoryDurationSource.Provider,
                UsedPercent: 10, SampledAt: 0, Origin: QuotaHistorySampleOrigin.LiveV3, IsActiveGroup: false),
            new QuotaHistorySample(
                ResetAt: 2, DurationSeconds: 2, DurationSource: QuotaHistoryDurationSource.Provider,
                UsedPercent: 40, SampledAt: 2, Origin: QuotaHistorySampleOrigin.LiveV3, IsActiveGroup: false),
        };
        var history = new[] { new QuotaHistorySeries("claude", "acct", "session.v1", samples) };
        var messages = new[] { Message(1000, "claude-code", "anthropic", 1000, 5.0) };

        var result = QuotaEquivalenceFold.Build(history, messages, confirmed);

        var id = new QuotaWindowIdentity("claude", "acct", "session.v1");
        Assert.True(result.ContainsKey(id));
    }

    [Fact]
    public void BoundFromMsFallsBackWhenNoCycleExists()
    {
        var bound = QuotaEquivalenceFold.BoundFromMs([], fallbackMs: 12345);
        Assert.Equal(12345, bound);
    }

    [Fact]
    public void BoundFromMsUsesTheEarliestEvidenceStartAcrossAllWindows()
    {
        var earlySamples = new[]
        {
            new QuotaHistorySample(
                ResetAt: 100, DurationSeconds: 50, DurationSource: QuotaHistoryDurationSource.Provider,
                UsedPercent: 10, SampledAt: 60, Origin: QuotaHistorySampleOrigin.LiveV3, IsActiveGroup: false),
            new QuotaHistorySample(
                ResetAt: 100, DurationSeconds: 50, DurationSource: QuotaHistoryDurationSource.Provider,
                UsedPercent: 40, SampledAt: 90, Origin: QuotaHistorySampleOrigin.LiveV3, IsActiveGroup: false),
        };
        var lateSamples = new[]
        {
            new QuotaHistorySample(
                ResetAt: 200, DurationSeconds: 50, DurationSource: QuotaHistoryDurationSource.Provider,
                UsedPercent: 10, SampledAt: 160, Origin: QuotaHistorySampleOrigin.LiveV3, IsActiveGroup: false),
            new QuotaHistorySample(
                ResetAt: 200, DurationSeconds: 50, DurationSource: QuotaHistoryDurationSource.Provider,
                UsedPercent: 40, SampledAt: 190, Origin: QuotaHistorySampleOrigin.LiveV3, IsActiveGroup: false),
        };
        var history = new[]
        {
            new QuotaHistorySeries("claude", "acct", "session.v1", earlySamples),
            new QuotaHistorySeries("codex", "acct", "weekly.v1", lateSamples),
        };

        var bound = QuotaEquivalenceFold.BoundFromMs(history, fallbackMs: long.MaxValue);

        // The earlier series' EvidenceStartMs (min(StartMs, FirstSampleMs)):
        // StartMs = (100 - 50) * 1000 = 50_000, FirstSampleMs = 60_000.
        Assert.Equal(50_000, bound);
    }

    // A series holding only its running cycle has no Cycles/Considered entries
    // at all (QuotaHistoryFold.Cycles excludes IsActiveGroup samples), so
    // without also consulting Active the bound falls back to "now" and the
    // scanned window collapses to [now, now) — a fresh install, or any window
    // whose first cycle has not completed, shows no usage.
    [Fact]
    public void BoundFromMsUsesThePlacedActiveCyclesStartWhenNoCycleHasCompleted()
    {
        var samples = new[]
        {
            new QuotaHistorySample(
                ResetAt: 200, DurationSeconds: 50, DurationSource: QuotaHistoryDurationSource.Provider,
                UsedPercent: 10, SampledAt: 160, Origin: QuotaHistorySampleOrigin.LiveV3, IsActiveGroup: true),
            new QuotaHistorySample(
                ResetAt: 200, DurationSeconds: 50, DurationSource: QuotaHistoryDurationSource.Provider,
                UsedPercent: 40, SampledAt: 190, Origin: QuotaHistorySampleOrigin.LiveV3, IsActiveGroup: true),
        };
        var history = new[] { new QuotaHistorySeries("claude", "acct", "session.v1", samples) };

        var bound = QuotaEquivalenceFold.BoundFromMs(history, fallbackMs: long.MaxValue);

        // StartMs = (200 - 50) * 1000 = 150_000, FirstSampleMs = 160_000 ->
        // min is the cycle's own start, not the earliest reading.
        Assert.Equal(150_000, bound);
    }

    // An active cycle whose newest sample reports no usable duration cannot be
    // placed on an axis (QuotaActiveCycle.IsPlaced false) and must not invent a
    // bound — falling back to fallbackMs, same as no cycle at all.
    [Fact]
    public void BoundFromMsIgnoresAnUnplacedActiveCycle()
    {
        var samples = new[]
        {
            new QuotaHistorySample(
                ResetAt: 0, DurationSeconds: 0, DurationSource: QuotaHistoryDurationSource.Provider,
                UsedPercent: 10, SampledAt: 160, Origin: QuotaHistorySampleOrigin.LiveV3, IsActiveGroup: true),
        };
        var history = new[] { new QuotaHistorySeries("claude", "acct", "session.v1", samples) };

        var bound = QuotaEquivalenceFold.BoundFromMs(history, fallbackMs: 12345);

        Assert.Equal(12345, bound);
    }
}

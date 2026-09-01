using System.Text.Json;
using TokenBar.Interop;
using Xunit;

namespace TokenBar.Core.Tests;

// Expectations transcribed from TokenBarCore/QuotaHistory.swift and
// QuotaCurve.swift, not invented.
public class QuotaHistoryFoldTests
{
    private const long FiveHours = 5 * 3_600;
    private const long ResetAt = 1_767_330_000;

    private static QuotaHistorySample Sample(
        long resetAt, long sampledAt, double usedPercent, bool isActiveGroup = false) =>
        new(
            ResetAt: resetAt,
            DurationSeconds: FiveHours,
            DurationSource: QuotaHistoryDurationSource.Provider,
            UsedPercent: usedPercent,
            SampledAt: sampledAt,
            Origin: QuotaHistorySampleOrigin.LiveV3,
            IsActiveGroup: isActiveGroup);

    // "a cycle first seen at 40% and last seen at 100% consumed 60 points as far
    // as this machine can tell, and reached the ceiling. Deriving 'never ran out'
    // from the span called that cycle a quiet one."
    [Fact]
    public void SpanAndPeakAreNotInterchangeable()
    {
        var cycle = Assert.Single(QuotaHistoryFold.Cycles(
        [
            Sample(ResetAt, ResetAt - FiveHours + 600, 40),
            Sample(ResetAt, ResetAt - FiveHours + 3_600, 70),
            Sample(ResetAt, ResetAt - 600, 100),
        ]));

        Assert.Equal(60, cycle.UsedPercent);
        Assert.Equal(100, cycle.PeakUsedPercent);
        Assert.Equal(3, cycle.SampleCount);
        Assert.Equal(ResetAt * 1000, cycle.ResetAtMs);
        Assert.Equal((ResetAt - FiveHours) * 1000, cycle.StartMs);
        Assert.Equal((ResetAt - FiveHours + 600) * 1000, cycle.FirstSampleMs);
        Assert.Equal((ResetAt - 600) * 1000, cycle.LastSampleMs);
        Assert.Equal((double)(FiveHours - 1_200) / FiveHours, cycle.ObservedFraction, 9);
    }

    // "The running cycle is excluded, and each point says for itself whether it
    // is in it."
    [Fact]
    public void TheRunningCycleIsNotACycle()
    {
        var running = ResetAt + FiveHours;

        var cycles = QuotaHistoryFold.Cycles(
        [
            Sample(ResetAt, ResetAt - FiveHours + 600, 10),
            Sample(ResetAt, ResetAt - 600, 80),
            Sample(running, running - FiveHours + 600, 5, isActiveGroup: true),
            Sample(running, running - 600, 30, isActiveGroup: true),
        ]);

        var only = Assert.Single(cycles);
        Assert.Equal(ResetAt * 1000, only.ResetAtMs);
    }

    // "Groups curve points into cycles, newest first."
    [Fact]
    public void CyclesComeBackNewestFirst()
    {
        var older = ResetAt - FiveHours;

        var cycles = QuotaHistoryFold.Cycles(
        [
            Sample(older, older - FiveHours + 600, 20),
            Sample(older, older - 600, 25),
            Sample(ResetAt, ResetAt - FiveHours + 600, 10),
            Sample(ResetAt, ResetAt - 600, 80),
        ]);

        Assert.Equal(new[] { ResetAt * 1000, older * 1000 }, cycles.Select(cycle => cycle.ResetAtMs).ToArray());
    }

    // "Rust always emits it, so an absent key is ABI drift. Defaulting it to
    // false would render every point as finished history — the exact misreading
    // this field exists to remove, arriving silently instead of as a decode
    // failure."
    [Fact]
    public void APayloadMissingIsActiveGroupFailsToDecode()
    {
        Assert.Throws<JsonException>(() => TbCore.DecodeEnvelope<IReadOnlyList<QuotaHistorySeries>>(
            """
            {"ok":true,"data":[{"providerId":"codex","accountScope":"scope","windowKey":"weekly.v1",
              "samples":[{"resetAt":1767330000,"durationSeconds":18000,"durationSource":"provider",
                          "usedPercent":40.0,"sampledAt":1767320000,"origin":"liveV3"}]}]}
            """));
    }

    [Fact]
    public void TheFullPayloadDecodesThroughTheEnvelope()
    {
        var series = Assert.Single(TbCore.DecodeEnvelope<IReadOnlyList<QuotaHistorySeries>>(
            """
            {"ok":true,"data":[{"providerId":"codex","accountScope":"scope","windowKey":"weekly.v1",
              "samples":[{"resetAt":1767330000,"durationSeconds":18000,"durationSource":"observed",
                          "usedPercent":40.0,"sampledAt":1767320000,"origin":"importedV2",
                          "isActiveGroup":true}]}]}
            """));

        Assert.Equal("codex", series.ProviderId);
        Assert.Equal("scope", series.AccountScope);
        Assert.Equal("weekly.v1", series.WindowKey);
        var sample = Assert.Single(series.Samples);
        Assert.Equal(QuotaHistoryDurationSource.Observed, sample.DurationSource);
        Assert.Equal(QuotaHistorySampleOrigin.ImportedV2, sample.Origin);
        Assert.True(sample.IsActiveGroup);
    }

    // ---- the running cycle (5e) ----------------------------------------

    // The same IsActiveGroup bit, read the other way round: what Cycles leaves
    // out is exactly what the Session-window card draws.
    [Fact]
    public void ActiveIsTheCycleCyclesExcludes()
    {
        QuotaHistorySample[] samples =
        [
            Sample(ResetAt - FiveHours, ResetAt - FiveHours - 600, 55),
            Sample(ResetAt, ResetAt - 3_600, 20, isActiveGroup: true),
            Sample(ResetAt, ResetAt - 600, 40, isActiveGroup: true),
        ];

        Assert.Single(QuotaHistoryFold.Cycles(samples));
        var active = QuotaHistoryFold.Active(samples);

        Assert.NotNull(active);
        Assert.True(active!.IsPlaced);
        Assert.Equal(ResetAt * 1000, active.ResetAtMs);
        Assert.Equal((ResetAt - FiveHours) * 1000, active.StartMs);
        // Oldest first, in ms, so the geometry can walk them straight through.
        Assert.Equal([20d, 40d], active.Samples.Select(sample => sample.UsedPercent));
        Assert.Equal((ResetAt - 3_600) * 1000, active.Samples[0].AtMs);
    }

    [Fact]
    public void NoActiveSampleMeansNothingIsRunning() =>
        Assert.Null(QuotaHistoryFold.Active([Sample(ResetAt, ResetAt - 600, 40)]));

    // The third outcome, and the one a nullable cycle alone would have merged
    // into "nothing is running": a window IS running and the provider reported
    // no usable duration to place it with.
    [Fact]
    public void ARunningWindowWithNoUsableDurationKeepsItsReadingsAndLosesItsPlacement()
    {
        var active = QuotaHistoryFold.Active(
        [
            Sample(ResetAt, ResetAt - 600, 40, isActiveGroup: true) with
            {
                DurationSeconds = 0,
            },
        ]);

        Assert.NotNull(active);
        Assert.False(active!.IsPlaced);
        Assert.Null(active.StartMs);
        Assert.Null(active.ResetAtMs);
        Assert.Single(active.Samples);
    }
}

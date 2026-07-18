using System.Globalization;
using TokenBar.Core;
using TokenBar.Interop;
using Xunit;

namespace TokenBar.Core.Tests;

// Ported from SelfTest.swift's UsagePace block. Fixtures use the typed v3
// paceStatus contract; WindowMinutes is only the compatibility mirror.
public class UsagePaceTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_750_000_000);

    private static UsageWindow Window(
        double used,
        UsagePaceState state = UsagePaceState.LearningHistory,
        long? durationSeconds = 3_600,
        double untilReset = 1_800,
        HistoricalPace? historicalPace = null,
        string? resetsAt = null,
        long? windowMinutes = null)
    {
        var duration = state is UsagePaceState.LearningHistory or UsagePaceState.Available
            ? durationSeconds : null;
        UsagePaceDurationSource? durationSource = duration is not null
            ? UsagePaceDurationSource.Contract
            : state == UsagePaceState.LearningDuration
                ? UsagePaceDurationSource.Observed : null;
        var status = new PaceStatus(
            State: state,
            WindowKey: state == UsagePaceState.LegacyMissing ? null : "session.v3",
            DurationSeconds: duration,
            DurationSource: durationSource,
            CompleteCycles: state == UsagePaceState.Available ? 5 : 0,
            Reason: state == UsagePaceState.Unavailable
                ? UsagePaceUnavailableReason.NonRecurring : null);
        var compatibilityMinutes = windowMinutes ??
            (duration is { } exactDuration
                ? exactDuration / 60
                : state == UsagePaceState.LegacyMissing ? 60 : null);

        return new UsageWindow(
            Label: "Session",
            UsedPercent: used,
            RemainingPercent: 100 - used,
            ResetsAt: resetsAt ?? ResetAt(untilReset),
            ResetText: null,
            WindowMinutes: compatibilityMinutes,
            CardId: state == UsagePaceState.LegacyMissing ? null : "session.v3",
            PaceStatus: status,
            HistoricalPace: historicalPace,
            DurationSeconds: duration);
    }

    private static string ResetAt(double seconds) =>
        Now.AddSeconds(seconds).UtcDateTime.ToString(
            "yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    [Fact]
    public void OnTrackAtHalfway()
    {
        var pace = UsagePace.Compute(Window(used: 50), Now);
        Assert.NotNull(pace);
        Assert.Equal(PaceStage.OnTrack, pace.Stage);
        Assert.Equal(UsagePaceBasis.Linear, pace.Basis);
        Assert.Equal("On pace", pace.Label);
    }

    [Fact]
    public void FarAheadLabelAndEta()
    {
        var pace = UsagePace.Compute(Window(used: 80), Now);
        Assert.NotNull(pace);
        Assert.Equal(PaceStage.FarAhead, pace.Stage);
        Assert.Equal("30% in deficit", pace.Label);
        // 80% in 30min → 100% in 37.5min, before the 30min reset → ETA 7.5min.
        Assert.False(pace.WillLastToReset);
        Assert.True(Math.Abs(pace.EtaSeconds!.Value - 450) < 1);
        Assert.Equal("Projected empty in 8m", pace.EtaText);
    }

    [Fact]
    public void BehindPaceIsReserveAndLasts()
    {
        var pace = UsagePace.Compute(Window(used: 40), Now);
        Assert.NotNull(pace);
        Assert.Equal(PaceStage.Behind, pace.Stage);
        Assert.Equal("10% in reserve", pace.Label);
        Assert.True(pace.WillLastToReset);
        Assert.Equal("Lasts until reset", pace.EtaText);
    }

    [Fact]
    public void HistoricalAvailablePreservesBackendProjection()
    {
        var historical = new HistoricalPace(
            ExpectedUsedPercent: 50,
            EtaSeconds: 120,
            WillLastToReset: false,
            RunOutProbability: 0.8);
        var pace = UsagePace.Compute(
            Window(used: 90, state: UsagePaceState.Available, historicalPace: historical),
            PaceMode.Historical, Now);

        Assert.NotNull(pace);
        Assert.Equal(UsagePaceBasis.Historical, pace.Basis);
        Assert.Equal(50, pace.ExpectedUsedPercent);
        Assert.Equal(120, pace.EtaSeconds);
        Assert.False(pace.WillLastToReset);
        Assert.True(pace.IsHistoricalDeficit);
        Assert.Equal(PaceStage.FarAhead, pace.Stage);
    }

    [Fact]
    public void HistoricalLearningHistoryFallsBackToLinear()
    {
        var pace = UsagePace.Compute(
            Window(used: 80), PaceMode.Historical, Now);

        Assert.NotNull(pace);
        Assert.Equal(UsagePaceBasis.Linear, pace.Basis);
        Assert.Equal(50, pace.ExpectedUsedPercent);
        Assert.True(pace.Stage.IsDeficit());
        Assert.False(pace.IsHistoricalDeficit);
    }

    [Fact]
    public void HistoricalOnlyDeficitUsesHistoricalBasis()
    {
        var pace = UsagePace.Compute(
            Window(
                used: 90,
                state: UsagePaceState.Available,
                historicalPace: new HistoricalPace(50, 120, false)),
            PaceMode.Historical, Now);

        Assert.NotNull(pace);
        Assert.True(pace.IsHistoricalDeficit);
    }

    [Theory]
    [InlineData(UsagePaceState.LearningDuration)]
    [InlineData(UsagePaceState.Unavailable)]
    [InlineData(UsagePaceState.LegacyMissing)]
    public void HistoricalModeSuppressesNonReadyStates(UsagePaceState state)
    {
        var window = Window(used: 50, state: state);
        Assert.Null(UsagePace.Compute(window, PaceMode.Historical, Now));
        Assert.Null(UsagePace.Compute(window, PaceMode.Linear, Now));
        Assert.Null(UsagePace.Compute(window, Now));
    }

    [Fact]
    public void HistoricalAvailableRequiresHistoricalPace()
    {
        var window = Window(used: 50, state: UsagePaceState.Available);
        Assert.Null(UsagePace.Compute(window, PaceMode.Historical, Now));
    }

    [Fact]
    public void LinearModeIgnoresHistoricalProjection()
    {
        var window = Window(
            used: 50,
            state: UsagePaceState.Available,
            historicalPace: new HistoricalPace(80, 120, false, 0.8));
        var pace = UsagePace.Compute(window, PaceMode.Linear, Now);

        Assert.NotNull(pace);
        Assert.Equal(UsagePaceBasis.Linear, pace.Basis);
        Assert.Equal(50, pace.ExpectedUsedPercent);
        Assert.Null(UsagePace.RunOutRiskLabel(window, pace));
        Assert.Null(UsagePace.Presentation(window, PaceMode.Linear, pace).RiskText);
    }

    [Fact]
    public void LinearModeRequiresDurationReadyState()
    {
        var historical = new HistoricalPace(80, 120, false, 0.8);
        Assert.Null(UsagePace.Compute(
            Window(50, UsagePaceState.LearningDuration, historicalPace: null),
            PaceMode.Linear, Now));
        Assert.Null(UsagePace.Compute(
            Window(50, UsagePaceState.Available, durationSeconds: null, historicalPace: historical),
            PaceMode.Linear, Now));
    }

    [Fact]
    public void PresentationSuppressesLastsWhenRiskIsVisible()
    {
        var window = Window(
            used: 50,
            state: UsagePaceState.Available,
            historicalPace: new HistoricalPace(
                ExpectedUsedPercent: 80,
                EtaSeconds: null,
                WillLastToReset: true,
                RunOutProbability: 0.2));
        var pace = UsagePace.Compute(window, PaceMode.Historical, Now);
        Assert.NotNull(pace);
        Assert.False(pace.IsHistoricalDeficit);
        Assert.Equal("Lasts until reset", pace.EtaText);

        var presentation = UsagePace.Presentation(window, PaceMode.Historical, pace);
        Assert.Null(presentation.EtaText);
        Assert.Equal("≈ 20% run-out risk", presentation.RiskText);
    }

    [Fact]
    public void RunOutRiskLabelFormatsHistoricalAvailableOnly()
    {
        var window = Window(
            used: 50,
            state: UsagePaceState.Available,
            historicalPace: new HistoricalPace(50, null, true, 0.3));
        Assert.Equal("≈ 30% run-out risk", UsagePace.RunOutRiskLabel(window));
        Assert.Null(UsagePace.RunOutRiskLabel(window, UsagePace.Compute(window, PaceMode.Linear, Now)));
        Assert.Null(UsagePace.RunOutRiskLabel(
            Window(50, state: UsagePaceState.LearningHistory), null));
        Assert.Null(UsagePace.RunOutRiskLabel(
            Window(50, state: UsagePaceState.Available,
                historicalPace: new HistoricalPace(50, null, true, 0)), null));
        Assert.Null(UsagePace.RunOutRiskLabel(
            Window(50, state: UsagePaceState.Available), null));
    }

    [Fact]
    public void HistoricalExhaustedEtaUsesBackendZero()
    {
        var window = Window(
            used: 100,
            state: UsagePaceState.Available,
            historicalPace: new HistoricalPace(80, 0, false, 1));
        var pace = UsagePace.Compute(window, PaceMode.Historical, Now);

        Assert.NotNull(pace);
        Assert.Equal(0, pace.EtaSeconds);
        Assert.False(pace.WillLastToReset);
        Assert.Equal("Projected empty now", pace.EtaText);
        Assert.Equal("≈ 100% run-out risk", UsagePace.RunOutRiskLabel(window));
    }

    [Fact]
    public void NoWindowLengthNoPace() =>
        Assert.Null(UsagePace.Compute(Window(used: 50, durationSeconds: null), Now));

    [Fact]
    public void PastResetNoPace() =>
        Assert.Null(UsagePace.Compute(Window(used: 50, untilReset: -10), Now));

    [Fact]
    public void ResetBeyondDurationNoPace() =>
        Assert.Null(UsagePace.Compute(Window(used: 50, untilReset: 3_601), Now));

    [Fact]
    public void ElapsedZeroWithUsageNoPace() =>
        Assert.Null(UsagePace.Compute(Window(used: 50, untilReset: 3_600), Now));

    [Fact]
    public void Rfc3339RequiresExplicitZoneAndFullConsumption()
    {
        var timestamp = Now.AddSeconds(1_800).UtcDateTime.ToString(
            "yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);
        string[] invalid =
        [
            timestamp,
            timestamp.Replace('T', 't') + "Z",
            timestamp + "+0000",
            timestamp + "Zjunk",
        ];

        foreach (var resetsAt in invalid)
        {
            Assert.Null(UsagePace.Compute(Window(used: 50, resetsAt: resetsAt), Now));
        }
    }

    [Fact]
    public void ExactDurationUsesPaceStatusSeconds()
    {
        // A non-minute duration proves timing uses exact v3 seconds rather than
        // the compatibility windowMinutes field.
        var pace = UsagePace.Compute(
            Window(used: 50, durationSeconds: 3_601), Now);
        var expected = (3_601d - 1_800) / 3_601 * 100;

        Assert.NotNull(pace);
        Assert.Equal(expected, pace.ExpectedUsedPercent);
        Assert.NotEqual(50, pace.ExpectedUsedPercent);
    }

    [Fact]
    public void ModeOffIsNull() =>
        Assert.Null(UsagePace.Compute(Window(used: 50), PaceMode.Off, Now));

    [Theory]
    [InlineData(130 * 60, "2h 10m")]
    [InlineData(26 * 3600, "1d 2h")]
    [InlineData(20, "now")]
    [InlineData(30, "1m")] // Swift .rounded() is away-from-zero: 0.5 min → 1m
    [InlineData(5 * 60, "5m")]
    public void DurationTextBands(double seconds, string expected) =>
        Assert.Equal(expected, UsagePace.DurationText(seconds));

    [Fact]
    public void CanonicalRfc3339VariantsParse()
    {
        var reset = Now.AddSeconds(1_800);
        var timestamp = reset.UtcDateTime.ToString(
            "yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);
        string[] valid =
        [
            timestamp + "Z",
            timestamp + "z",
            timestamp + ".123Z",
            timestamp + ".12345678Z",
            reset.ToOffset(TimeSpan.FromHours(8)).ToString(
                "yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture),
        ];

        foreach (var resetsAt in valid)
        {
            Assert.NotNull(UsagePace.Compute(Window(used: 50, resetsAt: resetsAt), Now));
        }
    }

    [Fact]
    public void LinearTimingMatchesFoundationUnixDoublePrecision()
    {
        var now = DateTimeOffset.Parse(
            "2026-07-10T12:00:00.123Z",
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind);
        var pace = UsagePace.Compute(
            Window(
                used: 40,
                durationSeconds: 18_000,
                resetsAt: "2026-07-10T15:00:00Z",
                windowMinutes: 300),
            PaceMode.Historical,
            now);

        Assert.NotNull(pace);
        Assert.Equal(40.00068333281411, pace.ExpectedUsedPercent);
        Assert.Equal(-0.0006833328141127026, pace.DeltaPercent);
    }

    [Fact]
    public void ResetFractionUsesFoundationMillisecondPrecision()
    {
        var now = DateTimeOffset.Parse(
            "2026-07-10T12:00:00Z",
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind);
        UsagePace Compute(string resetsAt) => UsagePace.Compute(
            Window(
                used: 40,
                durationSeconds: 18_000,
                resetsAt: resetsAt,
                windowMinutes: 300),
            PaceMode.Linear,
            now)!;

        var milliseconds = Compute("2026-07-10T15:00:00.123Z");
        var higherPrecision = Compute("2026-07-10T15:00:00.12345678Z");

        Assert.Equal(milliseconds.ExpectedUsedPercent, higherPrecision.ExpectedUsedPercent);
        Assert.Equal(milliseconds.DeltaPercent, higherPrecision.DeltaPercent);
    }
}

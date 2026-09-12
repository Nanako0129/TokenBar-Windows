using System.Text.Json;
using TokenBar.Interop;
using Xunit;

namespace TokenBar.Core.Tests;

// Issue #286 — Codex Spark's 5-hour and weekly windows carry the same label.
// Ported from TokenBarCore/SelfTest.swift's Codex Spark block (the fixture is
// the macOS oracle's own fixture, not derived from this port).
public class AgentUsageQualifierTests
{
    public AgentUsageQualifierTests() => Localization.Load("en", AppContext.BaseDirectory);

    private static AgentUsagePayload Decode(string json) =>
        JsonSerializer.Deserialize<AgentUsagePayload>(
            json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

    // The exact macOS SelfTest.swift fixture (lines 5056-5074): two Spark
    // windows the engine names identically, beside an ordinary "Weekly" window
    // that happens to share the secondary's period. Pins collision-not-period
    // (the "Weekly" entry is untouched despite sharing a period with the
    // qualified secondary), the engine's own vocabulary, and that no identity
    // moves.
    [Fact]
    public void SparkWindowLabelsMatchMacOsSelfTestFixture()
    {
        var payload = Decode(
            """
            {"generatedAt":"now","agents":[
              {"clientId":"codex","source":"fixture","updatedAt":"now",
               "windows":[
                 {"cardId":"additional.deadbeef.primary.v1","label":"Codex Spark",
                  "usedPercent":0,"remainingPercent":100,"windowMinutes":300,
                  "resetsAt":"2030-07-09T00:00:00Z",
                  "paceStatus":{"state":"learningHistory","windowKey":"additional.deadbeef.primary.v1",
                  "durationSeconds":18000,"durationSource":"provider","completeCycles":3}},
                 {"cardId":"additional.deadbeef.secondary.v1","label":"Codex Spark",
                  "usedPercent":0,"remainingPercent":100,"windowMinutes":10080,
                  "resetsAt":"2030-07-14T00:00:00Z",
                  "paceStatus":{"state":"learningHistory","windowKey":"additional.deadbeef.secondary.v1",
                  "durationSeconds":604800,"durationSource":"provider","completeCycles":2}},
                 {"cardId":"weekly.v1","label":"Weekly","usedPercent":40,"remainingPercent":60,
                  "windowMinutes":10080,"resetsAt":"2030-07-14T00:00:00Z",
                  "paceStatus":{"state":"learningHistory","windowKey":"weekly.v1",
                  "durationSeconds":604800,"durationSource":"contract","completeCycles":5}}
               ]}
            ]}
            """);

        var agent = payload.Agents[0];
        var windows = agent.UniqueCardWindows;

        Assert.Equal(
            new[] { "Codex Spark · Session", "Codex Spark · Weekly", "Weekly" },
            windows.Select(w => w.Label));

        // Qualifying a label changes no identity and no wire value.
        Assert.Equal(
            new[] {
                "additional.deadbeef.primary.v1", "additional.deadbeef.secondary.v1", "weekly.v1",
            },
            windows.Select(w => w.CardId));
        Assert.Equal(
            new[] {
                "additional.deadbeef.primary.v1", "additional.deadbeef.secondary.v1", "weekly.v1",
            },
            windows.Select(w => w.PaceStatus.WindowKey));
        Assert.Equal(
            new[] { "Codex Spark", "Codex Spark", "Weekly" }, agent.Windows.Select(w => w.Label));

        // Each qualified option still resolves to its own window.
        Assert.Equal(
            604_800,
            QuotaResolver.Resolve(payload, "codex|additional.deadbeef.secondary.v1")!
                .Window.DurationSeconds);
    }

    // Only one of two same-labelled windows has duration evidence — the
    // sibling is still learning its own (learningDuration) and carries no
    // resetsAt either, so no tier names both and qualification falls to
    // ordinals. This is the ordering the migration protection exists for:
    // RawCardWindows (read by QuotaResolver) must NOT see this qualification,
    // because a persisted "codex|Codex Spark" selection that matched both
    // windows before must not silently migrate to whichever one qualification
    // happened to leave nameable.
    [Fact]
    public void MixedDurationAvailabilityFallsToOrdinalAndProtectsMigration()
    {
        var payload = Decode(
            """
            {"generatedAt":"now","agents":[
              {"clientId":"codex","source":"fixture","updatedAt":"now",
               "windows":[
                 {"cardId":"additional.deadbeef.primary.v1","label":"Codex Spark",
                  "usedPercent":0,"remainingPercent":100,"windowMinutes":300,
                  "resetsAt":"2030-07-09T00:00:00Z",
                  "paceStatus":{"state":"learningHistory","windowKey":"additional.deadbeef.primary.v1",
                  "durationSeconds":18000,"durationSource":"provider","completeCycles":3}},
                 {"cardId":"additional.deadbeef.secondary.v1","label":"Codex Spark",
                  "usedPercent":0,"remainingPercent":100,
                  "resetsAt":"2030-07-09T00:00:00Z",
                  "paceStatus":{"state":"learningDuration",
                  "windowKey":"additional.deadbeef.secondary.v1",
                  "durationSource":"observed","completeCycles":0}}
               ]}
            ]}
            """);

        var agent = payload.Agents[0];
        Assert.Equal(
            new[] { "Codex Spark · 1", "Codex Spark · 2" },
            agent.UniqueCardWindows.Select(w => w.Label));
        Assert.Equal(
            new[] { "Codex Spark", "Codex Spark" }, agent.RawCardWindows.Select(w => w.Label));

        Assert.Equal(
            "codex|Codex Spark", QuotaResolver.CanonicalSelection(payload, "codex|Codex Spark"));
        Assert.Null(QuotaResolver.Resolve(payload, "codex|Codex Spark"));
    }

    private static string[] SparkPairLabels(long first, long second)
    {
        var json =
            """
            {"generatedAt":"now","agents":[
              {"clientId":"codex","source":"fixture","updatedAt":"now",
               "windows":[
                 {"cardId":"a.v1","label":"Codex Spark","usedPercent":0,
                  "remainingPercent":100,"windowMinutes":__firstMin__,
                  "resetsAt":"2030-07-09T00:00:00Z",
                  "paceStatus":{"state":"learningHistory","windowKey":"a.v1",
                  "durationSeconds":__first__,"durationSource":"provider","completeCycles":1}},
                 {"cardId":"b.v1","label":"Codex Spark","usedPercent":0,
                  "remainingPercent":100,"windowMinutes":__secondMin__,
                  "resetsAt":"2030-07-09T00:00:00Z",
                  "paceStatus":{"state":"learningHistory","windowKey":"b.v1",
                  "durationSeconds":__second__,"durationSource":"provider","completeCycles":1}}
               ]}
            ]}
            """
            .Replace("__firstMin__", (first / 60).ToString())
            .Replace("__secondMin__", (second / 60).ToString())
            .Replace("__first__", first.ToString())
            .Replace("__second__", second.ToString());
        return Decode(json).Agents[0].UniqueCardWindows.Select(w => w.Label).ToArray();
    }

    [Fact]
    public void DifferingDurationsBelowTheLargestUnitStillProduceDifferentNames() =>
        Assert.Equal(
            new[] { "Codex Spark · 1h", "Codex Spark · 1h 30m" }, SparkPairLabels(3_600, 5_400));

    [Fact]
    public void EngineVocabularyLengthsUseSessionAndWeekly() =>
        Assert.Equal(
            new[] { "Codex Spark · Session", "Codex Spark · Weekly" },
            SparkPairLabels(18_000, 604_800));

    // Two windows of one period have no period to be told apart by, so the
    // length tier is refused and the reset tier is asked — these carry no
    // reset either, so the ordinal is what remains.
    [Fact]
    public void DurationsThatRenderIdenticallyFallToOrdinal() =>
        Assert.Equal(new[] { "Codex Spark · 1", "Codex Spark · 2" }, SparkPairLabels(90, 119));

    // A generated name must also avoid a label some OTHER window already
    // carries — uniqueness inside the repeated group says nothing about the
    // rest of the card view.
    [Fact]
    public void GeneratedNameStepsOverALabelAnotherWindowAlreadyCarries()
    {
        var payload = Decode(
            """
            {"generatedAt":"now","agents":[
              {"clientId":"codex","source":"fixture","updatedAt":"now",
               "windows":[
                 {"cardId":"a.v1","label":"Foo","usedPercent":0,"remainingPercent":100},
                 {"cardId":"b.v1","label":"Foo","usedPercent":0,"remainingPercent":100},
                 {"cardId":"c.v1","label":"Foo · 1","usedPercent":0,"remainingPercent":100}
               ]}
            ]}
            """);

        var labels = payload.Agents[0].UniqueCardWindows.Select(w => w.Label).ToArray();
        Assert.Equal(3, labels.Distinct().Count());
        Assert.Contains("Foo · 1", labels);
    }

    // The state issue #286 was actually filed from: Codex reports a window
    // with no usage yet as unavailable(invalidEvidence), which clears the
    // duration AND windowMinutes, so both rows arrive at 100% remaining with
    // no length at all. The reset each row already displays is what
    // separates them (tier 2). Durations chosen with wide margins (5h, 7d) so
    // the few milliseconds between building resetsAt here and the qualifier
    // reading DateTimeOffset.UtcNow cannot cross a minute boundary.
    [Fact]
    public void DurationUnavailablePairIsNamedByReset()
    {
        var now = DateTimeOffset.UtcNow;
        var soon = now.AddHours(5).ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
        var later = now.AddDays(7).ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
        var payload = Decode(
            """
            {"generatedAt":"now","agents":[
              {"clientId":"codex","source":"fixture","updatedAt":"now",
               "windows":[
                 {"cardId":"additional.deadbeef.primary.v1","label":"Codex Spark",
                  "usedPercent":0,"remainingPercent":100,"resetsAt":"__soon__",
                  "paceStatus":{"state":"unavailable",
                  "windowKey":"additional.deadbeef.primary.v1",
                  "completeCycles":0,"reason":"invalidEvidence"}},
                 {"cardId":"additional.deadbeef.secondary.v1","label":"Codex Spark",
                  "usedPercent":0,"remainingPercent":100,"resetsAt":"__later__",
                  "paceStatus":{"state":"unavailable",
                  "windowKey":"additional.deadbeef.secondary.v1",
                  "completeCycles":0,"reason":"invalidEvidence"}}
               ]}
            ]}
            """
            .Replace("__soon__", soon)
            .Replace("__later__", later));

        Assert.Equal(
            new[] { "Codex Spark · 5h", "Codex Spark · 7d" },
            payload.Agents[0].UniqueCardWindows.Select(w => w.Label));
    }

    // The qualifier and the countdown beside it must round the same way: a
    // reset one second inside five hours must not read "4h 59m" in the name
    // while the row's own countdown says "Resets in 5h".
    [Fact]
    public void QualifierRoundingMatchesTheCountdownRounding()
    {
        var now = DateTimeOffset.UtcNow;
        var nearlyFiveHours = now.AddSeconds(5 * 3_600 - 1).ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
        var sevenDays = now.AddDays(7).ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
        var payload = Decode(
            """
            {"generatedAt":"now","agents":[
              {"clientId":"codex","source":"fixture","updatedAt":"now",
               "windows":[
                 {"cardId":"a.v1","label":"Codex Spark","usedPercent":0,
                  "remainingPercent":100,"resetsAt":"__nearlyFiveHours__",
                  "paceStatus":{"state":"unavailable","windowKey":"a.v1",
                  "completeCycles":0,"reason":"invalidEvidence"}},
                 {"cardId":"b.v1","label":"Codex Spark","usedPercent":0,
                  "remainingPercent":100,"resetsAt":"__sevenDays__",
                  "paceStatus":{"state":"unavailable","windowKey":"b.v1",
                  "completeCycles":0,"reason":"invalidEvidence"}}
               ]}
            ]}
            """
            .Replace("__nearlyFiveHours__", nearlyFiveHours)
            .Replace("__sevenDays__", sevenDays));

        var window = payload.Agents[0].UniqueCardWindows[0];
        Assert.Equal("Codex Spark · 5h", window.Label);
        Assert.Equal(
            "Resets in 5h", UsagePace.ResetText(window.ResetsAt!, DateTimeOffset.UtcNow));
    }
}

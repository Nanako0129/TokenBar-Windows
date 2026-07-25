using System.Text.Json;
using TokenBar.Interop;
using Xunit;

namespace TokenBar.Core.Tests;

public class DtoDecodeTests
{
    private static readonly JsonSerializerOptions Web =
        new(JsonSerializerDefaults.Web)
        {
            RespectRequiredConstructorParameters = true,
            RespectNullableAnnotations = true,
        };

    private const string AvailableJson = """
        {
          "cardId":"codex.weekly.v1",
          "label":"Weekly",
          "usedPercent":40.0,
          "remainingPercent":60.0,
          "resetsAt":"2026-07-09T00:00:00Z",
          "resetText":"Resets in 2d",
          "windowMinutes":10080,
          "paceStatus":{
            "state":"available",
            "windowKey":"codex.weekly.v1",
            "durationSeconds":604800,
            "durationSource":"provider",
            "completeCycles":5
          },
          "historicalPace":{
            "expectedUsedPercent":55.5,
            "etaSeconds":1200,
            "willLastToReset":false,
            "runOutProbability":0.7
          }
        }
        """;

    private const string LearningHistoryJson = """
        {
          "cardId":"claude.weekly.v1",
          "label":"Weekly",
          "usedPercent":25.0,
          "remainingPercent":75.0,
          "resetsAt":"2026-07-09T00:00:00Z",
          "windowMinutes":1440,
          "paceStatus":{
            "state":"learningHistory",
            "windowKey":"claude.weekly.v1",
            "durationSeconds":86400,
            "durationSource":"contract",
            "completeCycles":1
          }
        }
        """;

    private const string LearningDurationObservedJson = """
        {
          "cardId":"antigravity.session.v1",
          "label":"Session",
          "usedPercent":10.0,
          "remainingPercent":90.0,
          "resetsAt":"2026-07-09T00:00:00Z",
          "paceStatus":{
            "state":"learningDuration",
            "windowKey":"antigravity.session.v1",
            "durationSource":"observed",
            "completeCycles":0
          }
        }
        """;

    private const string LearningDurationJson = """
        {
          "cardId":"grok.session.v1",
          "label":"Session",
          "usedPercent":10.0,
          "remainingPercent":90.0,
          "resetsAt":"2026-07-09T00:00:00Z",
          "paceStatus":{
            "state":"learningDuration",
            "windowKey":"grok.session.v1",
            "completeCycles":0
          }
        }
        """;

    private const string UnavailableJson = """
        {
          "cardId":"provider.unknown.v1",
          "label":"Unknown",
          "usedPercent":50.0,
          "remainingPercent":50.0,
          "paceStatus":{
            "state":"unavailable",
            "completeCycles":0,
            "reason":"windowIdentity"
          }
        }
        """;

    private static UsageWindow DecodeWindow(string json) =>
        JsonSerializer.Deserialize<UsageWindow>(json, Web)
        ?? throw new InvalidOperationException("window decoded to null");

    private static AgentUsagePayload DecodeAgentUsagePayload(
        string? subscriptionsJson = null,
        string agentsJson = """[{"clientId":"codex","source":"oauth","updatedAt":"now","windows":[]}]""")
    {
        var subscriptions = subscriptionsJson is null
            ? ""
            : $$""","opencodeSubscriptions":{{subscriptionsJson}}""";
        return JsonSerializer.Deserialize<AgentUsagePayload>(
            $$"""{"generatedAt":"now","agents":{{agentsJson}}{{subscriptions}}}""",
            Web) ?? throw new InvalidOperationException("agent usage payload decoded to null");
    }

    private static void AssertRejects(string json) =>
        Assert.Throws<JsonException>(() => DecodeWindow(json));

    private static string Replace(string json, string oldValue, string newValue) =>
        json.Replace(oldValue, newValue, StringComparison.Ordinal);

    [Fact]
    public void TraceBucketDecodesSnakeCase()
    {
        var json = """
            {"ok":true,"data":[{"client":"claude","agent":"main","model":"claude-sonnet-4",
             "tokens":1234,"messages":5,"tokens_per_min":678.5}]}
            """;
        var buckets = TbCore.DecodeEnvelope<IReadOnlyList<TraceBucket>>(json);
        var b = Assert.Single(buckets);
        Assert.Equal("claude", b.Client);
        Assert.Equal(1234, b.Tokens);
        Assert.Equal(678.5, b.TokensPerMin);
    }

    [Fact]
    public void TokensPerMinDecodesCamelCase()
    {
        var v = TbCore.DecodeEnvelope<TokensPerMin>("""{"ok":true,"data":{"tokensPerMin":42.5}}""");
        Assert.Equal(42.5, v.Value);
    }

    [Fact]
    public void ModelReportDecodesLowercaseKQuirkAndOptionals()
    {
        var json = """
            {"ok":true,"data":{"entries":[{"client":"claude","model":"m","provider":"anthropic",
             "input":1,"output":2,"cacheRead":3,"cacheWrite":4,"reasoning":0,"total":10,
             "messageCount":1,"cost":0.5,"msPer1kTokens":12.5}],
             "totalInput":1,"totalOutput":2,"totalCacheRead":3,"totalCacheWrite":4,
             "totalMessages":1,"totalCost":0.5}}
            """;
        var r = TbCore.DecodeEnvelope<ModelReport>(json);
        Assert.Equal(12.5, r.Entries[0].MsPer1kTokens);
        Assert.Null(r.PricingUpdatedAt);
    }

    [Fact]
    public void AgentUsageDecodesLegacyFieldsWithoutTrustingScalars()
    {
        var json = """
            {"ok":true,"data":{"generatedAt":"2026-07-02T00:00:00Z","agents":[
             {"clientId":"codex","source":"oauth","updatedAt":"2026-07-02T00:00:00Z",
              "identity":{"email":null,"plan":"plus"},
              "windows":[{"label":"Weekly","usedPercent":40.0,"remainingPercent":60.0,
                "resetsAt":null,"resetText":"Resets in 2d","windowMinutes":10080,
                "historicalExpectedPercent":55.5,"runOutProbability":0.1,
                "durationSeconds":604800}],
              "credits":null,"error":null}]}}
            """;
        var p = TbCore.DecodeEnvelope<AgentUsagePayload>(json);
        var w = Assert.Single(Assert.Single(p.Agents).Windows);
        Assert.Equal(10080, w.WindowMinutes);
        Assert.Equal(UsageWindow.LegacyMissingPresentationId, w.CardId);
        Assert.Equal(UsagePaceState.LegacyMissing, w.PaceStatus.State);
        Assert.Null(w.DurationSeconds);
        Assert.Null(w.HistoricalPace);
        Assert.Null(typeof(UsageWindow).GetProperty("HistoricalExpectedPercent"));
        Assert.Null(typeof(UsageWindow).GetProperty("RunOutProbability"));
        Assert.Null(p.OpencodeSubscriptions);
    }

    [Fact]
    public void AgentUsagePayloadIgnoresUnknownPublicationGeneration()
    {
        var payload = TbCore.DecodeEnvelope<AgentUsagePayload>(
            """{"ok":true,"data":{"generatedAt":"now","publicationGeneration":42,"agents":[]}}""");
        Assert.Equal("now", payload.GeneratedAt);
        Assert.Empty(payload.Agents);
    }

    [Fact]
    public void AgentUsagePayloadDecodesStringSubscriptions()
    {
        var payload = DecodeAgentUsagePayload("""["Codex","Claude"]""");
        Assert.Equal(new[] { "Codex", "Claude" }, payload.OpencodeSubscriptions);
    }

    [Fact]
    public void AgentUsagePayloadAllowsOmittedOrNullSubscriptions()
    {
        Assert.Null(DecodeAgentUsagePayload().OpencodeSubscriptions);
        Assert.Null(DecodeAgentUsagePayload("null").OpencodeSubscriptions);
    }

    [Fact]
    public void AgentUsagePayloadRejectsNullSubscriptionElement() =>
        Assert.Throws<JsonException>(() => DecodeAgentUsagePayload("[null]"));

    [Fact]
    public void AgentUsagePayloadRejectsNullAgentElement() =>
        Assert.Throws<JsonException>(() => DecodeAgentUsagePayload(agentsJson: "[null]"));

    [Fact]
    public void AgentUsageSnapshotRejectsNullWindowElement() =>
        Assert.Throws<JsonException>(() => DecodeAgentUsagePayload(
            agentsJson: """[{"clientId":"codex","source":"oauth","updatedAt":"now","windows":[null]}]"""));

    [Fact]
    public void UsagePayloadDecodesNestedShape()
    {
        var json = """
            {"ok":true,"data":{
             "meta":{"generatedAt":"g","version":"v","dateRange":{"start":"2026-01-01","end":"2026-07-02"}},
             "summary":{"totalTokens":100,"totalCost":1.5,"totalDays":10,"activeDays":8,
               "averagePerDay":10.0,"maxCostInSingleDay":0.9,"clients":["claude"],"models":["m"]},
             "years":[{"year":"2026","totalTokens":100,"totalCost":1.5,
               "range":{"start":"2026-01-01","end":"2026-07-02"}}],
             "contributions":[{"date":"2026-07-01","totals":{"tokens":50,"cost":0.7,"messages":3},
               "intensity":2,"tokenBreakdown":{"input":10,"output":20,"cacheRead":15,"cacheWrite":5,"reasoning":0},
               "clients":[{"client":"claude","modelId":"m","providerId":"anthropic",
                 "tokens":{"input":10,"output":20,"cacheRead":15,"cacheWrite":5,"reasoning":0},
                 "cost":0.7,"messages":3}]}]}}
            """;
        var p = TbCore.DecodeEnvelope<UsagePayload>(json);
        Assert.Equal(8, p.Summary.ActiveDays);
        Assert.Equal("anthropic", p.Contributions[0].Clients[0].ProviderId);
        Assert.Equal(15, p.Contributions[0].TokenBreakdown.CacheRead);
    }

    [Fact]
    public void AvailablePaceDecodesTypedNestedValues()
    {
        var window = DecodeWindow(AvailableJson);
        Assert.Equal("codex.weekly.v1", window.CardId);
        Assert.Equal(UsagePaceState.Available, window.PaceStatus.State);
        Assert.Equal("codex.weekly.v1", window.PaceStatus.WindowKey);
        Assert.Equal(604800, window.PaceStatus.DurationSeconds);
        Assert.Equal(UsagePaceDurationSource.Provider, window.PaceStatus.DurationSource);
        Assert.Equal(5, window.PaceStatus.CompleteCycles);
        Assert.Null(window.PaceStatus.Reason);
        Assert.Equal(604800, window.DurationSeconds);
        Assert.Equal(55.5, window.HistoricalPace!.ExpectedUsedPercent);
        Assert.Equal(1200, window.HistoricalPace.EtaSeconds);
        Assert.False(window.HistoricalPace.WillLastToReset);
        Assert.Equal(0.7, window.HistoricalPace.RunOutProbability);
    }

    [Fact]
    public void LearningHistoryDecodesWithoutHistoricalPace()
    {
        var window = DecodeWindow(LearningHistoryJson);
        Assert.Equal(UsagePaceState.LearningHistory, window.PaceStatus.State);
        Assert.Equal(86400, window.DurationSeconds);
        Assert.Equal(1440, window.WindowMinutes);
        Assert.Null(window.HistoricalPace);
    }

    [Fact]
    public void LearningDurationSupportsObservedOrMissingSource()
    {
        var observed = DecodeWindow(LearningDurationObservedJson);
        Assert.Equal(UsagePaceState.LearningDuration, observed.PaceStatus.State);
        Assert.Equal(UsagePaceDurationSource.Observed, observed.PaceStatus.DurationSource);
        Assert.Null(observed.PaceStatus.DurationSeconds);
        Assert.Null(observed.DurationSeconds);

        var missing = DecodeWindow(LearningDurationJson);
        Assert.Equal(UsagePaceState.LearningDuration, missing.PaceStatus.State);
        Assert.Null(missing.PaceStatus.DurationSource);
        Assert.Null(missing.DurationSeconds);
    }

    [Fact]
    public void UnavailableIdentityPaceDecodesWithNullWindowKey()
    {
        var window = DecodeWindow(UnavailableJson);
        Assert.Equal(UsagePaceState.Unavailable, window.PaceStatus.State);
        Assert.Null(window.PaceStatus.WindowKey);
        Assert.Equal(UsagePaceUnavailableReason.WindowIdentity, window.PaceStatus.Reason);
        Assert.Null(window.DurationSeconds);
        Assert.Null(window.HistoricalPace);
    }

    [Fact]
    public void MissingPaceStatusKeyIsLegacyButPresentNullFails()
    {
        var legacy = DecodeWindow("""
            {"label":"Weekly","usedPercent":40,"remainingPercent":60}
            """);
        Assert.Equal(UsageWindow.LegacyMissingPresentationId, legacy.CardId);
        Assert.Equal(UsagePaceState.LegacyMissing, legacy.PaceStatus.State);

        AssertRejects("""
            {"cardId":"codex.weekly.v1","label":"Weekly","usedPercent":40,
             "remainingPercent":60,"paceStatus":null}
            """);
    }

    [Fact]
    public void LegacyPresentNullCardIdFails()
    {
        AssertRejects("""
            {"cardId":null,"label":"Weekly","usedPercent":40,"remainingPercent":60}
            """);
    }

    [Theory]
    [InlineData("cardId", "")]
    [InlineData("cardId", "   ")]
    [InlineData("cardId", "null")]
    [InlineData("cardId", "123")]
    public void V3CardIdMustBePresentNonEmptyAndString(string key, string value)
    {
        var json = value == "null" || value == "123"
            ? Replace(AvailableJson, "\"cardId\":\"codex.weekly.v1\"", $"\"{key}\":{value}")
            : Replace(AvailableJson, "\"cardId\":\"codex.weekly.v1\"", $"\"{key}\":\"{value}\"");
        AssertRejects(json);
    }

    [Fact]
    public void V3MissingCardIdFails()
    {
        AssertRejects(Replace(AvailableJson, "\"cardId\":\"codex.weekly.v1\",", ""));
    }

    [Theory]
    [InlineData("\"state\":\"available\"", "\"state\":\"future\"")]
    [InlineData("\"durationSource\":\"provider\"", "\"durationSource\":\"future\"")]
    public void UnknownPaceEnumValuesFail(string oldValue, string newValue) =>
        AssertRejects(Replace(AvailableJson, oldValue, newValue));

    [Fact]
    public void UnknownUnavailableReasonFails()
    {
        var json = """
            {"cardId":"x","label":"Unknown","usedPercent":50,"remainingPercent":50,
             "paceStatus":{"state":"unavailable","completeCycles":0,"reason":"future"}}
            """;
        AssertRejects(json);
    }

    [Fact]
    public void NegativeCompleteCyclesFail() =>
        AssertRejects(Replace(AvailableJson, "\"completeCycles\":5", "\"completeCycles\":-1"));

    [Theory]
    [InlineData("\"windowKey\":\"codex.weekly.v1\",", "")]
    [InlineData("\"windowKey\":\"codex.weekly.v1\",", "\"windowKey\":null,")]
    public void NonIdentityStateRequiresNonEmptyWindowKey(string oldValue, string newValue) =>
        AssertRejects(Replace(AvailableJson, oldValue, newValue));

    [Fact]
    public void IdentityUnavailableCannotCarryWindowKey()
    {
        var json = Replace(UnavailableJson, "\"completeCycles\":0,", "\"windowKey\":\"x\",\"completeCycles\":0,");
        AssertRejects(json);
    }

    [Fact]
    public void NonIdentityUnavailableRequiresWindowKey()
    {
        var json = """
            {"cardId":"x","label":"Unknown","usedPercent":50,"remainingPercent":50,
             "paceStatus":{"state":"unavailable","completeCycles":0,"reason":"missingReset"}}
            """;
        AssertRejects(json);
    }

    [Theory]
    [InlineData("\"durationSeconds\":604800,", "\"durationSeconds\":0,")]
    [InlineData("\"durationSeconds\":604800,", "\"durationSeconds\":34560001,")]
    [InlineData("\"durationSeconds\":604800,", "\"durationSeconds\":604800,")]
    public void DurationBoundsAndRequiredSourceAreStrict(string oldValue, string newValue)
    {
        var json = oldValue == newValue
            ? Replace(AvailableJson, "\"durationSource\":\"provider\",", "")
            : Replace(AvailableJson, oldValue, newValue);
        AssertRejects(json);
    }

    [Fact]
    public void DurationSourceWithoutDurationIsOnlyObservedLearningDuration()
    {
        var history = Replace(
            LearningHistoryJson,
            "\"durationSeconds\":86400,",
            "");
        AssertRejects(history);

        var duration = Replace(
            LearningDurationObservedJson,
            "\"state\":\"learningDuration\"",
            "\"state\":\"learningHistory\"");
        AssertRejects(duration);
    }

    [Fact]
    public void LearningDurationCannotCarryDurationOrReason()
    {
        var withDuration = Replace(
            LearningDurationJson,
            "\"completeCycles\":0",
            "\"durationSeconds\":60,\"durationSource\":\"observed\",\"completeCycles\":0");
        AssertRejects(withDuration);

        var withReason = Replace(
            LearningDurationJson,
            "\"completeCycles\":0",
            "\"completeCycles\":0,\"reason\":\"history\"");
        AssertRejects(withReason);
    }

    [Fact]
    public void UnavailableRequiresReasonAndCannotCarryHistoricalPace()
    {
        var missingReason = Replace(
            UnavailableJson,
            "\"reason\":\"windowIdentity\"",
            "\"unknown\":\"windowIdentity\"");
        AssertRejects(missingReason);

        var withHistory = """
            {"cardId":"provider.unknown.v1","label":"Unknown","usedPercent":50,"remainingPercent":50,
             "paceStatus":{"state":"unavailable","completeCycles":0,"reason":"windowIdentity"},
             "historicalPace":{"expectedUsedPercent":10,"willLastToReset":true}}
            """;
        AssertRejects(withHistory);
    }

    [Fact]
    public void UnavailableCannotCarryDuration()
    {
        var json = Replace(
            UnavailableJson,
            "\"remainingPercent\":50.0,",
            "\"remainingPercent\":50.0,\"windowMinutes\":1,");
        json = Replace(
            json,
            "\"completeCycles\":0,",
            "\"durationSeconds\":60,\"durationSource\":\"provider\",\"completeCycles\":0,");
        AssertRejects(json);
    }

    [Fact]
    public void V3ResetPresenceAndFormatMustMatchPaceState()
    {
        const string reset = "\"resetsAt\":\"2026-07-09T00:00:00Z\",";
        AssertRejects(Replace(AvailableJson, reset, ""));
        AssertRejects(Replace(LearningHistoryJson, reset, ""));
        AssertRejects(Replace(LearningDurationJson, reset, ""));
        AssertRejects(Replace(AvailableJson, reset, "\"resetsAt\":\"tomorrow\","));

        var missingResetWithReset = """
            {"cardId":"x","label":"Unknown","usedPercent":50,"remainingPercent":50,
             "resetsAt":"2026-07-09T00:00:00Z",
             "paceStatus":{"state":"unavailable","windowKey":"x",
               "completeCycles":0,"reason":"missingReset"}}
            """;
        AssertRejects(missingResetWithReset);
    }

    [Fact]
    public void WindowMinutesMustMirrorDurationOrBeAbsentWithoutDuration()
    {
        AssertRejects(Replace(AvailableJson, "\"windowMinutes\":10080", "\"windowMinutes\":10079"));

        var learning = """
            {"cardId":"grok.session.v1","label":"Session","usedPercent":10,"remainingPercent":90,
             "windowMinutes":1,
             "paceStatus":{"state":"learningDuration","windowKey":"grok.session.v1","completeCycles":0}}
            """;
        AssertRejects(learning);
    }

    [Fact]
    public void HistoricalPacePresenceMustMatchState()
    {
        AssertRejects(Replace(AvailableJson, "\"historicalPace\":", "\"ignoredPace\":"));

        var learningHistoryWithPace = """
            {"cardId":"claude.weekly.v1","label":"Weekly","usedPercent":25,"remainingPercent":75,
             "windowMinutes":1440,
             "paceStatus":{"state":"learningHistory","windowKey":"claude.weekly.v1",
               "durationSeconds":86400,"durationSource":"contract","completeCycles":1},
             "historicalPace":{"expectedUsedPercent":10,"willLastToReset":true}}
            """;
        AssertRejects(learningHistoryWithPace);
    }

    [Theory]
    [InlineData("\"expectedUsedPercent\":55.5", "\"expectedUsedPercent\":-0.1")]
    [InlineData("\"expectedUsedPercent\":55.5", "\"expectedUsedPercent\":100.1")]
    [InlineData("\"etaSeconds\":1200", "\"etaSeconds\":-1")]
    [InlineData("\"etaSeconds\":1200", "\"etaSeconds\":\"soon\"")]
    [InlineData("\"runOutProbability\":0.7", "\"runOutProbability\":-0.1")]
    [InlineData("\"runOutProbability\":0.7", "\"runOutProbability\":1.1")]
    public void HistoricalPaceRangesAndTypesAreStrict(string oldValue, string newValue) =>
        AssertRejects(Replace(AvailableJson, oldValue, newValue));

    [Theory]
    [InlineData("\"etaSeconds\":1200,", "\"etaSeconds\":null,")]
    [InlineData("\"willLastToReset\":false", "\"willLastToReset\":true")]
    public void HistoricalEtaAndWillLastMustAgree(string oldValue, string newValue) =>
        AssertRejects(Replace(AvailableJson, oldValue, newValue));

    [Fact]
    public void HistoricalRequiredFieldsAndWrongTypesFail()
    {
        AssertRejects(Replace(AvailableJson, "\"expectedUsedPercent\":55.5,", ""));
        AssertRejects(Replace(AvailableJson, "\"willLastToReset\":false,", ""));
        AssertRejects(Replace(AvailableJson, "\"expectedUsedPercent\":55.5", "\"expectedUsedPercent\":\"55.5\""));
    }

    [Theory]
    [InlineData("\"usedPercent\":40.0", "\"usedPercent\":-1")]
    [InlineData("\"usedPercent\":40.0", "\"usedPercent\":101")]
    [InlineData("\"remainingPercent\":60.0", "\"remainingPercent\":\"60\"")]
    [InlineData("\"remainingPercent\":60.0", "\"remainingPercent\":59")]
    public void PercentagesMustBeFiniteBoundedComplementaryAndNumeric(string oldValue, string newValue) =>
        AssertRejects(Replace(AvailableJson, oldValue, newValue));

    [Fact]
    public void RequiredLabelAndPercentKeysCannotBeMissing()
    {
        AssertRejects(Replace(AvailableJson, "\"label\":\"Weekly\",", ""));
        AssertRejects(Replace(AvailableJson, "\"usedPercent\":40.0,", ""));
        AssertRejects(Replace(AvailableJson, "\"remainingPercent\":60.0,", ""));
    }

    [Fact]
    public void StandardJsonCannotRepresentNaNPercentages()
    {
        Assert.Throws<JsonException>(() => DecodeWindow(
            Replace(AvailableJson, "\"usedPercent\":40.0", "\"usedPercent\":NaN")));
    }

    [Fact]
    public void OptionalResetFieldsOnlyAcceptStringOrNull()
    {
        AssertRejects(Replace(AvailableJson, "\"resetsAt\":\"2026-07-09T00:00:00Z\"", "\"resetsAt\":123"));
        AssertRejects(Replace(AvailableJson, "\"resetText\":\"Resets in 2d\"", "\"resetText\":false"));
        AssertRejects(Replace(AvailableJson, "\"windowMinutes\":10080", "\"windowMinutes\":10080.5"));
    }

    [Fact]
    public void TopLevelLegacyPaceScalarsCannotOverrideNestedV3Values()
    {
        var json = Replace(
            AvailableJson,
            "\"paceStatus\":",
            "\"historicalExpectedPercent\":99,\"runOutProbability\":0.99,\"durationSeconds\":1,\"paceStatus\":");
        var window = DecodeWindow(json);
        Assert.Equal(55.5, window.HistoricalPace!.ExpectedUsedPercent);
        Assert.Equal(604800, window.DurationSeconds);
        Assert.Equal(UsagePaceState.Available, window.PaceStatus.State);
    }

    [Fact]
    public void DirectDefaultWebOptionsUseUsageWindowAttributeConverter()
    {
        var window = JsonSerializer.Deserialize<UsageWindow>(
            AvailableJson,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.NotNull(window);
        Assert.Equal(UsagePaceState.Available, window!.PaceStatus.State);
        Assert.Equal(604800, window.DurationSeconds);
    }

    [Fact]
    public void UsageWindowSerializationDoesNotHitUnsupportedConverterWrite()
    {
        var json = JsonSerializer.Serialize(DecodeWindow(AvailableJson), Web);
        Assert.Contains("\"paceStatus\"", json);
        Assert.Contains("\"durationSeconds\":604800", json);
    }

    [Fact]
    public void UniqueCardWindowsPreservesOrderAndFirstDuplicate()
    {
        var first = DecodeWindow(AvailableJson) with { Label = "first" };
        var duplicate = DecodeWindow(AvailableJson) with { Label = "duplicate" };
        var other = DecodeWindow(LearningHistoryJson);
        var snapshot = new AgentUsageSnapshot(
            ClientId: "codex",
            Source: "oauth",
            UpdatedAt: "now",
            Windows: [first, duplicate, other]);

        var unique = snapshot.UniqueCardWindows;
        Assert.Equal(2, unique.Count);
        Assert.Same(first, unique[0]);
        Assert.Same(other, unique[1]);
    }
}

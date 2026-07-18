using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using TokenBar.Core;
using TokenBar.Interop;
using Xunit;

namespace TokenBar.Core.Tests;

public sealed class ProviderQuotaPaceV3FixtureTests
{
    private const string ExpectedSha256 =
        "412f6ffd05f23f00266820c243376f265d29024d9e419217e55f8e1559b36c50";
    private static readonly JsonSerializerOptions Web =
        new(JsonSerializerDefaults.Web);
    private static string FixturePath => Path.Combine(
        AppContext.BaseDirectory, "Fixtures", "provider-quota-pace-v3.json");

    [Fact]
    public void CanonicalFixtureExistsHasExpectedHashAndSchema()
    {
        Assert.True(File.Exists(FixturePath), FixturePath);
        var bytes = File.ReadAllBytes(FixturePath);

        Assert.Equal(7625, bytes.Length);
        Assert.Equal(
            ExpectedSha256,
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());

        using var fixture = JsonDocument.Parse(bytes);
        Assert.Equal(3, fixture.RootElement.GetProperty("schemaVersion").GetInt32());
    }

    [Fact]
    public void PayloadDecodesTypedWindowsInStableCardOrder()
    {
        using var fixture = ReadFixture();
        var payload = fixture.RootElement.GetProperty("payload")
            .Deserialize<AgentUsagePayload>(Web);

        Assert.NotNull(payload);
        var agent = Assert.Single(payload!.Agents);
        Assert.Equal(7, agent.Windows.Count);
        Assert.Equal(
            new[]
            {
                "ahead.invalid",
                "behind.invalid",
                "learning-history.invalid",
                "learning-duration.invalid",
                "missing-reset.invalid",
                "shared-first.invalid",
                "shared-second.invalid",
            },
            agent.Windows.Select(window => window.CardId));
        Assert.Equal(
            new[]
            {
                UsagePaceState.Available,
                UsagePaceState.Available,
                UsagePaceState.LearningHistory,
                UsagePaceState.LearningDuration,
                UsagePaceState.Unavailable,
                UsagePaceState.LearningHistory,
                UsagePaceState.LearningHistory,
            },
            agent.Windows.Select(window => window.PaceStatus.State));
        Assert.Equal(2, agent.Windows.Count(window => window.HistoricalPace is not null));

        var ahead = agent.Windows[0];
        Assert.Equal(18_000L, ahead.DurationSeconds);
        Assert.Equal(UsagePaceDurationSource.Provider, ahead.PaceStatus.DurationSource);

        var behind = agent.Windows[1];
        Assert.Equal(604_800L, behind.DurationSeconds);
        Assert.Equal(UsagePaceDurationSource.Contract, behind.PaceStatus.DurationSource);

        var learningHistory = agent.Windows[2];
        Assert.Equal(18_000L, learningHistory.DurationSeconds);
        Assert.Equal(
            UsagePaceDurationSource.Provider,
            learningHistory.PaceStatus.DurationSource);

        var learningDuration = agent.Windows[3];
        Assert.Null(learningDuration.DurationSeconds);
        Assert.Equal(
            UsagePaceDurationSource.Observed,
            learningDuration.PaceStatus.DurationSource);

        var missingReset = agent.Windows[4];
        Assert.Null(missingReset.DurationSeconds);
        Assert.Null(missingReset.PaceStatus.DurationSource);
        Assert.Equal(
            UsagePaceUnavailableReason.MissingReset,
            missingReset.PaceStatus.Reason);
    }

    [Fact]
    public void CasesHaveExpectedCountAndKinds()
    {
        using var fixture = ReadFixture();
        var cases = fixture.RootElement.GetProperty("cases");
        var caseItems = cases.EnumerateArray().ToArray();

        Assert.Equal(12, caseItems.Length);
        Assert.Equal(3, caseItems.Count(item => item.GetProperty("kind").GetString() == "pace"));
        Assert.Equal(3, caseItems.Count(item => item.GetProperty("kind").GetString() == "selection"));
        Assert.Equal(1, caseItems.Count(item => item.GetProperty("kind").GetString() == "legacy"));
        Assert.Equal(5, caseItems.Count(item => item.GetProperty("kind").GetString() == "malformed"));
    }

    [Fact]
    public void LegacyRawWindowUsesProductionConverterAndHasNoPace()
    {
        using var fixture = ReadFixture();
        var legacy = FindCase(fixture.RootElement, "legacy-missing-pace-status");
        var window = DeserializeWindow(legacy.GetProperty("rawWindow").GetString()!);
        var now = DateTimeOffset.Parse(
            legacy.GetProperty("now").GetString()!,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind);

        Assert.Equal(UsagePaceState.LegacyMissing, window.PaceStatus.State);
        Assert.Null(window.DurationSeconds);
        Assert.Equal(300L, window.WindowMinutes);
        Assert.Null(UsagePace.Compute(window, PaceMode.Historical, now));
        Assert.Null(UsagePace.Compute(window, PaceMode.Linear, now));
    }

    [Fact]
    public void EveryMalformedRawWindowIsRejectedByProductionConverter()
    {
        using var fixture = ReadFixture();
        var malformed = fixture.RootElement.GetProperty("cases")
            .EnumerateArray()
            .Where(item => item.GetProperty("kind").GetString() == "malformed")
            .ToArray();

        Assert.Equal(5, malformed.Length);
        Assert.All(malformed, item =>
            Assert.Throws<JsonException>(() => DeserializeWindow(
                item.GetProperty("rawWindow").GetString()!)));
    }

    private static JsonDocument ReadFixture() =>
        JsonDocument.Parse(File.ReadAllBytes(FixturePath));

    private static JsonElement FindCase(JsonElement root, string name) =>
        root.GetProperty("cases")
            .EnumerateArray()
            .Single(item => item.GetProperty("name").GetString() == name);

    private static UsageWindow DeserializeWindow(string rawWindow) =>
        JsonSerializer.Deserialize<UsageWindow>(rawWindow, Web)
        ?? throw new InvalidOperationException("window decoded to null");
}

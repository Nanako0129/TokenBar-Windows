using System.Text.Json;
using TokenBar.Interop;
using Xunit;

namespace TokenBar.Core.Tests;

// Ported from SelfTest.swift's quota-resolver block: the payload builds via
// JSON, same as the Swift fixture (the snapshot types have no builders).
public class QuotaResolverTests
{
    private static readonly AgentUsagePayload Payload = JsonSerializer.Deserialize<AgentUsagePayload>(
        """
        {"generatedAt":"now","agents":[
          {"clientId":"codex","source":"oauth","updatedAt":"now",
           "windows":[{"cardId":"session.v1","label":"Session","usedPercent":20,"remainingPercent":80},
                      {"cardId":"weekly.v1","label":"Weekly","usedPercent":65,"remainingPercent":35},
                      {"cardId":"model.gpt|preview.v1","label":"Delimiter","usedPercent":5,"remainingPercent":95}]},
          {"clientId":"claude","source":"oauth","updatedAt":"now",
           "windows":[{"cardId":"session.v1","label":"Session","usedPercent":88,"remainingPercent":12},
                      {"cardId":"weekly.v1","label":"Weekly","usedPercent":10,"remainingPercent":90}]},
          {"clientId":"broken","source":"oauth","updatedAt":"now",
           "windows":[{"cardId":"session.v1","label":"Session","usedPercent":99,"remainingPercent":1}],
           "error":"401"}
        ]}
        """, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

    private static readonly AgentUsagePayload DuplicatePayload = JsonSerializer.Deserialize<AgentUsagePayload>(
        """
        {"generatedAt":"now","agents":[
          {"clientId":"dupe","source":"fixture","updatedAt":"now",
           "windows":[
             {"cardId":"same.v1","label":"Ambiguous","usedPercent":20,"remainingPercent":80},
             {"cardId":"same.v1","label":"Ambiguous","usedPercent":99,"remainingPercent":1},
             {"cardId":"other.v1","label":"Ambiguous","usedPercent":70,"remainingPercent":30},
             {"cardId":"Session","label":"Other","usedPercent":90,"remainingPercent":10},
             {"cardId":"other-session.v1","label":"Session","usedPercent":75,"remainingPercent":25}
           ]}
        ]}
        """, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

    [Fact]
    public void AutoResolvesTheTightestHealthyWindow()
    {
        var pick = QuotaResolver.Resolve(Payload, "auto");
        Assert.NotNull(pick);
        Assert.Equal("claude", pick.ClientId);
        Assert.Equal("session.v1", pick.Window.CardId); // broken agent's 1% is skipped
    }

    [Fact]
    public void SelectionUsesCardIdAndPreservesEmbeddedDelimiter()
    {
        Assert.Equal("codex|weekly.v1", QuotaResolver.Selection("codex", "weekly.v1"));

        var selection = QuotaResolver.Selection("codex", "model.gpt|preview.v1");
        Assert.Equal("codex|model.gpt|preview.v1", selection);
        Assert.Equal(selection, QuotaResolver.CanonicalSelection(Payload, selection));
        Assert.Equal("model.gpt|preview.v1", QuotaResolver.Resolve(Payload, selection)!.Window.CardId);
    }

    [Fact]
    public void UniqueLegacyLabelMigratesToCardId()
    {
        Assert.Equal("codex|weekly.v1", QuotaResolver.CanonicalSelection(Payload, "codex|Weekly"));
        Assert.Equal("weekly.v1", QuotaResolver.Resolve(Payload, "codex|Weekly")!.Window.CardId);
    }

    [Fact]
    public void AmbiguousLegacyLabelStaysExplicitAndDoesNotResolve()
    {
        Assert.Equal("dupe|Ambiguous", QuotaResolver.CanonicalSelection(DuplicatePayload, "dupe|Ambiguous"));
        Assert.Null(QuotaResolver.Resolve(DuplicatePayload, "dupe|Ambiguous"));
    }

    [Fact]
    public void ExactCardIdWinsOverSameNamedLegacyLabel()
    {
        Assert.Equal("dupe|Session", QuotaResolver.CanonicalSelection(DuplicatePayload, "dupe|Other"));
        Assert.Equal("dupe|Session", QuotaResolver.CanonicalSelection(DuplicatePayload, "dupe|Session"));

        var exact = QuotaResolver.Resolve(DuplicatePayload, "dupe|Session");
        Assert.NotNull(exact);
        Assert.Equal("Session", exact!.Window.CardId);
        Assert.Equal("Other", exact.Window.Label);
    }

    [Fact]
    public void MissingExplicitClientOrCardStaysExplicitAndDoesNotResolve()
    {
        foreach (var selection in new[] { "nope|Session", "codex|stale" })
        {
            Assert.Equal(selection, QuotaResolver.CanonicalSelection(Payload, selection));
            Assert.Null(QuotaResolver.Resolve(Payload, selection));
        }
    }

    [Fact]
    public void PayloadNullPreservesWellFormedExplicitSelection()
    {
        const string selection = "future|legacy|card.v1";
        Assert.Equal(selection, QuotaResolver.CanonicalSelection(null, selection));
        Assert.Null(QuotaResolver.Resolve(null, selection));
    }

    [Theory]
    [InlineData("")]
    [InlineData("auto")]
    [InlineData("future")]
    [InlineData("|card")]
    [InlineData("future|")]
    [InlineData("   |card")]
    [InlineData("future|   ")]
    public void MalformedSelectionsNormalizeToAuto(string selection)
    {
        Assert.Equal(QuotaResolver.Auto, QuotaResolver.CanonicalSelection(Payload, selection));
    }

    [Fact]
    public void DuplicateCardIdKeepsFirstOccurrenceForAutoAndExplicit()
    {
        var agent = DuplicatePayload.Agents[0];
        Assert.Equal(
            new[] { "same.v1", "other.v1", "Session", "other-session.v1" },
            agent.UniqueCardWindows.Select(w => w.CardId));
        Assert.Equal(80, agent.UniqueCardWindows.First(w => w.CardId == "same.v1").RemainingPercent);

        Assert.Equal(80, QuotaResolver.Resolve(DuplicatePayload, "dupe|same.v1")!.Window.RemainingPercent);
        Assert.Equal("Session", QuotaResolver.Resolve(DuplicatePayload, "auto")!.Window.CardId);
    }

    [Fact]
    public void AutoSkipsErroredNonFiniteAndExcludedClients()
    {
        var payload = new AgentUsagePayload(
            "now",
            new[]
            {
                new AgentUsageSnapshot(
                    "nan", "fixture", "now",
                    new[] { new UsageWindow("NaN", 0, double.NaN, CardId: "nan.v1") }),
                new AgentUsageSnapshot(
                    "errored", "fixture", "now",
                    new[] { new UsageWindow("Error", 0, 1, CardId: "error.v1") },
                    Error: "401"),
                new AgentUsageSnapshot(
                    "healthy", "fixture", "now",
                    new[] { new UsageWindow("Healthy", 0, 50, CardId: "healthy.v1") }),
            });

        Assert.Equal("healthy", QuotaResolver.Resolve(payload, "auto")!.ClientId);
        Assert.Equal("healthy", QuotaResolver.Resolve(
            payload, "auto", new HashSet<string> { "nan", "errored" })!.ClientId);

        var visibleAfterHidingTightest = QuotaResolver.Resolve(
            Payload, "auto", new HashSet<string> { "claude" });
        Assert.Equal("codex", visibleAfterHidingTightest!.ClientId);
        Assert.Equal("weekly.v1", visibleAfterHidingTightest.Window.CardId);
    }

    [Fact]
    public void ExplicitSelectionIgnoresExclusionErrorAndNonFiniteRemaining()
    {
        var payload = new AgentUsagePayload(
            "now",
            new[]
            {
                new AgentUsageSnapshot(
                    "hidden", "fixture", "now",
                    new[] { new UsageWindow("Hidden", 0, 12, CardId: "hidden.v1") }),
                new AgentUsageSnapshot(
                    "errored", "fixture", "now",
                    new[] { new UsageWindow("Error", 0, double.PositiveInfinity, CardId: "error.v1") },
                    Error: "401"),
                new AgentUsageSnapshot(
                    "nan", "fixture", "now",
                    new[] { new UsageWindow("NaN", 0, double.NaN, CardId: "nan.v1") }),
            });
        var excluded = new HashSet<string> { "hidden", "errored", "nan" };

        Assert.Equal("hidden", QuotaResolver.Resolve(payload, "hidden|hidden.v1", excluded)!.ClientId);
        Assert.True(double.IsPositiveInfinity(
            QuotaResolver.Resolve(payload, "errored|error.v1", excluded)!.Window.RemainingPercent));
        Assert.True(double.IsNaN(
            QuotaResolver.Resolve(payload, "nan|nan.v1", excluded)!.Window.RemainingPercent));
    }

    [Fact]
    public void ExcludedAllCandidatesDetectsFullyHiddenAuto()
    {
        // Both resolvable clients hidden → auto would have resolved, now can't.
        Assert.True(QuotaResolver.ExcludedAllCandidates(
            Payload, "auto", new HashSet<string> { "claude", "codex" }));
        // codex survives → not fully excluded.
        Assert.False(QuotaResolver.ExcludedAllCandidates(
            Payload, "auto", new HashSet<string> { "claude" }));
        // Explicit selection ignores exclusion → never "all candidates hidden".
        Assert.False(QuotaResolver.ExcludedAllCandidates(
            Payload, "claude|session.v1", new HashSet<string> { "claude" }));
        // Unmatched explicit selections stay explicit too.
        Assert.False(QuotaResolver.ExcludedAllCandidates(
            Payload, "codex|stale", new HashSet<string> { "codex", "claude" }));
        // Empty exclusion and no payload → false.
        Assert.False(QuotaResolver.ExcludedAllCandidates(Payload, "auto", new HashSet<string>()));
        Assert.False(QuotaResolver.ExcludedAllCandidates(null, "auto", new HashSet<string> { "claude" }));
    }
}

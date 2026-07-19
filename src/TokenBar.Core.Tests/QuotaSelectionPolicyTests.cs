using TokenBar.Core;
using TokenBar.Interop;

namespace TokenBar.Core.Tests;

public class QuotaSelectionPolicyTests
{
    private static readonly AgentUsagePayload Payload = new(
        "now",
        new[]
        {
            new AgentUsageSnapshot(
                "codex",
                "fixture",
                "now",
                new[]
                {
                    new UsageWindow("Session", 20, 80, CardId: "session.v1"),
                    new UsageWindow("Weekly", 65, 35, CardId: "weekly.v1"),
                    new UsageWindow("Delimiter", 5, 95, CardId: "provider|window.v1"),
                }),
            new AgentUsageSnapshot(
                "claude",
                "fixture",
                "now",
                new[]
                {
                    new UsageWindow("Session", 88, 12, CardId: "session.v1"),
                }),
        });

    private static readonly AgentUsagePayload AmbiguousPayload = new(
        "now",
        new[]
        {
            new AgentUsageSnapshot(
                "dupe",
                "fixture",
                "now",
                new[]
                {
                    new UsageWindow("Ambiguous", 20, 80, CardId: "same.v1"),
                    new UsageWindow("Ambiguous", 65, 35, CardId: "other.v1"),
                }),
        });

    [Fact]
    public void EffectiveSelectionMigratesUniqueLabelAndPreservesCardIds()
    {
        Assert.Equal(
            "codex|weekly.v1",
            QuotaSelectionPolicy.EffectiveSelection(Payload, "codex|Weekly"));
        Assert.Equal(
            "codex|weekly.v1",
            QuotaSelectionPolicy.EffectiveSelection(Payload, "codex|weekly.v1"));
        Assert.Equal(
            "codex|provider|window.v1",
            QuotaSelectionPolicy.EffectiveSelection(Payload, "codex|provider|window.v1"));
    }

    [Fact]
    public void EffectiveSelectionKeepsStaleMissingAndAmbiguousExplicitSelections()
    {
        Assert.Equal(
            "codex|stale.v1",
            QuotaSelectionPolicy.EffectiveSelection(Payload, "codex|stale.v1"));
        Assert.Equal(
            "missing|weekly.v1",
            QuotaSelectionPolicy.EffectiveSelection(Payload, "missing|weekly.v1"));
        Assert.Equal(
            "dupe|Ambiguous",
            QuotaSelectionPolicy.EffectiveSelection(AmbiguousPayload, "dupe|Ambiguous"));
        Assert.Equal(
            "future|legacy|card.v1",
            QuotaSelectionPolicy.EffectiveSelection(null, "future|legacy|card.v1"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("auto")]
    [InlineData("codex")]
    [InlineData("|weekly.v1")]
    [InlineData("codex|")]
    public void EffectiveSelectionNormalizesMalformedAndEmptySelections(string persistedSelection)
    {
        Assert.Equal(
            QuotaResolver.Auto,
            QuotaSelectionPolicy.EffectiveSelection(Payload, persistedSelection));
    }

    [Fact]
    public void MigrationToPersistReturnsOnlyUniqueLabelMigration()
    {
        Assert.Equal(
            "codex|weekly.v1",
            QuotaSelectionPolicy.MigrationToPersist(Payload, "codex|Weekly"));

        Assert.Null(QuotaSelectionPolicy.MigrationToPersist(Payload, "codex|weekly.v1"));
        Assert.Null(QuotaSelectionPolicy.MigrationToPersist(Payload, "codex|stale.v1"));
        Assert.Null(QuotaSelectionPolicy.MigrationToPersist(Payload, "missing|weekly.v1"));
        Assert.Null(QuotaSelectionPolicy.MigrationToPersist(
            AmbiguousPayload,
            "dupe|Ambiguous"));
        Assert.Null(QuotaSelectionPolicy.MigrationToPersist(
            null,
            "future|legacy|card.v1"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("auto")]
    [InlineData("codex")]
    [InlineData("codex|")]
    public void MigrationToPersistDoesNotPersistAutoOrMalformedSelections(string persistedSelection)
    {
        Assert.Null(QuotaSelectionPolicy.MigrationToPersist(Payload, persistedSelection));
    }

    [Fact]
    public void ResolveUsesEffectiveSelectionAndAppliesAutoOnlyExclusion()
    {
        var auto = QuotaSelectionPolicy.Resolve(Payload, QuotaResolver.Auto);
        Assert.NotNull(auto);
        Assert.Equal("claude", auto!.ClientId);

        var hidden = QuotaSelectionPolicy.Resolve(
            Payload,
            QuotaResolver.Auto,
            new HashSet<string> { "claude" });
        Assert.NotNull(hidden);
        Assert.Equal("codex", hidden!.ClientId);
        Assert.Equal("weekly.v1", hidden.Window.CardId);

        var pinned = QuotaSelectionPolicy.Resolve(
            Payload,
            "claude|Session",
            new HashSet<string> { "claude" });
        Assert.NotNull(pinned);
        Assert.Equal("claude", pinned!.ClientId);
        Assert.Equal("session.v1", pinned.Window.CardId);

        Assert.Null(QuotaSelectionPolicy.Resolve(Payload, "codex|stale.v1"));
    }

    [Fact]
    public void AllHiddenAutoResolvesToNull()
    {
        Assert.Null(QuotaSelectionPolicy.Resolve(
            Payload,
            QuotaResolver.Auto,
            new HashSet<string> { "claude", "codex" }));
        Assert.True(QuotaResolver.ExcludedAllCandidates(
            Payload,
            QuotaResolver.Auto,
            new HashSet<string> { "claude", "codex" }));
    }

    [Fact]
    public void LastGoodRemainingRequiresTheSameEffectiveSelection()
    {
        Assert.Equal(
            12,
            QuotaSelectionPolicy.MatchingLastGoodRemaining(
                QuotaResolver.Auto, QuotaResolver.Auto, 12));
        Assert.Null(QuotaSelectionPolicy.MatchingLastGoodRemaining(
            "codex|weekly.v1", QuotaResolver.Auto, 12));
        Assert.Null(QuotaSelectionPolicy.MatchingLastGoodRemaining(
            QuotaResolver.Auto, null, 12));
    }

    [Fact]
    public void AutoThenMissingExplicitDoesNotReuseAutoValue()
    {
        var auto = QuotaSelectionPolicy.Resolve(Payload, QuotaResolver.Auto);
        Assert.Equal("claude", auto!.ClientId);
        Assert.Null(QuotaSelectionPolicy.Resolve(Payload, "codex|missing.v1"));
        Assert.Null(QuotaSelectionPolicy.MatchingLastGoodRemaining(
            "codex|missing.v1", QuotaResolver.Auto, auto.Window.RemainingPercent));
    }

    [Fact]
    public void ExplicitSelectionSwitchDoesNotReuseAnotherSourceAndSameSelectionRetainsItsValue()
    {
        var selected = QuotaSelectionPolicy.Resolve(Payload, "codex|weekly.v1");
        Assert.Equal(35, selected!.Window.RemainingPercent);
        Assert.Null(QuotaSelectionPolicy.Resolve(Payload, "codex|missing.v1"));
        Assert.Null(QuotaSelectionPolicy.MatchingLastGoodRemaining(
            "codex|missing.v1", "codex|weekly.v1", selected.Window.RemainingPercent));
        Assert.Equal(
            35,
            QuotaSelectionPolicy.MatchingLastGoodRemaining(
                "codex|weekly.v1", "codex|weekly.v1", selected.Window.RemainingPercent));
    }

    [Fact]
    public void PersistedRemainingNeedsMatchingSelectionKey()
    {
        Assert.Equal(
            35,
            QuotaSelectionPolicy.MatchingLastGoodRemaining(
                "codex|weekly.v1", "codex|weekly.v1", 35));
        Assert.Null(QuotaSelectionPolicy.MatchingLastGoodRemaining(
            "codex|weekly.v1", null, 35)); // legacy unkeyed scalar
        Assert.Null(QuotaSelectionPolicy.MatchingLastGoodRemaining(
            "codex|weekly.v1", "codex|session.v1", 35));
    }
}

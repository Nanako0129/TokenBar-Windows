using Xunit;

namespace TokenBar.Core.Tests;

public class ClientRegistryTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "tokenbar-tests", Guid.NewGuid().ToString("N"));

    private SettingsStore NewStore() => new(Path.Combine(_dir, "settings.json"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch
        {
        }
    }

    [Fact]
    public void KnownClientHasBrandStyle()
    {
        var style = ClientRegistry.Style("claude");
        Assert.Equal("Claude Code", style.DisplayName);
        Assert.Equal("#d97706", style.Color);
    }

    [Fact]
    public void UnknownClientTitleCasesWithGreyDisc()
    {
        var style = ClientRegistry.Style("mystery");
        Assert.Equal("Mystery", style.DisplayName);
        Assert.Equal("#6b7280", style.Color);
    }

    [Theory]
    [InlineData("claude", "Claude")]       // " Code" dropped
    [InlineData("codex", "Codex")]         // " CLI" dropped
    [InlineData("cursor", "Cursor")]       // " IDE" dropped
    [InlineData("amp", "Amp")]             // no suffix
    [InlineData("antigravity-cli", "Antigravity CLI")] // base collides with the IDE client
    public void ShortNameDropsFormFactorSafely(string id, string expected) =>
        Assert.Equal(expected, ClientRegistry.ShortName(id));

    // --- Tabs order & visibility delta ---

    [Fact]
    public void GrokBuildIsRegistered()
    {
        var style = ClientRegistry.Style("grok");
        Assert.Equal("Grok Build", style.DisplayName);
        Assert.Equal("#1f2937", style.Color);
    }

    [Fact]
    public void AllIdsAreSortedAndIncludeGrok()
    {
        var ids = ClientRegistry.AllIds;
        Assert.Contains("grok", ids);
        Assert.Equal(ids.OrderBy(x => x, StringComparer.Ordinal), ids);
    }

    [Theory]
    [InlineData("claude-code", "claude")]
    [InlineData("codex-cli", "codex")]
    [InlineData("gemini-cli", "gemini")]
    [InlineData("antigravity-cli", "antigravity-cli")] // NOT folded — distinct client
    [InlineData("amp", "amp")]
    public void CanonicalClientAliasesExplicitOnly(string id, string expected) =>
        Assert.Equal(expected, ClientRegistry.CanonicalClient(id));

    [Fact]
    public void ParseIdSetAndListTolerateEmptyAndBlanks()
    {
        Assert.Empty(ClientRegistry.ParseIdSet(""));
        Assert.Empty(ClientRegistry.ParseIdList(""));
        Assert.Equal(new HashSet<string> { "a", "b" }, ClientRegistry.ParseIdSet("a,,b"));
        Assert.Equal(["a", "b", "c"], ClientRegistry.ParseIdList("a,b,c"));
    }

    [Fact]
    public void QuotaExcludedIsUnionOfHiddenAndLimitsHidden()
    {
        var store = NewStore();
        store.SetString(ClientRegistry.TabHiddenKey, "claude,codex");
        store.SetString(ClientRegistry.LimitsHiddenKey, "codex,gemini");

        Assert.Equal(
            new HashSet<string> { "claude", "codex", "gemini" },
            ClientRegistry.QuotaExcludedClients(store));
    }

    [Fact]
    public void KnownLimitsClientsDedupesKeepingPresentThenQuotaOrder()
    {
        // present carries claude+codex; only codex has a known limit (quota),
        // claude is offered via placeholder; antigravity is quota-only.
        var known = ClientRegistry.KnownLimitsClients(
            present: ["claude", "codex", "mux"],
            quotaIds: ["codex", "antigravity"],
            placeholders: new HashSet<string> { "claude" });

        // mux has neither placeholder nor quota → dropped. codex appears once.
        Assert.Equal(["claude", "codex", "antigravity"], known);
    }

    [Fact]
    public void OrderedClientsSortsBySavedOrderAppendingUnknownStably()
    {
        var ordered = ClientRegistry.OrderedClients(
            ["gemini", "claude", "codex", "amp"], orderRaw: "codex,claude");
        // codex, claude by saved order; gemini, amp keep their incoming order.
        Assert.Equal(["codex", "claude", "gemini", "amp"], ordered);
    }

    [Fact]
    public void OrderedClientsWithEmptyOrderIsIdentity() =>
        Assert.Equal(["b", "a"], ClientRegistry.OrderedClients(["b", "a"], orderRaw: ""));

    [Fact]
    public void DisplayClientsFiltersHiddenThenOrders()
    {
        var display = ClientRegistry.DisplayClients(
            present: ["gemini", "claude", "codex"], hiddenRaw: "gemini", orderRaw: "codex,claude");
        Assert.Equal(["codex", "claude"], display);
    }

    [Fact]
    public void DisplayClientsStoreOverloadReadsBothKeys()
    {
        var store = NewStore();
        store.SetString(ClientRegistry.TabHiddenKey, "gemini");
        store.SetString(ClientRegistry.TabOrderKey, "codex,claude");

        Assert.Equal(
            ["codex", "claude"],
            ClientRegistry.DisplayClients(["gemini", "claude", "codex"], store));
    }

    [Fact]
    public void ResolveSelectionCanonicalizesPresentAndDefaultsToOverview()
    {
        var selection = ClientRegistry.ResolveSelection(
            present: ["claude-code", "claude", "codex-cli"],
            hiddenRaw: "",
            orderRaw: "codex,claude",
            activeTab: null);

        Assert.Equal(ClientRegistry.OverviewTab, selection.ActiveTab);
        Assert.Equal(["codex", "claude"], selection.DisplayClients);
        Assert.Equal(selection.DisplayClients, selection.SelectedClients);
    }

    [Fact]
    public void ResolveSelectionKeepsVisibleCanonicalActiveClient()
    {
        var selection = ClientRegistry.ResolveSelection(
            present: ["claude", "codex"],
            hiddenRaw: "",
            orderRaw: "",
            activeTab: "codex-cli");

        Assert.Equal("codex", selection.ActiveTab);
        Assert.Equal(["codex"], selection.SelectedClients);
    }

    [Fact]
    public void ResolveSelectionNormalizesHiddenOrMissingActiveClientToOverview()
    {
        var hidden = ClientRegistry.ResolveSelection(
            present: ["claude", "codex"],
            hiddenRaw: "codex",
            orderRaw: "",
            activeTab: "codex");
        var missing = ClientRegistry.ResolveSelection(
            present: ["claude", "codex"],
            hiddenRaw: "",
            orderRaw: "",
            activeTab: "gemini");

        Assert.Equal(ClientRegistry.OverviewTab, hidden.ActiveTab);
        Assert.Equal(["claude"], hidden.SelectedClients);
        Assert.Equal(ClientRegistry.OverviewTab, missing.ActiveTab);
        Assert.Equal(["claude", "codex"], missing.SelectedClients);
    }

    [Fact]
    public void ResolveSelectionKeepsAllHiddenAsEmptyOverview()
    {
        var selection = ClientRegistry.ResolveSelection(
            present: ["claude", "codex"],
            hiddenRaw: "claude,codex",
            orderRaw: "",
            activeTab: "codex");

        Assert.Equal(ClientRegistry.OverviewTab, selection.ActiveTab);
        Assert.Empty(selection.DisplayClients);
        Assert.Empty(selection.SelectedClients);
    }

    [Fact]
    public void ResolveSelectionReorderDoesNotChangeActiveMembership()
    {
        var selection = ClientRegistry.ResolveSelection(
            present: ["claude", "codex", "gemini"],
            hiddenRaw: "",
            orderRaw: "gemini,claude,codex",
            activeTab: "codex");

        Assert.Equal(["gemini", "claude", "codex"], selection.DisplayClients);
        Assert.Equal("codex", selection.ActiveTab);
        Assert.Equal(["codex"], selection.SelectedClients);
    }

    [Fact]
    public void ResolveSelectionStoreOverloadReadsOnlyTabKeys()
    {
        var store = NewStore();
        store.SetString(ClientRegistry.TabHiddenKey, "gemini");
        store.SetString(ClientRegistry.TabOrderKey, "codex,claude");
        store.SetString(ClientRegistry.ActiveTabKey, "codex");
        store.SetString(ClientRegistry.LimitsHiddenKey, "codex");

        var selection = ClientRegistry.ResolveSelection(
            ["gemini", "claude", "codex"], store);

        Assert.Equal(["codex", "claude"], selection.DisplayClients);
        Assert.Equal("codex", selection.ActiveTab);
        Assert.Equal(["codex"], selection.SelectedClients);
    }

    [Theory]
    [InlineData("a", "c", "b,c,a,d")] // drag down: insert after target
    [InlineData("d", "b", "a,d,b,c")] // drag up: insert before target
    [InlineData("a", "a", "a,b,c,d")] // same id: unchanged
    public void ReorderIsDirectionAware(string from, string to, string expected)
    {
        var result = ClientRegistry.Reorder(["a", "b", "c", "d"], from, to);
        Assert.Equal(expected.Split(','), result);
    }

    [Fact]
    public void MergeReorderPreservesOffscreenPositions()
    {
        // full universe a..e; visible subset only a,c,e (b,d hidden off-screen).
        // Drag a after e within the visible subset → a,c,e becomes c,e,a.
        var merged = ClientRegistry.MergeReorder(
            full: ["a", "b", "c", "d", "e"], visible: ["a", "c", "e"], from: "a", to: "e");
        // b stays at slot 1, d stays at slot 3; visible slots refill c,e,a.
        Assert.Equal(["c", "b", "e", "d", "a"], merged);
    }

    [Fact]
    public void MigrateLegacyOrderKeyFoldsOnceThenIsIdempotent()
    {
        var store = NewStore();
        store.SetString("tokenbar.limits.order", "codex,claude");

        ClientRegistry.MigrateLegacyOrderKey(store);
        Assert.Equal("codex,claude", store.GetString(ClientRegistry.TabOrderKey));

        // Idempotent: a second run must not overwrite a user-changed new value.
        store.SetString(ClientRegistry.TabOrderKey, "claude");
        ClientRegistry.MigrateLegacyOrderKey(store);
        Assert.Equal("claude", store.GetString(ClientRegistry.TabOrderKey));
    }
}

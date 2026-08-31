namespace TokenBar.Core;

// Client (agent) display registry, ported from TokenBarCore/ClientRegistry.swift
// (originally the Tauri app's src/lib/clients.ts). Carries the display name +
// brand disc color used by chart legends and model rows; icons come later.

public sealed record ClientStyle(string Id, string DisplayName, string Color);

public sealed record ClientSelection(
    IReadOnlyList<string> DisplayClients,
    IReadOnlyList<string> SelectedClients,
    string ActiveTab);

public static class ClientRegistry
{
    private static readonly Dictionary<string, (string DisplayName, string Color)> Entries = new()
    {
        ["claude"] = ("Claude Code", "#d97706"),
        ["openclaw"] = ("OpenClaw", "#dc2626"),
        ["gemini"] = ("Gemini CLI", "#60a5fa"),
        ["opencode"] = ("OpenCode", "#1f2937"),
        ["codex"] = ("Codex CLI", "#9ca3af"),
        ["copilot"] = ("Copilot CLI", "#1f2937"),
        ["cursor"] = ("Cursor IDE", "#0ea5e9"),
        ["amp"] = ("Amp", "#10b981"),
        ["droid"] = ("Droid", "#22c55e"),
        ["hermes"] = ("Hermes", "#a78bfa"),
        ["pi"] = ("Pi", "#f472b6"),
        ["kimi"] = ("Kimi CLI", "#fbbf24"),
        // Both take the same neutral grey the unregistered fallback uses, so
        // "junie" already rendered correctly by accident. Registering them is
        // still not a no-op: AllIds is the canonical universe demo fixtures
        // draw from, and RegisteredNames guards ShortName from collapsing one
        // client's name onto another's.
        ["junie"] = ("Junie", "#6b7280"),
        ["opencodereview"] = ("OpenCodeReview", "#6b7280"),
        ["qwen"] = ("Qwen CLI", "#7c3aed"),
        ["roocode"] = ("Roo Code", "#ef4444"),
        ["kilocode"] = ("KiloCode", "#f97316"),
        ["kilo"] = ("Kilo CLI", "#f59e0b"),
        ["mux"] = ("Mux", "#06b6d4"),
        ["crush"] = ("Crush", "#ec4899"),
        ["synthetic"] = ("Synthetic", "#64748b"),
        ["goose"] = ("Goose", "#14b8a6"),
        ["codebuff"] = ("Codebuff", "#8b5cf6"),
        ["antigravity"] = ("Antigravity", "#3b82f6"),
        ["zed"] = ("Zed", "#084fff"),
        ["kiro"] = ("Kiro", "#9046ff"),
        ["trae"] = ("Trae", "#ef4444"),
        ["warp"] = ("Warp", "#01a4ff"),
        ["cline"] = ("Cline", "#5b8def"),
        ["antigravity-cli"] = ("Antigravity CLI", "#6366f1"),
        ["jcode"] = ("Jcode", "#84cc16"),
        ["micode"] = ("MiMo Code", "#fb923c"),
        ["gjc"] = ("gjc", "#e11d48"),
        ["grok"] = ("Grok Build", "#1f2937"),
    };

    /// <summary>Every registered client id, sorted. Demo fixtures use this
    /// canonical universe so every usage surface renders the same client
    /// set.</summary>
    public static IReadOnlyList<string> AllIds =>
        [.. Entries.Keys.OrderBy(k => k, StringComparer.Ordinal)];

    private static readonly HashSet<string> RegisteredNames =
        [.. Entries.Values.Select(e => e.DisplayName)];

    public static ClientStyle Style(string id)
    {
        if (Entries.TryGetValue(id, out var entry))
        {
            return new ClientStyle(id, entry.DisplayName, entry.Color);
        }

        // Fallback: title-case the id, neutral grey disc.
        var displayName = id.Length == 0 ? id : char.ToUpperInvariant(id[0]) + id[1..];
        return new ClientStyle(id, displayName, "#6b7280");
    }

    /// <summary>Display name with the trailing form-factor word dropped, as
    /// the chart legend does ("Claude Code" → "Claude").</summary>
    public static string ShortName(string id)
    {
        var name = Style(id).DisplayName;
        foreach (var suffix in new[] { " CLI", " Code", " IDE" })
        {
            if (!name.EndsWith(suffix, StringComparison.Ordinal))
            {
                continue;
            }

            var baseName = name[..^suffix.Length];
            // Don't collapse onto a base that is itself another client's full
            // name — e.g. "Antigravity CLI" must stay distinct from the IDE
            // client "Antigravity".
            if (!RegisteredNames.Contains(baseName))
            {
                return baseName;
            }
        }

        return name;
    }

    // MARK: - Tab bar display order & visibility (new for tabs improvement)

    public const string TabOrderKey = "tokenbar.tabs.order";
    public const string TabHiddenKey = "tokenbar.tabs.hidden";
    public const string ActiveTabKey = "tokenbar.activeTab";
    public const string OverviewTab = "overview";

    /// <summary>Independent from <see cref="TabHiddenKey"/>: hides a client's
    /// Agent-limits quota card only, leaving its top tab (and cost/token/model
    /// data) visible. Added for accounts whose plan has no OAuth quota (e.g.
    /// Claude Console).</summary>
    public const string LimitsHiddenKey = "tokenbar.limits.hidden";

    /// <summary>Canonicalize a live-tail client id to the registry's short id.
    /// The usage trace reports raw ids (claude-code, codex-cli, gemini-cli)
    /// while the hidden set, quota snapshots, and registry all key on short ids
    /// (claude, codex, gemini). EXPLICIT aliases only — no generic -cli suffix
    /// rule: antigravity-cli is a registered client id distinct from the
    /// antigravity IDE, so stripping -cli would conflate the two (hiding one
    /// would mis-target the other).</summary>
    public static string CanonicalClient(string id) => id switch
    {
        "claude-code" => "claude",
        "codex-cli" => "codex",
        "gemini-cli" => "gemini",
        _ => id,
    };

    /// <summary>The client whose quota snapshot a client's usage is served by,
    /// where the two identities differ. <c>antigravity-cli</c> is a registered
    /// client in its own right — process identity, tab, icon and preferences all
    /// stay distinct — but it draws on the <c>antigravity</c> subscription and the
    /// quota views have always folded it that way. Anything reasoning about which
    /// subscription a client's tokens consume has to fold it too, or it will
    /// conclude the CLI owns no subscription at all.</summary>
    public static string QuotaOwner(string id) => id == "antigravity-cli" ? "antigravity" : id;

    /// <summary>The registered client behind an opencode subscription label.
    /// opencode reports which providers it is authed against as display labels
    /// rather than ids (<c>agent_usage.rs</c> builds them in
    /// <c>subscription_label</c>), so a consumer that needs the id must map them
    /// back here. These are the four labels <c>subscription_label</c> renames
    /// outright; everything else it emits is a capitalized provider key, which
    /// cannot be resolved from this table alone. Kept as data rather than a
    /// switch so a caller can tell a rename from a passthrough — three of the
    /// four lowercase to their own id, so comparing the result against
    /// <c>label.ToLowerInvariant()</c> cannot make that distinction.</summary>
    public static readonly IReadOnlyDictionary<string, string> SubscriptionLabelAliases =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Codex"] = "codex",
            ["Claude"] = "claude",
            ["Copilot"] = "copilot",
            ["Gemini"] = "antigravity",
        };

    public static string ClientIdForSubscriptionLabel(string label) =>
        SubscriptionLabelAliases.TryGetValue(label, out var alias) ? alias : label.ToLowerInvariant();

    /// <summary>Parses the comma-separated id form persisted by the tab
    /// order/hidden defaults into a set, tolerating an empty string. Single
    /// source of the CSV split so callers all agree on the shape.</summary>
    public static IReadOnlySet<string> ParseIdSet(string raw) =>
        new HashSet<string>(raw.Split(',', StringSplitOptions.RemoveEmptyEntries));

    /// <summary>Ordered variant of <see cref="ParseIdSet"/> — keeps the saved
    /// sequence for callers that need positions (reorder/order sorting), not
    /// just membership.</summary>
    public static IReadOnlyList<string> ParseIdList(string raw) =>
        raw.Split(',', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>The set of client ids the user has hidden from the top tabs (and
    /// now also from Agent limits cards).</summary>
    public static IReadOnlySet<string> HiddenClients(SettingsStore store) =>
        ParseIdSet(store.GetString(TabHiddenKey) ?? "");

    /// <summary>The set of client ids whose Agent-limits card the user has
    /// hidden, independent of top-tab visibility.</summary>
    public static IReadOnlySet<string> HiddenLimitsClients(SettingsStore store) =>
        ParseIdSet(store.GetString(LimitsHiddenKey) ?? "");

    /// <summary>Clients excluded from the menu-bar quota AUTO pick: tab-hidden ∪
    /// limits-hidden. A client hidden from either surface must not drive the tray
    /// quota % (an explicit tray selection is honored separately).</summary>
    public static IReadOnlySet<string> QuotaExcludedClients(SettingsStore store)
    {
        var excluded = new HashSet<string>(HiddenClients(store));
        excluded.UnionWith(HiddenLimitsClients(store));
        return excluded;
    }

    /// <summary>The superset of client ids that can show a row in the multi-agent
    /// Agent-limits card: <paramref name="present"/> clients that carry a known
    /// limit (a placeholder row or a live quota snapshot), unioned with every
    /// client that has a quota snapshot right now. Some agents (e.g. Antigravity)
    /// report OAuth quota with no local session logs, so they are absent from
    /// <paramref name="present"/> yet must still be offered a management row.
    /// <paramref name="quotaIds"/> is the ordered list of snapshot client ids;
    /// <paramref name="placeholders"/> the ids rendered with a placeholder row
    /// even without a snapshot.</summary>
    public static IReadOnlyList<string> KnownLimitsClients(
        IReadOnlyList<string> present, IReadOnlyList<string> quotaIds, IReadOnlySet<string> placeholders)
    {
        var quotaSet = new HashSet<string>(quotaIds);
        bool Known(string id) => placeholders.Contains(id) || quotaSet.Contains(id);
        var seen = new HashSet<string>();
        return present.Where(Known).Concat(quotaIds).Where(id => seen.Add(id)).ToList();
    }

    /// <summary>Sorts <paramref name="ids"/> by the user's saved tab order
    /// (<see cref="TabOrderKey"/>), appending ids not yet in the saved order at
    /// the end in their incoming order.</summary>
    public static IReadOnlyList<string> OrderedClients(IReadOnlyList<string> ids, SettingsStore store) =>
        OrderedClients(ids, store.GetString(TabOrderKey) ?? "");

    /// <summary>Overload taking the saved order string directly, so a reactive
    /// caller re-sorts the instant the order changes without re-reading the
    /// store.</summary>
    public static IReadOnlyList<string> OrderedClients(IReadOnlyList<string> ids, string orderRaw)
    {
        var order = ParseIdList(orderRaw);
        if (order.Count == 0)
        {
            return ids;
        }

        // First-occurrence position of each ordered id (matches Swift
        // firstIndex). A stable OrderBy preserves the incoming order among ids
        // sharing a position (unordered ids all map to int.MaxValue), which
        // reproduces Swift's explicit original-index tiebreak.
        var position = new Dictionary<string, int>();
        for (var i = 0; i < order.Count; i++)
        {
            if (!position.ContainsKey(order[i]))
            {
                position[order[i]] = i;
            }
        }

        return ids
            .OrderBy(id => position.TryGetValue(id, out var p) ? p : int.MaxValue)
            .ToList();
    }

    /// <summary>Returns the subset of <paramref name="present"/> clients to show
    /// in the top tab bar, filtered by hidden list and sorted according to the
    /// user's saved order. Clients not yet in the saved order are appended at the
    /// end (so newly discovered agents become visible without breaking existing
    /// custom order).</summary>
    public static IReadOnlyList<string> DisplayClients(IReadOnlyList<string> present, SettingsStore store)
    {
        var hidden = HiddenClients(store);
        return OrderedClients(present.Where(id => !hidden.Contains(id)).ToList(), store);
    }

    /// <summary>Overload taking the observed hidden/order raw strings, so a
    /// reactive caller re-renders the instant the user toggles a tab or reorders
    /// instead of waiting for the next poller tick to re-read the store.</summary>
    public static IReadOnlyList<string> DisplayClients(
        IReadOnlyList<string> present, string hiddenRaw, string orderRaw)
    {
        var hidden = ParseIdSet(hiddenRaw);
        return OrderedClients(present.Where(id => !hidden.Contains(id)).ToList(), orderRaw);
    }

    public static ClientSelection ResolveSelection(
        IReadOnlyList<string> present, string hiddenRaw, string orderRaw, string? activeTab)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var canonicalPresent = present
            .Select(CanonicalClient)
            .Where(seen.Add)
            .ToList();
        var display = DisplayClients(canonicalPresent, hiddenRaw, orderRaw);
        var requested = string.IsNullOrWhiteSpace(activeTab)
            ? OverviewTab
            : CanonicalClient(activeTab.Trim());
        var normalized = requested != OverviewTab
            && display.Contains(requested, StringComparer.Ordinal)
                ? requested
                : OverviewTab;
        IReadOnlyList<string> selected = normalized == OverviewTab ? display : [normalized];
        return new ClientSelection(display, selected, normalized);
    }

    public static ClientSelection ResolveSelection(IReadOnlyList<string> present, SettingsStore store) =>
        ResolveSelection(
            present,
            store.GetString(TabHiddenKey) ?? "",
            store.GetString(TabOrderKey) ?? "",
            store.GetString(ActiveTabKey));

    /// <summary>Direction-aware reorder helper (drag down inserts after, up
    /// before). Mirrors the logic used in AgentLimitsCard.</summary>
    public static IReadOnlyList<string> Reorder(IReadOnlyList<string> list, string from, string to)
    {
        var fromI = FirstIndex(list, from);
        var toI = FirstIndex(list, to);
        if (fromI < 0 || toI < 0 || fromI == toI)
        {
            return list;
        }

        var reordered = list.Where(id => id != from).ToList();
        var anchor = reordered.IndexOf(to);
        reordered.Insert(fromI < toI ? anchor + 1 : anchor, from);
        return reordered;
    }

    /// <summary>Reorder a <paramref name="visible"/> subset while preserving the
    /// positions of every id in <paramref name="full"/> that isn't part of that
    /// subset. The drag operates on the on-screen subset, yet the saved order key
    /// drives the whole tab universe: writing only the reordered visible sequence
    /// would silently drop every off-screen id. This recomputes the visible
    /// sequence, then rebuilds the full order by refilling the visible slots in
    /// their new order and leaving non-visible ids exactly where they were.
    /// Visible ids absent from <paramref name="full"/> are appended at the end
    /// (the existing "newly discovered agent" semantics).</summary>
    public static IReadOnlyList<string> MergeReorder(
        IReadOnlyList<string> full, IReadOnlyList<string> visible, string from, string to)
    {
        var newVisible = Reorder(visible, from, to);
        var visibleSet = new HashSet<string>(visible);
        var queue = new Queue<string>(newVisible);
        var merged = new List<string>();
        foreach (var id in full)
        {
            if (visibleSet.Contains(id))
            {
                // Refill this visible slot with the next id from the reordered
                // sequence. queue starts as a permutation of visible, so it has
                // at least as many ids as there are visible slots in full.
                if (queue.Count > 0)
                {
                    merged.Add(queue.Dequeue());
                }
            }
            else
            {
                merged.Add(id);
            }
        }

        // Visible ids that weren't already positioned in full land at the end.
        merged.AddRange(queue);
        return merged;
    }

    /// <summary>One-time migration: the Agent-limits drag order used to persist
    /// under "tokenbar.limits.order". It now shares <see cref="TabOrderKey"/>
    /// with the client tab bar, so fold an existing legacy value across once —
    /// otherwise upgrading users would silently lose their saved card
    /// arrangement. Idempotent: only fires when the new key is unset and a
    /// non-empty legacy value exists.</summary>
    public static void MigrateLegacyOrderKey(SettingsStore store)
    {
        const string legacyKey = "tokenbar.limits.order";
        if (store.GetString(TabOrderKey) is not null)
        {
            return;
        }

        var legacy = store.GetString(legacyKey);
        if (string.IsNullOrEmpty(legacy))
        {
            return;
        }

        store.SetString(TabOrderKey, legacy);
    }

    private static int FirstIndex(IReadOnlyList<string> list, string value)
    {
        for (var i = 0; i < list.Count; i++)
        {
            if (list[i] == value)
            {
                return i;
            }
        }

        return -1;
    }
}

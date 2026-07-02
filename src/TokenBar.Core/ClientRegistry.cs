namespace TokenBar.Core;

// Client (agent) display registry, ported from TokenBarCore/ClientRegistry.swift
// (originally the Tauri app's src/lib/clients.ts). Carries the display name +
// brand disc color used by chart legends and model rows; icons come later.

public sealed record ClientStyle(string Id, string DisplayName, string Color);

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
    };

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
}

using TokenBar.Interop;

namespace TokenBar.Core;

public sealed record QuotaPick(string ClientId, UsageWindow Window);

/// <summary>
/// Picks which quota window the tray displays (port of
/// TokenBarCore/QuotaResolver.swift). The selection string is "auto" (the
/// tightest window — lowest remaining percent — across every agent) or
/// "&lt;clientId&gt;|&lt;windowLabel&gt;" for an explicit pick.
/// </summary>
public static class QuotaResolver
{
    public const string Auto = "auto";

    public static string Selection(string clientId, string label) => $"{clientId}|{label}";

    /// <summary><paramref name="excluding"/> is the set of client ids to skip in
    /// AUTO mode only (the user's tab-hidden ∪ limits-hidden clients) — so the
    /// menu-bar quota can't surface a client the popover hides. An EXPLICIT
    /// <c>clientId|window</c> selection is always honored, even for an excluded
    /// client (the user deliberately picked it as the tray source). Null/empty
    /// set = pre-hide behavior, byte-identical.</summary>
    public static QuotaPick? Resolve(
        AgentUsagePayload? payload, string selection, IReadOnlySet<string>? excluding = null)
    {
        if (payload is null)
        {
            return null;
        }

        if (selection.Length == 0 || selection == Auto)
        {
            QuotaPick? best = null;
            foreach (var agent in payload.Agents.Where(
                a => a.Error is null && !(excluding?.Contains(a.ClientId) ?? false)))
            {
                foreach (var window in agent.Windows.Where(w => double.IsFinite(w.RemainingPercent)))
                {
                    if (best is null || window.RemainingPercent < best.Window.RemainingPercent)
                    {
                        best = new QuotaPick(agent.ClientId, window);
                    }
                }
            }

            return best;
        }

        // Empty-segment check mirrors Swift split's omittingEmptySubsequences:
        // "claude|" / "|label" resolve to nothing there and must here too.
        var parts = selection.Split('|', 2);
        if (parts.Length != 2 || parts[0].Length == 0 || parts[1].Length == 0)
        {
            return null;
        }

        // Same health filter as the auto path: an errored agent, or a window
        // with a non-finite RemainingPercent, is not a usable source even when
        // the user pinned it explicitly — otherwise the tray surfaces a stale
        // or NaN window that auto mode would have skipped.
        var picked = payload.Agents.FirstOrDefault(a => a.ClientId == parts[0] && a.Error is null);
        var pickedWindow = picked?.Windows.FirstOrDefault(
            w => w.Label == parts[1] && double.IsFinite(w.RemainingPercent));
        return pickedWindow is null ? null : new QuotaPick(picked!.ClientId, pickedWindow);
    }

    /// <summary>True when <see cref="Resolve"/> returned null ONLY because the
    /// exclusion removed every otherwise-resolvable auto candidate (there IS a
    /// healthy window, but all of them belong to excluded clients). Lets a caller
    /// distinguish "all candidates hidden" from "no payload / fetch failed / no
    /// healthy window": in the former it must suppress a stale cache fallback
    /// (the hidden client's last reading) rather than keep showing it. Only
    /// meaningful for the auto/empty selection — an explicit pick ignores the
    /// exclusion, so this returns false for it (and for an empty exclusion or no
    /// payload).</summary>
    public static bool ExcludedAllCandidates(
        AgentUsagePayload? payload, string selection, IReadOnlySet<string> excluding)
    {
        if (!(selection.Length == 0 || selection == Auto) || excluding.Count == 0)
        {
            return false;
        }

        return Resolve(payload, selection) is not null
            && Resolve(payload, selection, excluding) is null;
    }
}

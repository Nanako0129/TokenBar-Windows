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

    public static QuotaPick? Resolve(AgentUsagePayload? payload, string selection)
    {
        if (payload is null)
        {
            return null;
        }

        if (selection.Length == 0 || selection == Auto)
        {
            QuotaPick? best = null;
            foreach (var agent in payload.Agents.Where(a => a.Error is null))
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

        var parts = selection.Split('|', 2);
        if (parts.Length != 2)
        {
            return null;
        }

        var picked = payload.Agents.FirstOrDefault(a => a.ClientId == parts[0]);
        var pickedWindow = picked?.Windows.FirstOrDefault(w => w.Label == parts[1]);
        return pickedWindow is null ? null : new QuotaPick(picked!.ClientId, pickedWindow);
    }
}

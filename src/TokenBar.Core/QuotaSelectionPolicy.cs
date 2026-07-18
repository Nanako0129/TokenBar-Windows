using TokenBar.Interop;

namespace TokenBar.Core;

public static class QuotaSelectionPolicy
{
    public static string EffectiveSelection(
        AgentUsagePayload? payload,
        string persistedSelection) =>
        QuotaResolver.CanonicalSelection(payload, persistedSelection);

    public static string? MigrationToPersist(
        AgentUsagePayload? payload,
        string persistedSelection)
    {
        var canonical = EffectiveSelection(payload, persistedSelection);
        return canonical == QuotaResolver.Auto || canonical == persistedSelection
            ? null
            : canonical;
    }

    public static QuotaPick? Resolve(
        AgentUsagePayload? payload,
        string persistedSelection,
        IReadOnlySet<string>? excluding = null)
    {
        var selection = EffectiveSelection(payload, persistedSelection);
        return QuotaResolver.Resolve(payload, selection, excluding);
    }
}

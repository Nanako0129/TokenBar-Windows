using TokenBar.Interop;

namespace TokenBar.Core;

public static class TraceCollapse
{
    /// <summary>
    /// Collapse (client, agent, model) buckets to one row per client, for the
    /// trace card's compact view (port of TraceBucket.collapseByClient in
    /// TokenBarCore/UsageTrace.swift). Agent/model strings join sorted;
    /// "unknown" models drop out when a client has named ones too. Rows sort
    /// by tokens descending.
    /// </summary>
    /// <summary>Sum of live per-minute rates over <paramref name="buckets"/>,
    /// excluding <paramref name="hidden"/> clients (port of TraceBucket.totalRate
    /// in UsageTrace.swift; used to derive the menu-bar rate with hidden clients
    /// dropped, issue #35). Summing the 600s trace rows' rates equals the FFI
    /// rate_in_window(600) for the surviving clients, since every row's rate
    /// shares the same window divisor.
    ///
    /// Rows carry raw live-tail ids (claude-code); <paramref name="hidden"/>
    /// holds canonical short ids (claude), so each row is normalized via
    /// <see cref="ClientRegistry.CanonicalClient"/> before the membership test —
    /// otherwise hiding a client would leave its live rows in the rate.</summary>
    public static double TotalRate(IEnumerable<TraceBucket> buckets, IReadOnlySet<string> hidden) =>
        buckets.Sum(b => hidden.Contains(ClientRegistry.CanonicalClient(b.Client)) ? 0 : b.TokensPerMin);

    /// <summary>Keep only the selected canonical clients before callers collapse
    /// or cap rows. Live buckets use raw ids such as claude-code, so normalize
    /// both membership and the returned row id in one pass.</summary>
    public static IReadOnlyList<TraceBucket> FilterByClients(
        IEnumerable<TraceBucket> buckets, IReadOnlySet<string> selected) =>
        buckets
            .Select(b => b with { Client = ClientRegistry.CanonicalClient(b.Client) })
            .Where(b => selected.Contains(b.Client))
            .ToList();

    public static IReadOnlyList<TraceBucket> CollapseByClient(IEnumerable<TraceBucket> buckets)
    {
        var groups = new Dictionary<string, Slot>();
        var order = new List<string>();
        foreach (var bucket in buckets)
        {
            if (!groups.TryGetValue(bucket.Client, out var slot))
            {
                slot = new Slot();
                groups[bucket.Client] = slot;
                order.Add(bucket.Client);
            }

            slot.Tokens = slot.Tokens.SaturatingAdd(bucket.Tokens);
            slot.Messages += bucket.Messages;
            slot.TokensPerMin += bucket.TokensPerMin;
            slot.Agents.Add(bucket.Agent);
            slot.Models.Add(bucket.Model);
        }

        return order
            .Select(client =>
            {
                var slot = groups[client];
                var models = slot.Models.Order(StringComparer.Ordinal).ToList();
                if (models.Count > 1)
                {
                    models.RemoveAll(m => m == "unknown");
                }

                return new TraceBucket(
                    client,
                    string.Join(", ", slot.Agents.Order(StringComparer.Ordinal)),
                    string.Join(", ", models),
                    slot.Tokens,
                    slot.Messages,
                    slot.TokensPerMin);
            })
            .OrderByDescending(b => b.Tokens)
            .ToList();
    }

    private sealed class Slot
    {
        public long Tokens;
        public int Messages;
        public double TokensPerMin;
        public readonly HashSet<string> Agents = [];
        public readonly HashSet<string> Models = [];
    }
}

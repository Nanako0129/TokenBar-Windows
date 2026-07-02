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

            slot.Tokens += bucket.Tokens;
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

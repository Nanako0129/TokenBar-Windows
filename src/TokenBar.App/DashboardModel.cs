using Microsoft.UI.Dispatching;
using TokenBar.Interop;

namespace TokenBar.App;

/// <summary>
/// Polling engine, mirroring the macOS DashboardModel cadence: while the
/// flyout is open, graph + quota refresh every 60s and the live trace/rate every
/// 10s; everything runs off-thread (the FFI blocks) and lands back on the UI
/// thread via the DispatcherQueue. A process-lifetime snapshot survives
/// flyout close/reopen so the UI never flashes a loading state.
/// </summary>
public sealed class DashboardModel
{
    private static Snapshot? _lastSnapshot; // process lifetime, like macOS

    private readonly DispatcherQueue _dispatcher;
    private DispatcherQueueTimer? _slowTimer;
    private DispatcherQueueTimer? _fastTimer;
    private bool _slowInFlight;
    private bool _fastInFlight;
    private bool _quotaInFlight;
    // Latest quota fetch, kept outside the snapshot so a fetch that lands
    // before the first graph parse isn't lost — the first snapshot seeds
    // from it (quota is usually done in ~1s, the cold parse in seconds).
    private volatile AgentUsagePayload? _latestQuota;

    public DashboardModel(DispatcherQueue dispatcher)
    {
        _dispatcher = dispatcher;
        if (_lastSnapshot is not null)
        {
            Current = _lastSnapshot;
        }
    }

    public Snapshot? Current { get; private set; }

    public event Action? Updated;

    public sealed record Snapshot(
        UsagePayload Graph,
        ModelReport? Models,
        AgentUsagePayload? Quota,
        double TokensPerMin,
        IReadOnlyList<TraceBucket> Trace,
        DateTimeOffset FetchedAt)
    {
        // Lazily-loaded lenses (macOS ensureData parity): fetched on first
        // visit, then refreshed by the slow lane like everything else.
        public HourlyReport? Hourly { get; init; }
        public AgentsReport? Agents { get; init; }
    }

    private bool _hourlyWanted;
    private bool _agentsWanted;

    /// <summary>Marks a lazy lens as needed and fetches it once; later slow
    /// refreshes keep it current.</summary>
    public void EnsureHourly()
    {
        if (!_hourlyWanted)
        {
            _hourlyWanted = true;
            _ = Task.Run(() => FetchLazy(hourly: true, agents: false));
        }
    }

    public void EnsureAgents()
    {
        if (!_agentsWanted)
        {
            _agentsWanted = true;
            _ = Task.Run(() => FetchLazy(hourly: false, agents: true));
        }
    }

    private void FetchLazy(bool hourly, bool agents)
    {
        using var boost = ProcessPower.Boost();
        try
        {
            HourlyReport? h = hourly ? TbCore.HourlyReport() : null;
            AgentsReport? a = agents ? TbCore.AgentsReport() : null;
            Publish(s => s with
            {
                Hourly = h ?? s.Hourly,
                Agents = a ?? s.Agents,
            }, graph: null);
        }
        catch (Exception ex)
        {
            DevLog.Write($"lazy lens fetch failed: {ex.Message}");
        }
    }

    /// <summary>Begin polling (flyout opened). Fires an immediate refresh of
    /// both cadences, then 60s / 10s timers.</summary>
    public void Start()
    {
        if (_slowTimer is null)
        {
            _slowTimer = _dispatcher.CreateTimer();
            _slowTimer.Interval = TimeSpan.FromSeconds(60);
            _slowTimer.Tick += (_, _) =>
            {
                RefreshSlow();
                RefreshQuota();
            };
            _fastTimer = _dispatcher.CreateTimer();
            _fastTimer.Interval = TimeSpan.FromSeconds(10);
            _fastTimer.Tick += (_, _) => RefreshFast();
        }

        _slowTimer.Start();
        _fastTimer!.Start();
        RefreshSlow();
        RefreshQuota();
        RefreshFast();
    }

    /// <summary>Stop polling (flyout hidden). State stays for instant reopen.</summary>
    public void Stop()
    {
        _slowTimer?.Stop();
        _fastTimer?.Stop();
    }

    private void RefreshSlow()
    {
        if (_slowInFlight)
        {
            return;
        }

        _slowInFlight = true;
        _ = Task.Run(() =>
        {
            try
            {
                // macOS parity: the model report runs concurrently with the
                // graph parse (the engine shares one pass), and neither the
                // network nor the lazy lenses gate the first paint.
                var sw = System.Diagnostics.Stopwatch.StartNew();
                UsagePayload? graph;
                ModelReport? models;
                using (ProcessPower.Boost()) // EcoQoS off while parsing
                {
                    var modelsTask = Task.Run(() =>
                        TryFetch(() => TbCore.ModelReport(), "modelReport"));
                    graph = TryFetch(() => TbCore.Graph(), "graph");
                    models = modelsTask.Result; // joined before the boost lifts
                }

                DevLog.Write($"slow lane: graph+models={sw.ElapsedMilliseconds}ms");
                if (graph is not null)
                {
                    var seeded = graph;
                    Publish(s => s with
                    {
                        Graph = seeded ?? s.Graph,
                        Models = models ?? s.Models,
                    }, graph);
                }
                else if (models is not null)
                {
                    var m = models;
                    Publish(s => s with { Models = m }, graph: null);
                }

                if (_hourlyWanted || _agentsWanted)
                {
                    HourlyReport? hourly;
                    AgentsReport? agentsReport;
                    using (ProcessPower.Boost())
                    {
                        hourly = _hourlyWanted
                            ? TryFetch(() => TbCore.HourlyReport(), "hourly") : null;
                        agentsReport = _agentsWanted
                            ? TryFetch(() => TbCore.AgentsReport(), "agents") : null;
                    }

                    if (hourly is not null || agentsReport is not null)
                    {
                        Publish(s => s with
                        {
                            Hourly = hourly ?? s.Hourly,
                            Agents = agentsReport ?? s.Agents,
                        }, graph: null);
                    }
                }
            }
            finally
            {
                _slowInFlight = false;
            }
        });
    }

    /// <summary>The OAuth quota lane, macOS pollAgentUsage parity: fully
    /// independent of the parse lane so a slow provider (the fetch can hang
    /// for ~30s per agent) never delays the first paint, never holds the
    /// EcoQoS boost through a network wait, and never blocks the next
    /// graph tick behind <c>_slowInFlight</c>.</summary>
    private void RefreshQuota()
    {
        if (_quotaInFlight)
        {
            return;
        }

        _quotaInFlight = true;
        _ = Task.Run(() =>
        {
            try
            {
                var quota = TryFetch(() => TbCore.AgentUsage(), "agentUsage");
                if (quota is not null)
                {
                    _latestQuota = quota;
                    Publish(s => s with { Quota = quota }, graph: null);
                }
            }
            finally
            {
                _quotaInFlight = false;
            }
        });
    }

    private static T? TryFetch<T>(Func<T> fetch, string label) where T : class
    {
        try
        {
            return fetch();
        }
        catch (Exception ex)
        {
            DevLog.Write($"{label} refresh failed: {ex.Message}");
            return null;
        }
    }

    private void RefreshFast()
    {
        if (_fastInFlight)
        {
            return;
        }

        _fastInFlight = true;
        _ = Task.Run(() =>
        {
            using var boost = ProcessPower.Boost(); // live-tail parse
            try
            {
                var rate = TbCore.TokensPerMin();
                var trace = TbCore.UsageTrace(600);
                Publish(s => s with { TokensPerMin = rate, Trace = trace }, graph: null);
            }
            catch (Exception ex)
            {
                DevLog.Write($"trace refresh failed: {ex.Message}");
            }
            finally
            {
                _fastInFlight = false;
            }
        });
    }

    /// <summary>Merge a partial update into the snapshot on the UI thread.
    /// The first slow refresh creates the snapshot; fast-lane results before
    /// that are parked on a placeholder graph-less state (dropped — the slow
    /// lane lands within a second on a warm cache).</summary>
    private void Publish(Func<Snapshot, Snapshot> update, UsagePayload? graph)
    {
        _ = _dispatcher.TryEnqueue(() =>
        {
            var baseline = Current;
            if (baseline is null)
            {
                if (graph is null)
                {
                    return; // fast lane cannot seed the snapshot
                }

                baseline = new Snapshot(graph, null, _latestQuota, 0, [], DateTimeOffset.Now);
                DevLog.Write("first snapshot ready"); // cold-parse timing anchor
            }

            Current = update(baseline) with { FetchedAt = DateTimeOffset.Now };
            _lastSnapshot = Current;
            Updated?.Invoke();
        });
    }
}

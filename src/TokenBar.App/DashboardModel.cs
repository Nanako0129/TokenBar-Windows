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
            _slowTimer.Tick += (_, _) => RefreshSlow();
            _fastTimer = _dispatcher.CreateTimer();
            _fastTimer.Interval = TimeSpan.FromSeconds(10);
            _fastTimer.Tick += (_, _) => RefreshFast();
        }

        _slowTimer.Start();
        _fastTimer!.Start();
        RefreshSlow();
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
                var graph = TbCore.Graph();
                ModelReport? models = null;
                try
                {
                    models = TbCore.ModelReport();
                }
                catch (Exception ex)
                {
                    DevLog.Write($"modelReport failed: {ex.Message}");
                }

                AgentUsagePayload? quota = null;
                try
                {
                    quota = TbCore.AgentUsage(); // network-bound; per-agent errors ride inside
                }
                catch (Exception ex)
                {
                    DevLog.Write($"agentUsage failed: {ex.Message}");
                }

                HourlyReport? hourly = _hourlyWanted
                    ? TryFetch(() => TbCore.HourlyReport(), "hourly") : null;
                AgentsReport? agentsReport = _agentsWanted
                    ? TryFetch(() => TbCore.AgentsReport(), "agents") : null;
                Publish(s => s with
                {
                    Graph = graph,
                    Models = models ?? s.Models,
                    Quota = quota ?? s.Quota,
                    Hourly = hourly ?? s.Hourly,
                    Agents = agentsReport ?? s.Agents,
                }, graph);
            }
            catch (Exception ex)
            {
                DevLog.Write($"graph refresh failed: {ex.Message}");
            }
            finally
            {
                _slowInFlight = false;
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

                baseline = new Snapshot(graph, null, null, 0, [], DateTimeOffset.Now);
                DevLog.Write("first snapshot ready"); // cold-parse timing anchor
            }

            Current = update(baseline) with { FetchedAt = DateTimeOffset.Now };
            _lastSnapshot = Current;
            Updated?.Invoke();
        });
    }
}

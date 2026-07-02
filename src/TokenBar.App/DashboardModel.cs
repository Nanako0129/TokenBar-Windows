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
        DateTimeOffset FetchedAt);

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

                Publish(s => s with
                {
                    Graph = graph,
                    Models = models ?? s.Models,
                    Quota = quota ?? s.Quota,
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
            }

            Current = update(baseline) with { FetchedAt = DateTimeOffset.Now };
            _lastSnapshot = Current;
            Updated?.Invoke();
        });
    }
}

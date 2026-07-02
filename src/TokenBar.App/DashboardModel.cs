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
    // 0/1 via Interlocked: the year-switch re-run enters from a lane thread,
    // so this gate races the UI-thread timer tick (unlike the other flags,
    // whose callers all live on the dispatcher).
    private int _slowInFlight;
    private bool _fastInFlight;
    private bool _quotaInFlight;
    // Latest quota fetch, kept outside the snapshot so a fetch that lands
    // before the first graph parse isn't lost — the first snapshot seeds
    // from it (quota is usually done in ~1s, the cold parse in seconds).
    private volatile AgentUsagePayload? _latestQuota;

    // Year filter for every lens (macOS DashboardModel.year); null = all
    // time. Fetch lanes capture it per pass and drop slices fetched for a
    // stale filter, so a late old-year payload can never overwrite the new
    // selection's data. Writes go through _yearGate: the vanish-clear runs
    // on a lane thread and must not clobber a pick the user made mid-flight.
    private readonly object _yearGate = new();
    private volatile string? _year = ResolveYear();
    private volatile List<string> _knownYears = [];

    public string? Year => _year;

    /// <summary>Union of the payload's years across loads — a year-filtered
    /// payload only reports the selected year, so the picker remembers the
    /// rest (macOS knownYears).</summary>
    public IReadOnlyList<string> KnownYears => _knownYears;

    /// <summary>The `--year=` debug flag wins over the persisted selection
    /// (and is itself never persisted), macOS resolveYear parity.</summary>
    private static string? ResolveYear()
    {
        var flag = Environment.GetCommandLineArgs()
            .FirstOrDefault(a => a.StartsWith("--year=", StringComparison.Ordinal));
        return flag is not null
            ? (flag["--year=".Length..] is { Length: > 0 } y ? y : null)
            : AppSettings.Store.GetString("tokenbar.dashboard.year");
    }

    /// <summary>Switch the year filter and re-fetch every lens for the new
    /// slice (served from the engine's per-year cache when fresh).</summary>
    public void SetYear(string? year)
    {
        lock (_yearGate)
        {
            if (year == _year)
            {
                return;
            }

            _year = year;
            if (year is null)
            {
                AppSettings.Store.Remove("tokenbar.dashboard.year");
            }
            else
            {
                AppSettings.Store.SetString("tokenbar.dashboard.year", year);
            }
        }

        _refreshing = true; // macOS setYear spins the header control too
        Updated?.Invoke();
        RefreshSlow(); // an in-flight lane re-runs itself when the year flips
    }

    // Manual-refresh state: _forceRequested makes the next slow pass bypass
    // the engine cache (tb_refresh_graph); _refreshing drives the header
    // spinner and holds until the pass that serves the request completes.
    private int _forceRequested;
    private volatile bool _refreshing;

    public bool Refreshing => _refreshing;

    /// <summary>Manual refresh (the header button; macOS refresh()): forces
    /// a full log re-read. No-op while one is already running.</summary>
    public void RefreshForce()
    {
        if (_refreshing)
        {
            return;
        }

        _refreshing = true;
        Volatile.Write(ref _forceRequested, 1);
        Updated?.Invoke();
        RefreshSlow();
    }

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
        var year = _year;
        try
        {
            HourlyReport? h = hourly ? TbCore.HourlyReport(year) : null;
            AgentsReport? a = agents ? TbCore.AgentsReport(year) : null;
            if (_year != year)
            {
                return; // stale slice; the slow lane re-fetches for the new year
            }

            Publish(s => s with
            {
                Hourly = hourly ? h : s.Hourly,
                Agents = agents ? a : s.Agents,
            }, graph: null, stillValid: () => _year == year);
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
        if (Interlocked.Exchange(ref _slowInFlight, 1) == 1)
        {
            return;
        }

        _ = Task.Run(() =>
        {
            var year = _year; // one slice per pass; a mid-flight switch re-runs below
            var force = Interlocked.Exchange(ref _forceRequested, 0) == 1;
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
                        TryFetch(() => TbCore.ModelReport(year), "modelReport"));
                    graph = TryFetch(() => force
                        ? TbCore.RefreshGraph(year) : TbCore.Graph(year), "graph");
                    models = modelsTask.Result; // joined before the boost lifts
                }

                DevLog.Write($"slow lane: graph+models={sw.ElapsedMilliseconds}ms " +
                    $"year={year ?? "all"}{(force ? " force" : "")}");
                if (graph is not null && year is not null
                    && !graph.Years.Any(y => y.Year == year))
                {
                    // macOS apply() parity: the selected year's logs vanished —
                    // clear the filter instead of stranding the dashboard on an
                    // empty slice. Only if it is still THIS pass's year, though:
                    // a pick the user made mid-flight must survive. The finally
                    // below re-runs the lane either way.
                    lock (_yearGate)
                    {
                        if (_year == year)
                        {
                            _year = null;
                            AppSettings.Store.Remove("tokenbar.dashboard.year");
                        }
                    }

                    // No publish happens on this path, so nudge the UI to
                    // resync the year picker with the cleared filter.
                    _ = _dispatcher.TryEnqueue(() => Updated?.Invoke());
                    return;
                }

                if (_year != year)
                {
                    return; // stale slice; the finally re-runs with the new year
                }

                if (graph is not null)
                {
                    RememberYears(graph);
                    var seeded = graph;
                    Publish(s => s with
                    {
                        Graph = seeded ?? s.Graph,
                        Models = models ?? s.Models,
                    }, graph, stillValid: () => _year == year);
                }
                else if (models is not null)
                {
                    var m = models;
                    Publish(s => s with { Models = m }, graph: null,
                        stillValid: () => _year == year);
                }

                var wantHourly = _hourlyWanted;
                var wantAgents = _agentsWanted;
                if (wantHourly || wantAgents)
                {
                    HourlyReport? hourly;
                    AgentsReport? agentsReport;
                    using (ProcessPower.Boost())
                    {
                        hourly = wantHourly
                            ? TryFetch(() => TbCore.HourlyReport(year), "hourly") : null;
                        agentsReport = wantAgents
                            ? TryFetch(() => TbCore.AgentsReport(year), "agents") : null;
                    }

                    if (_year == year)
                    {
                        // Fetched lenses land as-is (macOS reload assigns the
                        // result directly): a failed refetch blanks the lens
                        // rather than leaving another year's rows under the
                        // new filter.
                        Publish(s => s with
                        {
                            Hourly = wantHourly ? hourly : s.Hourly,
                            Agents = wantAgents ? agentsReport : s.Agents,
                        }, graph: null, stillValid: () => _year == year);
                    }
                }
            }
            finally
            {
                Volatile.Write(ref _slowInFlight, 0);
                if (_year != year || Volatile.Read(ref _forceRequested) == 1)
                {
                    RefreshSlow(); // stale year or a force queued mid-pass
                }
                else if (_refreshing)
                {
                    // This pass served the manual refresh / year switch; the
                    // publishes carry no spinner state, so nudge the UI off.
                    _refreshing = false;
                    _ = _dispatcher.TryEnqueue(() => Updated?.Invoke());
                }
            }
        });
    }

    private void RememberYears(UsagePayload graph)
    {
        var merged = _knownYears.Union(graph.Years.Select(y => y.Year))
            .OrderByDescending(y => y, StringComparer.Ordinal).ToList();
        if (!merged.SequenceEqual(_knownYears))
        {
            _knownYears = merged;
        }
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
    private void Publish(
        Func<Snapshot, Snapshot> update, UsagePayload? graph,
        Func<bool>? stillValid = null)
    {
        _ = _dispatcher.TryEnqueue(() =>
        {
            // Re-checked on the dispatcher: the year can flip between a
            // lane's own staleness check and this callback running.
            if (stillValid?.Invoke() == false)
            {
                return;
            }

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

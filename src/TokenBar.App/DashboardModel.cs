using Microsoft.UI.Dispatching;
using TokenBar.Core;
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
    private readonly GraphRequestCoordinator _graphCoordinator;
    private readonly GraphConsumerState _graphState = new();
    private DispatcherQueueTimer? _slowTimer;
    private DispatcherQueueTimer? _fastTimer;
    // 0/1 via Interlocked throughout: every gate is entered on the UI thread
    // (timer tick) but reset in a background Task.Run's finally, so a plain
    // bool's reset isn't guaranteed visible back on the dispatcher — on ARM64
    // (a CI/test target) that can wedge a lane permanently "in flight".
    private int _slowInFlight;
    private int _fastInFlight;
    private int _quotaInFlight;
    // Latest quota fetch, kept outside the snapshot so a fetch that lands
    // before the first graph parse isn't lost — the first snapshot seeds
    // from it (quota is usually done in ~1s, the cold parse in seconds).
    private volatile AgentUsagePayload? _latestQuota;

    /// <summary>Whether a quota fetch has ever finished, held outside the
    /// snapshot for the same reason <see cref="_latestQuota"/> is.
    ///
    /// <para>Publish drops a graph-less update while Current is null, so on a
    /// cold start where the quota request fails before the graph lane seeds
    /// the first snapshot, publishing the completion is not enough — it is
    /// discarded, and the snapshot the graph then builds carries the record's
    /// default of false. The loading line therefore survived the very failure
    /// it was fixed not to survive, until the next 60-second tick. Seeded into
    /// the baseline below.</para></summary>
    private volatile bool _quotaAttempted;

    // Year filter for every lens (macOS DashboardModel.year); null = all
    // time. Fetch lanes capture it per pass and drop slices fetched for a
    // stale filter, so a late old-year payload can never overwrite the new
    // selection's data. Writes go through _yearGate: the vanish-clear runs
    // on a lane thread and must not clobber a pick the user made mid-flight.
    private readonly object _yearGate = new();
    private volatile string? _year = ResolveYear();
    private volatile List<string> _knownYears = [];
    private volatile UsagePayload? _allTimeGraph;
    private GraphRequestId? _snapshotRequestId;
    private GraphRequestId? _pendingModelRequestId;
    private ModelReport? _pendingModel;

    // Hourly/Agents are already aggregated across clients, so their FFI scans
    // capture one immutable selection generation. Graph/Models stay all-client.
    private readonly object _selectionGate = new();
    private string[] _selectedClients = [];
    private HashSet<string> _selectedSet = new(StringComparer.Ordinal);
    private HashSet<string> _hiddenClients = new(StringComparer.Ordinal);
    private long _selectionGeneration;
    private bool _selectionInitialized;
    private int _lazyInFlight;
    private int _lazyPending;
    private GraphRequestId? _lazyGraphRequestId;

    public string? Year => _year;

    /// <summary>Union of the payload's years across loads — a year-filtered
    /// payload only reports the selected year, so the picker remembers the
    /// rest (macOS knownYears). An all-time graph can additionally remove years
    /// whose activity belongs only to hidden clients.</summary>
    public IReadOnlyList<string> KnownYears
    {
        get
        {
            var years = _knownYears;
            if (_allTimeGraph is not { } graph)
            {
                return years;
            }

            HashSet<string> hidden;
            lock (_selectionGate)
            {
                hidden = new HashSet<string>(_hiddenClients, StringComparer.Ordinal);
            }

            var visible = UsageStatsVisibility.YearsWithVisibleActivity(
                graph.Contributions, hidden);
            return years.Where(visible.Contains).ToList();
        }
    }

    /// <summary>Install the canonical Dashboard selection for pre-aggregated
    /// lazy reports. Reordering updates the immutable capture order but does not
    /// invalidate membership or trigger another scan.</summary>
    public bool SetClientSelection(IReadOnlyList<string> clients)
    {
        var canonical = clients
            .Select(ClientRegistry.CanonicalClient)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var hidden = ClientRegistry.HiddenClients(AppSettings.Store)
            .Select(ClientRegistry.CanonicalClient)
            .ToHashSet(StringComparer.Ordinal);
        bool changed;
        lock (_selectionGate)
        {
            changed = !_selectionInitialized || !_selectedSet.SetEquals(canonical);
            _selectionInitialized = true;
            _selectedClients = canonical;
            _selectedSet = canonical.ToHashSet(StringComparer.Ordinal);
            _hiddenClients = hidden;
            if (changed)
            {
                _selectionGeneration++;
            }
        }

        if (changed)
        {
            ClearLazyReports(emptySelection: canonical.Length == 0);
            RequestLazyRefresh();
        }

        var year = _year;
        if (year is not null && Current is { } current
            && !UsageStatsVisibility.HasVisibleActivity(current.Graph.Contributions, hidden))
        {
            _ = _dispatcher.TryEnqueue(() =>
            {
                HashSet<string> latestHidden;
                lock (_selectionGate)
                {
                    latestHidden = new HashSet<string>(_hiddenClients, StringComparer.Ordinal);
                }

                if (_year == year && Current is { } latest
                    && !UsageStatsVisibility.HasVisibleActivity(
                        latest.Graph.Contributions, latestHidden))
                {
                    SetYear(null);
                }
            });
        }

        return changed;
    }

    /// <summary>The `--year=` debug flag wins over the persisted selection
    /// (and is itself never persisted), macOS resolveYear parity.</summary>
    private static string? ResolveYear()
    {
        var flag = Environment.GetCommandLineArgs()
            .FirstOrDefault(a => a.StartsWith("--year=", StringComparison.Ordinal));
        var year = flag is not null
            ? flag["--year=".Length..]
            : AppSettings.Store.GetString("tokenbar.dashboard.year");
        return GraphQuery.Normalize(year).Year;
    }

    /// <summary>Switch the year filter and re-fetch every lens for the new
    /// slice (served from the engine's per-year cache when fresh).</summary>
    public void SetYear(string? year)
    {
        year = GraphQuery.Normalize(year).Year;
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

        // Drop the old year's lazy lenses now. The next exact graph request
        // publication schedules their replacement once for that request.
        ClearLazyReports(emptySelection: SelectionIsEmpty());

        _refreshing = true; // macOS setYear spins the header control too
        RefreshSlow(); // an in-flight lane re-runs itself when the year flips
    }

    // Manual-refresh state: _forceRequested makes the next slow pass bypass
    // the engine cache (tb_refresh_graph); _refreshing drives the header
    // spinner and holds until the pass that serves the request completes.
    private int _forceRequested;
    private volatile bool _refreshing;
    private volatile bool _polling;

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

    public DashboardModel(
        DispatcherQueue dispatcher,
        GraphRequestCoordinator graphCoordinator)
    {
        _dispatcher = dispatcher;
        _graphCoordinator = graphCoordinator;
        _graphCoordinator.Started += OnGraphStarted;
        _graphCoordinator.Published += OnGraphPublished;
        _graphCoordinator.Completed += OnGraphCompleted;
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
        DateTimeOffset FetchedAt,
        bool CostAuthoritative = false)
    {
        // Lazily-loaded lenses (macOS ensureData parity): fetched on first
        // visit, then refreshed by the slow lane like everything else.
        public HourlyReport? Hourly { get; init; }
        public AgentsReport? Agents { get; init; }

        /// <summary>Whether a quota fetch has finished, whatever it returned.
        ///
        /// <para>A fact about the request, not about the result, and that is
        /// the whole reason it exists. Inferring it from <c>Quota is not
        /// null</c> conflates "still asking" with "asked and it failed":
        /// RefreshQuota publishes nothing when the fetch throws, so a payload
        /// that never arrives is indistinguishable from one that has not
        /// arrived yet, and the Overview sat on "Checking agent limits…"
        /// forever. macOS passes the same signal in as `usageAttempted` rather
        /// than deriving it; this port had let it degrade into a
        /// derivation.</para></summary>
        public bool QuotaAttempted { get; init; }

        /// <summary>The persisted quota curves, for the Quota lens's two
        /// cards. A third lazy lens, read straight from the store — it does not
        /// depend on the client selection the way Hourly/Agents do.</summary>
        public IReadOnlyList<QuotaHistorySeries>? QuotaHistory { get; init; }

        /// <summary>Whether the history READ has finished, whatever it
        /// returned — the same fact-about-the-request as
        /// <see cref="QuotaAttempted"/>, and a separate one: that flag reports
        /// the agent-usage lane, this one the store lane, and a lens that read
        /// the wrong lane's progress would announce "nothing recorded yet"
        /// while its own fetch was still in flight.
        ///
        /// <para><c>QuotaHistory is not null</c> cannot answer it: a read that
        /// returned no series and a read that has not happened both leave it
        /// null.</para></summary>
        public bool QuotaHistoryAttempted { get; init; }

        /// <summary>The per-message rows behind the Quota lens's ≈ lines
        /// (5d-1's export). A fourth lazy lens, fetched only once
        /// <see cref="QuotaHistory"/> has told this lane how far back to ask —
        /// the export is expensive enough (macOS's own probe: 67s over 15
        /// days) that it must never be called with an unbounded range.</summary>
        public Interop.WindowUsage? WindowUsage { get; init; }

        /// <summary>Whether the window-usage READ has finished, whatever it
        /// returned — the same fact-about-the-request as
        /// <see cref="QuotaHistoryAttempted"/>, and a separate one: a fetch
        /// that found no window to bound itself against still has to report
        /// completion, or the equivalence lines wait forever for an answer
        /// that was never going to come.</summary>
        public bool WindowUsageAttempted { get; init; }

        /// <summary>
        /// The three facts <see cref="WindowUsageAttempted"/> and
        /// <see cref="WindowUsage"/> together carry, collapsed here so a call
        /// site reads one signal instead of re-deriving it: not attempted yet,
        /// attempted and the fetch threw (this lane's <c>TryFetch</c> swallows
        /// the exception and publishes completion anyway, but with no data —
        /// <see cref="WindowUsage"/> stays whatever it was before, which is
        /// null on a first fetch that fails), or attempted and it landed.
        /// <para>
        /// A caller that instead read <c>WindowUsageAttempted</c> alone and
        /// defaulted <c>WindowUsage?.Messages</c> to <c>[]</c> could not tell
        /// a completed empty scan from a scan that never ran — the exact
        /// ambiguity <see cref="WindowEquivalence.FetchOutcome"/> exists to
        /// remove.
        /// </para>
        /// </summary>
        public WindowEquivalence.FetchOutcome WindowUsageOutcome =>
            !WindowUsageAttempted ? WindowEquivalence.FetchOutcome.NotAttempted
            : WindowUsage is null ? WindowEquivalence.FetchOutcome.Failed
            : WindowEquivalence.FetchOutcome.Succeeded;
    }

    private volatile bool _hourlyWanted;
    private volatile bool _agentsWanted;
    private volatile bool _quotaHistoryWanted;
    private volatile bool _windowUsageWanted;

    /// <summary>Marks a lazy lens as needed and fetches it once; later slow
    /// refreshes keep it current.</summary>
    public void EnsureHourly()
    {
        _hourlyWanted = true;
        RequestLazyRefresh();
    }

    public void EnsureAgents()
    {
        _agentsWanted = true;
        RequestLazyRefresh();
    }

    public void EnsureQuotaHistory()
    {
        _quotaHistoryWanted = true;
        RequestLazyRefresh();
    }

    public void EnsureWindowUsage()
    {
        _windowUsageWanted = true;
        RequestLazyRefresh();
    }

    private void RequestLazyRefresh()
    {
        if (!_hourlyWanted && !_agentsWanted && !_quotaHistoryWanted && !_windowUsageWanted)
        {
            return;
        }

        Volatile.Write(ref _lazyPending, 1);
        if (Interlocked.CompareExchange(ref _lazyInFlight, 1, 0) != 0)
        {
            return;
        }

        _ = Task.Run(() =>
        {
            try
            {
                while (Interlocked.Exchange(ref _lazyPending, 0) == 1)
                {
                    FetchLazyWanted();
                }
            }
            finally
            {
                Volatile.Write(ref _lazyInFlight, 0);
                if (Volatile.Read(ref _lazyPending) == 1)
                {
                    RequestLazyRefresh();
                }
            }
        });
    }

    private void FetchLazyWanted()
    {
        var hourly = _hourlyWanted;
        var agents = _agentsWanted;
        var quotaHistory = _quotaHistoryWanted;
        var windowUsage = _windowUsageWanted;
        string[] clients;
        long generation;
        lock (_selectionGate)
        {
            if (!_selectionInitialized)
            {
                return;
            }

            clients = _selectedClients;
            generation = _selectionGeneration;
        }

        var year = _year;
        HourlyReport? hourlyReport;
        AgentsReport? agentsReport;
        if (clients.Length == 0)
        {
            hourlyReport = hourly ? new HourlyReport([], 0) : null;
            agentsReport = agents ? new AgentsReport([], 0, 0) : null;
        }
        else
        {
            using var boost = ProcessPower.Boost();
            hourlyReport = hourly
                ? TryFetch(() => TbCore.HourlyReport(year, clients), "hourly") : null;
            agentsReport = agents
                ? TryFetch(() => TbCore.AgentsReport(year, clients), "agents") : null;
        }

        // Selection-independent: the store is keyed by provider/account/window,
        // not by the client tabs, so this is read outside the branch above and
        // survives the staleness check below.
        var history = quotaHistory
            ? TryFetch(TbCore.QuotaHistory, "quotaHistory")
            : null;

        // Bounded by whichever history is freshest: this pass's own read when
        // one was taken, the last-published one otherwise. Never unbounded —
        // 5d-1's export scans the whole local corpus when its cache is cold,
        // which macOS's own probe measured at 67s over 15 days.
        Interop.WindowUsage? usage = null;
        if (windowUsage)
        {
            var forBound = history ?? Current?.QuotaHistory ?? [];
            if (forBound.Count == 0)
            {
                usage = new Interop.WindowUsage([], 0, 0);
            }
            else
            {
                // The scan this export can trigger is CPU-bound, not I/O-bound
                // like the quota-history read above — the same reason the
                // hourly/agents fetch above it boosts.
                using var boost = ProcessPower.Boost();
                usage = TryFetch(
                    () => TbCore.WindowUsage(
                        QuotaEquivalenceFold.BoundFromMs(forBound, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()),
                        DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()),
                    "windowUsage");
            }
        }

        if (!SelectionStillValid(year, generation))
        {
            RequestLazyRefresh();
            return;
        }

        Publish(s => s with
        {
            Hourly = hourly ? hourlyReport : s.Hourly,
            Agents = agents ? agentsReport : s.Agents,
            // A failed read keeps whatever was already there rather than
            // replacing a complete set with an empty one — the "absent because
            // we could not ask" mistake, arriving as a partial result.
            QuotaHistory = history ?? s.QuotaHistory,
            // A failed read publishes completion with nothing to show, for the
            // same reason the agent-usage lane does: a lens that reads null as
            // "not yet" would wait forever for an answer that already came back.
            QuotaHistoryAttempted = quotaHistory || s.QuotaHistoryAttempted,
            // Same failed-read and same completion rules as QuotaHistory,
            // immediately above, and for the same two reasons.
            WindowUsage = usage ?? s.WindowUsage,
            WindowUsageAttempted = windowUsage || s.WindowUsageAttempted,
        }, graph: null, stillValid: () => SelectionStillValid(year, generation));
    }

    private bool SelectionStillValid(string? year, long generation)
    {
        lock (_selectionGate)
        {
            return _year == year && _selectionGeneration == generation;
        }
    }

    private bool SelectionIsEmpty()
    {
        lock (_selectionGate)
        {
            return _selectionInitialized && _selectedClients.Length == 0;
        }
    }

    private void ClearLazyReports(bool emptySelection)
    {
        if (Current is not { } current)
        {
            return;
        }

        // QuotaHistory is deliberately untouched: it is not filtered by the
        // client selection, so clearing it here would drop a valid read and
        // send the Quota lens back to its loading line for no reason.
        Current = current with
        {
            Hourly = emptySelection ? new HourlyReport([], 0) : null,
            Agents = emptySelection ? new AgentsReport([], 0, 0) : null,
        };
        _lastSnapshot = Current;
    }

    /// <summary>Begin polling (flyout opened). Fires an immediate refresh of
    /// both cadences, then 60s / 10s timers.</summary>
    public void Start()
    {
        _polling = true;
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
        AttachGraph(_year);
        RefreshQuota();
        RefreshFast();
    }

    /// <summary>Stop polling (flyout hidden). State stays for instant reopen.</summary>
    public void Stop()
    {
        _polling = false;
        _slowTimer?.Stop();
        _fastTimer?.Stop();
    }

    public void Dispose()
    {
        Stop();
        _graphState.Dispose();
        _pendingModelRequestId = null;
        _pendingModel = null;
        _graphCoordinator.Started -= OnGraphStarted;
        _graphCoordinator.Published -= OnGraphPublished;
        _graphCoordinator.Completed -= OnGraphCompleted;
    }

    private void RefreshSlow()
    {
        if (!_polling)
        {
            return;
        }

        // The coordinator coalesces duplicate same-query requests; do not use
        // one global lane gate here because a year switch must supersede an
        // older query immediately.
        Volatile.Write(ref _slowInFlight, 1);
        var year = _year;
        var force = Interlocked.Exchange(ref _forceRequested, 0) == 1;
        _graphCoordinator.Request(
            year,
            force,
            OnGraphRequestStarted,
            OnGraphPublished);
    }

    private void AttachGraph(string? year)
    {
        Interlocked.Exchange(ref _slowInFlight, 1);
        var attachment = _graphCoordinator.Attach(year, OnGraphRequestStarted);
        if (!attachment.InFlight)
        {
            Volatile.Write(ref _slowInFlight, 0);
        }

        if (attachment.Latest is { } latest)
        {
            OnGraphPublished(latest);
        }

        if (GraphResumePolicy.ShouldRefreshAfterAttach(attachment))
        {
            RefreshSlow();
        }
    }

    private void OnGraphStarted(GraphRequestId requestId)
    {
        if (!_polling || _year != requestId.Query.Year)
        {
            return;
        }

        OnGraphRequestStarted(requestId);
    }

    private void OnGraphRequestStarted(GraphRequestId requestId)
    {
        if (!_polling || _year != requestId.Query.Year
            || !_graphState.Begin(requestId))
        {
            return;
        }

        if (_dispatcher.HasThreadAccess)
        {
            ApplyGraphRequestStart(requestId);
        }
        else
        {
            _ = _dispatcher.TryEnqueue(() => ApplyGraphRequestStart(requestId));
        }
    }

    private void ApplyGraphRequestStart(GraphRequestId requestId)
    {
        if (!_graphState.IsCurrent(requestId))
        {
            return;
        }

        var previous = _snapshotRequestId;
        _snapshotRequestId = requestId;
        if (_pendingModelRequestId is { } pending
            && pending != requestId)
        {
            _pendingModelRequestId = null;
            _pendingModel = null;
        }

        if (previous is { } old && old.Query != requestId.Query)
        {
            // Year payloads cannot reconstruct hidden-only year visibility.
            // Keep the retained all-time graph while clearing the rendered query.
            Current = null;
            _lastSnapshot = null;
            Updated?.Invoke();
            return;
        }

        if (Current is { } current)
        {
            // Keep same-query topology as a visual baseline, but revoke
            // the old model identity and every cost-bearing surface.
            Current = current with
            {
                Models = null,
                CostAuthoritative = false,
                FetchedAt = DateTimeOffset.Now,
            };
            _lastSnapshot = Current;
            Updated?.Invoke();
        }

    }

    private void OnGraphPublished(GraphPublication publication)
    {
        if (!_graphState.TryAcceptGraph(
                publication.RequestId, publication.Payload, publication.Stage))
        {
            return;
        }

        if (_graphState.TryBeginModel(publication.RequestId))
        {
            StartModelReport(publication.RequestId);
        }

        _ = _dispatcher.TryEnqueue(() =>
        {
            // Dispatcher-delayed callbacks repeat the exact state check before
            // touching Current, so an old query/generation cannot repaint it.
            if (!_graphState.TryAcceptGraph(
                    publication.RequestId, publication.Payload, publication.Stage)
                || _year != publication.RequestId.Query.Year)
            {
                return;
            }

            // A request can begin on a worker completion path. If its
            // dispatcher begin callback is still queued behind this
            // publication, apply that revoke first so an old same-query
            // snapshot cannot receive the new generation's graph/model data.
            if (_snapshotRequestId != publication.RequestId)
            {
                ApplyGraphRequestStart(publication.RequestId);
            }

            if (!_graphState.IsCurrent(publication.RequestId)
                || _snapshotRequestId != publication.RequestId)
            {
                return;
            }

            var year = publication.RequestId.Query.Year;
            if (GraphYearPolicy.ShouldClearToAllTime(_year, publication))
            {
                SetYear(null);
                return;
            }

            RememberYears(publication.Payload);
            if (year is null)
            {
                _allTimeGraph = publication.Payload;
            }

            var baseline = Current;
            if (baseline is null)
            {
                baseline = CreateBaseline(publication.Payload);
                DevLog.Write("first snapshot ready");
            }

            var pendingModel = _pendingModelRequestId == publication.RequestId
                ? _pendingModel
                : null;
            if (pendingModel is not null)
            {
                _pendingModelRequestId = null;
                _pendingModel = null;
            }

            Current = baseline with
            {
                Graph = publication.Payload,
                Models = pendingModel ?? baseline.Models,
                CostAuthoritative = _graphState.CostAuthoritative,
                FetchedAt = DateTimeOffset.Now,
            };
            _lastSnapshot = Current;
            Updated?.Invoke();
            if (GraphLazyRefreshPolicy.ShouldRequest(
                    _lazyGraphRequestId, publication.RequestId))
            {
                _lazyGraphRequestId = publication.RequestId;
                RequestLazyRefresh();
            }
        });
    }

    private void StartModelReport(GraphRequestId requestId)
    {
        _ = Task.Run(() =>
        {
            var report = TryFetch(
                () => TbCore.ModelReport(requestId.Query.Year), "modelReport");
            if (report is null || !_graphState.TryAcceptModel(requestId, report))
            {
                return;
            }

            _ = _dispatcher.TryEnqueue(() =>
            {
                // Repeat the exact generation guard at the UI boundary.
                if (!_graphState.TryAcceptModel(requestId, report)
                    || _year != requestId.Query.Year)
                {
                    return;
                }

                // ModelReport may beat the graph publication callback to the
                // dispatcher. Retain it for this exact generation instead of
                // dropping a valid once-only result when Current is still null
                // or still carries the prior same-query request.
                if (Current is null || _snapshotRequestId != requestId)
                {
                    _pendingModelRequestId = requestId;
                    _pendingModel = report;
                    return;
                }

                Current = Current with
                {
                    Models = report,
                    FetchedAt = DateTimeOffset.Now,
                };
                _lastSnapshot = Current;
                Updated?.Invoke();
            });
        });
    }

    private void OnGraphCompleted(GraphRequestCompletion completion)
    {
        var decision = GraphCompletionPolicy.Decide(
            _graphState.IsCurrent(completion.RequestId),
            _polling,
            _year == completion.RequestId.Query.Year,
            Volatile.Read(ref _forceRequested) == 1,
            _refreshing);
        if (!decision.IsCurrent)
        {
            return;
        }

        Volatile.Write(ref _slowInFlight, 0);
        if (decision.ShouldRerun)
        {
            RefreshSlow();
        }
        else if (decision.ClearRefreshing)
        {
            _refreshing = false;
            _ = _dispatcher.TryEnqueue(() => Updated?.Invoke());
        }
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
        if (Interlocked.Exchange(ref _quotaInFlight, 1) == 1)
        {
            return;
        }

        _ = Task.Run(() =>
        {
            try
            {
                var quota = TryFetch(
                    () => AgentUsageFetchCoordinator.Shared.FetchAsync().GetAwaiter().GetResult(),
                    "agentUsage");
                // Recorded before publishing, and outside the snapshot: the
                // publish below is dropped entirely if the graph lane has not
                // seeded Current yet, and this is the only thing that survives
                // that window.
                _quotaAttempted = true;
                if (quota is not null)
                {
                    _latestQuota = quota;
                    Publish(s => s with { Quota = quota, QuotaAttempted = true }, graph: null);
                }
                else
                {
                    // Publish the completion even though there is nothing to
                    // show. Without this the failure is silent in exactly the
                    // way that matters: Quota stays null, and a surface that
                    // reads null as "not yet" waits forever for an answer that
                    // already came back.
                    Publish(s => s with { QuotaAttempted = true }, graph: null);
                }
            }
            finally
            {
                Volatile.Write(ref _quotaInFlight, 0);
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
        if (Interlocked.Exchange(ref _fastInFlight, 1) == 1)
        {
            return;
        }

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
                Volatile.Write(ref _fastInFlight, 0);
            }
        });
    }

    /// <summary>Merge a partial update into the snapshot on the UI thread.
    /// The first slow refresh creates the snapshot; fast-lane results before
    /// that are parked on a placeholder graph-less state (dropped — the slow
    /// lane lands within a second on a warm cache).</summary>
    /// <summary>The first snapshot, from whichever lane gets there first.
    ///
    /// <para>One function because there were two, and they had already drifted:
    /// the reachable one seeded the quota payload and the cost-authority flag,
    /// the other seeded the quota payload and the attempt flag, and neither
    /// seeded all three. The attempt flag was added to the unreachable one —
    /// every Publish caller passes a null graph, so only OnGraphPublished ever
    /// builds a baseline — which is why a failed cold-start fetch went on
    /// rendering as a request still in flight after being "fixed".</para>
    ///
    /// <para>Everything held outside the snapshot is seeded here, and nowhere
    /// else. A second construction site is what let a field be remembered on
    /// one path and forgotten on the other.</para></summary>
    private Snapshot CreateBaseline(UsagePayload graph) =>
        new(graph, null, _latestQuota, 0, [], DateTimeOffset.Now, _graphState.CostAuthoritative)
        {
            QuotaAttempted = _quotaAttempted,
        };

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

                baseline = CreateBaseline(graph);
                DevLog.Write("first snapshot ready"); // cold-parse timing anchor
            }

            Current = update(baseline) with { FetchedAt = DateTimeOffset.Now };
            _lastSnapshot = Current;
            Updated?.Invoke();
        });
    }
}

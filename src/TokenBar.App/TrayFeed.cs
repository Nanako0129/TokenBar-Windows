using Microsoft.UI.Dispatching;
using TokenBar.Core;
using TokenBar.Interop;

namespace TokenBar.App;

/// <summary>
/// Tray-lifetime data feed (macOS AppDelegate title loop + TrayAnimator
/// pollers): the tray icon keeps its numbers fresh whether or not the
/// flyout is open — rate every 30s, graph and quota every 300s, all served
/// mostly from the engine's caches. The dashboard's own lanes stay separate,
/// same as macOS. The tokenbar.refresh.intervalMin forced re-read joins
/// with the settings panel.
/// </summary>
public sealed class TrayFeed : IDisposable
{
    private readonly DispatcherQueue _dispatcher;
    private readonly DispatcherQueueTimer _fast;
    private readonly DispatcherQueueTimer _slow;
    private readonly Action<string> _onStoreChanged;
    private int _fastInFlight; // Interlocked: reset in a background finally
    private int _slowInFlight;
    private bool _disposed;

    public UsagePayload? Graph { get; private set; }

    public IReadOnlyList<TraceBucket> Trace { get; private set; } = [];

    public TrayTotals? VisibleTotals { get; private set; }

    public double? TokensPerMin { get; private set; }

    public AgentUsagePayload? Quota { get; private set; }

    private bool _hasTrace;
    private string? _cachedQuotaSelection;
    private double? _cachedQuotaRemaining;

    /// <summary>Resolved remaining % for the selected quota window. Boots
    /// from the persisted last reading only when its selection identity matches
    /// the current effective selection.</summary>
    public double? QuotaRemaining { get; private set; }

    public event Action? Changed;

    public TrayFeed(DispatcherQueue dispatcher)
    {
        _dispatcher = dispatcher;
        var persistedSelection = AppSettings.Store.GetString(
            "tokenbar.quota.lastSelection");
        var currentSelection = AppSettings.Store.GetString(
            "tokenbar.quota.source", QuotaResolver.Auto) ?? QuotaResolver.Auto;
        var persisted = AppSettings.Store.GetDouble("tokenbar.quota.lastRemaining", double.NaN);
        _cachedQuotaRemaining = QuotaSelectionPolicy.MatchingLastGoodRemaining(
            QuotaSelectionPolicy.EffectiveSelection(null, currentSelection),
            persistedSelection,
            double.IsNaN(persisted) ? null : persisted);
        _cachedQuotaSelection = _cachedQuotaRemaining is null ? null : persistedSelection;
        QuotaRemaining = _cachedQuotaRemaining;

        _fast = dispatcher.CreateTimer();
        _fast.Interval = TimeSpan.FromSeconds(30);
        _fast.Tick += (_, _) => RefreshFast();
        _fast.Start();
        _slow = dispatcher.CreateTimer();
        _slow.Interval = TimeSpan.FromSeconds(300);
        _slow.Tick += (_, _) => RefreshSlow();
        _slow.Start();
        RefreshFast();
        RefreshSlow();

        _onStoreChanged = key =>
        {
            if (key == "tokenbar.quota.source")
            {
                _ = _dispatcher.TryEnqueue(() =>
                {
                    ResolveRemaining(); // re-pick from the cached payload
                    Changed?.Invoke();
                });
            }
            else if (key is ClientRegistry.TabHiddenKey or ClientRegistry.LimitsHiddenKey)
            {
                _ = _dispatcher.TryEnqueue(() =>
                {
                    RecomputeVisibleUsage();
                    ResolveRemaining();
                    Changed?.Invoke();
                });
            }
        };
        AppSettings.Store.Changed += _onStoreChanged;
    }

    /// <summary>Stop polling and unsubscribe so the feed can't raise Changed
    /// into a disposed tray icon after shutdown.</summary>
    public void Dispose()
    {
        _disposed = true; // fences any in-flight lane's enqueued callback
        _fast.Stop();
        _slow.Stop();
        AppSettings.Store.Changed -= _onStoreChanged;
    }

    private void RefreshFast()
    {
        if (Interlocked.Exchange(ref _fastInFlight, 1) == 1)
        {
            return;
        }

        _ = Task.Run(() =>
        {
            try
            {
                using var boost = ProcessPower.Boost(); // live-tail parse
                var trace = TbCore.UsageTrace(600);
                _ = _dispatcher.TryEnqueue(() =>
                {
                    if (_disposed)
                    {
                        return; // don't touch the tray icon after shutdown
                    }

                    Trace = trace;
                    _hasTrace = true;
                    RecomputeVisibleUsage();
                    Changed?.Invoke();
                });
            }
            catch (Exception ex)
            {
                DevLog.Write($"tray rate failed: {ex.Message}");
            }
            finally
            {
                Volatile.Write(ref _fastInFlight, 0);
            }
        });
    }

    // Forced full re-read cadence (tokenbar.refresh.intervalMin, default 30):
    // the cached path keeps data fresh continuously; this is the macOS title
    // loop's belt against anything the incremental scan misses. An instance
    // field, so restarting timers never triggers an immediate re-read.
    private DateTimeOffset _lastFullRefresh = DateTimeOffset.Now;

    private void RefreshSlow()
    {
        if (Interlocked.Exchange(ref _slowInFlight, 1) == 1)
        {
            return;
        }

        var intervalMin = Math.Max(1, AppSettings.Store.GetInt("tokenbar.refresh.intervalMin", 30));
        var force = DateTimeOffset.Now - _lastFullRefresh >= TimeSpan.FromMinutes(intervalMin);
        _ = Task.Run(() =>
        {
            try
            {
                UsagePayload? graph;
                using (ProcessPower.Boost())
                {
                    graph = TryFetch(() => force
                        ? TbCore.RefreshGraph() : TbCore.Graph(), "tray graph");
                }

                if (force && graph is not null)
                {
                    _lastFullRefresh = DateTimeOffset.Now;
                }

                var quota = TryFetch(
                    () => AgentUsageFetchCoordinator.Shared.FetchAsync().GetAwaiter().GetResult(),
                    "tray quota");
                _ = _dispatcher.TryEnqueue(() =>
                {
                    if (_disposed)
                    {
                        return; // don't touch the tray icon after shutdown
                    }

                    Graph = graph ?? Graph;
                    Quota = quota ?? Quota;
                    if (quota is not null)
                    {
                        var persistedSelection = AppSettings.Store.GetString(
                            "tokenbar.quota.source", QuotaResolver.Auto) ?? QuotaResolver.Auto;
                        if (QuotaSelectionPolicy.MigrationToPersist(quota, persistedSelection)
                            is { } migrated)
                        {
                            AppSettings.Store.SetString("tokenbar.quota.source", migrated);
                        }
                    }

                    RecomputeVisibleUsage();
                    ResolveRemaining();
                    Changed?.Invoke();
                });
            }
            finally
            {
                Volatile.Write(ref _slowInFlight, 0);
            }
        });
    }

    private void RecomputeVisibleUsage()
    {
        var hidden = ClientRegistry.HiddenClients(AppSettings.Store);
        VisibleTotals = Graph?.TrayTotals(hidden, Format.TodayKey());
        TokensPerMin = _hasTrace ? TraceCollapse.TotalRate(Trace, hidden) : null;
    }

    private void ResolveRemaining()
    {
        var persistedSelection = AppSettings.Store.GetString(
            "tokenbar.quota.source", QuotaResolver.Auto) ?? QuotaResolver.Auto;
        var selection = QuotaSelectionPolicy.EffectiveSelection(Quota, persistedSelection);
        var hidden = ClientRegistry.QuotaExcludedClients(AppSettings.Store);
        if (QuotaResolver.Resolve(Quota, selection, hidden) is { } pick)
        {
            var resolved = Math.Clamp(pick.Window.RemainingPercent, 0, 100);
            if (resolved != QuotaRemaining)
            {
                DevLog.Write($"tray quota pick: {pick.ClientId}|{pick.Window.CardId} {resolved:F1}%");
            }

            QuotaRemaining = resolved;
            _cachedQuotaSelection = selection;
            _cachedQuotaRemaining = resolved;
            // Write-through pair so the next launch boots only for this
            // selection (the store no-ops when values have not changed).
            AppSettings.Store.SetDouble("tokenbar.quota.lastRemaining", resolved);
            AppSettings.Store.SetString("tokenbar.quota.lastSelection", selection);
        }
        else if (QuotaResolver.ExcludedAllCandidates(Quota, selection, hidden))
        {
            // All healthy AUTO candidates are hidden. Suppress only the
            // displayed reading; keep the selected-source last-good pair.
            QuotaRemaining = null;
        }
        else
        {
            // Fetch/provider/explicit-selection failures keep only this
            // selection's last-good reading visible.
            QuotaRemaining = QuotaSelectionPolicy.MatchingLastGoodRemaining(
                selection, _cachedQuotaSelection, _cachedQuotaRemaining);
        }
    }

    private static T? TryFetch<T>(Func<T> fetch, string label) where T : class
    {
        try
        {
            return fetch();
        }
        catch (Exception ex)
        {
            DevLog.Write($"{label} failed: {ex.Message}");
            return null;
        }
    }
}

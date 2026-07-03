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
    private bool _fastInFlight;
    private bool _slowInFlight;

    public UsagePayload? Graph { get; private set; }

    public double? TokensPerMin { get; private set; }

    public AgentUsagePayload? Quota { get; private set; }

    /// <summary>Resolved remaining % for the selected quota window. Boots
    /// from the persisted last reading (macOS lastRemaining: the gauge never
    /// blanks across restarts) until a fresh fetch lands.</summary>
    public double? QuotaRemaining { get; private set; }

    public event Action? Changed;

    public TrayFeed(DispatcherQueue dispatcher)
    {
        _dispatcher = dispatcher;
        var persisted = AppSettings.Store.GetDouble("tokenbar.quota.lastRemaining", double.NaN);
        QuotaRemaining = double.IsNaN(persisted) ? null : persisted;

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
        };
        AppSettings.Store.Changed += _onStoreChanged;
    }

    /// <summary>Stop polling and unsubscribe so the feed can't raise Changed
    /// into a disposed tray icon after shutdown.</summary>
    public void Dispose()
    {
        _fast.Stop();
        _slow.Stop();
        AppSettings.Store.Changed -= _onStoreChanged;
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
                using var boost = ProcessPower.Boost(); // live-tail parse
                var rate = TbCore.TokensPerMin();
                _ = _dispatcher.TryEnqueue(() =>
                {
                    TokensPerMin = rate;
                    Changed?.Invoke();
                });
            }
            catch (Exception ex)
            {
                DevLog.Write($"tray rate failed: {ex.Message}");
            }
            finally
            {
                _fastInFlight = false;
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
        if (_slowInFlight)
        {
            return;
        }

        var intervalMin = Math.Max(1, AppSettings.Store.GetInt("tokenbar.refresh.intervalMin", 30));
        var force = DateTimeOffset.Now - _lastFullRefresh >= TimeSpan.FromMinutes(intervalMin);
        _slowInFlight = true;
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

                var quota = TryFetch(() => TbCore.AgentUsage(), "tray quota");
                _ = _dispatcher.TryEnqueue(() =>
                {
                    Graph = graph ?? Graph;
                    Quota = quota ?? Quota;
                    ResolveRemaining();
                    Changed?.Invoke();
                });
            }
            finally
            {
                _slowInFlight = false;
            }
        });
    }

    private void ResolveRemaining()
    {
        var selection = AppSettings.Store.GetString("tokenbar.quota.source", "auto")
            ?? QuotaResolver.Auto;
        if (QuotaResolver.Resolve(Quota, selection) is { } pick)
        {
            var resolved = Math.Clamp(pick.Window.RemainingPercent, 0, 100);
            if (resolved != QuotaRemaining)
            {
                DevLog.Write($"tray quota pick: {pick.ClientId}|{pick.Window.Label} {resolved:F1}%");
            }

            QuotaRemaining = resolved;
            // Write-through cache so the next launch boots with a reading
            // (the store no-ops when the value hasn't changed).
            AppSettings.Store.SetDouble("tokenbar.quota.lastRemaining", resolved);
        }
        else if (Quota is not null)
        {
            // A payload arrived but the selected window is gone (auth failed,
            // no windows): drop the reading so the tray doesn't keep showing
            // a stale percentage. lastRemaining stays only as a cold-boot
            // seed, used before the first payload — not now.
            QuotaRemaining = null;
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

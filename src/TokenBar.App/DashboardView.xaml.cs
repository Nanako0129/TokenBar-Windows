using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using TokenBar.Core;
using TokenBar.Interop;
using Windows.UI;
// TokenBar.Core.Grid (the contribution-grid builder) collides with the XAML
// Grid — same clash the macOS port hit with SwiftUI's GridLayout.
using Grid = Microsoft.UI.Xaml.Controls.Grid;

namespace TokenBar.App;

public enum AppView
{
    Overview,
    Models,
    Daily,
    Hourly,
    Stats,
    Agents,
}

/// <summary>
/// The dashboard shell: global header, lens switch, and the six lenses from
/// the macOS AppView router, each built in code from the current snapshot.
/// </summary>
public sealed partial class DashboardView : UserControl
{
    private DashboardModel.Snapshot? _snapshot;
    private DashboardModel? _model;
    private AppView _view = AppView.Overview;
    private readonly Dictionary<AppView, Button> _tabs = [];
    private IReadOnlyList<string> _displayClients = [];
    private IReadOnlyList<string> _selectedClients = [];
    private HashSet<string> _selectedSet = new(StringComparer.Ordinal);
    private UsageStats? _selectedStats;
    private string _activeClientTab = ClientRegistry.OverviewTab;
    private string _clientTabsSignature = "";
    private string? _expandedDay; // Daily drill-down state
    private bool _hourlyProfileMode;
    private int _hourlyWindow = 48; // Timeline rows shown; +48 per "Show more"
    // Chart toggles, persisted with the macOS rawValue strings.
    private StackBy _chartStackBy =
        AppSettings.Store.GetString("tokenbar.chart.stackBy") == "agent"
            ? StackBy.Agent : StackBy.Model;
    private ChartMetric _chartMetric =
        AppSettings.Store.GetString("tokenbar.chart.metric") == "cost"
            ? ChartMetric.Cost : ChartMetric.Tokens;
    private bool _chartView3D =
        AppSettings.Store.GetString("tokenbar.chart.view", "2d") == "3d";

    public DashboardView()
    {
        InitializeComponent();
        ProductTitle.Text = ProductIdentity.Name;
        // WinUI otherwise synthesizes a tooltip containing "Esc" for the
        // dashboard-wide Escape accelerator whenever the pointer rests over
        // the graph. The accelerator remains active; only its automatic
        // tooltip chrome is suppressed.
        KeyboardAcceleratorPlacementMode =
            Microsoft.UI.Xaml.Input.KeyboardAcceleratorPlacementMode.Hidden;

        foreach (var view in Enum.GetValues<AppView>())
        {
            var button = new Button
            {
                Content = view.ToString(),
                FontSize = 11,
                Padding = new Thickness(8, 4, 8, 4),
                Background = new SolidColorBrush(Colors.Transparent),
                BorderThickness = new Thickness(0),
            };
            button.Click += (_, _) => SwitchTo(view);
            _tabs[view] = button;
            TabsPanel.Children.Add(button);
        }

        UpdateTabChrome();

        AddHandler(
            PointerWheelChangedEvent,
            new Microsoft.UI.Xaml.Input.PointerEventHandler(OnWheel),
            handledEventsToo: true);

        RefreshButton.Click += (_, _) =>
        {
            _model?.RefreshForce();
            UpdateRefreshControl();
        };
        HoverTip.Attach(RefreshButton, () => "Refresh usage data");

        SettingsButton.Click += (_, _) => TrayService.OpenSettings?.Invoke();
        HoverTip.Attach(SettingsButton, () => "Settings");
        QuitButton.Click += (_, _) => TrayService.QuitApp?.Invoke();

        // Limits/trace settings re-render the open flyout live (the macOS
        // panel's right-column preview equivalent is the flyout itself).
        AppSettings.Store.Changed += key =>
        {
            if (key is ClientRegistry.TabHiddenKey or ClientRegistry.TabOrderKey)
            {
                _ = DispatcherQueue.TryEnqueue(() => RefreshSelection(animated: false));
            }
            else if (key.StartsWith("tokenbar.limits.", StringComparison.Ordinal)
                || key == "tokenbar.trace.detailed")
            {
                _ = DispatcherQueue.TryEnqueue(() => RenderContent(animated: false));
            }
        };

        // In-flyout shortcuts, the macOS ⌘ set on Ctrl: Esc/Ctrl+W close,
        // Ctrl+R refresh, Ctrl+, settings, Ctrl+Q quit, Ctrl+1..6 lenses,
        // Ctrl+[ / Ctrl+] cycle.
        // Esc yields to an open transient (the year menu): light-dismiss
        // should collapse the popup, not slide the whole flyout away.
        var escape = new Microsoft.UI.Xaml.Input.KeyboardAccelerator
        {
            Key = Windows.System.VirtualKey.Escape,
        };
        escape.Invoked += (_, e) =>
        {
            // Yield only to a REAL transient (the year MenuFlyout). The HoverTip
            // tooltip is also an open popup for this XamlRoot but must not
            // swallow Esc, or Esc silently fails to close the flyout whenever a
            // tooltip happens to be showing.
            if (Microsoft.UI.Xaml.Media.VisualTreeHelper
                .GetOpenPopupsForXamlRoot(XamlRoot).Any(p => !HoverTip.IsHoverPopup(p)))
            {
                return; // unhandled: the real popup takes it
            }

            HideRequested?.Invoke();
            e.Handled = true;
        };
        KeyboardAccelerators.Add(escape);
        AddAccel(Windows.System.VirtualKey.W, Windows.System.VirtualKeyModifiers.Control,
            () => HideRequested?.Invoke());
        AddAccel(Windows.System.VirtualKey.R, Windows.System.VirtualKeyModifiers.Control,
            () =>
            {
                _model?.RefreshForce();
                UpdateRefreshControl();
            });
        AddAccel(Windows.System.VirtualKey.G, Windows.System.VirtualKeyModifiers.Control,
            ToggleChartView);
        AddAccel((Windows.System.VirtualKey)0xBC /* comma */,
            Windows.System.VirtualKeyModifiers.Control,
            () => TrayService.OpenSettings?.Invoke());
        AddAccel(Windows.System.VirtualKey.Q, Windows.System.VirtualKeyModifiers.Control,
            () => TrayService.QuitApp?.Invoke());
        var lenses = Enum.GetValues<AppView>();
        for (var i = 0; i < lenses.Length && i < 9; i++)
        {
            var view = lenses[i];
            AddAccel(Windows.System.VirtualKey.Number1 + i,
                Windows.System.VirtualKeyModifiers.Control, () => SwitchTo(view));
        }

        AddAccel((Windows.System.VirtualKey)0xDB /* [ */,
            Windows.System.VirtualKeyModifiers.Control, () => CycleLens(-1));
        AddAccel((Windows.System.VirtualKey)0xDD /* ] */,
            Windows.System.VirtualKeyModifiers.Control, () => CycleLens(1));

        ActualThemeChanged += (_, _) => UpdateGraph3DData(_snapshot);
    }

    /// <summary>The flyout owns hiding (Esc/Ctrl+W land here).</summary>
    public event Action? HideRequested;

    // ── 3D contribution graph ────────────────────────────────────────────

    private Graph3DPanel? _graph3d;
    private Border? _graph3dContentHost;
    private bool _graph3dDevMode;
    private bool _flyoutVisible;

    /// <summary>Mount the 3D contribution-graph panel and reveal its host.
    /// Dev-only Gate 0 path used by --graph3d / --soak3d. The normal product
    /// path mounts the same panel inside the Token Usage card.</summary>
    public void EnableGraph3D()
    {
        if (_graph3dDevMode)
        {
            return;
        }

        _graph3dDevMode = true;
        var panel = EnsureGraph3D();
        DetachGraph3DContentHost();
        _graph3d = panel;
        Graph3DHost.Child = _graph3d;
        Graph3DHost.Visibility = Visibility.Visible;
        UpdateGraph3DData(_snapshot);
        SyncGraph3DActivity();
    }

    /// <summary>Flyout show/hide hooks — the renderer holds no GPU resources
    /// while the flyout is away.</summary>
    public void OnFlyoutShown()
    {
        _flyoutVisible = true;
        SyncGraph3DActivity();
    }

    public void OnFlyoutHidden()
    {
        _flyoutVisible = false;
        _graph3d?.Release();
    }

    private Graph3DPanel EnsureGraph3D() => _graph3d ??= new Graph3DPanel();

    private bool Graph3DShouldBeActive =>
        _graph3dDevMode || (_chartView3D
            && _view is AppView.Overview or AppView.Stats);

    private void SyncGraph3DActivity()
    {
        if (_flyoutVisible && Graph3DShouldBeActive)
        {
            EnsureGraph3D().Activate();
        }
        else
        {
            _graph3d?.Release();
        }
    }

    private void UpdateGraph3DData(DashboardModel.Snapshot? snapshot)
    {
        if (snapshot is null || _graph3d is null)
        {
            return;
        }

        var stats = _selectedStats ?? new UsageStats(snapshot.Graph, _selectedSet);
        var year = _model?.Year ?? Format.TodayKey()[..4];
        var grid = TokenBar.Core.Grid.Build(year, stats.PerDayMap);
        _graph3d.SetData(grid, ActualTheme == ElementTheme.Dark);
    }

    private void DetachGraph3DContentHost()
    {
        if (_graph3dContentHost is not null
            && ReferenceEquals(_graph3dContentHost.Child, _graph3d))
        {
            _graph3dContentHost.Child = null;
        }

        _graph3dContentHost = null;
    }

    private void ToggleChartView() => SetChartView(!_chartView3D);

    private void SetChartView(bool use3D)
    {
        if (_chartView3D == use3D)
        {
            return;
        }

        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        _chartView3D = use3D;
        AppSettings.Store.SetString("tokenbar.chart.view", use3D ? "3d" : "2d");
        RenderContent(animated: false);
        SyncGraph3DActivity();
        var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        DevLog.Write($"graph3d: toggle view={(use3D ? "3d" : "2d")} "
            + $"elapsed={elapsed:F1}ms");
    }

    private void AddAccel(
        Windows.System.VirtualKey key, Windows.System.VirtualKeyModifiers mods,
        Action action)
    {
        var accel = new Microsoft.UI.Xaml.Input.KeyboardAccelerator
        {
            Key = key,
            Modifiers = mods,
        };
        accel.Invoked += (_, e) =>
        {
            action();
            e.Handled = true;
        };
        KeyboardAccelerators.Add(accel);
    }

    private void CycleLens(int step)
    {
        var lenses = Enum.GetValues<AppView>();
        var index = Array.IndexOf(lenses, _view);
        SwitchTo(lenses[(index + step + lenses.Length) % lenses.Length]);
    }

    /// <summary>One control, two states (macOS refreshButton): the glyph
    /// while idle, a spinner while a forced re-read or the initial load runs.</summary>
    private void UpdateRefreshControl(bool loading = false)
    {
        var spinning = loading || _model?.Refreshing == true;
        RefreshButton.Visibility = spinning ? Visibility.Collapsed : Visibility.Visible;
        RefreshSpinner.Visibility = spinning ? Visibility.Visible : Visibility.Collapsed;
        RefreshSpinner.IsActive = spinning;
    }

    /// <summary>The model powers lazy lens loading (hourly/agents).</summary>
    public void Bind(DashboardModel model) => _model = model;

    /// <summary>Persist a requested client tab. The next selection pass validates
    /// it against the visible clients before any usage surface renders.</summary>
    public void SelectClientTab(string? clientId)
    {
        var requested = string.IsNullOrWhiteSpace(clientId)
            ? ClientRegistry.OverviewTab
            : ClientRegistry.CanonicalClient(clientId.Trim());
        AppSettings.Store.SetString(ClientRegistry.ActiveTabKey, requested);
        RefreshSelection(animated: false);
    }

    public void SwitchTo(AppView view)
    {
        if (_view == view)
        {
            return;
        }

        _view = view;
        if (view == AppView.Hourly)
        {
            _model?.EnsureHourly();
        }

        if (view == AppView.Agents)
        {
            _model?.EnsureAgents();
        }

        UpdateTabChrome();
        RenderContent(animated: true);
    }

    public void Render(DashboardModel.Snapshot? snapshot)
    {
        _snapshot = snapshot;
        UpdateRefreshControl(loading: snapshot is null);
        if (snapshot is null)
        {
            FooterText.Text = "loading usage…";
            return;
        }

        snapshot = ApplyClientSelection(snapshot);
        RenderHeader(snapshot);
        UpdateYearPicker();
        UpdateGraph3DData(snapshot);
        RenderContent(animated: false);
    }

    private void RefreshSelection(bool animated)
    {
        if (_snapshot is not { } snapshot)
        {
            return;
        }

        snapshot = ApplyClientSelection(snapshot);
        RenderHeader(snapshot);
        UpdateYearPicker();
        UpdateGraph3DData(snapshot);
        RenderContent(animated);
    }

    private DashboardModel.Snapshot ApplyClientSelection(DashboardModel.Snapshot snapshot)
    {
        var selection = ClientRegistry.ResolveSelection(
            snapshot.Graph.Summary.Clients, AppSettings.Store);
        _displayClients = selection.DisplayClients;
        _selectedClients = selection.SelectedClients;
        _selectedSet = new HashSet<string>(selection.SelectedClients, StringComparer.Ordinal);
        _selectedStats = new UsageStats(snapshot.Graph, _selectedSet);
        _activeClientTab = selection.ActiveTab;
        UpdateClientTabs();

        if (_model?.SetClientSelection(selection.SelectedClients) == true
            && _model.Current is { } current)
        {
            // Membership changes clear stale pre-aggregated lazy reports in the
            // model before this same Dashboard render reaches Hourly/Agents.
            snapshot = current;
        }

        _snapshot = snapshot;
        if (AppSettings.Store.GetString(ClientRegistry.ActiveTabKey) != selection.ActiveTab)
        {
            AppSettings.Store.SetString(ClientRegistry.ActiveTabKey, selection.ActiveTab);
        }

        return snapshot;
    }

    private void RenderHeader(DashboardModel.Snapshot snapshot)
    {
        var stats = _selectedStats ?? new UsageStats(snapshot.Graph, _selectedSet);
        stats.PerDayMap.TryGetValue(Format.TodayKey(), out var today);
        var rate = TraceCollapse.FilterByClients(snapshot.Trace, _selectedSet)
            .Sum(b => b.TokensPerMin);
        TodayValue.Text = Format.CompactTokens(today?.Tokens ?? 0);
        TotalValue.Text = Format.CompactTokens(stats.TotalTokens);
        RateValue.Text = Format.CompactTokens((long)rate);
        CostLine.Text =
            $"{Format.Usd(today?.Cost ?? 0)} today · {Format.Usd(stats.TotalCost)} all time · " +
            $"{stats.ActiveDays} active days";
        FooterText.Text = $"updated {snapshot.FetchedAt:HH:mm:ss}";
    }

    private string _yearPickerSignature = "";

    /// <summary>Rebuilds the year menu only when the selection or the known
    /// years actually change, so the 10s fast lane's re-render never closes
    /// a menu the user has open.</summary>
    private void UpdateYearPicker()
    {
        var model = _model;
        var years = model?.KnownYears ?? [];
        if (model is null || years.Count == 0)
        {
            YearButton.Visibility = Visibility.Collapsed;
            return;
        }

        YearButton.Visibility = Visibility.Visible;
        var signature = $"{model.Year}|{string.Join(',', years)}";
        if (signature == _yearPickerSignature)
        {
            return;
        }

        _yearPickerSignature = signature;
        YearButton.Content = model.Year ?? "All";
        var flyout = new MenuFlyout();
        AddYearItem(flyout, "All years", null, model);
        foreach (var year in years)
        {
            AddYearItem(flyout, year, year, model);
        }

        YearButton.Flyout = flyout;
    }

    private void AddYearItem(
        MenuFlyout flyout, string label, string? value, DashboardModel model)
    {
        // Radio (not Toggle): re-clicking the checked item must not flip its
        // checkmark off while the filter stays active.
        var item = new RadioMenuFlyoutItem
        {
            Text = label,
            IsChecked = model.Year == value,
            GroupName = "tokenbar.year",
        };
        item.Click += (_, _) =>
        {
            model.SetYear(value);
            UpdateYearPicker(); // reflect the pick before the new slice lands
        };
        flyout.Items.Add(item);
    }

    public void ScrollBy(double delta) =>
        CardsScroll.ChangeView(
            null, CardsScroll.VerticalOffset + delta, null, disableAnimation: false);

    /// <summary>Route the flyout's WH_MOUSE_LL wheel event. Coordinates are
    /// physical pixels relative to the window; graph hit-testing happens in
    /// XAML DIPs, and only wheel events over the live 3D surface become zoom.</summary>
    public void RouteGlobalWheel(double windowPixelX, double windowPixelY, int delta)
    {
        var rasterScale = XamlRoot?.RasterizationScale ?? 1.0;
        var point = new Windows.Foundation.Point(
            windowPixelX / rasterScale, windowPixelY / rasterScale);
        if (!TryZoomGraphAt(point, delta))
        {
            ScrollBy(-delta);
        }
    }

    private void OnWheel(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        // Graph3DPanel consumes native wheel events itself. This root handler is
        // registered with handledEventsToo for the rest of the dashboard, so
        // honor that flag or a focused 3D panel zooms twice per notch.
        if (e.Handled)
        {
            return;
        }

        var point = e.GetCurrentPoint(this);
        if (!TryZoomGraphAt(point.Position, point.Properties.MouseWheelDelta))
        {
            ScrollBy(-point.Properties.MouseWheelDelta);
        }

        e.Handled = true;
    }

    private bool TryZoomGraphAt(Windows.Foundation.Point point, int delta)
    {
        var panel = _graph3d;
        if (!Graph3DShouldBeActive || panel?.IsActive != true
            || panel.ActualWidth <= 0 || panel.ActualHeight <= 0)
        {
            return false;
        }

        try
        {
            var origin = panel.TransformToVisual(this)
                .TransformPoint(new Windows.Foundation.Point(0, 0));
            if (point.X < origin.X || point.Y < origin.Y
                || point.X >= origin.X + panel.ActualWidth
                || point.Y >= origin.Y + panel.ActualHeight)
            {
                return false;
            }

            panel.ZoomFromWheel(delta);
            return true;
        }
        catch (InvalidOperationException)
        {
            // The content presenter can detach the old chart between a model
            // refresh and this queued hook callback. Treat it as normal scroll.
            return false;
        }
    }

    private void UpdateTabChrome()
    {
        foreach (var (view, button) in _tabs)
        {
            var active = view == _view;
            button.FontWeight = active
                ? Microsoft.UI.Text.FontWeights.SemiBold
                : Microsoft.UI.Text.FontWeights.Normal;
            button.Opacity = active ? 1.0 : 0.6;
            button.Background = active
                ? (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"]
                : new SolidColorBrush(Colors.Transparent);
        }
    }

    private void UpdateClientTabs()
    {
        var signature = $"{_activeClientTab}|{string.Join(',', _displayClients)}";
        if (signature == _clientTabsSignature)
        {
            return;
        }

        _clientTabsSignature = signature;
        ClientTabsPanel.Children.Clear();
        AddClientTab(ClientRegistry.OverviewTab, "Overview");
        foreach (var id in _displayClients)
        {
            AddClientTab(id, ClientRegistry.ShortName(id));
        }
    }

    private void AddClientTab(string id, string label)
    {
        var active = id == _activeClientTab;
        var button = new Button
        {
            Content = label,
            FontSize = 11,
            Padding = new Thickness(9, 4, 9, 4),
            FontWeight = active
                ? Microsoft.UI.Text.FontWeights.SemiBold
                : Microsoft.UI.Text.FontWeights.Normal,
            Opacity = active ? 1.0 : 0.6,
            Background = active
                ? (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"]
                : new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(
            button, $"ClientTab_{id}");
        button.Click += (_, _) => SelectClientTab(id);
        if (id != ClientRegistry.OverviewTab)
        {
            HoverTip.Attach(button, () => ClientRegistry.Style(id).DisplayName);
        }

        ClientTabsPanel.Children.Add(button);
    }

    private void RenderContent(bool animated)
    {
        if (_snapshot is null)
        {
            return;
        }

        DetachGraph3DContentHost();
        UIElement content = _view switch
        {
            AppView.Models => BuildModels(_snapshot),
            AppView.Daily => BuildDaily(_snapshot),
            AppView.Hourly => BuildHourly(_snapshot),
            AppView.Stats => BuildStats(_snapshot),
            AppView.Agents => BuildAgents(_snapshot),
            _ => BuildOverview(_snapshot),
        };
        ContentHost.Content = content;
        SyncGraph3DActivity();
        if (animated)
        {
            CardsScroll.ChangeView(null, 0, null, disableAnimation: true);
            AnimateLensIn(content);
        }
    }

    /// <summary>Lens-switch transition, macOS parity: crossfade + subtle
    /// scale from the top (0.16s).</summary>
    private static void AnimateLensIn(UIElement element)
    {
        var visual = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(element);
        var compositor = visual.Compositor;
        visual.Opacity = 0f;
        visual.Scale = new System.Numerics.Vector3(0.985f, 0.985f, 1f);
        var easing = compositor.CreateCubicBezierEasingFunction(
            new System.Numerics.Vector2(0.2f, 0.8f), new System.Numerics.Vector2(0.2f, 1f));
        var fade = compositor.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(1f, 1f, easing);
        fade.Duration = TimeSpan.FromMilliseconds(160);
        var scale = compositor.CreateVector3KeyFrameAnimation();
        scale.InsertKeyFrame(1f, System.Numerics.Vector3.One, easing);
        scale.Duration = TimeSpan.FromMilliseconds(160);
        visual.StartAnimation("Opacity", fade);
        visual.StartAnimation("Scale", scale);
    }

    // ── Overview ─────────────────────────────────────────────────────────

    private UIElement BuildOverview(DashboardModel.Snapshot snapshot)
    {
        var stack = new StackPanel { Spacing = 10 };
        stack.Children.Add(BuildUsageChartCard(snapshot));
        stack.Children.Add(Ui.Card("Agent limits", BuildLimits(snapshot)));
        var trace = BuildTrace(snapshot);
        if (trace is not null)
        {
            stack.Children.Add(Ui.Card("Live session", trace));
        }

        stack.Children.Add(Ui.Card("Models", BuildModelRows(snapshot, maxRows: 8)));
        stack.Children.Add(Ui.Card("Streaks", BuildStreaks(snapshot)));
        return stack;
    }

    /// <summary>The same persisted 2D/3D usage card is shared by Overview and
    /// Stats, matching the macOS UsageChartCard ownership. Keeping one builder
    /// also ensures the single Graph3DPanel is reparented through the same
    /// lifecycle path in both lenses.</summary>
    private Border BuildUsageChartCard(DashboardModel.Snapshot snapshot) =>
        Ui.Card(
            "Token Usage",
            BuildChart(snapshot),
            _chartView3D ? "Full year" : "30 days",
            BuildChartViewToggle());

    private FrameworkElement BuildChartViewToggle()
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 2,
        };
        var twoD = LensPill("2D", !_chartView3D);
        var threeD = LensPill("3D", _chartView3D);
        twoD.Click += (_, _) => SetChartView(false);
        threeD.Click += (_, _) => SetChartView(true);
        row.Children.Add(twoD);
        row.Children.Add(threeD);
        return row;
    }

    private FrameworkElement BuildChart(DashboardModel.Snapshot snapshot)
    {
        if (_selectedClients.Count == 0)
        {
            return Ui.Dim("No visible client usage.");
        }

        if (_chartView3D)
        {
            return BuildGraph3D(snapshot);
        }

        var colors = new ModelColorMap(snapshot.Models);
        var stats = _selectedStats ?? new UsageStats(snapshot.Graph, _selectedSet);
        var bars = DayBars.Build(
            snapshot.Graph,
            _selectedClients,
            _chartStackBy,
            _chartMetric,
            colors,
            Format.TodayKey(),
            rangeEnd: stats.DateRange.End);

        var holder = new StackPanel();
        var canvas = new Canvas { Height = 120 };
        holder.Children.Add(canvas);

        // Stack-by + metric pills, the macOS UsageChartCard secondary row.
        var toggles = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 6, 0, 0),
        };
        var byModel = LensPill("Model", _chartStackBy == StackBy.Model);
        var byAgent = LensPill("Agent", _chartStackBy == StackBy.Agent);
        byModel.Click += (_, _) => SetStackBy(StackBy.Model);
        byAgent.Click += (_, _) => SetStackBy(StackBy.Agent);
        var byTokens = LensPill("Tokens", _chartMetric == ChartMetric.Tokens);
        var byCost = LensPill("Price", _chartMetric == ChartMetric.Cost);
        byTokens.Click += (_, _) => SetMetric(ChartMetric.Tokens);
        byCost.Click += (_, _) => SetMetric(ChartMetric.Cost);
        toggles.Children.Add(byModel);
        toggles.Children.Add(byAgent);
        toggles.Children.Add(new Border { Width = 8 });
        toggles.Children.Add(byTokens);
        toggles.Children.Add(byCost);

        void SetStackBy(StackBy value)
        {
            _chartStackBy = value;
            AppSettings.Store.SetString("tokenbar.chart.stackBy",
                value == StackBy.Agent ? "agent" : "model");
            RenderContent(false);
        }

        void SetMetric(ChartMetric value)
        {
            _chartMetric = value;
            AppSettings.Store.SetString("tokenbar.chart.metric",
                value == ChartMetric.Cost ? "cost" : "tokens");
            RenderContent(false);
        }
        holder.Children.Add(toggles);
        // Wrapping legend, capped like the macOS FlowLayout (12 + "+N").
        var legend = new WrapRow { Margin = new Thickness(0, 8, 0, 0) };
        var allSegments = DayBars.Legend(bars, _chartMetric);
        foreach (var seg in allSegments.Take(12))
        {
            var item = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            item.Children.Add(Ui.Disc(seg.Color));
            item.Children.Add(Ui.Text(seg.Label, 10, 0.75));
            legend.Children.Add(item);
        }

        if (allSegments.Count > 12)
        {
            legend.Children.Add(Ui.Text($"+{allSegments.Count - 12}", 10, 0.6));
        }

        holder.Children.Add(legend);

        void Draw()
        {
            canvas.Children.Clear();
            var width = canvas.ActualWidth;
            var height = canvas.Height;
            if (width <= 0 || bars.Count == 0)
            {
                return;
            }

            // Bar length follows the metric toggle (tokens or spend).
            double SegValue(DaySegment s) =>
                _chartMetric == ChartMetric.Cost ? s.Cost : s.Tokens;
            double BarValue(DayBar b) =>
                _chartMetric == ChartMetric.Cost ? b.TotalCost : b.TotalTokens;
            var maxValue = Math.Max(bars.Max(BarValue), 1e-9);
            const double gap = 3;
            var barWidth = Math.Max(2, (width - gap * (bars.Count - 1)) / bars.Count);
            for (var i = 0; i < bars.Count; i++)
            {
                var bar = bars[i];
                var x = i * (barWidth + gap);
                var y = height;
                foreach (var seg in bar.Segments)
                {
                    var segHeight = height * SegValue(seg) / maxValue;
                    if (segHeight < 0.5)
                    {
                        continue;
                    }

                    y -= segHeight;
                    var rect = new Rectangle
                    {
                        Width = barWidth,
                        Height = segHeight,
                        Fill = Ui.BrushFromHex(seg.Color),
                        RadiusX = 1,
                        RadiusY = 1,
                    };
                    Canvas.SetLeft(rect, x);
                    Canvas.SetTop(rect, y);
                    canvas.Children.Add(rect);
                }

                if (bar.IsEmpty)
                {
                    var tick = new Rectangle
                    {
                        Width = barWidth,
                        Height = 2,
                        Fill = new SolidColorBrush(Color.FromArgb(40, 128, 128, 128)),
                    };
                    Canvas.SetLeft(tick, x);
                    Canvas.SetTop(tick, height - 2);
                    canvas.Children.Add(tick);
                }
                else
                {
                    var outline = new Rectangle
                    {
                        Width = barWidth,
                        Height = height - y,
                        Fill = new SolidColorBrush(Colors.Transparent),
                        Stroke = new SolidColorBrush(Colors.Transparent),
                        StrokeThickness = 1,
                        RadiusX = 1,
                        RadiusY = 1,
                        IsHitTestVisible = false,
                    };
                    Canvas.SetLeft(outline, x);
                    Canvas.SetTop(outline, y);
                    canvas.Children.Add(outline);

                    // Full-height hover column: ONE tooltip per day listing
                    // every segment with its color dot (the macOS layout),
                    // instead of sliver-sized per-segment targets.
                    var overlay = new Rectangle
                    {
                        Width = barWidth + gap,
                        Height = height,
                        Fill = new SolidColorBrush(Colors.Transparent),
                        Stroke = new SolidColorBrush(Colors.Transparent),
                        StrokeThickness = 2,
                    };
                    Canvas.SetLeft(overlay, x - gap / 2);
                    Canvas.SetTop(overlay, 0);
                    var capturedBar = bar;
                    AttachHoverOutline(overlay, hovered =>
                        outline.Stroke = hovered
                            ? HoverOutlineBrush()
                            : new SolidColorBrush(Colors.Transparent));
                    HoverTip.AttachRich(overlay, () => DayTip(capturedBar));
                    canvas.Children.Add(overlay);
                }
            }
        }

        canvas.SizeChanged += (_, _) => Draw();
        return holder;
    }

    private FrameworkElement BuildGraph3D(DashboardModel.Snapshot snapshot)
    {
        const double chartHeight = 196;
        var root = new Grid { Height = chartHeight };
        var panel = EnsureGraph3D();
        UpdateGraph3DData(snapshot);

        // --graph3d owns the panel through the fixed Gate 0 host. In normal
        // operation the same panel lives here, inside the product card.
        if (!_graph3dDevMode)
        {
            _graph3dContentHost = new Border
            {
                Height = chartHeight,
                Child = panel,
            };
            root.Children.Add(_graph3dContentHost);
        }

        var controls = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            Margin = new Thickness(6),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
        };
        var fit = LensPill("Fit", false);
        var reset = LensPill("Reset", false);
        fit.Click += (_, _) => panel.FitToContent();
        reset.Click += (_, _) => panel.ResetCamera();
        HoverTip.Attach(fit, () => "Frame active days");
        HoverTip.Attach(reset, () => "Reset the saved 3D camera");
        controls.Children.Add(fit);
        controls.Children.Add(reset);
        root.Children.Add(controls);
        return root;
    }

    private static FrameworkElement BuildLimits(DashboardModel.Snapshot snapshot)
    {
        var panel = new StackPanel { Spacing = 10 };
        var agents = snapshot.Quota?.Agents ?? [];
        if (agents.Count == 0)
        {
            panel.Children.Add(Ui.Dim("No quota data yet."));
            return panel;
        }

        // macOS windowRow settings: fill direction, density, pace policy.
        var asUsed = AppSettings.Store.GetBool("tokenbar.limits.asUsed", false);
        var classic = AppSettings.Store.GetString("tokenbar.limits.layout", "full") == "classic";
        var paceMode = AppSettings.Store.GetString("tokenbar.limits.paceMode", "historical") switch
        {
            "linear" => PaceMode.Linear,
            "off" => PaceMode.Off,
            _ => PaceMode.Historical,
        };

        var now = DateTimeOffset.Now;
        foreach (var agent in agents)
        {
            var section = new StackPanel { Spacing = 5 };
            var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            header.Children.Add(Ui.Disc(ClientRegistry.Style(agent.ClientId).Color));
            header.Children.Add(Ui.Text(ClientRegistry.ShortName(agent.ClientId), 12, bold: true));
            if (agent.Identity?.Plan is { } plan)
            {
                header.Children.Add(Ui.Dim(plan, 10));
            }

            section.Children.Add(header);
            if (agent.Error is { } error)
            {
                section.Children.Add(Ui.Dim(error, 11));
                panel.Children.Add(section);
                continue;
            }

            foreach (var window in agent.UniqueCardWindows)
            {
                var row = UsagePace.RowPresentation(
                    window, paceMode, asUsed, classic, now);
                section.Children.Add(QuotaRow(window, row, classic));
            }

            panel.Children.Add(section);
        }

        return panel;
    }

    private FrameworkElement? BuildTrace(DashboardModel.Snapshot snapshot)
    {
        // tokenbar.trace.detailed (macOS UsageTraceCard): one row per
        // agent-and-model bucket instead of one collapsed row per app.
        // Selection must happen before collapse and Take(5), or a high-rate
        // hidden client can evict every selected row from the card.
        var selected = TraceCollapse.FilterByClients(snapshot.Trace, _selectedSet);
        var detailed = AppSettings.Store.GetBool("tokenbar.trace.detailed", false);
        var rows = detailed
            ? selected
                .Select(b => (b.Client, b.Model, b.TokensPerMin)).Take(5).ToList()
            : TraceCollapse.CollapseByClient(selected)
                .Select(r => (r.Client, r.Model, r.TokensPerMin)).Take(5).ToList();
        if (rows.Count == 0)
        {
            return null;
        }

        var panel = new StackPanel { Spacing = 6 };
        foreach (var row in rows)
        {
            var name = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            name.Children.Add(Ui.Disc(ClientRegistry.Style(row.Client).Color));
            name.Children.Add(Ui.Text($"{ClientRegistry.ShortName(row.Client)} · {row.Model}", 11));
            panel.Children.Add(Ui.Row(
                name, Ui.Text($"{Format.CompactTokens((long)row.TokensPerMin)}/min", 11, 0.75)));
        }

        return panel;
    }

    private FrameworkElement BuildStreaks(DashboardModel.Snapshot snapshot)
    {
        var stats = _selectedStats ?? new UsageStats(snapshot.Graph, _selectedSet);
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 24 };
        row.Children.Add(Metric($"{stats.Streaks.Current}d", "current"));
        row.Children.Add(Metric($"{stats.Streaks.Longest}d", "longest"));
        row.Children.Add(Metric(
            stats.BestDay is { } best ? Format.MonthDay(best.Date) : "—", "best day"));
        return row;
    }

    // ── Models lens ──────────────────────────────────────────────────────

    private UIElement BuildModels(DashboardModel.Snapshot snapshot)
    {
        var stack = new StackPanel { Spacing = 10 };
        var report = snapshot.Models;
        var entries = SelectedModelEntries(snapshot);
        var subtitleParts = new List<string>();
        if (report is not null)
        {
            subtitleParts.Add($"{entries.Count} models · {Format.Usd(entries.Sum(e => e.Cost))}");
            if (report.PricingUpdatedAt is { } ts)
            {
                subtitleParts.Add($"Prices updated {Format.RelativeTime(ts)}");
            }
        }

        // Token-kind legend header, the macOS Models card top row.
        var content = new StackPanel { Spacing = 10 };
        var legend = new WrapRow();
        (string Label, string Color)[] kinds =
        [
            ("Input", Ui.TokenKinds[0].Color),
            ("Output", Ui.TokenKinds[1].Color),
            ("Cache read", Ui.TokenKinds[2].Color),
            ("Cache write", Ui.TokenKinds[3].Color),
            ("Reasoning", Ui.TokenKinds[4].Color),
        ];
        foreach (var (label, color) in kinds)
        {
            var item = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            item.Children.Add(new Microsoft.UI.Xaml.Shapes.Rectangle
            {
                Width = 8,
                Height = 8,
                RadiusX = 2,
                RadiusY = 2,
                Fill = Ui.BrushFromHex(color),
                VerticalAlignment = VerticalAlignment.Center,
            });
            item.Children.Add(Ui.Text(label, 10, 0.75));
            legend.Children.Add(item);
        }

        content.Children.Add(legend);
        content.Children.Add(BuildModelRows(snapshot, maxRows: null));
        stack.Children.Add(Ui.Card("Models", content, string.Join(" · ", subtitleParts)));
        return stack;
    }

    private List<ModelReportEntry> SelectedModelEntries(DashboardModel.Snapshot snapshot) =>
        (snapshot.Models?.Entries ?? [])
            .Where(e => _selectedSet.Contains(ClientRegistry.CanonicalClient(e.Client)))
            .ToList();

    private FrameworkElement BuildModelRows(DashboardModel.Snapshot snapshot, int? maxRows)
    {
        var panel = new StackPanel { Spacing = 8 };
        var entries = SelectedModelEntries(snapshot)
            .OrderByDescending(e => e.Cost)
            .ToList();
        if (maxRows is { } cap)
        {
            entries = entries.Take(cap).ToList();
        }

        if (entries.Count == 0)
        {
            panel.Children.Add(Ui.Dim("No model data yet."));
            return panel;
        }

        var colors = new ModelColorMap(snapshot.Models);
        foreach (var entry in entries)
        {
            var block = new StackPanel { Spacing = 3 };
            var name = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            var (discHost, discGlow) = GlowingDisc(colors.Color(entry.Provider, entry.Model));
            name.Children.Add(discHost);
            name.Children.Add(Ui.Text(entry.Model, 11));
            // Right column, macOS style: tokens over cost.
            var trailing = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right };
            var tokensText = Ui.Text(Format.CompactTokens(entry.Total), 11, 0.9);
            tokensText.HorizontalAlignment = HorizontalAlignment.Right;
            var costText = Ui.Text(Format.Usd(entry.Cost), 10, 0.65);
            costText.HorizontalAlignment = HorizontalAlignment.Right;
            trailing.Children.Add(tokensText);
            trailing.Children.Add(costText);
            block.Children.Add(Ui.Row(name, trailing));
            var tokenBar = TokenKindBar(entry);
            var barHost = new Grid { Height = 4 };
            // White alpha brightens each segment; the accent edge reads as a halo.
            var barGlow = new Border
            {
                Background = new SolidColorBrush(HoverGlowFill),
                BorderBrush = new SolidColorBrush(Colors.Transparent),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(1),
                IsHitTestVisible = false,
                Opacity = 0,
                RenderTransform = new ScaleTransform { ScaleX = 1, ScaleY = 1.75 },
                RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5),
            };
            barHost.Children.Add(tokenBar);
            barHost.Children.Add(barGlow);
            block.Children.Add(barHost);
            var captured = entry;
            // One row target covers the disc and bar so their hover state cannot drift.
            AttachHoverOutline(block, hovered =>
            {
                if (hovered)
                {
                    var accent = HoverOutlineBrush();
                    barGlow.BorderBrush = accent;
                    discGlow.Stroke = accent;
                }

                barGlow.Opacity = hovered ? 1 : 0;
                discGlow.Opacity = hovered ? 1 : 0;
            });
            HoverTip.AttachRich(block, () => ModelTip(captured, colors));
            panel.Children.Add(block);
        }

        return panel;
    }

    // Hover brightening for the model bar and its disc. Tuned down from the
    // first attempt, which read as washed-out rather than lit; white over a
    // saturated fill loses colour faster than it gains brightness.
    private static readonly Color HoverGlowFill = Color.FromArgb(48, 255, 255, 255);

    /// <summary>A provider disc plus its hover glow. The fixed host keeps the
    /// disc's arranged size while the glow scales out, so lighting one up never
    /// moves the row. Used by the Models lens and by the per-model rows inside
    /// an expanded Daily entry, which must light up the same way.</summary>
    private static (Grid Host, Ellipse Glow) GlowingDisc(string hex, double size = 8)
    {
        var disc = Ui.Disc(hex, size);
        var glow = new Ellipse
        {
            Width = size,
            Height = size,
            Fill = new SolidColorBrush(HoverGlowFill),
            Stroke = new SolidColorBrush(Colors.Transparent),
            StrokeThickness = 1,
            IsHitTestVisible = false,
            Opacity = 0,
            RenderTransform = new ScaleTransform { ScaleX = 1.35, ScaleY = 1.35 },
            RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5),
        };
        var host = new Grid
        {
            Width = size,
            Height = size,
            VerticalAlignment = VerticalAlignment.Center,
        };
        host.Children.Add(disc);
        host.Children.Add(glow);
        return (host, glow);
    }

    // Keep hover state attached to the target; visual changes do not alter
    // measured or arranged bounds while the tooltip follows the pointer.
    private static void AttachHoverOutline(
        FrameworkElement target, Action<bool> setVisible)
    {
        // A panel with no Background is hit-tested only where its children are,
        // so the gap between a row's left and right columns would fire
        // PointerExited while the pointer is still inside the lit row. Every
        // hover target routes through here, so one guard covers them all.
        if (target is Panel panel && panel.Background is null)
        {
            panel.Background = new SolidColorBrush(Colors.Transparent);
        }

        target.PointerEntered += (_, _) => setVisible(true);
        target.PointerExited += (_, _) => setVisible(false);
        target.Unloaded += (_, _) => setVisible(false);
    }

    private Brush HoverOutlineBrush() =>
        (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"];

    // ── Daily lens ───────────────────────────────────────────────────────

    private UIElement BuildDaily(DashboardModel.Snapshot snapshot)
    {
        var panel = new StackPanel { Spacing = 6 };
        var colors = new ModelColorMap(snapshot.Models);
        var days = DailyRows.Build(snapshot.Graph, _selectedClients);
        if (days.Count == 0)
        {
            panel.Children.Add(Ui.Dim("No active days."));
        }

        foreach (var selectedDay in days)
        {
            var block = new StackPanel { Spacing = 4 };
            var summary = $"{selectedDay.Messages} msgs";
            if (selectedDay.Turns is { } turns)
            {
                var scope = selectedDay.TurnClients.Count switch
                {
                    1 => $"{ClientRegistry.ShortName(selectedDay.TurnClients[0])} only",
                    > 1 => $"{string.Join(" + ", selectedDay.TurnClients.Select(ClientRegistry.ShortName))} only",
                    _ => "selected clients",
                };
                summary += $" · {turns} turns · {scope}";
            }

            summary += $" · {Format.CompactTokens(selectedDay.Tokens)} · {Format.Usd(selectedDay.Cost)}";
            var head = Ui.Row(
                Ui.Text(Format.MonthDay(selectedDay.Date), 12, bold: true),
                Ui.Text(summary, 11, 0.8));
            block.Children.Add(head);

            if (_expandedDay == selectedDay.Date)
            {
                foreach (var client in selectedDay.Clients)
                {
                    var name = new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 6,
                        Margin = new Thickness(12, 0, 0, 0),
                    };
                    var (subDiscHost, subDiscGlow) =
                        GlowingDisc(colors.Color(client.ProviderId, client.ModelId), 6);
                    name.Children.Add(subDiscHost);
                    name.Children.Add(Ui.Text(
                        $"{client.ModelId} · {ClientRegistry.ShortName(client.Client)}", 10, 0.85));
                    var row = Ui.Row(
                        name,
                        Ui.Text($"{Format.CompactTokens(client.Tokens.Total)} · {Format.Usd(client.Cost)}", 10, 0.7));
                    var capturedClient = client;
                    // Same treatment as the Models lens: the disc lights up with
                    // the card, so the row the card describes is unambiguous.
                    // Highlight the sub-row itself, not the day card that
                    // encloses it: the card the tooltip describes is this model,
                    // and lighting the whole day would point at the wrong thing.
                    var rowGlow = new Border
                    {
                        BorderBrush = new SolidColorBrush(Colors.Transparent),
                        BorderThickness = new Thickness(1),
                        CornerRadius = new CornerRadius(3),
                        IsHitTestVisible = false,
                        Opacity = 0,
                    };
                    var rowHost = new Grid();
                    rowHost.Children.Add(row);
                    rowHost.Children.Add(rowGlow);
                    AttachHoverOutline(rowHost, hovered =>
                    {
                        if (hovered)
                        {
                            var accent = HoverOutlineBrush();
                            subDiscGlow.Stroke = accent;
                            rowGlow.BorderBrush = accent;
                        }

                        subDiscGlow.Opacity = hovered ? 1 : 0;
                        rowGlow.Opacity = hovered ? 0.55 : 0;
                    });
                    HoverTip.AttachRich(rowHost, () => ModelTip(capturedClient, colors));
                    block.Children.Add(rowHost);
                }
            }

            var card = new Border
            {
                Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(10, 8, 10, 8),
                Child = block,
            };
            var date = selectedDay.Date;
            card.Tapped += (_, _) =>
            {
                _expandedDay = _expandedDay == date ? null : date;
                RenderContent(animated: false);
            };

            panel.Children.Add(card);
        }

        return panel;
    }

    // ── Hourly lens ──────────────────────────────────────────────────────

    private UIElement BuildHourly(DashboardModel.Snapshot snapshot)
    {
        var stack = new StackPanel { Spacing = 10 };
        if (snapshot.Hourly is not { } hourly)
        {
            stack.Children.Add(Ui.Card("Hourly", Ui.Dim("Loading hourly data…")));
            return stack;
        }

        var toggle = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        var timelineBtn = LensPill("Timeline", !_hourlyProfileMode);
        var profileBtn = LensPill("Profile", _hourlyProfileMode);
        timelineBtn.Click += (_, _) => { _hourlyProfileMode = false; RenderContent(false); };
        profileBtn.Click += (_, _) => { _hourlyProfileMode = true; RenderContent(false); };
        toggle.Children.Add(timelineBtn);
        toggle.Children.Add(profileBtn);
        stack.Children.Add(toggle);

        if (_hourlyProfileMode)
        {
            // 24-hour rhythm profile.
            var byHour = new long[24];
            foreach (var entry in hourly.Entries)
            {
                // "YYYY-MM-DD HH:00" local slots.
                if (entry.Hour.Length >= 13 &&
                    int.TryParse(entry.Hour.AsSpan(11, 2), out var h) && h is >= 0 and < 24)
                {
                    byHour[h] += entry.Total;
                }
            }

            var max = Math.Max(1, byHour.Max());
            var panel = new StackPanel { Spacing = 3 };
            for (var h = 0; h < 24; h++)
            {
                var row = new Grid { ColumnSpacing = 8 };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.Children.Add(Ui.Text($"{h:D2}:00", 10, 0.7));
                var bar = Ui.ShareBar(byHour[h] / (double)max, h == DateTime.Now.Hour ? "#22c55e" : "#3b82f6");
                bar.VerticalAlignment = VerticalAlignment.Center;
                Grid.SetColumn(bar, 1);
                row.Children.Add(bar);
                var count = Ui.Text(Format.CompactTokens(byHour[h]), 10, 0.7);
                Grid.SetColumn(count, 2);
                row.Children.Add(count);
                panel.Children.Add(row);
            }

            stack.Children.Add(Ui.Card("24h profile", panel));
        }
        else
        {
            var entries = hourly.Entries.AsEnumerable().Reverse().ToList(); // newest first
            // Invariant: the engine's entry.Hour keys are Gregorian "yyyy-MM-dd
            // HH:00"; a non-Gregorian ambient calendar (th-TH Buddhist, etc.)
            // would render a different year here and the current-hour highlight
            // would never match.
            var currentSlot = DateTime.Now.ToString(
                "yyyy-MM-dd HH:00", System.Globalization.CultureInfo.InvariantCulture);
            var panel = new StackPanel { Spacing = 5 };
            foreach (var entry in entries.Take(_hourlyWindow))
            {
                var label = Ui.Text(entry.Hour, 11, entry.Hour == currentSlot ? 1.0 : 0.8);
                if (entry.Hour == currentSlot)
                {
                    label.Foreground = Ui.BrushFromHex("#22c55e");
                }

                panel.Children.Add(Ui.Row(
                    label,
                    Ui.Text(
                        $"{Format.CompactTokens(entry.Total)} · {Format.Usd(entry.Cost)}", 11, 0.75)));
            }

            if (entries.Count > _hourlyWindow)
            {
                var more = LensPill($"Show more ({entries.Count - _hourlyWindow} left)", false);
                more.HorizontalAlignment = HorizontalAlignment.Center;
                more.Click += (_, _) => { _hourlyWindow += 48; RenderContent(false); };
                panel.Children.Add(more);
            }

            stack.Children.Add(Ui.Card("Timeline", panel, $"{hourly.Entries.Count} slots"));
        }

        return stack;
    }

    // ── Stats lens ───────────────────────────────────────────────────────

    private UIElement BuildStats(DashboardModel.Snapshot snapshot)
    {
        var stack = new StackPanel { Spacing = 10 };
        stack.Children.Add(BuildUsageChartCard(snapshot));

        var stats = _selectedStats ?? new UsageStats(snapshot.Graph, _selectedSet);
        var grid = new Grid { ColumnSpacing = 16, RowSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition());
        grid.RowDefinitions.Add(new RowDefinition());
        var favorite = SelectedModelEntries(snapshot)
            .OrderByDescending(e => e.Cost)
            .FirstOrDefault();
        (string Value, string Label)[] metrics =
        [
            (Format.Usd(stats.TotalCost), "total spend"),
            (Format.CompactTokens(stats.TotalTokens), "tokens"),
            ($"{stats.ActiveDays}", "active days"),
            (Format.Usd(stats.AveragePerDay), "avg/day"),
            (stats.BestDay is { } b ? Format.MonthDay(b.Date) : "—", "best day"),
        ];
        for (var i = 0; i < metrics.Length; i++)
        {
            var cell = Metric(metrics[i].Value, metrics[i].Label);
            Grid.SetColumn(cell, i % 3);
            Grid.SetRow(cell, i / 3);
            grid.Children.Add(cell);
        }

        // Favorite model gets a full-width row — long model ids don't fit a
        // third of the card.
        grid.RowDefinitions.Add(new RowDefinition());
        var favoriteCell = Metric(favorite?.Model ?? "—", "favorite model");
        Grid.SetRow(favoriteCell, 2);
        Grid.SetColumnSpan(favoriteCell, 3);
        grid.Children.Add(favoriteCell);

        stack.Children.Add(Ui.Card("Stats", grid));
        stack.Children.Add(Ui.Card("Streaks", BuildStreaks(snapshot)));
        return stack;
    }

    // ── Agents lens ──────────────────────────────────────────────────────

    private UIElement BuildAgents(DashboardModel.Snapshot snapshot)
    {
        var stack = new StackPanel { Spacing = 10 };
        if (snapshot.Agents is not { } agents)
        {
            stack.Children.Add(Ui.Card("Agents", Ui.Dim("Loading agent data…")));
            return stack;
        }

        var panel = new StackPanel { Spacing = 8 };
        var entries = agents.Entries.OrderByDescending(e => e.Cost).ToList();
        var maxCost = Math.Max(entries.FirstOrDefault()?.Cost ?? 0, 0.0001);
        foreach (var entry in entries)
        {
            var block = new StackPanel { Spacing = 3 };
            block.Children.Add(Ui.Row(
                Ui.Text(entry.Agent, 11, bold: true),
                Ui.Text(
                    $"{entry.Messages} msgs · {Format.CompactTokens(entry.Total)} · {Format.Usd(entry.Cost)}",
                    10, 0.75)));
            block.Children.Add(Ui.ShareBar(entry.Cost / maxCost, "#3b82f6"));
            block.Children.Add(Ui.Dim(string.Join(", ",
                entry.Clients.Select(ClientRegistry.ShortName)), 9));
            panel.Children.Add(block);
        }

        if (entries.Count == 0)
        {
            panel.Children.Add(Ui.Dim("No sub-agent usage recorded."));
        }

        stack.Children.Add(Ui.Card(
            "Agents by cost", panel, $"{agents.TotalMessages} messages"));
        return stack;
    }

    // ── shared pieces ────────────────────────────────────────────────────

    private static StackPanel Metric(string value, string label)
    {
        var cell = new StackPanel();
        cell.Children.Add(Ui.Text(value, 16, bold: true));
        cell.Children.Add(Ui.Dim(label, 10));
        return cell;
    }

    private static Button LensPill(string text, bool active) => new()
    {
        Content = text,
        FontSize = 10,
        Padding = new Thickness(8, 3, 8, 3),
        Opacity = active ? 1.0 : 0.6,
        FontWeight = active
            ? Microsoft.UI.Text.FontWeights.SemiBold
            : Microsoft.UI.Text.FontWeights.Normal,
    };

    // ── rich tooltips (macOS hover-card layouts; explicit light foreground
    //    because the tip card is always dark) ─────────────────────────────

    private static TextBlock TipText(string text, double size = 11, double opacity = 1.0,
        bool bold = false) => new()
    {
        Text = text,
        FontSize = size,
        Opacity = opacity,
        FontWeight = bold ? Microsoft.UI.Text.FontWeights.SemiBold
            : Microsoft.UI.Text.FontWeights.Normal,
        Foreground = new SolidColorBrush(Color.FromArgb(255, 240, 240, 245)),
    };

    private static Grid TipRow(UIElement left, string right, double opacity = 0.75)
    {
        var grid = new Grid { ColumnSpacing = 16 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(left);
        var value = TipText(right, 11, opacity);
        value.HorizontalAlignment = HorizontalAlignment.Right;
        Grid.SetColumn(value, 1);
        grid.Children.Add(value);
        return grid;
    }

    private static StackPanel TipLabel(string hex, string text, bool square = false)
    {
        var label = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        if (square)
        {
            label.Children.Add(new Rectangle
            {
                Width = 8,
                Height = 8,
                RadiusX = 2,
                RadiusY = 2,
                Fill = Ui.BrushFromHex(hex),
                VerticalAlignment = VerticalAlignment.Center,
            });
        }
        else
        {
            label.Children.Add(Ui.Disc(hex, 7));
        }

        label.Children.Add(TipText(text, 11, 0.95));
        return label;
    }

    /// <summary>Whole-day chart tooltip: date, totals row, then one dotted
    /// line per segment — the macOS bar tooltip.</summary>
    private UIElement DayTip(DayBar bar)
    {
        var panel = new StackPanel { Spacing = 5, MinWidth = 200 };
        panel.Children.Add(TipText(Format.MonthDay(bar.Date), 12, bold: true));
        panel.Children.Add(TipRow(
            TipText($"{Format.ExactTokens(bar.TotalTokens)} tokens", 11, 0.9),
            Format.Usd(bar.TotalCost), 0.9));
        var ordered = _chartMetric == ChartMetric.Cost
            ? bar.Segments.OrderByDescending(s => s.Cost)
            : bar.Segments.OrderByDescending(s => s.Tokens);
        foreach (var seg in ordered)
        {
            panel.Children.Add(TipRow(
                TipLabel(seg.Color, seg.Label),
                $"{Format.CompactTokens(seg.Tokens)} · {Format.Usd(seg.Cost)}"));
        }

        return panel;
    }

    /// <summary>Model-row tooltip: header disc + name, source line, totals,
    /// then per-kind colored rows with share percentages — the macOS
    /// ModelBreakdownCard hover.</summary>
    private static UIElement ModelTip(ContributionClient entry, ModelColorMap colors) =>
        ModelTip(new ModelReportEntry(
            entry.Client,
            entry.ModelId,
            entry.ProviderId,
            entry.Tokens.Input,
            entry.Tokens.Output,
            entry.Tokens.CacheRead,
            entry.Tokens.CacheWrite,
            entry.Tokens.Reasoning,
            entry.Tokens.Total,
            entry.Messages,
            entry.Cost), colors, includeZeroKinds: true);

    private static UIElement ModelTip(
        ModelReportEntry entry, ModelColorMap colors, bool includeZeroKinds = false)
    {
        long[] values =
            [entry.Input, entry.Output, entry.CacheRead, entry.CacheWrite, entry.Reasoning];
        // Same saturating aggregate TokenBreakdown.Total uses: a corrupt lane
        // clamped to long.MaxValue must not throw out of a tooltip, and
        // Enumerable.Sum over long is checked.
        var total = Math.Max(
            1, values.Aggregate(0L, (running, v) => running.SaturatingAdd(v)));
        var panel = new StackPanel { Spacing = 5, MinWidth = 200 };
        var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        head.Children.Add(Ui.Disc(colors.Color(entry.Provider, entry.Model), 7));
        head.Children.Add(TipText(entry.Model, 12, bold: true));
        panel.Children.Add(head);
        panel.Children.Add(TipText(
            $"{ClientRegistry.ShortName(entry.Client)} · {entry.Provider}", 10, 0.6));
        panel.Children.Add(TipRow(
            TipText($"{Format.CompactTokens(entry.Total)} tokens", 11, 0.9),
            Format.Usd(entry.Cost), 0.9));
        (string Label, string Color)[] kinds =
        [
            ("Input", Ui.TokenKinds[0].Color),
            ("Output", Ui.TokenKinds[1].Color),
            ("Cache read", Ui.TokenKinds[2].Color),
            ("Cache write", Ui.TokenKinds[3].Color),
            ("Reasoning", Ui.TokenKinds[4].Color),
        ];
        for (var i = 0; i < values.Length; i++)
        {
            if (includeZeroKinds || values[i] > 0)
            {
                panel.Children.Add(TipRow(
                    TipLabel(kinds[i].Color, kinds[i].Label, square: true),
                    $"{Format.CompactTokens(values[i])} · {100.0 * values[i] / total:F0}%"));
            }
        }

        return panel;
    }

    private static Grid TokenKindBar(ModelReportEntry entry)
    {
        long[] values =
            [entry.Input, entry.Output, entry.CacheRead, entry.CacheWrite, entry.Reasoning];
        var weights = values.Select(v => Math.Sqrt(v)).ToArray();
        var total = weights.Sum();
        var bar = new Grid { Height = 4, ColumnSpacing = 1 };
        if (total <= 0)
        {
            return bar;
        }

        for (var i = 0; i < values.Length; i++)
        {
            if (weights[i] <= 0)
            {
                continue;
            }

            bar.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(weights[i] / total, GridUnitType.Star),
            });
            var seg = new Rectangle
            {
                Fill = Ui.BrushFromHex(Ui.TokenKinds[i].Color),
                RadiusX = 1,
                RadiusY = 1,
            };
            Grid.SetColumn(seg, bar.ColumnDefinitions.Count - 1);
            bar.Children.Add(seg);
        }

        return bar;
    }

    internal const string PaceOrange = "#ff9500"; // macOS Color.orange

    /// <summary>Render one complete quota window row from Core's precomputed
    /// display values. The responsive footer is built once and only toggles
    /// visibility when the actual row width crosses its measured threshold.</summary>
    internal static FrameworkElement QuotaRow(
        UsageWindow window, UsagePaceRowPresentation row, bool classic)
    {
        var root = new StackPanel { Spacing = 3 };
        var headerTrailing = Ui.Text(
            classic ? window.ResetText ?? row.AmountText : window.ResetText ?? "",
            10, 0.6);
        root.Children.Add(Ui.Row(
            Ui.Text(window.Label, 11, bold: true), headerTrailing));
        root.Children.Add(GaugeBar(
            row.FillPercent,
            row.RemainingPercent,
            classic ? null : row.MarkerPercent,
            classic ? null : row.ExpectedUsedPercent,
            row.IsHistoricalDeficit));

        TextBlock AmountLabel() => Ui.Text(row.AmountText, 10, 0.75);
        TextBlock PaceLabel(string text)
        {
            var label = Ui.Text(text, 10, row.IsHistoricalDeficit ? 1.0 : 0.7);
            label.HorizontalAlignment = HorizontalAlignment.Right;
            label.TextAlignment = TextAlignment.Right;
            if (row.IsHistoricalDeficit)
            {
                label.Foreground = Ui.BrushFromHex(PaceOrange);
            }

            return label;
        }

        if (classic)
        {
            if (window.ResetText is not null)
            {
                root.Children.Add(AmountLabel());
            }

            return root;
        }

        if (string.IsNullOrEmpty(row.ProjectionText))
        {
            if (string.IsNullOrEmpty(row.PaceText))
            {
                root.Children.Add(AmountLabel());
            }
            else
            {
                root.Children.Add(Ui.Row(AmountLabel(), PaceLabel(row.PaceText)));
            }

            return root;
        }

        var wideAmount = AmountLabel();
        var wideDetails = PaceLabel(string.Join(" · ",
            new[] { row.PaceText, row.ProjectionText }
                .Where(static text => !string.IsNullOrEmpty(text))));
        var wideFooter = Ui.Row(wideAmount, wideDetails);

        var narrowFooter = new StackPanel { Spacing = 1 };
        var narrowFirst = string.IsNullOrEmpty(row.PaceText)
            ? (FrameworkElement)AmountLabel()
            : Ui.Row(AmountLabel(), PaceLabel(row.PaceText));
        narrowFooter.Children.Add(narrowFirst);

        var narrowProjection = PaceLabel(row.ProjectionText);
        narrowProjection.TextWrapping = TextWrapping.Wrap;
        narrowProjection.TextTrimming = TextTrimming.None;
        narrowProjection.HorizontalAlignment = HorizontalAlignment.Right;
        narrowProjection.TextAlignment = TextAlignment.Right;
        var projectionRow = new Grid();
        projectionRow.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(1, GridUnitType.Star),
        });
        projectionRow.Children.Add(narrowProjection);
        narrowFooter.Children.Add(projectionRow);

        narrowFooter.Visibility = Visibility.Collapsed;
        root.Children.Add(wideFooter);
        root.Children.Add(narrowFooter);

        wideAmount.Measure(new Windows.Foundation.Size(
            double.PositiveInfinity, double.PositiveInfinity));
        wideDetails.Measure(new Windows.Foundation.Size(
            double.PositiveInfinity, double.PositiveInfinity));
        var requiredWidth = wideAmount.DesiredSize.Width
            + wideDetails.DesiredSize.Width + 8;
        var wideVisible = true;
        root.SizeChanged += (_, _) =>
        {
            var width = root.ActualWidth;
            if (!double.IsFinite(width) || width <= 0)
            {
                return;
            }

            var nextWide = wideVisible
                ? width >= requiredWidth
                : width > requiredWidth + 8;
            if (nextWide == wideVisible)
            {
                return;
            }

            wideVisible = nextWide;
            wideFooter.Visibility = nextWide ? Visibility.Visible : Visibility.Collapsed;
            narrowFooter.Visibility = nextWide ? Visibility.Collapsed : Visibility.Visible;
        };

        return root;
    }

    /// <summary>The quota bar: fills by used or remaining per the setting,
    /// colors by remaining either way (macOS gaugeColor), and carries the
    /// precomputed pace marker on the same axis. Internal: the settings
    /// window's preview column renders through the same bar.</summary>
    internal static FrameworkElement GaugeBar(
        double fillPercent,
        double remainingForColor,
        double? markerPercent,
        double? expectedUsedPercent,
        bool historicalDeficit)
    {
        var fill = double.IsFinite(fillPercent)
            ? Math.Clamp(fillPercent, 0, 100) : 0;
        var remaining = double.IsFinite(remainingForColor)
            ? Math.Clamp(remainingForColor, 0, 100) : 100;
        var color = remaining <= 10 ? "#ef4444"
            : remaining <= 25 ? "#f59e0b" : "#22c55e";
        var track = new Grid
        {
            Height = 5,
            CornerRadius = new CornerRadius(2.5),
            Background = new SolidColorBrush(Color.FromArgb(36, 128, 128, 128)),
        };
        track.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(fill, GridUnitType.Star),
        });
        track.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(100 - fill, GridUnitType.Star),
        });
        track.Children.Add(new Border
        {
            Background = Ui.BrushFromHex(color),
            CornerRadius = new CornerRadius(2.5),
        });
        if (markerPercent is not { } markerValue || !double.IsFinite(markerValue))
        {
            return track;
        }

        // The shared row owns the axis; this renderer only places the tick.
        var markerPosition = Math.Clamp(markerValue, 0, 100);
        var holder = new Grid { Height = 9 };
        track.VerticalAlignment = VerticalAlignment.Center;
        holder.Children.Add(track);
        var lanes = new Grid();
        lanes.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(markerPosition, GridUnitType.Star),
        });
        lanes.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(100 - markerPosition, GridUnitType.Star),
        });
        var marker = new Rectangle
        {
            Width = 1.5,
            RadiusX = 0.75,
            RadiusY = 0.75,
            Fill = historicalDeficit ? Ui.BrushFromHex(PaceOrange)
                : new SolidColorBrush(Color.FromArgb(150, 160, 160, 160)),
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 0, -0.75, 0),
        };
        Grid.SetColumn(marker, 0);
        lanes.Children.Add(marker);
        holder.Children.Add(lanes);
        if (expectedUsedPercent is { } expected && double.IsFinite(expected))
        {
            var expectedUsed = Math.Clamp(expected, 0, 100);
            HoverTip.Attach(marker, () => $"Expected {expectedUsed:F0}% used by now");
        }

        return holder;
    }
}

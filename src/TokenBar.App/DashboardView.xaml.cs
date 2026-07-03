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

    public DashboardView()
    {
        InitializeComponent();

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
        QuitButton.Click += (_, _) => Application.Current.Exit();

        // Limits/trace settings re-render the open flyout live (the macOS
        // panel's right-column preview equivalent is the flyout itself).
        AppSettings.Store.Changed += key =>
        {
            if (key.StartsWith("tokenbar.limits.", StringComparison.Ordinal)
                || key == "tokenbar.trace.detailed")
            {
                _ = DispatcherQueue.TryEnqueue(() => RenderContent(animated: false));
            }
        };

        // In-flyout shortcuts, the macOS ⌘ set on Ctrl: Esc/Ctrl+W close,
        // Ctrl+R refresh, Ctrl+, settings, Ctrl+Q quit, Ctrl+1..6 lenses,
        // Ctrl+[ / Ctrl+] cycle.
        AddAccel(Windows.System.VirtualKey.Escape, Windows.System.VirtualKeyModifiers.None,
            () => HideRequested?.Invoke());
        AddAccel(Windows.System.VirtualKey.W, Windows.System.VirtualKeyModifiers.Control,
            () => HideRequested?.Invoke());
        AddAccel(Windows.System.VirtualKey.R, Windows.System.VirtualKeyModifiers.Control,
            () =>
            {
                _model?.RefreshForce();
                UpdateRefreshControl();
            });
        AddAccel((Windows.System.VirtualKey)0xBC /* comma */,
            Windows.System.VirtualKeyModifiers.Control,
            () => TrayService.OpenSettings?.Invoke());
        AddAccel(Windows.System.VirtualKey.Q, Windows.System.VirtualKeyModifiers.Control,
            () => Application.Current.Exit());
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
    }

    /// <summary>The flyout owns hiding (Esc/Ctrl+W land here).</summary>
    public event Action? HideRequested;

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

        var graph = snapshot.Graph;
        TodayValue.Text = Format.CompactTokens(Format.TodayTokens(graph));
        TotalValue.Text = Format.CompactTokens(graph.Summary.TotalTokens);
        RateValue.Text = Format.CompactTokens((long)snapshot.TokensPerMin);
        CostLine.Text =
            $"{Format.Usd(Format.TodayCost(graph))} today · {Format.Usd(graph.Summary.TotalCost)} all time · " +
            $"{graph.Summary.ActiveDays} active days";
        FooterText.Text = $"updated {snapshot.FetchedAt:HH:mm:ss}";
        UpdateYearPicker();
        RenderContent(animated: false);
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

    private void OnWheel(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        ScrollBy(-e.GetCurrentPoint(this).Properties.MouseWheelDelta);
        e.Handled = true;
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

    private void RenderContent(bool animated)
    {
        if (_snapshot is null)
        {
            return;
        }

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
        stack.Children.Add(Ui.Card("Token Usage", BuildChart(snapshot), "30 days"));
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

    private FrameworkElement BuildChart(DashboardModel.Snapshot snapshot)
    {
        var colors = new ModelColorMap(snapshot.Models);
        var bars = DayBars.Build(
            snapshot.Graph,
            [.. snapshot.Graph.Summary.Clients],
            _chartStackBy,
            colors,
            Format.TodayKey());

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
                    // Full-height hover column: ONE tooltip per day listing
                    // every segment with its color dot (the macOS layout),
                    // instead of sliver-sized per-segment targets.
                    var overlay = new Rectangle
                    {
                        Width = barWidth + gap,
                        Height = height,
                        Fill = new SolidColorBrush(Colors.Transparent),
                    };
                    Canvas.SetLeft(overlay, x - gap / 2);
                    Canvas.SetTop(overlay, 0);
                    var capturedBar = bar;
                    HoverTip.AttachRich(overlay, () => DayTip(capturedBar));
                    canvas.Children.Add(overlay);
                }
            }
        }

        canvas.SizeChanged += (_, _) => Draw();
        return holder;
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

            foreach (var window in agent.Windows)
            {
                var remaining = Math.Clamp(window.RemainingPercent, 0, 100);
                var used = Math.Clamp(window.UsedPercent, 0, 100);
                var fill = asUsed ? used : remaining;
                var amount = asUsed ? $"{used:F0}% used" : $"{remaining:F0}% left";
                var pace = classic ? null : UsagePace.Compute(window, paceMode, now);
                var paceText = pace is null ? ""
                    : pace.EtaText is { } eta ? $"{pace.Label} · {eta}" : pace.Label;
                var paceLabel = Ui.Text(paceText, 10,
                    pace?.Stage.IsDeficit() == true ? 1.0 : 0.7);
                if (pace?.Stage.IsDeficit() == true)
                {
                    paceLabel.Foreground = Ui.BrushFromHex(PaceOrange);
                }

                section.Children.Add(Ui.Row(
                    Ui.Text($"{window.Label} · {amount}", 11),
                    paceLabel));
                section.Children.Add(GaugeBar(fill, remaining, pace, asUsed));
                if (!classic && window.ResetText is { } reset)
                {
                    section.Children.Add(Ui.Dim(reset, 10));
                }
            }

            panel.Children.Add(section);
        }

        return panel;
    }

    private static FrameworkElement? BuildTrace(DashboardModel.Snapshot snapshot)
    {
        // tokenbar.trace.detailed (macOS UsageTraceCard): one row per
        // agent-and-model bucket instead of one collapsed row per app.
        var detailed = AppSettings.Store.GetBool("tokenbar.trace.detailed", false);
        var rows = detailed
            ? snapshot.Trace
                .Select(b => (b.Client, b.Model, b.TokensPerMin)).Take(5).ToList()
            : TraceCollapse.CollapseByClient(snapshot.Trace)
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

    private static FrameworkElement BuildStreaks(DashboardModel.Snapshot snapshot)
    {
        var stats = new UsageStats(
            snapshot.Graph, new HashSet<string>(snapshot.Graph.Summary.Clients));
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
        var subtitleParts = new List<string>();
        if (report is not null)
        {
            subtitleParts.Add($"{report.Entries.Count} models · {Format.Usd(report.TotalCost)}");
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

    private FrameworkElement BuildModelRows(DashboardModel.Snapshot snapshot, int? maxRows)
    {
        var panel = new StackPanel { Spacing = 8 };
        var entries = (snapshot.Models?.Entries ?? [])
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
            name.Children.Add(Ui.Disc(colors.Color(entry.Provider, entry.Model)));
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
            block.Children.Add(TokenKindBar(entry));
            var captured = entry;
            HoverTip.AttachRich(block, () => ModelTip(captured, colors));
            panel.Children.Add(block);
        }

        return panel;
    }

    // ── Daily lens ───────────────────────────────────────────────────────

    private UIElement BuildDaily(DashboardModel.Snapshot snapshot)
    {
        var panel = new StackPanel { Spacing = 6 };
        var colors = new ModelColorMap(snapshot.Models);
        var days = snapshot.Graph.Contributions
            .Where(c => c.Totals.Tokens > 0 || c.Totals.Cost > 0)
            .Reverse()
            .ToList();
        if (days.Count == 0)
        {
            panel.Children.Add(Ui.Dim("No active days."));
        }

        foreach (var day in days)
        {
            var block = new StackPanel { Spacing = 4 };
            var head = Ui.Row(
                Ui.Text(Format.MonthDay(day.Date), 12, bold: true),
                Ui.Text(
                    $"{day.Totals.Messages} msgs · {Format.CompactTokens(day.Totals.Tokens)} · {Format.Usd(day.Totals.Cost)}",
                    11, 0.8));
            block.Children.Add(head);

            if (_expandedDay == day.Date)
            {
                foreach (var client in day.Clients.OrderByDescending(c => c.Cost))
                {
                    var t = client.Tokens;
                    var tokens = t.Input + t.Output + t.CacheRead + t.CacheWrite + t.Reasoning;
                    var name = new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 6,
                        Margin = new Thickness(12, 0, 0, 0),
                    };
                    name.Children.Add(Ui.Disc(colors.Color(client.ProviderId, client.ModelId), 6));
                    name.Children.Add(Ui.Text(
                        $"{client.ModelId} · {ClientRegistry.ShortName(client.Client)}", 10, 0.85));
                    block.Children.Add(Ui.Row(
                        name,
                        Ui.Text($"{Format.CompactTokens(tokens)} · {Format.Usd(client.Cost)}", 10, 0.7)));
                }
            }

            var card = new Border
            {
                Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(10, 8, 10, 8),
                Child = block,
            };
            var date = day.Date;
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
            var currentSlot = DateTime.Now.ToString("yyyy-MM-dd HH:00");
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
        stack.Children.Add(Ui.Card("Token Usage", BuildChart(snapshot), "30 days"));

        var stats = new UsageStats(
            snapshot.Graph, new HashSet<string>(snapshot.Graph.Summary.Clients));
        var grid = new Grid { ColumnSpacing = 16, RowSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition());
        grid.RowDefinitions.Add(new RowDefinition());
        var favorite = (snapshot.Models?.Entries ?? [])
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
    private static UIElement ModelTip(ModelReportEntry entry, ModelColorMap colors)
    {
        long[] values =
            [entry.Input, entry.Output, entry.CacheRead, entry.CacheWrite, entry.Reasoning];
        var total = Math.Max(1, values.Sum());
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
            if (values[i] > 0)
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

    private const string PaceOrange = "#ff9500"; // macOS Color.orange

    /// <summary>The quota bar: fills by used or remaining per the setting,
    /// colors by remaining either way (macOS gaugeColor), and carries the
    /// pace marker on the same axis so it lines up with the fill. Internal:
    /// the settings window's preview column renders through the same bar.</summary>
    internal static FrameworkElement GaugeBar(
        double fillPercent, double remainingForColor, UsagePace? pace = null,
        bool asUsed = false)
    {
        var remaining = Math.Clamp(fillPercent, 0, 100);
        var color = remainingForColor < 10 ? "#ef4444"
            : remainingForColor < 25 ? "#f59e0b" : "#22c55e";
        var track = new Grid
        {
            Height = 5,
            CornerRadius = new CornerRadius(2.5),
            Background = new SolidColorBrush(Color.FromArgb(36, 128, 128, 128)),
        };
        track.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(remaining, GridUnitType.Star),
        });
        track.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(100 - remaining, GridUnitType.Star),
        });
        track.Children.Add(new Border
        {
            Background = Ui.BrushFromHex(color),
            CornerRadius = new CornerRadius(2.5),
        });
        if (pace is null)
        {
            return track;
        }

        // macOS-parity pace marker: a slim tick at the expected position,
        // taller than the bar; orange in deficit, dim otherwise. It rides
        // whichever axis the fill uses.
        var paceLeft = Math.Clamp(
            asUsed ? pace.ExpectedUsedPercent : 100 - pace.ExpectedUsedPercent, 0, 100);
        var holder = new Grid { Height = 9 };
        track.VerticalAlignment = VerticalAlignment.Center;
        holder.Children.Add(track);
        var lanes = new Grid();
        lanes.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(paceLeft, GridUnitType.Star),
        });
        lanes.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(100 - paceLeft, GridUnitType.Star),
        });
        var marker = new Rectangle
        {
            Width = 1.5,
            RadiusX = 0.75,
            RadiusY = 0.75,
            Fill = pace.Stage.IsDeficit() ? Ui.BrushFromHex(PaceOrange)
                : new SolidColorBrush(Color.FromArgb(150, 160, 160, 160)),
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 0, -0.75, 0),
        };
        Grid.SetColumn(marker, 0);
        lanes.Children.Add(marker);
        holder.Children.Add(lanes);
        var expectedUsed = asUsed ? paceLeft : 100 - paceLeft;
        HoverTip.Attach(marker, () => $"Expected {expectedUsed:F0}% used by now");
        return holder;
    }
}

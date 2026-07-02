using Microsoft.UI;
using Microsoft.UI.Xaml;
// TokenBar.Core.Grid (the contribution-grid builder) collides with the XAML
// Grid — same clash the macOS port hit with SwiftUI's GridLayout.
using Grid = Microsoft.UI.Xaml.Controls.Grid;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using TokenBar.Core;
using TokenBar.Interop;
using Windows.UI;

namespace TokenBar.App;

/// <summary>
/// The overview lens: the macOS OverviewView's card stack (chart, agent
/// limits with pace, live trace, model breakdown, streaks), rendered from a
/// DashboardModel snapshot. XAML shapes instead of Win2D for now — 30 bars ×
/// a handful of segments is trivial, and per-segment native tooltips come
/// free. Win2D enters with the gauges/3D later.
/// </summary>
public sealed partial class DashboardView : UserControl
{
    private static readonly (string Key, string Label, string Color)[] TokenKinds =
    [
        ("input", "In", "#3b82f6"),
        ("output", "Out", "#22c55e"),
        ("cacheRead", "CR", "#f59e0b"),
        ("cacheWrite", "CW", "#a855f7"),
        ("reasoning", "R", "#ec4899"),
    ];

    private DashboardModel.Snapshot? _snapshot;
    private IReadOnlyList<DayBar> _bars = [];

    public DashboardView()
    {
        InitializeComponent();
    }

    public void Render(DashboardModel.Snapshot? snapshot)
    {
        _snapshot = snapshot;
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

        RenderChart(snapshot);
        RenderLimits(snapshot);
        RenderTrace(snapshot);
        RenderModels(snapshot);
        RenderStreaks(snapshot);
        FooterText.Text = $"updated {snapshot.FetchedAt:HH:mm:ss}";
    }

    private void OnChartSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_snapshot is not null)
        {
            RenderChart(_snapshot);
        }
    }

    private void RenderChart(DashboardModel.Snapshot snapshot)
    {
        var colors = new ModelColorMap(snapshot.Models);
        _bars = DayBars.Build(
            snapshot.Graph,
            [.. snapshot.Graph.Summary.Clients],
            StackBy.Model,
            colors,
            Format.TodayKey());

        ChartCanvas.Children.Clear();
        var width = ChartCanvas.ActualWidth;
        var height = ChartCanvas.Height;
        if (width <= 0 || _bars.Count == 0)
        {
            return;
        }

        var maxTokens = Math.Max(1, _bars.Max(b => b.TotalTokens));
        const double gap = 3;
        var barWidth = Math.Max(2, (width - gap * (_bars.Count - 1)) / _bars.Count);
        for (var i = 0; i < _bars.Count; i++)
        {
            var bar = _bars[i];
            var x = i * (barWidth + gap);
            var y = height;
            foreach (var seg in bar.Segments)
            {
                var segHeight = height * seg.Tokens / (double)maxTokens;
                if (segHeight < 0.5)
                {
                    continue;
                }

                y -= segHeight;
                var rect = new Rectangle
                {
                    Width = barWidth,
                    Height = segHeight,
                    Fill = BrushFromHex(seg.Color),
                    RadiusX = 1,
                    RadiusY = 1,
                };
                Canvas.SetLeft(rect, x);
                Canvas.SetTop(rect, y);
                ToolTipService.SetToolTip(rect,
                    $"{Format.MonthDay(bar.Date)} · {seg.Label}\n" +
                    $"{Format.ExactTokens(seg.Tokens)} tokens · {Format.Usd(seg.Cost)}");
                ChartCanvas.Children.Add(rect);
            }

            if (bar.IsEmpty)
            {
                // Empty-day baseline tick so gaps read as "no usage", not "no data".
                var tick = new Rectangle
                {
                    Width = barWidth,
                    Height = 2,
                    Fill = new SolidColorBrush(Color.FromArgb(40, 128, 128, 128)),
                };
                Canvas.SetLeft(tick, x);
                Canvas.SetTop(tick, height - 2);
                ChartCanvas.Children.Add(tick);
            }
        }

        LegendPanel.Items.Clear();
        foreach (var seg in DayBars.Legend(_bars, ChartMetric.Tokens).Take(5))
        {
            LegendPanel.Items.Add(LegendItem(seg.Color, seg.Label));
        }
    }

    private void RenderLimits(DashboardModel.Snapshot snapshot)
    {
        LimitsPanel.Children.Clear();
        var agents = snapshot.Quota?.Agents ?? [];
        if (agents.Count == 0)
        {
            LimitsPanel.Children.Add(Dim("No quota data yet."));
            return;
        }

        var now = DateTimeOffset.Now;
        foreach (var agent in agents)
        {
            var section = new StackPanel { Spacing = 5 };
            var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            header.Children.Add(Disc(ClientRegistry.Style(agent.ClientId).Color));
            header.Children.Add(new TextBlock
            {
                Text = ClientRegistry.ShortName(agent.ClientId),
                FontSize = 12,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            });
            if (agent.Identity?.Plan is { } plan)
            {
                header.Children.Add(Dim(plan, 10));
            }

            section.Children.Add(header);

            if (agent.Error is { } error)
            {
                section.Children.Add(Dim(error, 11));
                LimitsPanel.Children.Add(section);
                continue;
            }

            foreach (var window in agent.Windows)
            {
                var row = new Grid { ColumnSpacing = 8 };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var label = new TextBlock
                {
                    Text = $"{window.Label} · {window.RemainingPercent:F0}% left",
                    FontSize = 11,
                };
                row.Children.Add(label);
                var pace = UsagePace.Compute(window, PaceMode.Historical, now);
                if (pace is not null)
                {
                    var paceText = new TextBlock
                    {
                        Text = pace.EtaText is { } eta ? $"{pace.Label} · {eta}" : pace.Label,
                        FontSize = 10,
                        Opacity = 0.7,
                        HorizontalAlignment = HorizontalAlignment.Right,
                    };
                    Grid.SetColumn(paceText, 1);
                    row.Children.Add(paceText);
                }

                section.Children.Add(row);
                section.Children.Add(GaugeBar(window.RemainingPercent));
                if (window.ResetText is { } reset)
                {
                    section.Children.Add(Dim(reset, 10));
                }
            }

            LimitsPanel.Children.Add(section);
        }
    }

    private void RenderTrace(DashboardModel.Snapshot snapshot)
    {
        TracePanel.Children.Clear();
        var rows = TraceCollapse.CollapseByClient(snapshot.Trace).Take(5).ToList();
        TraceCard.Visibility = rows.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        foreach (var row in rows)
        {
            var line = new Grid { ColumnSpacing = 8 };
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var name = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            name.Children.Add(Disc(ClientRegistry.Style(row.Client).Color));
            name.Children.Add(new TextBlock
            {
                Text = $"{ClientRegistry.ShortName(row.Client)} · {row.Model}",
                FontSize = 11,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            line.Children.Add(name);
            var rate = new TextBlock
            {
                Text = $"{Format.CompactTokens((long)row.TokensPerMin)}/min",
                FontSize = 11,
                Opacity = 0.75,
            };
            Grid.SetColumn(rate, 1);
            line.Children.Add(rate);
            TracePanel.Children.Add(line);
        }
    }

    private void RenderModels(DashboardModel.Snapshot snapshot)
    {
        ModelsPanel.Children.Clear();
        var entries = (snapshot.Models?.Entries ?? [])
            .OrderByDescending(e => e.Cost)
            .Take(8)
            .ToList();
        if (entries.Count == 0)
        {
            ModelsPanel.Children.Add(Dim("No model data yet."));
            return;
        }

        var colors = new ModelColorMap(snapshot.Models);
        foreach (var entry in entries)
        {
            var block = new StackPanel { Spacing = 3 };
            var head = new Grid { ColumnSpacing = 8 };
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var name = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            name.Children.Add(Disc(colors.Color(entry.Provider, entry.Model)));
            name.Children.Add(new TextBlock
            {
                Text = entry.Model,
                FontSize = 11,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            head.Children.Add(name);
            var cost = new TextBlock { Text = Format.Usd(entry.Cost), FontSize = 11, Opacity = 0.85 };
            Grid.SetColumn(cost, 1);
            head.Children.Add(cost);
            block.Children.Add(head);
            block.Children.Add(TokenKindBar(entry));
            ModelsPanel.Children.Add(block);
        }
    }

    private void RenderStreaks(DashboardModel.Snapshot snapshot)
    {
        var stats = new UsageStats(
            snapshot.Graph, new HashSet<string>(snapshot.Graph.Summary.Clients));
        CurrentStreak.Text = $"{stats.Streaks.Current}d";
        LongestStreak.Text = $"{stats.Streaks.Longest}d";
        BestDay.Text = stats.BestDay is { } best ? Format.MonthDay(best.Date) : "—";
    }

    /// <summary>Thin stacked bar of token kinds, sqrt-scaled so cache-read
    /// doesn't drown everything (the macOS/web Wave 5 lesson).</summary>
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
                Fill = BrushFromHex(TokenKinds[i].Color),
                RadiusX = 1,
                RadiusY = 1,
            };
            Grid.SetColumn(seg, bar.ColumnDefinitions.Count - 1);
            ToolTipService.SetToolTip(seg,
                $"{TokenKinds[i].Label}: {Format.ExactTokens(values[i])} " +
                $"({100.0 * values[i] / Math.Max(1, values.Sum()):F1}%)");
            bar.Children.Add(seg);
        }

        return bar;
    }

    /// <summary>Quota bar with the shared gauge palette: green, amber under
    /// 25 % remaining, red under 10.</summary>
    private static Grid GaugeBar(double remainingPercent)
    {
        var remaining = Math.Clamp(remainingPercent, 0, 100);
        var color = remaining < 10 ? "#ef4444" : remaining < 25 ? "#f59e0b" : "#22c55e";
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
        var fill = new Border
        {
            Background = BrushFromHex(color),
            CornerRadius = new CornerRadius(2.5),
        };
        track.Children.Add(fill);
        return track;
    }

    private static StackPanel LegendItem(string hex, string label)
    {
        var item = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        item.Children.Add(Disc(hex));
        item.Children.Add(new TextBlock { Text = label, FontSize = 10, Opacity = 0.75 });
        return item;
    }

    private static Ellipse Disc(string hex) => new()
    {
        Width = 8,
        Height = 8,
        Fill = BrushFromHex(hex),
        VerticalAlignment = VerticalAlignment.Center,
    };

    private static TextBlock Dim(string text, double size = 11) => new()
    {
        Text = text,
        FontSize = size,
        Opacity = 0.6,
        TextWrapping = TextWrapping.Wrap,
    };

    private static SolidColorBrush BrushFromHex(string hex)
    {
        var h = hex.TrimStart('#');
        return new SolidColorBrush(Color.FromArgb(
            255,
            Convert.ToByte(h[..2], 16),
            Convert.ToByte(h[2..4], 16),
            Convert.ToByte(h[4..6], 16)));
    }
}

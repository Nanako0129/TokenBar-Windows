using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using TokenBar.Core;
using Windows.UI;
using Grid = Microsoft.UI.Xaml.Controls.Grid;

namespace TokenBar.App;

/// <summary>
/// The Quota lens: the strip card (past windows) and the heatmap (when the
/// allowance goes), ported from <c>QuotaHistoryStripCard.swift</c> and
/// <c>QuotaHeatmapCard.swift</c>.
/// <para>
/// Every state choice and every string comes from <see cref="QuotaLensText"/>
/// and <see cref="QuotaLabels"/>; nothing here re-decides a branch, because
/// this file is compiled by no test project.
/// </para>
/// <para>
/// Deliberately absent: the <c>≈ token / $</c> equivalence lines both macOS
/// cards carry. They need WindowEquivalence/UsageAttribution, which have no
/// Windows source — an absent line, not a blank one.
/// </para>
/// </summary>
public sealed partial class DashboardView
{
    // ── Visual parameters, from QuotaHeatmapCard.swift:32-44 and
    // QuotaHistoryStripCard.swift:20-26. Named so a round of visual feedback
    // edits one line.
    private const double HeatmapRowHeight = 13;
    private const double HeatmapCellGap = 1;
    private const double HeatmapLabelWidth = 18;
    /// <summary>Faintest and strongest fill for a slot that carries anything.
    /// An occupied slot never drops to invisible: "a little" and "nothing" are
    /// different answers and the grid has to keep them apart.</summary>
    private const double HeatmapFloorOpacity = 0.14;
    private const double HeatmapPeakOpacity = 0.92;
    /// <summary>Below this share of the peak a slot is drawn at the floor
    /// rather than proportionally, so a grid with one dominant hour still shows
    /// its quiet activity instead of 167 blank cells.</summary>
    private const double HeatmapFloorShare = 0.06;
    /// <summary>An empty slot still gets a plate, so the grid reads as a grid
    /// rather than as floating squares.</summary>
    private const double HeatmapEmptyOpacity = 0.05;

    private const double StripHeight = 26;
    private const double StripBarGap = 1.5;
    /// <summary>A recorded cycle always draws something. A cycle that consumed
    /// 1% is not the same as one that was never recorded, and a bar rounding to
    /// zero height would make them identical.</summary>
    private const double StripMinimumInk = 1;
    private const double StripLatestOpacity = 0.86;
    /// <summary>The newest cycle is the one being asked about; the rest are
    /// context, so they recede.</summary>
    private const double StripOlderOpacity = 0.32;

    private const string HeatmapWindowKey = "tokenbar.heatmap.window";

    private string _heatmapWindow =
        AppSettings.Store.GetString(HeatmapWindowKey) ?? string.Empty;

    private UIElement BuildQuota(DashboardModel.Snapshot snapshot)
    {
        // The client tab renders the same two all-clients cards for now: the
        // per-client quota lens (Session window / history) needs the
        // equivalence subsystem this slice does not ship.
        var (summaries, windows, grids) =
            QuotaLensData.Build(snapshot.QuotaHistory, snapshot.Quota);
        var attempted = snapshot.QuotaHistoryAttempted;
        var stack = new StackPanel { Spacing = 10 };
        stack.Children.Add(BuildQuotaStripCard(summaries, attempted));
        stack.Children.Add(BuildQuotaHeatmapCard(windows, grids, attempted));
        // The Agent-limits card closes the lens on macOS, below the heatmap.
        // The same builder the Overview uses, deliberately: this card answers
        // "where does the allowance stand right now" while the two above answer
        // "where has it gone", and a second implementation of the first
        // question would be free to disagree with the first one.
        stack.Children.Add(Ui.Card("Agent limits".Localized(), BuildLimits(snapshot)));
        return stack;
    }

    // ── Strip card ───────────────────────────────────────────────────────

    private FrameworkElement BuildQuotaStripCard(
        IReadOnlyList<QuotaWindowSummary> summaries, bool attempted)
    {
        var body = new StackPanel { Spacing = 10 };
        switch (QuotaLensText.StripState(summaries, attempted))
        {
            case QuotaStripState.Rows:
                foreach (var summary in summaries)
                {
                    body.Children.Add(StripRow(summary));
                }

                break;
            case QuotaStripState.NoCompletedWindows:
                body.Children.Add(Ui.Dim(QuotaLensText.NoCompletedWindows()));
                break;
            default:
                body.Children.Add(Ui.Dim(QuotaLensText.Loading()));
                break;
        }

        return Ui.Card(
            QuotaLensText.StripTitle(), body, QuotaLensText.StripSubtitle(summaries));
    }

    private FrameworkElement StripRow(QuotaWindowSummary summary)
    {
        var block = new StackPanel { Spacing = 3 };
        var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
        head.Children.Add(Ui.Disc(ClientRegistry.Style(summary.Id.ProviderId).Color, 8));
        head.Children.Add(Ui.Text(QuotaLabels.RowLabel(summary), 11));
        block.Children.Add(Ui.Row(
            head, Ui.Text(QuotaLensText.WindowCount(summary.CycleCount), 9, 0.5)));
        block.Children.Add(Strip(summary));
        block.Children.Add(Ui.Text(QuotaLensText.Headline(summary), 9, 0.5));
        return block;
    }

    /// <summary>Fixed 0…100 with the ceiling drawn as a dashed rule. Rescaling
    /// to the tallest bar would make a run of 2% cycles look like a run of 60%
    /// ones, which is the one comparison this strip exists to support.</summary>
    private FrameworkElement Strip(QuotaWindowSummary summary)
    {
        var bars = new Grid { Height = StripHeight, ColumnSpacing = StripBarGap };
        var accent = AccentColor();
        for (var index = 0; index < summary.Recent.Count; index++)
        {
            bars.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(1, GridUnitType.Star),
            });
            var value = summary.Recent[index];
            var latest = index == summary.Recent.Count - 1;
            var column = new Grid
            {
                Background = new SolidColorBrush(Colors.Transparent), // hit-test
            };
            column.Children.Add(new Border
            {
                Height = Math.Max(StripMinimumInk, StripHeight * Math.Min(1, value / 100)),
                VerticalAlignment = VerticalAlignment.Bottom,
                Background = new SolidColorBrush(
                    Tint(accent, latest ? StripLatestOpacity : StripOlderOpacity)),
            });
            // The strip is oldest-to-newest, so counting back from the end is
            // what turns a bar into "how many windows ago".
            var ago = summary.Recent.Count - 1 - index;
            var peak = index < summary.RecentPeaks.Count ? summary.RecentPeaks[index] : 0;
            HoverTip.AttachRich(column, () => StripTip(summary, ago, value, peak));
            Grid.SetColumn(column, index);
            bars.Children.Add(column);
        }

        var host = new Grid();
        host.Children.Add(bars);
        host.Children.Add(new Rectangle
        {
            Height = 1,
            VerticalAlignment = VerticalAlignment.Top,
            Stroke = new SolidColorBrush(Tint(accent, 0.35)),
            StrokeThickness = 1,
            StrokeDashArray = new DoubleCollection { 2, 3 },
        });
        return host;
    }

    private static UIElement StripTip(
        QuotaWindowSummary summary, int windowsAgo, double usedPercent, double peakPercent)
    {
        var panel = new StackPanel { Spacing = 3, MinWidth = 168 };
        panel.Children.Add(TipText(QuotaLabels.RowLabel(summary), 11, bold: true));
        panel.Children.Add(TipText(QuotaLensText.BarAge(windowsAgo), 9, 0.6));
        panel.Children.Add(TipText(QuotaLensText.BarConsumed(usedPercent)));
        if (peakPercent >= QuotaOverviewFold.ExhaustedPercent)
        {
            var ranOut = TipText(QuotaLensText.RanOut(), 9);
            ranOut.Foreground = Ui.BrushFromHex(PaceOrange);
            panel.Children.Add(ranOut);
        }

        return panel;
    }

    // ── Heatmap card ─────────────────────────────────────────────────────

    private FrameworkElement BuildQuotaHeatmapCard(
        IReadOnlyList<QuotaHeatmapWindow> windows,
        IReadOnlyDictionary<QuotaWindowIdentity, QuotaHeatmap> grids,
        bool attempted)
    {
        // The list already excludes windows with no movement and leads with the
        // heaviest, so the fallback is simply "the first one" rather than a
        // search for one with something to draw.
        var selected = windows.FirstOrDefault(w => WindowId(w.Id) == _heatmapWindow)
            ?? windows.FirstOrDefault();
        var grid = selected is null ? null : grids.GetValueOrDefault(selected.Id);

        var body = new StackPanel { Spacing = 4 };
        switch (QuotaLensText.HeatmapState(grid, attempted))
        {
            case QuotaHeatmapState.Grid:
                body.Children.Add(Heatmap(grid!));
                body.Children.Add(HourAxis());
                if (QuotaLensText.Footnote(grid!) is { } footnote)
                {
                    body.Children.Add(Ui.Dim(footnote, 9));
                }

                break;
            case QuotaHeatmapState.Unplaced:
                body.Children.Add(Ui.Dim(QuotaLensText.UnplacedBody(grid!.UnplacedPercent)));
                break;
            case QuotaHeatmapState.NoMovement:
                body.Children.Add(Ui.Dim(QuotaLensText.NoMovement()));
                break;
            default:
                body.Children.Add(Ui.Dim(QuotaLensText.Loading()));
                break;
        }

        return Ui.Card(
            QuotaLensText.HeatmapTitle(),
            body,
            QuotaLensText.HeatmapSubtitle(grid),
            windows.Count > 1 ? WindowPicker(windows, selected) : null);
    }

    /// <summary>A menu rather than a segmented control: window names are long
    /// enough that four of them do not fit across the popover, and this card is
    /// not the place to abbreviate a subscription's own label.</summary>
    private FrameworkElement WindowPicker(
        IReadOnlyList<QuotaHeatmapWindow> windows, QuotaHeatmapWindow? selected)
    {
        var flyout = new MenuFlyout();
        foreach (var window in windows)
        {
            var item = new RadioMenuFlyoutItem
            {
                Text = QuotaLabels.PickerLabel(window),
                IsChecked = window == selected,
                GroupName = HeatmapWindowKey,
            };
            var id = WindowId(window.Id);
            item.Click += (_, _) =>
            {
                _heatmapWindow = id;
                AppSettings.Store.SetString(HeatmapWindowKey, id);
                RenderContent(animated: false);
            };
            flyout.Items.Add(item);
        }

        return new Button
        {
            // A control label, not content: below the subtitle's size and well
            // below the title, so it does not read as a second heading.
            Content = selected is null ? string.Empty : QuotaLabels.PickerLabel(selected),
            FontSize = 9,
            Opacity = 0.6,
            Padding = new Thickness(6, 2, 6, 2),
            Flyout = flyout,
        };
    }

    private FrameworkElement Heatmap(QuotaHeatmap grid)
    {
        var accent = AccentColor();
        var host = new Grid { ColumnSpacing = 0 };
        host.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(HeatmapLabelWidth),
        });
        host.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(1, GridUnitType.Star),
        });

        var labels = new StackPanel { Spacing = HeatmapCellGap };
        var cells = new Grid
        {
            ColumnSpacing = HeatmapCellGap,
            RowSpacing = HeatmapCellGap,
        };
        for (var hour = 0; hour < 24; hour++)
        {
            cells.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(1, GridUnitType.Star),
            });
        }

        for (var weekday = 0; weekday < 7; weekday++)
        {
            cells.RowDefinitions.Add(new RowDefinition
            {
                Height = new GridLength(HeatmapRowHeight),
            });
            var label = Ui.Text(QuotaLensText.WeekdayLabels[weekday].Localized(), 8, 0.45);
            label.Height = HeatmapRowHeight;
            labels.Children.Add(label);
            for (var hour = 0; hour < 24; hour++)
            {
                var value = grid.Cells[weekday][hour];
                var cell = new Border
                {
                    CornerRadius = new CornerRadius(2),
                    Background = new SolidColorBrush(CellFill(accent, value, grid.Peak)),
                };
                var (day, slot) = (weekday, hour);
                HoverTip.AttachRich(cell, () => SlotTip(day, slot, value));
                Grid.SetRow(cell, weekday);
                Grid.SetColumn(cell, hour);
                cells.Children.Add(cell);
            }
        }

        Grid.SetColumn(cells, 1);
        host.Children.Add(labels);
        host.Children.Add(cells);
        return host;
    }

    private static Color CellFill(Color accent, double value, double peak)
    {
        if (value <= 0 || peak <= 0)
        {
            return Tint(Colors.Gray, HeatmapEmptyOpacity);
        }

        var share = value / peak;
        var opacity = share <= HeatmapFloorShare
            ? HeatmapFloorOpacity
            : HeatmapFloorOpacity
                + ((HeatmapPeakOpacity - HeatmapFloorOpacity)
                    * ((share - HeatmapFloorShare) / (1 - HeatmapFloorShare)));
        return Tint(accent, opacity);
    }

    private static UIElement SlotTip(int weekday, int hour, double value)
    {
        var panel = new StackPanel { Spacing = 3, MinWidth = 186 };
        panel.Children.Add(TipText(QuotaLensText.SlotHeader(weekday, hour), 11, bold: true));
        panel.Children.Add(value <= 0
            ? TipText(QuotaLensText.SlotEmpty(), 9, 0.6)
            : TipText(QuotaLensText.SlotSpend(value)));
        return panel;
    }

    private static FrameworkElement HourAxis()
    {
        var host = new Grid();
        host.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(HeatmapLabelWidth),
        });
        var axis = new Grid { ColumnSpacing = HeatmapCellGap };
        for (var hour = 0; hour < 24; hour++)
        {
            axis.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(1, GridUnitType.Star),
            });
        }

        // Axis labels at 0 / 6 / 12 / 18, as macOS draws them.
        int[] marks = [0, 6, 12, 18];
        foreach (var hour in marks)
        {
            var label = Ui.Text(hour.ToString(System.Globalization.CultureInfo.InvariantCulture), 8, 0.45);
            Grid.SetColumn(label, hour);
            axis.Children.Add(label);
        }

        host.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(1, GridUnitType.Star),
        });
        Grid.SetColumn(axis, 1);
        host.Children.Add(axis);
        return host;
    }

    /// <summary>The store's own triple, flattened for the one place a window has
    /// to survive as a settings string.</summary>
    private static string WindowId(QuotaWindowIdentity id) =>
        $"{id.ProviderId}|{id.AccountScope}|{id.WindowKey}";

    private static Color Tint(Color color, double opacity) =>
        Color.FromArgb((byte)Math.Clamp(opacity * 255, 0, 255), color.R, color.G, color.B);

    private static Color AccentColor() =>
        Application.Current.Resources.TryGetValue("SystemAccentColor", out var value)
            && value is Color color
            ? color
            : Color.FromArgb(255, 0x3b, 0x82, 0xf6);
}

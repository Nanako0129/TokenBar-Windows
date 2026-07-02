using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace TokenBar.App;

/// <summary>Minimal wrapping panel (WinUI ships none built in): children flow
/// left-to-right and wrap, with fixed spacing — the chart legend's
/// counterpart of the macOS FlowLayout.</summary>
public sealed partial class WrapRow : Panel
{
    private const double SpacingX = 10;
    private const double SpacingY = 4;

    protected override Size MeasureOverride(Size availableSize)
    {
        double x = 0, y = 0, rowHeight = 0;
        foreach (var child in Children)
        {
            child.Measure(new Size(availableSize.Width, double.PositiveInfinity));
            var d = child.DesiredSize;
            if (x > 0 && x + d.Width > availableSize.Width)
            {
                x = 0;
                y += rowHeight + SpacingY;
                rowHeight = 0;
            }

            x += d.Width + SpacingX;
            rowHeight = Math.Max(rowHeight, d.Height);
        }

        return new Size(
            double.IsInfinity(availableSize.Width) ? x : availableSize.Width,
            y + rowHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double x = 0, y = 0, rowHeight = 0;
        foreach (var child in Children)
        {
            var d = child.DesiredSize;
            if (x > 0 && x + d.Width > finalSize.Width)
            {
                x = 0;
                y += rowHeight + SpacingY;
                rowHeight = 0;
            }

            child.Arrange(new Rect(x, y, d.Width, d.Height));
            x += d.Width + SpacingX;
            rowHeight = Math.Max(rowHeight, d.Height);
        }

        return finalSize;
    }
}

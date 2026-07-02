using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;

namespace TokenBar.App;

/// <summary>Small element factories shared by every lens.</summary>
public static class Ui
{
    public static readonly (string Key, string Label, string Color)[] TokenKinds =
    [
        ("input", "In", "#3b82f6"),
        ("output", "Out", "#22c55e"),
        ("cacheRead", "CR", "#f59e0b"),
        ("cacheWrite", "CW", "#a855f7"),
        ("reasoning", "R", "#ec4899"),
    ];

    public static Border Card(string title, UIElement content, string? subtitle = null)
    {
        var stack = new StackPanel();
        var head = new Microsoft.UI.Xaml.Controls.Grid { Margin = new Thickness(0, 0, 0, 8) };
        head.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 13,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        });
        if (subtitle is not null)
        {
            head.Children.Add(new TextBlock
            {
                Text = subtitle,
                FontSize = 11,
                Opacity = 0.6,
                HorizontalAlignment = HorizontalAlignment.Right,
            });
        }

        stack.Children.Add(head);
        stack.Children.Add(content);
        return new Border
        {
            Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12),
            Child = stack,
        };
    }

    public static Ellipse Disc(string hex, double size = 8) => new()
    {
        Width = size,
        Height = size,
        Fill = BrushFromHex(hex),
        VerticalAlignment = VerticalAlignment.Center,
    };

    public static TextBlock Text(string text, double size = 11, double opacity = 1.0,
        bool bold = false) => new()
    {
        Text = text,
        FontSize = size,
        Opacity = opacity,
        FontWeight = bold ? Microsoft.UI.Text.FontWeights.SemiBold
            : Microsoft.UI.Text.FontWeights.Normal,
        TextTrimming = TextTrimming.CharacterEllipsis,
    };

    public static TextBlock Dim(string text, double size = 11) => new()
    {
        Text = text,
        FontSize = size,
        Opacity = 0.6,
        TextWrapping = TextWrapping.Wrap,
    };

    /// <summary>Two-column row: content left, trailing text right.</summary>
    public static Microsoft.UI.Xaml.Controls.Grid Row(UIElement left, UIElement right)
    {
        var grid = new Microsoft.UI.Xaml.Controls.Grid { ColumnSpacing = 8 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(left);
        Microsoft.UI.Xaml.Controls.Grid.SetColumn((FrameworkElement)right, 1);
        grid.Children.Add(right);
        return grid;
    }

    /// <summary>Horizontal proportion bar (share of a total), brand colored.</summary>
    public static Microsoft.UI.Xaml.Controls.Grid ShareBar(double fraction, string hex)
    {
        var clamped = Math.Clamp(fraction, 0, 1);
        var track = new Microsoft.UI.Xaml.Controls.Grid
        {
            Height = 4,
            CornerRadius = new CornerRadius(2),
            Background = new SolidColorBrush(Color.FromArgb(36, 128, 128, 128)),
        };
        track.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(Math.Max(clamped, 0.0001), GridUnitType.Star),
        });
        track.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(Math.Max(1 - clamped, 0.0001), GridUnitType.Star),
        });
        track.Children.Add(new Border
        {
            Background = BrushFromHex(hex),
            CornerRadius = new CornerRadius(2),
        });
        return track;
    }

    public static SolidColorBrush BrushFromHex(string hex)
    {
        var h = hex.TrimStart('#');
        return new SolidColorBrush(Color.FromArgb(
            255,
            Convert.ToByte(h[..2], 16),
            Convert.ToByte(h[2..4], 16),
            Convert.ToByte(h[4..6], 16)));
    }
}

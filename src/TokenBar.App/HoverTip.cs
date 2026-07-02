using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace TokenBar.App;

/// <summary>
/// Instant, styled hover tooltip — the native ToolTip's fixed ~1s delay and
/// plain chrome are nowhere near the macOS onContinuousHover cards, and WinUI
/// exposes no delay knob. One shared Popup follows the pointer with a small
/// offset and flips to stay inside the root bounds.
/// </summary>
public static class HoverTip
{
    private static Popup? _popup;
    private static TextBlock? _text;
    private static Border? _card;

    public static void Attach(FrameworkElement target, Func<string> content)
    {
        target.PointerEntered += (_, e) => Show(target, content(), e);
        target.PointerMoved += (_, e) => Move(target, e);
        target.PointerExited += (_, _) => Hide();
        target.Unloaded += (_, _) => Hide();
    }

    private static void EnsurePopup(XamlRoot root)
    {
        if (_popup is not null)
        {
            _popup.XamlRoot = root;
            return;
        }

        _text = new TextBlock
        {
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.FromArgb(255, 240, 240, 245)),
        };
        _card = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(235, 32, 32, 38)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(60, 255, 255, 255)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 7, 10, 7),
            MaxWidth = 280,
            Child = _text,
            IsHitTestVisible = false,
        };
        _popup = new Popup
        {
            Child = _card,
            IsHitTestVisible = false,
            XamlRoot = root,
        };
    }

    private static void Show(FrameworkElement target, string content, PointerRoutedEventArgs e)
    {
        if (target.XamlRoot is not { } root || string.IsNullOrEmpty(content))
        {
            return;
        }

        EnsurePopup(root);
        _text!.Text = content;
        Position(root, e);
        _popup!.IsOpen = true;
    }

    private static void Move(FrameworkElement target, PointerRoutedEventArgs e)
    {
        if (_popup is { IsOpen: true } && target.XamlRoot is { } root)
        {
            Position(root, e);
        }
    }

    private static void Position(XamlRoot root, PointerRoutedEventArgs e)
    {
        var p = e.GetCurrentPoint(root.Content).Position;
        _card!.Measure(new Windows.Foundation.Size(280, double.PositiveInfinity));
        var size = _card.DesiredSize;
        var x = p.X + 14;
        var y = p.Y + 18;
        if (x + size.Width > root.Size.Width - 4)
        {
            x = p.X - size.Width - 10; // flip left near the right edge
        }

        if (y + size.Height > root.Size.Height - 4)
        {
            y = p.Y - size.Height - 10;
        }

        _popup!.HorizontalOffset = Math.Max(4, x);
        _popup.VerticalOffset = Math.Max(4, y);
    }

    private static void Hide()
    {
        if (_popup is not null)
        {
            _popup.IsOpen = false;
        }
    }
}

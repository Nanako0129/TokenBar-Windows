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
/// exposes no delay knob. A Popup follows the pointer with a small offset and
/// flips to stay inside the root bounds. Content is arbitrary UI (the macOS
/// tooltips carry colored discs and metric rows, not just text).
///
/// One host (Popup + Border) is kept PER XamlRoot. A single shared Popup cannot
/// be re-pointed across the flyout's and the settings window's XamlRoots — WinUI
/// forbids reassigning a rooted Popup's XamlRoot, which threw inside the pointer
/// handler and (with no global handler) crashed the app — so each window's root
/// gets its own host.
/// </summary>
public static class HoverTip
{
    private sealed record Host(Popup Popup, Border Card);

    private static readonly Dictionary<XamlRoot, Host> _hosts = [];

    public static void Attach(FrameworkElement target, Func<string> content) =>
        AttachRich(target, () => new TextBlock
        {
            Text = content(),
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.FromArgb(255, 240, 240, 245)),
        });

    public static void AttachRich(FrameworkElement target, Func<UIElement> build)
    {
        target.PointerEntered += (_, e) => Show(target, build(), e);
        target.PointerMoved += (_, e) => Move(target, e);
        target.PointerExited += (_, _) => Hide(target);
        target.Unloaded += (_, _) => Hide(target);
    }

    private static Host EnsureHost(XamlRoot root)
    {
        if (_hosts.TryGetValue(root, out var existing))
        {
            return existing;
        }

        var card = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(238, 30, 30, 36)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(60, 255, 255, 255)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 8, 10, 8),
            MaxWidth = 300,
            IsHitTestVisible = false,
        };
        var host = new Host(
            new Popup
            {
                Child = card,
                IsHitTestVisible = false,
                XamlRoot = root,
            },
            card);
        _hosts[root] = host;
        return host;
    }

    private static void Show(FrameworkElement target, UIElement content, PointerRoutedEventArgs e)
    {
        if (target.XamlRoot is not { } root)
        {
            return;
        }

        var host = EnsureHost(root);
        host.Card.Child = content;
        Position(host, root, e);
        host.Popup.IsOpen = true;
    }

    private static void Move(FrameworkElement target, PointerRoutedEventArgs e)
    {
        if (target.XamlRoot is { } root
            && _hosts.TryGetValue(root, out var host)
            && host.Popup.IsOpen)
        {
            Position(host, root, e);
        }
    }

    private static void Position(Host host, XamlRoot root, PointerRoutedEventArgs e)
    {
        var p = e.GetCurrentPoint(root.Content).Position;
        host.Card.Measure(new Windows.Foundation.Size(300, double.PositiveInfinity));
        var size = host.Card.DesiredSize;
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

        host.Popup.HorizontalOffset = Math.Max(4, x);
        host.Popup.VerticalOffset = Math.Max(4, y);
    }

    private static void Hide(FrameworkElement target)
    {
        if (target.XamlRoot is { } root && _hosts.TryGetValue(root, out var host))
        {
            host.Popup.IsOpen = false;
            return;
        }

        // XamlRoot already gone (e.g. Unloaded): close any open tooltip so a
        // stray card can't linger.
        foreach (var h in _hosts.Values)
        {
            h.Popup.IsOpen = false;
        }
    }

    /// <summary>True when <paramref name="popup"/> is one of HoverTip's tooltip
    /// popups, so light-dismiss logic (e.g. the flyout's Esc handler) can ignore
    /// it and only yield to a real transient such as a MenuFlyout.</summary>
    public static bool IsHoverPopup(Popup popup)
    {
        foreach (var host in _hosts.Values)
        {
            if (ReferenceEquals(host.Popup, popup))
            {
                return true;
            }
        }

        return false;
    }
}

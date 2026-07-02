using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using TokenBar.Interop;

namespace TokenBar.App;

/// <summary>
/// The tray flyout: borderless, Acrylic-backed, hides on deactivate, and
/// positions itself against the taskbar corner — the NSPopover counterpart.
/// Created once at launch; the tray icon toggles visibility.
/// </summary>
public sealed partial class FlyoutWindow : Window
{
    private const int FlyoutWidth = 400;
    private const int FlyoutHeight = 520;

    public FlyoutWindow()
    {
        InitializeComponent();

        // Transient surface → Acrylic, via the manual controller so the
        // backdrop stays translucent while unfocused: the flyout is a
        // glanceable dashboard, not a focused editor — matching the macOS
        // NSPopover, whose vibrancy never depends on key status.
        SetupAlwaysOnAcrylic();

        var presenter = (OverlappedPresenter)AppWindow.Presenter;
        presenter.SetBorderAndTitleBar(false, false);
        presenter.IsResizable = false;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        presenter.IsAlwaysOnTop = true;

        ApplyPopupChrome();

        AppWindow.IsShownInSwitchers = false;
        AppWindow.Closing += (_, e) =>
        {
            // Tray-resident: closing the window means hiding it.
            e.Cancel = true;
            HideFlyout();
        };
        // --keep-open: verification hook — screen-capture helpers steal focus,
        // which would correctly dismiss the flyout right before the shot.
        var keepOpen = Environment.GetCommandLineArgs().Contains("--keep-open");
        Activated += (_, e) =>
        {
            if (e.WindowActivationState == WindowActivationState.Deactivated && !keepOpen)
            {
                HideFlyout();
            }
        };
    }

    public bool IsFlyoutVisible => AppWindow.IsVisible;

    public void ToggleFlyout()
    {
        if (IsFlyoutVisible)
        {
            HideFlyout();
        }
        else
        {
            ShowFlyout();
        }
    }

    private bool _hiding;
    private long _slideToken;

    public void ShowFlyout()
    {
        _hiding = false;
        var target = PositionNearTray();
        // Start below the resting spot so the WINDOW (backdrop included)
        // slides up as one surface — the native taskbar-flyout motion. A
        // content-visual offset alone reads as text sliding over a static
        // acrylic slab.
        const int rise = 24;
        AppWindow.Move(new PointInt32(target.X, target.Y + rise));
        AppWindow.Show();
        // Show can rebuild frame state; re-assert the chrome afterwards.
        ApplyPopupChrome();
        Activate();
        _ = SlideWindowAsync(target.Y + rise, target.Y, durationMs: 180, decelerate: true);
        FadeContent(from: 0f, to: 1f, durationMs: 180);
        RefreshProbe();
    }

    /// <summary>Move the top-level window each frame; DWM re-blurs the
    /// acrylic live, so the whole surface travels together.</summary>
    private async Task SlideWindowAsync(int fromY, int toY, int durationMs, bool decelerate)
    {
        var token = ++_slideToken;
        var x = AppWindow.Position.X;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (watch.ElapsedMilliseconds < durationMs)
        {
            if (token != _slideToken)
            {
                return; // superseded by a newer slide
            }

            var t = Math.Clamp(watch.ElapsedMilliseconds / (double)durationMs, 0, 1);
            var eased = decelerate ? 1 - Math.Pow(1 - t, 3) : Math.Pow(t, 2);
            AppWindow.Move(new PointInt32(x, (int)Math.Round(fromY + (toY - fromY) * eased)));
            await Task.Delay(8); // ~120Hz pacing; DWM coalesces to refresh rate
        }

        if (token == _slideToken)
        {
            AppWindow.Move(new PointInt32(x, toY));
        }
    }

    private void FadeContent(float from, float to, int durationMs)
    {
        var visual = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview
            .GetElementVisual(Content);
        var compositor = visual.Compositor;
        visual.Opacity = from;
        var fade = compositor.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(1f, to);
        fade.Duration = TimeSpan.FromMilliseconds(durationMs);
        visual.StartAnimation("Opacity", fade);
    }

    /// <summary>Exit: the window sinks while the content fades, then the real
    /// hide.</summary>
    private async void AnimateOut(Action completed)
    {
        var y = AppWindow.Position.Y;
        FadeContent(from: 1f, to: 0f, durationMs: 120);
        await SlideWindowAsync(y, y + 16, durationMs: 120, decelerate: false);
        completed();
        // Reset for the next entrance.
        var visual = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview
            .GetElementVisual(Content);
        visual.Opacity = 1f;
    }

    /// <summary>Popup chrome: strip every residual frame style down to
    /// WS_POPUP (the presenter's borderless mode leaves bits that draw a 1px
    /// outline), then round the corners and erase the DWM border color.</summary>
    private void ApplyPopupChrome()
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);

        const int GWL_STYLE = -16;
        const long WS_POPUP = 0x80000000L;
        const long WS_VISIBLE = 0x10000000L;
        const long WS_CLIPCHILDREN = 0x02000000L;
        var oldStyle = GetWindowLongPtrW(hwnd, GWL_STYLE);
        var newStyle = (nint)(WS_POPUP | WS_CLIPCHILDREN |
            ((long)oldStyle & WS_VISIBLE));
        _ = SetWindowLongPtrW(hwnd, GWL_STYLE, newStyle);

        var corner = 2; // DWMWCP_ROUND
        var hrCorner = DwmSetWindowAttribute(
            hwnd, 33 /* DWMWA_WINDOW_CORNER_PREFERENCE */, ref corner, sizeof(int));
        var borderColor = unchecked((int)0xFFFFFFFE); // DWMWA_COLOR_NONE
        var hrBorder = DwmSetWindowAttribute(
            hwnd, 34 /* DWMWA_BORDER_COLOR */, ref borderColor, sizeof(int));

        const uint SWP_FRAMECHANGED = 0x0020;
        const uint SWP_NOMOVE = 0x0002;
        const uint SWP_NOSIZE = 0x0001;
        const uint SWP_NOZORDER = 0x0004;
        const uint SWP_NOACTIVATE = 0x0010;
        _ = SetWindowPos(hwnd, 0, 0, 0, 0, 0,
            SWP_FRAMECHANGED | SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);

        DevLog.Write(
            $"chrome: style 0x{(long)oldStyle:x8}->0x{(long)newStyle:x8} " +
            $"corner=0x{hrCorner:x} border=0x{hrBorder:x}");
    }

    public void HideFlyout()
    {
        if (_hiding || !AppWindow.IsVisible)
        {
            return;
        }

        _hiding = true;
        AnimateOut(() =>
        {
            AppWindow.Hide();
            _hiding = false;
        });
    }

    /// <summary>Bottom-right of the work area on the tray's display —
    /// covers the default bottom taskbar; edge-aware placement for
    /// left/top taskbars lands with the DPI pass (plan Phase 4 item 2).
    /// Sizes the window and returns the resting position (the slide-in
    /// animates toward it).</summary>
    private PointInt32 PositionNearTray()
    {
        var display = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
        var work = display.WorkArea;
        var scale = Content.XamlRoot?.RasterizationScale ?? 1.0;
        var width = (int)(FlyoutWidth * scale);
        var height = (int)(FlyoutHeight * scale);
        const int margin = 12;
        AppWindow.Resize(new SizeInt32(width, height));
        return new PointInt32(
            work.X + work.Width - width - margin,
            work.Y + work.Height - height - margin);
    }

    /// <summary>Skeleton data hookup: prove the FFI seam from inside the
    /// WinUI process (the real DashboardModel arrives in plan Phase 4 item 4).</summary>
    private void RefreshProbe()
    {
        _ = DispatcherQueue.TryEnqueue(async () =>
        {
            try
            {
                var probe = await Task.Run(TbCore.Probe);
                ProbeText.Text = $"tb_probe: {probe.Messages:N0} messages parsed";
                DevLog.Write($"probe ok: {probe.Messages}");
            }
            catch (Exception ex)
            {
                ProbeText.Text = $"tb_probe failed: {ex.Message}";
                DevLog.Write($"probe failed: {ex.Message}");
            }
        });
    }

    private DesktopAcrylicController? _acrylic;
    private SystemBackdropConfiguration? _acrylicConfig;

    /// <summary>Acrylic that never falls back to the opaque inactive color:
    /// IsInputActive is pinned true for the window's lifetime.</summary>
    private void SetupAlwaysOnAcrylic()
    {
        if (!DesktopAcrylicController.IsSupported())
        {
            DevLog.Write("acrylic unsupported, falling back to default backdrop");
            SystemBackdrop = new DesktopAcrylicBackdrop();
            return;
        }

        _acrylicConfig = new SystemBackdropConfiguration
        {
            IsInputActive = true,
            Theme = SystemBackdropTheme.Default,
        };
        _acrylic = new DesktopAcrylicController();
        _acrylic.AddSystemBackdropTarget(
            WinRT.CastExtensions.As<Microsoft.UI.Composition.ICompositionSupportsSystemBackdrop>(this));
        _acrylic.SetSystemBackdropConfiguration(_acrylicConfig);
        Closed += (_, _) =>
        {
            _acrylic?.Dispose();
            _acrylic = null;
        };
    }

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        nint hwnd, int attribute, ref int value, int size);

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtrW(nint hwnd, int index);

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLongPtrW(nint hwnd, int index, nint value);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetWindowPos(
        nint hwnd, nint hwndInsertAfter, int x, int y, int cx, int cy, uint flags);
}

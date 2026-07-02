using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using TokenBar.Core;
using TokenBar.Interop;
using Windows.Graphics;

namespace TokenBar.App;

/// <summary>
/// The tray flyout: borderless, Acrylic-backed, hides on deactivate, and
/// positions itself against the taskbar edge — the NSPopover counterpart.
/// Created once at launch; the tray icon toggles visibility.
/// </summary>
public sealed partial class FlyoutWindow : Window
{
    private const int FlyoutWidth = 400;
    private const int FlyoutHeight = 640;
    private const int Margin = 12;
    private const int SlideDistance = 24;

    private readonly DashboardModel _model;

    public FlyoutWindow()
    {
        InitializeComponent();

        _model = new DashboardModel(DispatcherQueue);
        _model.Updated += RenderSnapshot;
        Dashboard.Bind(_model);

        // --view=<lens> debug flag (the macOS `defaults write tokenbar.view`
        // counterpart) so remote lens screenshots need no clicking.
        var viewArg = Environment.GetCommandLineArgs()
            .FirstOrDefault(a => a.StartsWith("--view=", StringComparison.Ordinal));
        if (viewArg is not null &&
            Enum.TryParse<AppView>(viewArg["--view=".Length..], ignoreCase: true, out var lens))
        {
            Dashboard.SwitchTo(lens);
        }

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
        InstallWheelHook();
        var (resting, start) = PositionNearTray();
        // Start offset toward the taskbar edge so the WINDOW (backdrop
        // included) slides in as one surface — the native taskbar-flyout
        // motion. A content-visual offset alone reads as text sliding over a
        // static acrylic slab.
        AppWindow.Move(start);
        AppWindow.Show();
        // Show can rebuild frame state; re-assert the chrome afterwards.
        ApplyPopupChrome();
        Activate();
        // Activate() can silently fail to take real keyboard focus when the
        // process lacks foreground rights (schtasks/tray launches) — and
        // WM_MOUSEWHEEL is delivered to the FOCUSED window, so a focusless
        // flyout scrolls by scrollbar drag but ignores the wheel.
        _ = SetForegroundWindow(WinRT.Interop.WindowNative.GetWindowHandle(this));
        _ = SlideWindowAsync(start, resting, durationMs: 180, decelerate: true);
        FadeContent(from: 0f, to: 1f, durationMs: 180);
        _model.Start();
        RenderSnapshot();
    }

    public void HideFlyout()
    {
        if (_hiding || !AppWindow.IsVisible)
        {
            return;
        }

        _hiding = true;
        RemoveWheelHook();
        _model.Stop();
        var resting = AppWindow.Position;
        var sink = Toward(resting, TaskbarEdge(), 16);
        FadeContent(from: 1f, to: 0f, durationMs: 120);
        _ = FinishHideAsync(resting, sink);
    }

    private async Task FinishHideAsync(PointInt32 from, PointInt32 to)
    {
        await SlideWindowAsync(from, to, durationMs: 120, decelerate: false);
        AppWindow.Hide();
        _hiding = false;
        var visual = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview
            .GetElementVisual(Content);
        visual.Opacity = 1f;
    }

    /// <summary>Move the top-level window each frame; DWM re-blurs the
    /// acrylic live, so the whole surface travels together. (Known gap: not
    /// compositor-synced — the native-feel research item tracks this.)</summary>
    private async Task SlideWindowAsync(PointInt32 from, PointInt32 to, int durationMs, bool decelerate)
    {
        var token = ++_slideToken;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (watch.ElapsedMilliseconds < durationMs)
        {
            if (token != _slideToken)
            {
                return; // superseded by a newer slide
            }

            var t = Math.Clamp(watch.ElapsedMilliseconds / (double)durationMs, 0, 1);
            var eased = decelerate ? 1 - Math.Pow(1 - t, 3) : Math.Pow(t, 2);
            AppWindow.Move(new PointInt32(
                (int)Math.Round(from.X + (to.X - from.X) * eased),
                (int)Math.Round(from.Y + (to.Y - from.Y) * eased)));
            await Task.Delay(8); // ~120Hz pacing; DWM coalesces to refresh rate
        }

        if (token == _slideToken)
        {
            AppWindow.Move(to);
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

    private enum Edge
    {
        Left = 0,
        Top = 1,
        Right = 2,
        Bottom = 3,
    }

    /// <summary>Which screen edge the taskbar sits on (ABM_GETTASKBARPOS);
    /// bottom when the shell won't say.</summary>
    private static Edge TaskbarEdge()
    {
        var data = new APPBARDATA { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<APPBARDATA>() };
        return SHAppBarMessage(0x0005 /* ABM_GETTASKBARPOS */, ref data) != 0
            ? (Edge)data.uEdge
            : Edge.Bottom;
    }

    /// <summary>Offset a point toward a taskbar edge (the hide direction).</summary>
    private static PointInt32 Toward(PointInt32 p, Edge edge, int distance) => edge switch
    {
        Edge.Left => new PointInt32(p.X - distance, p.Y),
        Edge.Top => new PointInt32(p.X, p.Y - distance),
        Edge.Right => new PointInt32(p.X + distance, p.Y),
        _ => new PointInt32(p.X, p.Y + distance),
    };

    /// <summary>Anchor against the taskbar corner of the work area, on
    /// whichever edge the taskbar occupies. Sizes the window and returns the
    /// resting position plus the slide start (offset toward the taskbar).</summary>
    private (PointInt32 Resting, PointInt32 Start) PositionNearTray()
    {
        var display = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
        var work = display.WorkArea;
        // GetDpiForWindow works before the first Show, unlike XamlRoot's
        // RasterizationScale (null until shown → windows sized for 100%).
        var scale = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96.0;
        var width = (int)(FlyoutWidth * scale);
        var height = (int)(FlyoutHeight * scale);
        AppWindow.Resize(new SizeInt32(width, height));

        var edge = TaskbarEdge();
        var resting = edge switch
        {
            // Tray lives at the taskbar's far end: bottom/top → right corner,
            // left/right → bottom corner.
            Edge.Left => new PointInt32(work.X + Margin, work.Y + work.Height - height - Margin),
            Edge.Top => new PointInt32(work.X + work.Width - width - Margin, work.Y + Margin),
            Edge.Right => new PointInt32(work.X + work.Width - width - Margin, work.Y + work.Height - height - Margin),
            _ => new PointInt32(work.X + work.Width - width - Margin, work.Y + work.Height - height - Margin),
        };
        return (resting, Toward(resting, edge, SlideDistance));
    }

    private void RenderSnapshot() => Dashboard.Render(_model.Current);

    /// <summary>Popup chrome: strip every residual frame style down to
    /// WS_POPUP (the presenter's borderless mode leaves WS_DLGFRAME behind,
    /// which draws a 1px outline DWMWA_BORDER_COLOR cannot remove), then
    /// round the corners and erase the DWM border color.</summary>
    private void ApplyPopupChrome()
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);

        const int GWL_STYLE = -16;
        const long WS_POPUP = 0x80000000L;
        const long WS_VISIBLE = 0x10000000L;
        const long WS_CLIPCHILDREN = 0x02000000L;
        var oldStyle = GetWindowLongPtrW(hwnd, GWL_STYLE);
        var newStyle = (nint)(WS_POPUP | WS_CLIPCHILDREN | ((long)oldStyle & WS_VISIBLE));
        _ = SetWindowLongPtrW(hwnd, GWL_STYLE, newStyle);

        var corner = 2; // DWMWCP_ROUND
        _ = DwmSetWindowAttribute(hwnd, 33 /* DWMWA_WINDOW_CORNER_PREFERENCE */, ref corner, sizeof(int));
        var borderColor = unchecked((int)0xFFFFFFFE); // DWMWA_COLOR_NONE
        _ = DwmSetWindowAttribute(hwnd, 34 /* DWMWA_BORDER_COLOR */, ref borderColor, sizeof(int));

        // HWND_TOPMOST directly: the presenter's IsAlwaysOnTop doesn't
        // survive the WS_POPUP style stomp, so the flyout sank under normal
        // windows whenever anything else was open. Re-assert through the
        // presenter as well (toggle forces a reapply of the z-band).
        const uint SWP_FRAMECHANGED = 0x0020;
        const uint SWP_NOMOVE = 0x0002;
        const uint SWP_NOSIZE = 0x0001;
        const uint SWP_NOACTIVATE = 0x0010;
        var HWND_TOPMOST = new nint(-1);
        var posOk = SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0,
            SWP_FRAMECHANGED | SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);

        if (AppWindow.Presenter is OverlappedPresenter p)
        {
            p.IsAlwaysOnTop = false;
            p.IsAlwaysOnTop = true;
        }

        const int GWL_EXSTYLE = -20;
        var exStyle = (long)GetWindowLongPtrW(hwnd, GWL_EXSTYLE);
        DevLog.Write(
            $"chrome: topmost posOk={posOk} exTopmost={(exStyle & 0x8) != 0} ex=0x{exStyle:x8}");
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

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct APPBARDATA
    {
        public int cbSize;
        public nint hWnd;
        public uint uCallbackMessage;
        public uint uEdge;
        public RECT rc;
        public nint lParam;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [System.Runtime.InteropServices.DllImport("shell32.dll")]
    private static extern nuint SHAppBarMessage(uint dwMessage, ref APPBARDATA pData);

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

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint hwnd);

    private delegate nint LowLevelMouseProc(int code, nint wParam, nint lParam);

    private LowLevelMouseProc? _mouseProc; // kept alive while the hook is set
    private nint _mouseHook;

    /// <summary>Last resort for wheel input: the system never delivers wheel
    /// messages to this focusless popup (verified at every hwnd of the
    /// process), so while the flyout is visible a WH_MOUSE_LL hook watches
    /// for wheel events inside the flyout rect, scrolls the dashboard, and
    /// swallows the event so the focused app doesn't also scroll. Installed
    /// only while visible; the tray-flyout standard (EarTrumpet et al.).</summary>
    private void InstallWheelHook()
    {
        if (_mouseHook != 0)
        {
            return;
        }

        _mouseProc = (code, wParam, lParam) =>
        {
            if (code >= 0 && wParam == 0x020A /* WM_MOUSEWHEEL */)
            {
                var data = System.Runtime.InteropServices.Marshal
                    .PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                var pos = AppWindow.Position;
                var size = AppWindow.Size;
                var inside = AppWindow.IsVisible &&
                    data.pt.X >= pos.X && data.pt.X < pos.X + size.Width &&
                    data.pt.Y >= pos.Y && data.pt.Y < pos.Y + size.Height;
                if (inside)
                {
                    var delta = unchecked((short)(data.mouseData >> 16));
                    _ = DispatcherQueue.TryEnqueue(() => Dashboard.ScrollBy(-delta));
                    return 1; // consumed: don't let the focused app scroll too
                }
            }

            return CallNextHookEx(0, code, wParam, lParam);
        };
        _mouseHook = SetWindowsHookExW(
            14 /* WH_MOUSE_LL */, _mouseProc, GetModuleHandleW(null), 0);
    }

    private void RemoveWheelHook()
    {
        if (_mouseHook != 0)
        {
            _ = UnhookWindowsHookEx(_mouseHook);
            _mouseHook = 0;
            _mouseProc = null;
        }
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT
    {
        public PointL pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public nuint dwExtraInfo;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct PointL
    {
        public int X;
        public int Y;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern nint SetWindowsHookExW(
        int hookId, LowLevelMouseProc proc, nint module, uint threadId);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(nint hook);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern nint CallNextHookEx(nint hook, int code, nint wParam, nint lParam);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern nint GetModuleHandleW(string? moduleName);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hwnd);
}

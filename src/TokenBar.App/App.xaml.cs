using Microsoft.UI.Xaml;

namespace TokenBar.App;

public partial class App : Application
{
    private static Mutex? _singleInstance;

    private TrayService? _tray;
    private FlyoutWindow? _flyout;
    private bool _started;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // A stray UI-thread exception (a render / hover / animation callback)
        // shouldn't take the whole tray-resident app down once it's up; during
        // startup we let it propagate so a broken launch stays visible.
        UnhandledException += OnUnhandledException;

        // Tray-resident app: no window shows at launch; the tray icon owns
        // the flyout's lifetime (mirrors the macOS NSStatusItem shell).
        // Single instance: a second launch (autostart + manual, say) exits
        // quietly; the mutex lives for the process lifetime.
        _singleInstance = new Mutex(true, @"Local\TokenBar.App.SingleInstance", out var isFirst);
        if (!isFirst)
        {
            DevLog.Write("launch: another instance is running, exiting");
            Current.Exit();
            return;
        }

        ProcessPower.EnsureNormalPriority();

        try
        {
            DevLog.Write("launch: creating flyout");
            _flyout = new FlyoutWindow();
            DevLog.Write("launch: creating tray");
            _tray = new TrayService(_flyout);
            DevLog.Write("launch: tray up");

            if (Environment.GetCommandLineArgs().Contains("--dump-tray-icons"))
            {
                TrayIconGallery.Dump();
            }

            // Debug flag, macOS --settings parity.
            if (Environment.GetCommandLineArgs().Contains("--settings"))
            {
                _tray.ShowSettings();
            }

            // Debug flag, same convention as the macOS --open-popover: pop the
            // flyout right away so remote verification needs no tray click.
            if (Environment.GetCommandLineArgs().Contains("--open-flyout"))
            {
                _flyout.ShowFlyout();
                DevLog.Write($"launch: flyout shown, visible={_flyout.AppWindow.IsVisible}, " +
                    $"pos={_flyout.AppWindow.Position.X},{_flyout.AppWindow.Position.Y} " +
                    $"size={_flyout.AppWindow.Size.Width}x{_flyout.AppWindow.Size.Height}");
            }

            _started = true;

            // Dev-only 3D panel (Phase 8 Gate 0). --graph3d mounts and shows
            // it for manual inspection; --soak3d runs the device-lifecycle
            // soak (50 open/close cycles) and exits with a pass/fail code.
            if (Environment.GetCommandLineArgs().Contains("--graph3d"))
            {
                _flyout.EnableGraph3D();
                _flyout.ShowFlyout();
            }

            if (Environment.GetCommandLineArgs().Contains("--soak3d"))
            {
                Soak3D.Run(_flyout);
            }
        }
        catch (Exception ex)
        {
            DevLog.Write($"launch FAILED: {ex}");
            throw;
        }
    }

    private void OnUnhandledException(
        object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        DevLog.Write($"UNHANDLED: {e.Message}\n{e.Exception}");

        // Keep the tray alive for steady-state faults (a stray render / hover /
        // animation throw); let startup faults surface by leaving them unhandled.
        if (_started)
        {
            e.Handled = true;
        }
    }
}

using Microsoft.UI.Xaml;

namespace TokenBar.App;

public partial class App : Application
{
    private static Mutex? _singleInstance;

    private TrayService? _tray;
    private FlyoutWindow? _flyout;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
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

        try
        {
            DevLog.Write("launch: creating flyout");
            _flyout = new FlyoutWindow();
            DevLog.Write("launch: creating tray");
            _tray = new TrayService(_flyout);
            DevLog.Write("launch: tray up");

            // Debug flag, same convention as the macOS --open-popover: pop the
            // flyout right away so remote verification needs no tray click.
            if (Environment.GetCommandLineArgs().Contains("--open-flyout"))
            {
                _flyout.ShowFlyout();
                DevLog.Write($"launch: flyout shown, visible={_flyout.AppWindow.IsVisible}, " +
                    $"pos={_flyout.AppWindow.Position.X},{_flyout.AppWindow.Position.Y} " +
                    $"size={_flyout.AppWindow.Size.Width}x{_flyout.AppWindow.Size.Height}");
            }
        }
        catch (Exception ex)
        {
            DevLog.Write($"launch FAILED: {ex}");
            throw;
        }
    }
}

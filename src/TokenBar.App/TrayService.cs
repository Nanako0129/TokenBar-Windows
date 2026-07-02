using H.NotifyIcon;
using H.NotifyIcon.Core;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using TokenBar.Core;

namespace TokenBar.App;

/// <summary>
/// Owns the notification-area icon (H.NotifyIcon): left click toggles the
/// flyout, right click gets the context menu (quota-source picker joins with
/// the full menu work item). The icon is drawn live: a non-hidden tray mode
/// renders its short value into the icon (Windows has no tray text — parity
/// table #1: value in icon, full string in the tooltip, always-on flyout
/// header), Hidden mode shows the pictorial gauge.
/// </summary>
public sealed class TrayService : IDisposable
{
    private static readonly string[] IconKeys =
    [
        TrayModes.StorageKey, "tokenbar.tray.animationStyle",
        "tokenbar.icon.coloring", "tokenbar.quota.source",
    ];

    private readonly TaskbarIcon _icon;
    private readonly TrayFeed _feed;
    private string _iconSignature = "";
    private nint _hicon;

    public TrayService(FlyoutWindow flyout)
    {
        _icon = new TaskbarIcon
        {
            ToolTipText = "TokenBar",
        };
        _icon.LeftClickCommand = new RelayCommand(flyout.ToggleFlyout);
        _icon.NoLeftClickDelay = true;

        // Minimal context menu (Open/Quit) — the quota-source picker joins in
        // plan Phase 7. Without Quit the only way out is Task Manager.
        var menu = new Microsoft.UI.Xaml.Controls.MenuFlyout();
        var open = new Microsoft.UI.Xaml.Controls.MenuFlyoutItem { Text = "Open TokenBar" };
        open.Click += (_, _) => flyout.ShowFlyout();
        var quit = new Microsoft.UI.Xaml.Controls.MenuFlyoutItem { Text = "Quit" };
        quit.Click += (_, _) =>
        {
            Dispose();
            Microsoft.UI.Xaml.Application.Current.Exit();
        };
        menu.Items.Add(open);
        menu.Items.Add(quit);
        _icon.ContextFlyout = menu;
        // NOT SecondWindow: that mode parks a transparent helper window over
        // the desktop, which swallowed hover/wheel input meant for the flyout.
        _icon.ContextMenuMode = ContextMenuMode.PopupMenu;

        _feed = new TrayFeed(DispatcherQueue.GetForCurrentThread());
        _feed.Changed += UpdateIcon;
        AppSettings.Store.Changed += key =>
        {
            if (IconKeys.Contains(key))
            {
                UpdateIcon(); // settings writes happen on the UI thread
            }
        };

        UpdateIcon();
        _icon.ForceCreate();
    }

    private void UpdateIcon()
    {
        var mode = TrayModes.Parse(AppSettings.Store.GetString(TrayModes.StorageKey));
        var styleRaw = AppSettings.Store.GetString("tokenbar.tray.animationStyle", "cat");
        var coloring = TrayIconRenderer.ParseColoring(
            AppSettings.Store.GetString("tokenbar.icon.coloring"));
        var dark = IsSystemDark();
        var remaining = _feed.QuotaRemaining;
        var title = mode.Title(_feed.Graph, _feed.TokensPerMin, remaining);

        // Re-render only when the drawn state actually changed (macOS
        // iconSettingsSignature): the feed ticks far more often than the
        // numbers move.
        var signature = $"{mode}|{styleRaw}|{coloring}|{dark}|{title}|{remaining:F1}";
        if (signature == _iconSignature)
        {
            return;
        }

        _iconSignature = signature;
        using var bmp = mode != TrayMode.Hidden && title.Length > 0
            ? TrayIconRenderer.RenderTitle(
                TrayModes.IconTitle(title),
                mode == TrayMode.QuotaLeft && remaining is { } q
                    ? TrayIconRenderer.GaugeColor(q) : null,
                dark)
            : TrayIconRenderer.RenderGauge(
                // cat/parrot render as bars until the tray animator lands.
                TrayIconRenderer.ParseGaugeStyle(styleRaw) ?? QuotaIconStyle.Bars,
                remaining, dark, coloring);

        var hicon = bmp.GetHicon();
        _icon.Icon = System.Drawing.Icon.FromHandle(hicon);
        if (_hicon != 0)
        {
            _ = DestroyIcon(_hicon);
        }

        _hicon = hicon;
        _icon.ToolTipText = title.Length == 0
            ? "TokenBar"
            : $"TokenBar — {mode.ShortLabel()}: {title}";
    }

    /// <summary>Taskbar theme; missing value = dark (the Windows default).</summary>
    private static bool IsSystemDark()
    {
        try
        {
            return Microsoft.Win32.Registry.GetValue(
                @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                "SystemUsesLightTheme", 0) is not int light || light == 0;
        }
        catch
        {
            return true;
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool DestroyIcon(nint hIcon);

    public void Dispose() => _icon.Dispose();
}

/// <summary>Minimal ICommand so the skeleton avoids an MVVM package pull.</summary>
public sealed class RelayCommand(Action execute) : System.Windows.Input.ICommand
{
    public event EventHandler? CanExecuteChanged { add { } remove { } }

    public bool CanExecute(object? parameter) => true;

    public void Execute(object? parameter) => execute();
}

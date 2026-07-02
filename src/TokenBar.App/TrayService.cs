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
        "tokenbar.tray.animate",
    ];

    private readonly TaskbarIcon _icon;
    private readonly TrayFeed _feed;
    private readonly TrayAnimator _animator;
    private string _iconSignature = "";
    private nint _hicon;

    public TrayService(FlyoutWindow flyout)
    {
        _icon = new TaskbarIcon
        {
            ToolTipText = "TokenBar",
        };
        _flyout = flyout;
        _icon.LeftClickCommand = new RelayCommand(flyout.ToggleFlyout);
        _icon.NoLeftClickDelay = true;
        // NOT SecondWindow: that mode parks a transparent helper window over
        // the desktop, which swallowed hover/wheel input meant for the flyout.
        _icon.ContextMenuMode = ContextMenuMode.PopupMenu;

        _feed = new TrayFeed(DispatcherQueue.GetForCurrentThread());
        _animator = new TrayAnimator(
            DispatcherQueue.GetForCurrentThread(), () => _feed.TokensPerMin, ApplyCachedIcon);
        _feed.Changed += () =>
        {
            UpdateIcon();
            RebuildMenu(); // quota percentages in the source picker move
        };
        AppSettings.Store.Changed += key =>
        {
            if (IconKeys.Contains(key))
            {
                UpdateIcon(); // settings writes happen on the UI thread
                RebuildMenu();
            }
        };

        UpdateIcon();
        RebuildMenu();
        _icon.ForceCreate();
    }

    private readonly FlyoutWindow _flyout;

    /// <summary>The full context menu (macOS splits this between the
    /// status-item right-click quota menu and the settings panel; Windows
    /// keeps one battery-style menu): Open · Menu bar shows · Quota source ·
    /// Settings · Quit. Rebuilt whenever the data it displays moves — the
    /// flyout is closed at that moment, so a rebuild is invisible.</summary>
    private void RebuildMenu()
    {
        var menu = new Microsoft.UI.Xaml.Controls.MenuFlyout();
        var open = new Microsoft.UI.Xaml.Controls.MenuFlyoutItem { Text = "Open TokenBar" };
        open.Click += (_, _) => _flyout.ShowFlyout();
        menu.Items.Add(open);
        menu.Items.Add(new Microsoft.UI.Xaml.Controls.MenuFlyoutSeparator());

        // Menu bar shows — the seven TrayModes as a radio group.
        var modes = new Microsoft.UI.Xaml.Controls.MenuFlyoutSubItem { Text = "Menu bar shows" };
        var current = TrayModes.Parse(AppSettings.Store.GetString(TrayModes.StorageKey));
        foreach (var mode in TrayModes.All)
        {
            var item = new Microsoft.UI.Xaml.Controls.RadioMenuFlyoutItem
            {
                Text = mode.Label(),
                GroupName = "tray.mode",
                IsChecked = mode == current,
            };
            var picked = mode;
            item.Click += (_, _) =>
                AppSettings.Store.SetString(TrayModes.StorageKey, picked.RawValue());
            modes.Items.Add(item);
        }

        menu.Items.Add(modes);

        // Quota source — macOS showQuotaMenu: Auto plus every error-free
        // agent's windows with live remaining percentages.
        var source = new Microsoft.UI.Xaml.Controls.MenuFlyoutSubItem { Text = "Quota source" };
        var selection = AppSettings.Store.GetString("tokenbar.quota.source", "auto")
            ?? QuotaResolver.Auto;
        AddQuotaChoice(source, "Auto (tightest window)", QuotaResolver.Auto, selection);
        var payload = _feed.Quota;
        if (payload is null)
        {
            source.Items.Add(new Microsoft.UI.Xaml.Controls.MenuFlyoutItem
            {
                Text = "Loading quotas…",
                IsEnabled = false,
            });
        }
        else
        {
            foreach (var agent in payload.Agents.Where(
                a => a.Error is null && a.Windows.Count > 0))
            {
                source.Items.Add(new Microsoft.UI.Xaml.Controls.MenuFlyoutSeparator());
                source.Items.Add(new Microsoft.UI.Xaml.Controls.MenuFlyoutItem
                {
                    Text = ClientRegistry.ShortName(agent.ClientId),
                    IsEnabled = false,
                });
                foreach (var window in agent.Windows)
                {
                    var left = (int)Math.Round(
                        Math.Clamp(window.RemainingPercent, 0, 100),
                        MidpointRounding.AwayFromZero);
                    AddQuotaChoice(
                        source, $"{window.Label} — {left}% left",
                        QuotaResolver.Selection(agent.ClientId, window.Label), selection);
                }
            }
        }

        menu.Items.Add(source);
        menu.Items.Add(new Microsoft.UI.Xaml.Controls.MenuFlyoutSeparator());
        var quit = new Microsoft.UI.Xaml.Controls.MenuFlyoutItem { Text = "Quit" };
        quit.Click += (_, _) =>
        {
            Dispose();
            Microsoft.UI.Xaml.Application.Current.Exit();
        };
        menu.Items.Add(quit);
        _icon.ContextFlyout = menu;
    }

    private static void AddQuotaChoice(
        Microsoft.UI.Xaml.Controls.MenuFlyoutSubItem parent,
        string label, string value, string selection)
    {
        var item = new Microsoft.UI.Xaml.Controls.RadioMenuFlyoutItem
        {
            Text = label,
            GroupName = "quota.source",
            IsChecked = selection == value,
        };
        item.Click += (_, _) =>
            AppSettings.Store.SetString("tokenbar.quota.source", value);
        parent.Items.Add(item);
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
        _icon.ToolTipText = title.Length == 0
            ? "TokenBar"
            : $"TokenBar — {mode.ShortLabel()}: {title}";

        // Hidden mode with an animation style hands the icon to the
        // animator; every other state renders one static frame here.
        if (mode == TrayMode.Hidden && styleRaw is "cat" or "parrot")
        {
            _animator.Start(styleRaw, dark,
                animate: AppSettings.Store.GetBool("tokenbar.tray.animate", true));
            return;
        }

        _animator.Stop();
        using var bmp = mode != TrayMode.Hidden && title.Length > 0
            ? TrayIconRenderer.RenderTitle(
                TrayModes.IconTitle(title),
                mode == TrayMode.QuotaLeft && remaining is { } q
                    ? TrayIconRenderer.GaugeColor(q) : null,
                dark)
            : TrayIconRenderer.RenderGauge(
                TrayIconRenderer.ParseGaugeStyle(styleRaw) ?? QuotaIconStyle.Bars,
                remaining, dark, coloring);
        ApplyIcon(bmp);
    }

    /// <summary>One-shot render: the service owns the HICON and destroys it
    /// when the next owned one replaces it.</summary>
    private void ApplyIcon(System.Drawing.Bitmap bmp)
    {
        var hicon = bmp.GetHicon();
        _icon.Icon = System.Drawing.Icon.FromHandle(hicon);
        if (_hicon != 0)
        {
            _ = DestroyIcon(_hicon);
        }

        _hicon = hicon;
    }

    /// <summary>Animator frames: cached icons the animator owns — never
    /// destroyed here, but a previously owned one-shot HICON is released
    /// once it is off screen.</summary>
    private void ApplyCachedIcon(System.Drawing.Icon icon)
    {
        _icon.Icon = icon;
        if (_hicon != 0)
        {
            _ = DestroyIcon(_hicon);
            _hicon = 0;
        }
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

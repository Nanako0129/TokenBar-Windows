using H.NotifyIcon;
using H.NotifyIcon.Core;
using Microsoft.UI.Xaml;

namespace TokenBar.App;

/// <summary>
/// Owns the notification-area icon (H.NotifyIcon): left click toggles the
/// flyout, right click gets the quota-source menu later (plan Phase 7).
/// Skeleton uses a generated placeholder glyph; the real gauge/animated
/// icons arrive with the tray renderers.
/// </summary>
public sealed class TrayService : IDisposable
{
    private readonly TaskbarIcon _icon;

    public TrayService(FlyoutWindow flyout)
    {
        _icon = new TaskbarIcon
        {
            ToolTipText = "TokenBar",
            IconSource = new GeneratedIconSource
            {
                Text = "TB",
                FontSize = 36,
            },
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
        _icon.ContextMenuMode = ContextMenuMode.SecondWindow;

        _icon.ForceCreate();
    }

    public void Dispose() => _icon.Dispose();
}

/// <summary>Minimal ICommand so the skeleton avoids an MVVM package pull.</summary>
public sealed class RelayCommand(Action execute) : System.Windows.Input.ICommand
{
    public event EventHandler? CanExecuteChanged { add { } remove { } }

    public bool CanExecute(object? parameter) => true;

    public void Execute(object? parameter) => execute();
}

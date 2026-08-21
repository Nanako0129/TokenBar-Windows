using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System.Reflection;
using TokenBar.Core;
using TokenBar.Interop;
// TokenBar.Core.Grid (the contribution-grid builder) collides with the XAML
// Grid — same clash DashboardView notes.
using Grid = Microsoft.UI.Xaml.Controls.Grid;

namespace TokenBar.App;

/// <summary>
/// The settings window (macOS SettingsWindowController + SettingsPanel):
/// single instance, Mica backdrop, fixed size, closing hides so position
/// survives. Content rebuilds on every Show — and, deferred, on every
/// settings write — so the panel always reflects live state (autostart is
/// re-read from the registry, quota windows from the tray feed). Every
/// control binds the same tokenbar.* keys the cards and tray read.
/// </summary>
public sealed class SettingsWindow : Window
{
    private static SettingsWindow? _shared;
    private readonly Func<AgentUsagePayload?> _quota;
    private readonly Func<UsagePayload?> _graph;
    private readonly Func<IReadOnlyList<TraceBucket>> _trace;
    private readonly ScrollViewer _scroll = new()
    {
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        Padding = new Thickness(20, 12, 16, 20),
    };

    // The macOS panel's right column: a live preview that tracks every
    // settings write, so picking an icon style or pace mode shows its
    // effect immediately.
    private readonly StackPanel _preview = new()
    {
        Spacing = 12,
        Width = 280,
        Margin = new Thickness(4, 12, 20, 20),
        VerticalAlignment = VerticalAlignment.Top,
    };

    // Sidebar (macOS SettingsPanel.Page): one NavigationView built once, its
    // four items never rebuilt. Rebuild() only regenerates each page's
    // StackPanel and re-attaches whichever one matches _selectedTag — the
    // NavigationView's own SelectedItem is untouched by a rebuild, which is
    // what keeps the user on the same page across a settings write and
    // across hide/show (the window is hidden, never destroyed, so this
    // singleton's state simply survives).
    private readonly NavigationView _nav = new()
    {
        PaneDisplayMode = NavigationViewPaneDisplayMode.Left,
        IsSettingsVisible = false,
        IsBackButtonVisible = NavigationViewBackButtonVisible.Collapsed,
        // The window is fixed at 900x640 and not resizable, so collapsing the
        // pane buys nothing — and the items carry no icons, so the compact
        // state has nothing to fall back to and just clips the labels to
        // "Menu"/"Dash"/"Gene"/"Abou". macOS has no such control either.
        IsPaneToggleButtonVisible = false,
        // NavigationView's default is 320, which would eat the width the 732
        // -> 900 widening was meant to give the content column: 900 - 304
        // (preview) - 320 - 36 (scroll padding) leaves ~240 for a panel whose
        // MaxWidth is 380, i.e. NARROWER than before the widening. 180 is the
        // macOS sidebar width (170pt) rounded to the Fluent metric, and keeps
        // the content column at its pre-existing ~380.
        OpenPaneLength = 180,
    };
    private readonly NavigationViewItem _menuBarItem = new() { Content = "Menu bar".Localized(), Tag = "menubar" };
    private readonly NavigationViewItem _dashboardItem = new() { Content = "Dashboard".Localized(), Tag = "dashboard" };
    private readonly NavigationViewItem _generalItem = new() { Content = "General".Localized(), Tag = "general" };
    private readonly NavigationViewItem _aboutItem = new() { Content = "About".Localized(), Tag = "about" };
    private readonly Dictionary<string, StackPanel> _pages = new(StringComparer.Ordinal);
    private string _selectedTag = "menubar";

    public static void Present(
        Func<AgentUsagePayload?> quota, Func<UsagePayload?> graph,
        Func<IReadOnlyList<TraceBucket>> trace)
    {
        _shared ??= new SettingsWindow(quota, graph, trace);
        _shared.Rebuild();
        _shared.AppWindow.Show();
        // Activate() alone cannot bring the window forward when the opener
        // has no foreground rights (tray/schtasks context, the flyout's old
        // lesson) — hoist it in z-order explicitly.
        _shared.AppWindow.MoveInZOrderAtTop();
        _shared.Activate();
        DevLog.Write($"settings: shown visible={_shared.AppWindow.IsVisible} " +
            $"pos={_shared.AppWindow.Position.X},{_shared.AppWindow.Position.Y} " +
            $"size={_shared.AppWindow.Size.Width}x{_shared.AppWindow.Size.Height}");
        _ = _shared.DispatcherQueue.TryEnqueue(
            Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            DevLog.Write($"settings layout: scroll={_shared._scroll.ActualWidth:F0}x" +
                $"{_shared._scroll.ActualHeight:F0} page={_shared._selectedTag} children=" +
                $"{(_shared._scroll.Content as StackPanel)?.Children.Count ?? -1}"));
    }

    private SettingsWindow(
        Func<AgentUsagePayload?> quota, Func<UsagePayload?> graph,
        Func<IReadOnlyList<TraceBucket>> trace)
    {
        _quota = quota;
        _graph = graph;
        _trace = trace;
        Title = $"{ProductIdentity.Name} Settings";
        SystemBackdrop = new MicaBackdrop();
        var root = new Grid();
        root.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(1, GridUnitType.Star),
        });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // NavigationView paints its content region with an opaque theme brush,
        // which reads as a lighter slab between the Mica pane and the Mica
        // preview column — one window showing two materials. The pane and the
        // preview column are both already transparent to the MicaBackdrop, so
        // clearing this one brush is what makes the whole window one surface.
        _nav.Resources["NavigationViewContentBackground"] =
            new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        _nav.Content = _scroll;
        _nav.PaneFooter = BuildFooter();
        _nav.MenuItems.Add(_menuBarItem);
        _nav.MenuItems.Add(_dashboardItem);
        _nav.MenuItems.Add(_generalItem);
        _nav.MenuItems.Add(_aboutItem);
        _nav.SelectedItem = _menuBarItem;
        _nav.SelectionChanged += (_, e) =>
        {
            if (e.SelectedItem is NavigationViewItem { Tag: string tag })
            {
                _selectedTag = tag;
                ShowPage(tag);
            }
        };
        root.Children.Add(_nav);
        Grid.SetColumn(_preview, 1);
        root.Children.Add(_preview);
        Content = root;

        // ApplicationIcon only reaches the executable; the title bar reads its
        // icon from the window. Fail soft — a missing asset should leave the
        // default icon, not take the settings window down with it.
        try
        {
            AppWindow.SetIcon(Path.Combine(
                AppContext.BaseDirectory, "Assets", "syrtis.ico"));
        }
        catch (Exception ex)
        {
            DevLog.Write($"settings icon: {ex.Message}");
        }

        var presenter = (OverlappedPresenter)AppWindow.Presenter;
        presenter.IsResizable = false;
        presenter.IsMaximizable = false;
        ApplySize();
        // Something in the show path renormalizes the size on a non-96-DPI
        // monitor (observed 525x800 → 420x640 at 125%); re-apply once the
        // window actually has focus and its final DPI.
        Activated += (_, _) => ApplySize();
        AppWindow.Closing += (_, e) =>
        {
            e.Cancel = true; // hide, macOS isReleasedWhenClosed=false parity
            AppWindow.Hide();
        };

        // A write from any control may change which sub-controls apply
        // (animate vs coloring, paceMode hidden in classic) — rebuild after
        // the click's event stack unwinds.
        AppSettings.Store.Changed += key =>
        {
            if (!key.StartsWith("tokenbar.", StringComparison.Ordinal)
                || key is "tokenbar.quota.lastRemaining" or "tokenbar.popover.height")
            {
                return;
            }

            // Changed fires on the writing thread — and a vanished-year clear
            // runs Store.Remove on a background parse lane — so hop to the UI
            // thread BEFORE touching AppWindow or any XAML. Only the two keys
            // that change which sub-controls exist rebuild the whole panel (a
            // full rebuild drops keyboard focus and scroll position, breaking
            // arrow-key radio navigation); everything else refreshes the
            // preview column in place.
            var rebuildAll = key is "tokenbar.tray.animationStyle"
                or "tokenbar.limits.layout"
                or ClientRegistry.TabHiddenKey
                or ClientRegistry.TabOrderKey;
            _ = DispatcherQueue.TryEnqueue(() =>
            {
                if (!AppWindow.IsVisible)
                {
                    return;
                }

                if (rebuildAll)
                {
                    Rebuild();
                }
                else
                {
                    RebuildPreview();
                }
            });
        };
    }

    private void ApplySize()
    {
        var scale = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96.0;
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        // Clamp before resizing: the window is fixed and not resizable, so a
        // display narrower than the requested size has no recovery path. The
        // centering below would compute a negative X and push both edges —
        // including the title-bar controls — off-screen. 640 is clamped for
        // the same reason on short work areas.
        var size = new Windows.Graphics.SizeInt32(
            Math.Min((int)(900 * scale), area.Width),
            Math.Min((int)(640 * scale), area.Height));
        if (AppWindow.Size == size)
        {
            return;
        }

        AppWindow.Resize(size);
        AppWindow.Move(new Windows.Graphics.PointInt32(
            area.X + (area.Width - AppWindow.Size.Width) / 2,
            area.Y + (area.Height - AppWindow.Size.Height) / 3));
    }

    private void Rebuild()
    {
        var store = AppSettings.Store;
        _pages["menubar"] = BuildMenuBarPage(store);
        _pages["dashboard"] = BuildDashboardPage(store);
        _pages["general"] = BuildGeneralPage(store);
        _pages["about"] = BuildAboutPage();
        ShowPage(_selectedTag);
        RebuildPreview();
    }

    /// <summary>Re-attaches whichever page's StackPanel matches
    /// <paramref name="tag"/> to the ScrollViewer. Called both from a fresh
    /// Rebuild() and directly from the NavigationView's SelectionChanged, so
    /// switching pages never waits on a rebuild.</summary>
    private void ShowPage(string tag)
    {
        if (_pages.TryGetValue(tag, out var page))
        {
            _scroll.Content = page;
            // Every page shares this one ScrollViewer, which keeps its offset
            // when the content swaps if that offset is still valid for the new
            // extent. Scrolling down Dashboard and switching to Menu bar would
            // otherwise open it part-way down with its first section cut off.
            // Offset 0 is always in range, so this needs no layout pass.
            _scroll.ChangeView(null, 0, null, disableAnimation: true);
        }
    }

    // ── Menu bar page: Tray shows, Tray icon, Quota source ──────────────
    private StackPanel BuildMenuBarPage(SettingsStore store)
    {
        var panel = new StackPanel { Spacing = 16, MaxWidth = 380 };

        // ── Menubar title (tray value) ─────────────────────────────────
        panel.Children.Add(Section("Tray shows".Localized(), RadioGroup(
            "tray.mode",
            TrayModes.All.Select(m => (m.RawValue(), m.Label())),
            TrayModes.Parse(store.GetString(TrayModes.StorageKey)).RawValue(),
            raw => store.SetString(TrayModes.StorageKey, raw))));

        // ── Tray icon ──────────────────────────────────────────────────
        var styleRaw = store.GetString("tokenbar.tray.animationStyle", "cat") ?? "cat";
        var icon = new StackPanel { Spacing = 8 };
        icon.Children.Add(RadioGroup(
            "tray.style",
            [
                ("cat", "Cat".Localized()), ("parrot", "Parrot".Localized()), ("bars", "Signal bars".Localized()),
                ("ring", "Ring gauge".Localized()), ("popsicle", "Melting popsicle".Localized()),
            ],
            styleRaw,
            raw => store.SetString("tokenbar.tray.animationStyle", raw)));
        if (styleRaw is "cat" or "parrot")
        {
            var animate = new ToggleSwitch
            {
                IsOn = store.GetBool("tokenbar.tray.animate", true),
                OnContent = null,
                OffContent = null,
            };
            animate.Toggled += (_, _) =>
                store.SetBool("tokenbar.tray.animate", animate.IsOn);
            icon.Children.Add(ToggleRow("Animate with token rate".Localized(), animate));
            icon.Children.Add(Hint(
                ("Idle purrs at 2 fps; a heavy session sprints. Shown only in "
                    + "the icon-only tray mode.").Localized()));
        }
        else
        {
            icon.Children.Add(RadioGroup(
                "icon.coloring",
                [
                    ("warning", "Color on warning only".Localized()),
                    ("always", "Always colored".Localized()),
                    ("never", "Never colored".Localized()),
                ],
                store.GetString("tokenbar.icon.coloring", "warning") ?? "warning",
                raw => store.SetString("tokenbar.icon.coloring", raw)));
            icon.Children.Add(Hint(
                "Battery-icon behavior: the gauge picks up color under 25% left."
                .Localized()));
        }

        panel.Children.Add(Section("Tray icon".Localized(), icon));

        // ── Quota source ───────────────────────────────────────────────
        var persistedSelection = store.GetString(
            "tokenbar.quota.source", QuotaResolver.Auto) ?? QuotaResolver.Auto;
        var payload = _quota();
        var selection = QuotaSelectionPolicy.EffectiveSelection(
            payload, persistedSelection);
        var choices = new List<(string, string)> { (QuotaResolver.Auto, "Auto (tightest window)".Localized()) };
        if (payload is not null)
        {
            foreach (var agent in payload.Agents.Where(a => a.Error is null))
            {
                foreach (var window in agent.UniqueCardWindows)
                {
                    choices.Add((
                        QuotaResolver.Selection(agent.ClientId, window.CardId),
                        $"{ClientRegistry.ShortName(agent.ClientId)} · {window.Label}"));
                }
            }
        }

        var quotaGroup = new StackPanel { Spacing = 8 };
        quotaGroup.Children.Add(RadioGroup(
            "quota.source", choices, selection,
            raw => store.SetString("tokenbar.quota.source", raw)));
        quotaGroup.Children.Add(Hint(
            "Feeds the gauge icon and the Quota left tray mode.".Localized()));
        panel.Children.Add(Section("Quota source".Localized(), quotaGroup));

        return panel;
    }

    // ── Dashboard page: Agent limits, Client tabs, Live trace, Flyout size ──
    private StackPanel BuildDashboardPage(SettingsStore store)
    {
        var panel = new StackPanel { Spacing = 16, MaxWidth = 380 };

        // ── Agent limits ───────────────────────────────────────────────
        var limits = new StackPanel { Spacing = 8 };
        var asUsed = new ToggleSwitch
        {
            IsOn = store.GetBool("tokenbar.limits.asUsed", false),
            OnContent = null,
            OffContent = null,
        };
        asUsed.Toggled += (_, _) => store.SetBool("tokenbar.limits.asUsed", asUsed.IsOn);
        limits.Children.Add(ToggleRow("Show as used".Localized(), asUsed));
        limits.Children.Add(Hint("Bars count up (used) instead of down (left).".Localized()));
        var layoutRaw = store.GetString("tokenbar.limits.layout", "full") ?? "full";
        limits.Children.Add(RadioGroup(
            "limits.layout",
            [("full", "Full (pace + run-out)".Localized()), ("classic", "Classic (compact)".Localized())],
            layoutRaw,
            raw => store.SetString("tokenbar.limits.layout", raw)));
        if (layoutRaw != "classic")
        {
            limits.Children.Add(RadioGroup(
                "limits.paceMode",
                [
                    ("historical", "Historical pace".Localized()),
                    ("linear", "Linear pace".Localized()),
                    ("off", "Pace off".Localized()),
                ],
                store.GetString("tokenbar.limits.paceMode", "historical") ?? "historical",
                raw => store.SetString("tokenbar.limits.paceMode", raw)));
            limits.Children.Add(Hint(
                ("The deficit/reserve marker. Historical learns your weekly "
                    + "usage curve; Linear paces evenly by the clock; Off hides it.")
                .Localized()));
        }

        panel.Children.Add(Section("Agent limits".Localized(), limits));

        // ── View tabs ──────────────────────────────────────────────────
        panel.Children.Add(Section("View tabs".Localized(), BuildViewTabs(store)));

        // ── Client tabs ────────────────────────────────────────────────
        panel.Children.Add(Section("Client tabs".Localized(), BuildClientTabs(store)));

        // ── Live trace ─────────────────────────────────────────────────
        var detailed = new ToggleSwitch
        {
            IsOn = store.GetBool("tokenbar.trace.detailed", false),
            OnContent = null,
            OffContent = null,
        };
        detailed.Toggled += (_, _) =>
            store.SetBool("tokenbar.trace.detailed", detailed.IsOn);
        var trace = new StackPanel { Spacing = 8 };
        trace.Children.Add(ToggleRow("Detailed rows".Localized(), detailed));
        trace.Children.Add(Hint("One row per agent and model instead of per app.".Localized()));
        panel.Children.Add(Section("Live trace".Localized(), trace));

        // ── Flyout size ────────────────────────────────────────────────
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        // Stored in DIPs (the flyout multiplies by its own monitor's scale);
        // WorkArea is physical, so divide before offering it as the range.
        var dipScale = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96.0;
        var workDips = area.Height / dipScale;
        var height = store.GetDouble("tokenbar.popover.height", 0);
        var sizeRow = new StackPanel { Spacing = 8 };
        var sizeLabel = Ui.Text(
            height <= 0
                ? "Height · Auto".Localized()
                : "Height · {0}px".Localized($"{height:F0}"),
            12);
        var auto = new Button
        {
            Content = "Auto".Localized(),
            FontSize = 11,
            Padding = new Thickness(8, 2, 8, 3),
            HorizontalAlignment = HorizontalAlignment.Right,
            Visibility = height > 0 ? Visibility.Visible : Visibility.Collapsed,
        };
        var slider = new Slider
        {
            Minimum = 480,
            Maximum = Math.Max(600, workDips - 24),
            StepFrequency = 10,
            Value = height <= 0 ? Math.Min(640, workDips * 0.6) : height,
        };
        // The height key skips the panel rebuild (see the Changed filter),
        // so the label and reset button update in place instead.
        slider.ValueChanged += (_, e) =>
        {
            if (e.NewValue > 0)
            {
                store.SetDouble("tokenbar.popover.height", e.NewValue);
                sizeLabel.Text = "Height · {0}px".Localized($"{e.NewValue:F0}");
                auto.Visibility = Visibility.Visible;
            }
        };
        auto.Click += (_, _) =>
        {
            store.SetDouble("tokenbar.popover.height", 0);
            sizeLabel.Text = "Height · Auto".Localized();
            auto.Visibility = Visibility.Collapsed;
        };
        var labelRow = new Grid();
        labelRow.Children.Add(sizeLabel);
        labelRow.Children.Add(auto);
        sizeRow.Children.Add(labelRow);
        sizeRow.Children.Add(slider);
        panel.Children.Add(Section("Flyout size".Localized(), sizeRow));

        return panel;
    }

    // ── General page: Startup, Data refresh ──────────────────────────────
    private StackPanel BuildGeneralPage(SettingsStore store)
    {
        var panel = new StackPanel { Spacing = 16, MaxWidth = 380 };

        // ── Startup ────────────────────────────────────────────────────
        var autostart = new ToggleSwitch
        {
            IsOn = AutostartService.IsEnabled,
            OnContent = null,
            OffContent = null,
        };
        autostart.Toggled += (_, _) =>
        {
            if (!AutostartService.SetEnabled(autostart.IsOn))
            {
                autostart.IsOn = AutostartService.IsEnabled; // stay honest
            }
        };
        panel.Children.Add(Section("Startup".Localized(), ToggleRow("Launch at login".Localized(), autostart)));

        // ── Data refresh ───────────────────────────────────────────────
        var refresh = new StackPanel { Spacing = 8 };
        refresh.Children.Add(RadioGroup(
            "refresh.interval",
            [
                ("1", "Every minute".Localized()), ("5", "Every 5 minutes".Localized()),
                ("15", "Every 15 minutes".Localized()), ("30", "Every 30 minutes".Localized()),
                ("60", "Every hour".Localized()),
            ],
            Math.Max(1, store.GetInt("tokenbar.refresh.intervalMin", 30)).ToString(),
            raw => store.SetInt("tokenbar.refresh.intervalMin", int.Parse(raw))));
        refresh.Children.Add(Hint(
            ("How often the tray forces a full log re-read; cached reads stay "
                + "continuous either way.").Localized()));
        panel.Children.Add(Section("Data refresh".Localized(), refresh));

        // ── Language ───────────────────────────────────────────────────
        var languageStored = store.GetString(AppLanguage.StorageKey, AppLanguage.System)
            ?? AppLanguage.System;
        var language = new StackPanel { Spacing = 2 };
        var relaunchNotice = Hint("Restart to apply the new language.".Localized());
        void SyncRelaunchNotice() =>
            relaunchNotice.Visibility = AppLanguage.NeedsRelaunch(
                store.GetString(AppLanguage.StorageKey, AppLanguage.System)
                    ?? AppLanguage.System,
                Localization.CurrentTag)
                ? Visibility.Visible
                : Visibility.Collapsed;

        SyncRelaunchNotice();
        language.Children.Add(RadioGroup(
            "language",
            AppLanguage.Options.Select(option => (option.Value, option.Label)),
            languageStored,
            picked =>
            {
                store.SetString(AppLanguage.StorageKey, picked);
                SyncRelaunchNotice();
            }));
        language.Children.Add(relaunchNotice);
        panel.Children.Add(Section("Language".Localized().Localized(), language));

        return panel;
    }

    // ── About page: About ─────────────────────────────────────────────────
    private static StackPanel BuildAboutPage()
    {
        var panel = new StackPanel { Spacing = 16, MaxWidth = 380 };

        // ── About ──────────────────────────────────────────────────────
        var about = new StackPanel { Spacing = 4 };
        about.Children.Add(Ui.Row(
            Ui.Text("Version".Localized(), 12),
            Ui.Dim(
                typeof(SettingsWindow).Assembly
                    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                    ?.InformationalVersion ?? "dev",
                12)));
        about.Children.Add(Hint(
            ("Shared parsing engine from tokscale-core, originally derived "
                + "from tokscale by junhoyeo; menu-bar concept from "
                + "handlecusion's tokcat.").Localized()));
        panel.Children.Add(Section("About".Localized(), about));

        return panel;
    }

    /// <summary>NavigationView.PaneFooter: GitHub and Sponsor links (macOS
    /// SettingsWindowView's FooterLink) — plain color (not the system link
    /// blue), a hover chip so two adjacent links read as separate targets.
    /// HyperlinkButton gives the pointing-hand cursor and the click-to-launch
    /// behavior for free via NavigateUri.</summary>
    private static StackPanel BuildFooter()
    {
        var footer = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            Margin = new Thickness(12, 4, 12, 12),
        };
        footer.Children.Add(FooterLink(
            "GitHub", "https://github.com/Nanako0129/TokenBar-Windows"));
        footer.Children.Add(FooterLink(
            "Sponsor".Localized(), "https://www.patreon.com/cw/Nanako0129/membership"));
        return footer;
    }

    private static HyperlinkButton FooterLink(string title, string url)
    {
        var transparent = new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0));
        var chip = new SolidColorBrush(Windows.UI.Color.FromArgb(23, 128, 128, 128));
        var secondary = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
        var primary = (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"];
        var link = new HyperlinkButton
        {
            Content = title,
            NavigateUri = new Uri(url),
            FontSize = 11,
            Padding = new Thickness(7, 3, 7, 4),
            Background = transparent,
            Foreground = secondary,
        };
        link.PointerEntered += (_, _) =>
        {
            link.Background = chip;
            link.Foreground = primary;
        };
        link.PointerExited += (_, _) =>
        {
            link.Background = transparent;
            link.Foreground = secondary;
        };
        return link;
    }

    /// <summary>One switch per hideable lens (macOS SettingsPanel's
    /// "View tabs"). Overview and Models are absent by construction — Overview
    /// is the fallback every hidden lens returns to, so offering to hide it
    /// would let the user remove the only guaranteed destination.</summary>
    private static StackPanel BuildViewTabs(SettingsStore store)
    {
        var panel = new StackPanel { Spacing = 2 };
        foreach (var view in AppViews.Toggleable)
        {
            var hidden = ClientRegistry.ParseIdSet(
                store.GetString(AppViews.HiddenKey) ?? string.Empty);
            var toggle = new ToggleSwitch
            {
                IsOn = !hidden.Contains(AppViews.Id(view)),
                OnContent = null,
                OffContent = null,
            };
            var id = AppViews.Id(view);
            toggle.Toggled += (_, _) =>
            {
                var next = new SortedSet<string>(ClientRegistry.ParseIdSet(
                    store.GetString(AppViews.HiddenKey) ?? string.Empty),
                    StringComparer.Ordinal);
                if (toggle.IsOn)
                {
                    next.Remove(id);
                }
                else
                {
                    next.Add(id);
                }

                store.SetString(AppViews.HiddenKey, string.Join(',', next));
            };
            panel.Children.Add(ToggleRow(AppViews.Label(view), toggle));
        }

        panel.Children.Add(Hint(
            ("Off removes a tab from the flyout's tab row. "
                + "Cost and token data are unaffected.").Localized()));
        return panel;
    }

    private StackPanel BuildClientTabs(SettingsStore store)
    {
        var panel = new StackPanel { Spacing = 6 };
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var present = (_graph()?.Summary.Clients ?? [])
            .Select(ClientRegistry.CanonicalClient)
            .Where(seen.Add)
            .ToList();
        var ordered = ClientRegistry.OrderedClients(present, store);
        if (ordered.Count == 0)
        {
            panel.Children.Add(Hint("No usage clients discovered yet.".Localized()));
            return panel;
        }

        var hidden = ClientRegistry.HiddenClients(store);
        for (var i = 0; i < ordered.Count; i++)
        {
            var id = ordered[i];
            var row = new Grid { ColumnSpacing = 4 };
            row.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(1, GridUnitType.Star),
            });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var name = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                VerticalAlignment = VerticalAlignment.Center,
            };
            name.Children.Add(Ui.Disc(ClientRegistry.Style(id).Color));
            name.Children.Add(Ui.Text(ClientRegistry.Style(id).DisplayName, 12));
            row.Children.Add(name);

            var up = new Button
            {
                Content = "↑",
                FontSize = 11,
                Padding = new Thickness(7, 2, 7, 3),
                IsEnabled = i > 0,
            };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(
                up, $"ClientTabUp_{id}");
            if (i > 0)
            {
                var target = ordered[i - 1];
                up.Click += (_, _) => MoveClientTab(store, present, id, target);
            }
            Grid.SetColumn(up, 1);
            row.Children.Add(up);

            var down = new Button
            {
                Content = "↓",
                FontSize = 11,
                Padding = new Thickness(7, 2, 7, 3),
                IsEnabled = i < ordered.Count - 1,
            };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(
                down, $"ClientTabDown_{id}");
            if (i < ordered.Count - 1)
            {
                var target = ordered[i + 1];
                down.Click += (_, _) => MoveClientTab(store, present, id, target);
            }
            Grid.SetColumn(down, 2);
            row.Children.Add(down);

            var shown = new ToggleSwitch
            {
                IsOn = !hidden.Contains(id),
                OnContent = null,
                OffContent = null,
                MinWidth = 0,
                Margin = new Thickness(2, -4, 0, -4),
            };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(
                shown, $"ClientTabVisibility_{id}");
            shown.Toggled += (_, _) => SetClientTabVisible(store, id, shown.IsOn);
            Grid.SetColumn(shown, 3);
            row.Children.Add(shown);
            HoverTip.Attach(up, () => "Move {0} up".Localized(ClientRegistry.ShortName(id)));
            HoverTip.Attach(down, () => "Move {0} down".Localized(ClientRegistry.ShortName(id)));
            HoverTip.Attach(shown, () => (shown.IsOn
                ? "Shown in client tabs"
                : "Hidden from client tabs").Localized());
            panel.Children.Add(row);
        }

        panel.Children.Add(Hint(
            "Overview includes every shown client. Hidden clients stay available in this list."
            .Localized()));
        return panel;
    }

    private static void MoveClientTab(
        SettingsStore store, IReadOnlyList<string> present, string from, string to)
    {
        var orderRaw = store.GetString(ClientRegistry.TabOrderKey) ?? "";
        var visible = ClientRegistry.OrderedClients(present, orderRaw);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var full = ClientRegistry.ParseIdList(orderRaw)
            .Select(ClientRegistry.CanonicalClient)
            .Concat(visible)
            .Where(seen.Add)
            .ToList();
        var merged = ClientRegistry.MergeReorder(full, visible, from, to);
        store.SetString(ClientRegistry.TabOrderKey, string.Join(',', merged));
    }

    private static void SetClientTabVisible(SettingsStore store, string id, bool visible)
    {
        var hidden = ClientRegistry.ParseIdList(
                store.GetString(ClientRegistry.TabHiddenKey) ?? "")
            .Select(ClientRegistry.CanonicalClient)
            .ToList();
        hidden.RemoveAll(value => value == id);
        if (!visible)
        {
            hidden.Add(id);
        }

        store.SetString(ClientRegistry.TabHiddenKey, string.Join(',', hidden));
    }

    /// <summary>Sample titles for the tray preview — the macOS mock menubar
    /// strips; quota_left uses the live reading when one exists.</summary>
    private static string SampleTitle(TrayMode mode, double? remaining) => mode switch
    {
        TrayMode.TodayTokens => "50M",
        TrayMode.TodayCost => "$5.20",
        TrayMode.TotalTokens => "1.5B",
        TrayMode.TotalCost => "$889.13",
        TrayMode.TokensPerMin => "12.4K/m",
        TrayMode.QuotaLeft => mode.Title(null, null, remaining ?? 57),
        _ => "",
    };

    private void RebuildPreview()
    {
        var store = AppSettings.Store;
        _preview.Children.Clear();
        _preview.Children.Add(Ui.Text("PREVIEW".Localized(), 10, 0.55, bold: true));

        var mode = TrayModes.Parse(store.GetString(TrayModes.StorageKey));
        var styleRaw = store.GetString("tokenbar.tray.animationStyle", "cat") ?? "cat";
        var coloring = TrayIconRenderer.ParseColoring(
            store.GetString("tokenbar.icon.coloring"));
        var persistedSelection = store.GetString(
            "tokenbar.quota.source", QuotaResolver.Auto) ?? QuotaResolver.Auto;
        var payload = _quota();
        var selection = QuotaSelectionPolicy.EffectiveSelection(
            payload, persistedSelection);
        var hidden = ClientRegistry.QuotaExcludedClients(store);
        var lastRemaining = store.GetDouble(
            "tokenbar.quota.lastRemaining", double.NaN);
        var lastSelection = store.GetString("tokenbar.quota.lastSelection");
        double? remaining = QuotaSelectionPolicy.Resolve(payload, selection, hidden)
            is { } pick
            ? Math.Clamp(pick.Window.RemainingPercent, 0, 100)
            : QuotaResolver.ExcludedAllCandidates(payload, selection, hidden)
                ? null
                : QuotaSelectionPolicy.MatchingLastGoodRemaining(
                    selection,
                    lastSelection,
                    double.IsFinite(lastRemaining) ? lastRemaining : null);
        var title = SampleTitle(mode, remaining);

        System.Drawing.Color? titleColor = mode == TrayMode.QuotaLeft
            ? TrayIconRenderer.GaugeColor(remaining ?? 57) : null;
        var gaugeStyle = TrayIconRenderer.ParseGaugeStyle(styleRaw);
        foreach (var dark in new[] { true, false })
        {
            // Hidden + cat/parrot really shows the animator, so the preview
            // uses the animation's first frame, not a gauge stand-in.
            using var bmp = mode != TrayMode.Hidden && title.Length > 0
                ? TrayIconRenderer.RenderTitle(
                    TrayModes.IconTitle(title), titleColor, dark)
                : gaugeStyle is { } style
                    ? TrayIconRenderer.RenderGauge(style, remaining ?? 57, dark, coloring)
                    : AnimationFrame(styleRaw, dark);
            var strip = new Border
            {
                Background = new SolidColorBrush(dark
                    ? Windows.UI.Color.FromArgb(255, 30, 30, 30)
                    : Windows.UI.Color.FromArgb(255, 240, 240, 240)),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(12, 6, 12, 6),
            };
            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 10,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var image = new Image { Width = 32, Height = 32, Source = ToImage(bmp) };
            row.Children.Add(image);
            var clock = Ui.Text(dark ? "下午 9:41" : "上午 9:41", 11, 0.7);
            clock.Foreground = new SolidColorBrush(dark
                ? Windows.UI.Color.FromArgb(255, 235, 235, 235)
                : Windows.UI.Color.FromArgb(255, 30, 30, 30));
            clock.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(clock);
            strip.Child = row;
            _preview.Children.Add(strip);
        }

        // Agent limits preview: mock windows through the real bar pipeline,
        // so asUsed/layout/paceMode picks show their effect immediately.
        _preview.Children.Add(Ui.Text("AGENT LIMITS".Localized(), 10, 0.55, bold: true));
        var asUsed = store.GetBool("tokenbar.limits.asUsed", false);
        var classic = store.GetString("tokenbar.limits.layout", "full") == "classic";
        var paceMode = store.GetString("tokenbar.limits.paceMode", "historical") switch
        {
            "linear" => PaceMode.Linear,
            "off" => PaceMode.Off,
            _ => PaceMode.Historical,
        };
        var now = DateTimeOffset.Now;
        var card = new StackPanel { Spacing = 5 };
        UsageWindow[] mocks =
        [
            new(
                Label: "Session", UsedPercent: 62, RemainingPercent: 38,
                ResetsAt: now.AddMinutes(95).UtcDateTime.ToString(
                    "yyyy-MM-dd'T'HH:mm:ss'Z'",
                    System.Globalization.CultureInfo.InvariantCulture),
                ResetText: "Resets in 1h 35m", WindowMinutes: 300,
                CardId: "session.v3",
                PaceStatus: new(
                    State: UsagePaceState.Available,
                    WindowKey: "session.v3",
                    DurationSeconds: 300 * 60,
                    DurationSource: UsagePaceDurationSource.Contract,
                    CompleteCycles: 5),
                HistoricalPace: new(
                    ExpectedUsedPercent: 48, EtaSeconds: 1_800,
                    WillLastToReset: false, RunOutProbability: 0.65)),
            new(
                Label: "Weekly", UsedPercent: 31, RemainingPercent: 69,
                ResetsAt: now.AddDays(4).UtcDateTime.ToString(
                    "yyyy-MM-dd'T'HH:mm:ss'Z'",
                    System.Globalization.CultureInfo.InvariantCulture),
                ResetText: "Resets in 4d", WindowMinutes: 10080,
                CardId: "weekly.v3",
                PaceStatus: new(
                    State: UsagePaceState.LearningHistory,
                    WindowKey: "weekly.v3",
                    DurationSeconds: 10080 * 60,
                    DurationSource: UsagePaceDurationSource.Contract,
                    CompleteCycles: 0)),
            new(
                Label: "Monthly", UsedPercent: 18, RemainingPercent: 82,
                ResetsAt: now.AddDays(20).UtcDateTime.ToString(
                    "yyyy-MM-dd'T'HH:mm:ss'Z'",
                    System.Globalization.CultureInfo.InvariantCulture),
                ResetText: "Resets in 20d",
                CardId: "monthly.v3",
                PaceStatus: new(
                    State: UsagePaceState.LearningDuration,
                    WindowKey: "monthly.v3",
                    DurationSource: UsagePaceDurationSource.Observed,
                    CompleteCycles: 0)),
            new(
                Label: "Project", UsedPercent: 54, RemainingPercent: 46,
                ResetsAt: now.AddDays(2).UtcDateTime.ToString(
                    "yyyy-MM-dd'T'HH:mm:ss'Z'",
                    System.Globalization.CultureInfo.InvariantCulture),
                ResetText: "Resets in 2d",
                CardId: "project.v3",
                PaceStatus: new(
                    State: UsagePaceState.Unavailable,
                    WindowKey: "project.v3",
                    CompleteCycles: 0,
                    Reason: UsagePaceUnavailableReason.History)),
        ];
        foreach (var window in mocks)
        {
            var row = UsagePace.RowPresentation(
                window, paceMode, asUsed, classic, now);
            card.Children.Add(DashboardView.QuotaRow(window, row, classic));
        }

        _preview.Children.Add(card);

        // Live session (macOS UsageTraceCard, the preview column's third
        // block). Rendered through the same Ui.TraceRows the Overview lens
        // uses, so the preview cannot drift from the real card. TrayFeed
        // already polls the live tail and marshals it back, so this reads a
        // value rather than making an FFI call on the UI thread.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var present = (_graph()?.Summary.Clients ?? [])
            .Select(ClientRegistry.CanonicalClient)
            .Where(seen.Add)
            .ToList();
        var selected = new HashSet<string>(
            ClientRegistry.DisplayClients(present, store), StringComparer.Ordinal);
        var detailed = store.GetBool("tokenbar.trace.detailed", false);
        if (Ui.TraceRows(_trace(), selected, detailed) is { } rows)
        {
            _preview.Children.Add(Ui.Text("LIVE SESSION".Localized(), 10, 0.55, bold: true));
            _preview.Children.Add(rows);
        }
    }

    /// <summary>frame-00 of the cat/parrot set, letterboxed like the
    /// animator does (kept tiny — the animator itself caches HICONs).</summary>
    private static System.Drawing.Bitmap AnimationFrame(string styleRaw, bool dark)
    {
        var canvas = new System.Drawing.Bitmap(32, 32);
        try
        {
            var dir = Path.Combine(
                AppContext.BaseDirectory, "Assets",
                $"anim-{(styleRaw == "parrot" ? "parrot" : "cat2")}{(dark ? "" : "-light")}");
            using var raw = new System.Drawing.Bitmap(Path.Combine(dir, "frame-00.png"));
            using var g = System.Drawing.Graphics.FromImage(canvas);
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            var scale = Math.Min(32.0 / raw.Width, 32.0 / raw.Height);
            var w = (float)(raw.Width * scale);
            var h = (float)(raw.Height * scale);
            g.DrawImage(raw, (32 - w) / 2, (32 - h) / 2, w, h);
        }
        catch
        {
            // missing asset: an empty square beats a crash in a preview
        }

        return canvas;
    }

    private static Microsoft.UI.Xaml.Media.Imaging.BitmapImage ToImage(
        System.Drawing.Bitmap bmp)
    {
        var stream = new MemoryStream();
        bmp.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
        stream.Position = 0;
        var image = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage();
        image.SetSource(stream.AsRandomAccessStream());
        return image;
    }

    // ── Control primitives (macOS SettingsPanel's four) ────────────────

    private static StackPanel Section(string title, UIElement content)
    {
        var section = new StackPanel { Spacing = 6 };
        var header = Ui.Text(title.ToUpperInvariant(), 10, 0.55, bold: true);
        section.Children.Add(header);
        section.Children.Add(content);
        return section;
    }

    private static StackPanel RadioGroup(
        string group, IEnumerable<(string Raw, string Label)> options,
        string current, Action<string> pick)
    {
        var stack = new StackPanel { Spacing = 2 };
        foreach (var (raw, label) in options)
        {
            var radio = new RadioButton
            {
                Content = label,
                GroupName = group,
                IsChecked = raw == current,
                FontSize = 12,
                MinHeight = 28,
                Padding = new Thickness(6, 0, 0, 0),
            };
            var value = raw;
            radio.Checked += (_, _) => pick(value);
            stack.Children.Add(radio);
        }

        return stack;
    }

    private static Grid ToggleRow(string label, ToggleSwitch toggle)
    {
        var row = new Grid();
        var text = Ui.Text(label, 12);
        text.VerticalAlignment = VerticalAlignment.Center;
        row.Children.Add(text);
        toggle.HorizontalAlignment = HorizontalAlignment.Right;
        toggle.MinWidth = 0; // drop the content-label reserve
        toggle.Margin = new Thickness(0, -4, 0, -4);
        row.Children.Add(toggle);
        return row;
    }

    private static TextBlock Hint(string text)
    {
        var hint = Ui.Dim(text, 11);
        hint.Opacity = 0.55;
        return hint;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hwnd);
}

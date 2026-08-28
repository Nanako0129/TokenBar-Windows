using System.Diagnostics;
using System.Reflection;
using System.Windows.Forms;

namespace Syrtis.Installer;

/// <summary>The four-page install wizard: Welcome, Location, Installing, Done.
/// One fixed-size form; pages swap in and out of a single content panel, with
/// a Back / Next / Cancel bar along the bottom (Done replaces that bar with a
/// single Finish button). Per-user only — never asks about machine-wide
/// install, never elevates.</summary>
internal sealed class WizardForm : Form
{
    private enum WizardStep { Welcome, Location, Installing, Done }

    private readonly string _setupExePath;
    private readonly string _productName;
    private readonly string _versionText;

    private WizardStep _step = WizardStep.Welcome;
    private string _installDir = SetupRunner.DefaultInstallDirectory;
    private SetupRunResult? _result;

    private readonly Panel _content;
    private readonly Button _backButton;
    private readonly Button _nextButton;
    private readonly Button _cancelButton;
    private readonly Button _finishButton;

    // Set while the Location page is on screen; used to keep Next's enabled
    // state in sync as the user types.
    private TextBox? _locationTextBox;

    // Set while the Done page is on screen; read when Finish is clicked.
    private CheckBox? _launchCheckBox;

    internal WizardForm(string setupExePath)
    {
        _setupExePath = setupExePath;
        _productName = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyProductAttribute>()?.Product ?? "Syrtis";
        _versionText = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "?";

        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96f, 96f);
        Text = Strings.WindowTitle;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(500, 360);
        var iconPath = Path.Combine(AppContext.BaseDirectory, "syrtis.ico");
        if (File.Exists(iconPath))
        {
            try { Icon = new Icon(iconPath); } catch (ArgumentException) { /* corrupt/unreadable icon file: keep the default */ }
        }

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
        };
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 1f));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 48f));

        _content = new Panel { Dock = DockStyle.Fill, Padding = new Padding(16) };

        var separator = new Panel { Dock = DockStyle.Fill, BackColor = SystemColors.ControlDark };

        var buttonBar = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(12, 10, 12, 10),
        };

        _cancelButton = MakeButton(Strings.ButtonCancel, OnCancelClicked);
        _nextButton = MakeButton(Strings.ButtonNext, OnNextClicked);
        _backButton = MakeButton(Strings.ButtonBack, OnBackClicked);
        _finishButton = MakeButton(Strings.ButtonFinish, OnFinishClicked);
        // Added in the visual left-to-right order Back, Next, Cancel: a
        // RightToLeft FlowLayoutPanel places the first added control
        // rightmost, so Cancel goes in first. Finish's position doesn't
        // matter — it's shown alone, replacing the other three.
        buttonBar.Controls.Add(_cancelButton);
        buttonBar.Controls.Add(_nextButton);
        buttonBar.Controls.Add(_backButton);
        buttonBar.Controls.Add(_finishButton);

        root.Controls.Add(_content, 0, 0);
        root.Controls.Add(separator, 0, 1);
        root.Controls.Add(buttonBar, 0, 2);
        Controls.Add(root);

        AcceptButton = _nextButton;

        FormClosing += (_, e) =>
        {
            // Killing Setup mid-install would leave a broken install; the
            // Installing page already disables Cancel for the same reason,
            // this closes the same door via the title-bar X / Alt+F4.
            if (_step == WizardStep.Installing)
            {
                e.Cancel = true;
            }
        };

        GoToStep(WizardStep.Welcome);
    }

    private static Button MakeButton(string text, EventHandler onClick)
    {
        var button = new Button
        {
            Text = text,
            AutoSize = true,
            MinimumSize = new Size(90, 26),
            Margin = new Padding(6, 0, 0, 0),
        };
        button.Click += onClick;
        return button;
    }

    private void GoToStep(WizardStep step)
    {
        _step = step;
        _content.Controls.Clear();
        _locationTextBox = null;
        _launchCheckBox = null;

        Control page = step switch
        {
            WizardStep.Welcome => BuildWelcomePage(),
            WizardStep.Location => BuildLocationPage(),
            WizardStep.Installing => BuildInstallingPage(),
            WizardStep.Done => BuildDonePage(),
            _ => throw new InvalidOperationException($"Unknown wizard step: {step}"),
        };
        page.Dock = DockStyle.Fill;
        _content.Controls.Add(page);

        UpdateButtons();
    }

    private void UpdateButtons()
    {
        var showFinish = _step == WizardStep.Done;
        _backButton.Visible = !showFinish;
        _nextButton.Visible = !showFinish;
        _cancelButton.Visible = !showFinish;
        _finishButton.Visible = showFinish;

        switch (_step)
        {
            case WizardStep.Welcome:
                _backButton.Enabled = false;
                _nextButton.Enabled = true;
                _cancelButton.Enabled = true;
                break;
            case WizardStep.Location:
                _backButton.Enabled = true;
                _nextButton.Enabled = IsValidInstallPath(_locationTextBox?.Text ?? string.Empty);
                _cancelButton.Enabled = true;
                break;
            case WizardStep.Installing:
                // All three disabled, Cancel included: there is no safe way
                // to interrupt Setup.exe once it has started.
                _backButton.Enabled = false;
                _nextButton.Enabled = false;
                _cancelButton.Enabled = false;
                break;
            case WizardStep.Done:
                _finishButton.Enabled = true;
                break;
        }
    }

    // --- Page 1: Welcome ---------------------------------------------------

    private Control BuildWelcomePage()
    {
        var layout = new TableLayoutPanel { ColumnCount = 1, RowCount = 3, Dock = DockStyle.Fill };

        var iconPath = Path.Combine(AppContext.BaseDirectory, "syrtis.ico");
        if (File.Exists(iconPath))
        {
            try
            {
                using var icon = new Icon(iconPath, new Size(96, 96));
                var picture = new PictureBox
                {
                    Image = icon.ToBitmap(),
                    SizeMode = PictureBoxSizeMode.Zoom,
                    Size = new Size(96, 96),
                    Margin = new Padding(0, 8, 0, 16),
                };
                layout.Controls.Add(picture);
            }
            catch (ArgumentException) { /* corrupt/unreadable icon file: show no image */ }
        }

        var heading = new Label
        {
            Text = Strings.WelcomeHeading(_productName),
            Font = new Font(Font.FontFamily, 13f, FontStyle.Bold),
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 8),
        };
        layout.Controls.Add(heading);

        var body = new Label
        {
            Text = Strings.WelcomeBody(_productName, _versionText),
            AutoSize = true,
            MaximumSize = new Size(440, 0),
        };
        layout.Controls.Add(body);

        return layout;
    }

    // --- Page 2: Location ---------------------------------------------------

    private Control BuildLocationPage()
    {
        var layout = new TableLayoutPanel { ColumnCount = 1, RowCount = 5, Dock = DockStyle.Fill, AutoSize = true };

        layout.Controls.Add(new Label
        {
            Text = Strings.LocationHeading,
            Font = new Font(Font.FontFamily, 12f, FontStyle.Bold),
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 8),
        });

        layout.Controls.Add(new Label
        {
            Text = Strings.LocationBody,
            AutoSize = true,
            MaximumSize = new Size(460, 0),
            Margin = new Padding(0, 0, 0, 12),
        });

        var pathRow = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Top };
        pathRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        pathRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        var textBox = new TextBox { Text = _installDir, Width = 320, Anchor = AnchorStyles.Left | AnchorStyles.Right };
        textBox.TextChanged += (_, _) =>
        {
            _installDir = textBox.Text;
            UpdateButtons();
        };
        _locationTextBox = textBox;

        var browseButton = new Button { Text = Strings.ButtonBrowse, AutoSize = true, Margin = new Padding(8, 0, 0, 0) };
        browseButton.Click += (_, _) => BrowseForFolder(textBox);

        pathRow.Controls.Add(textBox, 0, 0);
        pathRow.Controls.Add(browseButton, 1, 0);
        layout.Controls.Add(pathRow);

        layout.Controls.Add(new Label
        {
            Text = Strings.LocationPerUserNotice,
            AutoSize = true,
            MaximumSize = new Size(460, 0),
            Margin = new Padding(0, 16, 0, 0),
            ForeColor = SystemColors.GrayText,
        });

        var invalidHint = new Label
        {
            Text = Strings.LocationInvalidPath,
            AutoSize = true,
            ForeColor = Color.Firebrick,
            Margin = new Padding(0, 8, 0, 0),
            Visible = !IsValidInstallPath(_installDir),
        };
        textBox.TextChanged += (_, _) => invalidHint.Visible = !IsValidInstallPath(textBox.Text);
        layout.Controls.Add(invalidHint);

        return layout;
    }

    private void BrowseForFolder(TextBox textBox)
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = Strings.BrowseDialogDescription,
            SelectedPath = IsValidInstallPath(textBox.Text) ? textBox.Text : SetupRunner.DefaultInstallDirectory,
            ShowNewFolderButton = true,
        };
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            textBox.Text = dialog.SelectedPath;
        }
    }

    /// <summary>A "usable absolute directory path": rooted, and syntactically
    /// well-formed (Path.GetFullPath does not throw). Whether it can actually
    /// be created is left to Setup.exe, which reports failure through its
    /// exit code on the Done page — this is user-typing feedback, not a
    /// filesystem permission check.</summary>
    private static bool IsValidInstallPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path))
        {
            return false;
        }

        try
        {
            Path.GetFullPath(path);
            return true;
        }
        catch (ArgumentException) { return false; }
        catch (NotSupportedException) { return false; }
        catch (PathTooLongException) { return false; }
    }

    // --- Page 3: Installing --------------------------------------------------

    private Control BuildInstallingPage()
    {
        var layout = new TableLayoutPanel { ColumnCount = 1, RowCount = 3, Dock = DockStyle.Fill };

        layout.Controls.Add(new Label
        {
            Text = Strings.InstallingHeading,
            Font = new Font(Font.FontFamily, 12f, FontStyle.Bold),
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 8),
        });

        layout.Controls.Add(new Label
        {
            Text = Strings.InstallingStatus,
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 16),
        });

        // Velopack's Setup.exe --silent reports no progress; an indeterminate
        // bar is the honest control here, not a faked percentage.
        layout.Controls.Add(new ProgressBar
        {
            Style = ProgressBarStyle.Marquee,
            MarqueeAnimationSpeed = 30,
            Width = 440,
            Height = 20,
        });

        return layout;
    }

    private void StartInstall()
    {
        var setupExePath = _setupExePath;
        var installDir = _installDir;
        var logPath = SetupRunner.DefaultLogPath;

        Task.Run(() => SetupRunner.Run(setupExePath, installDir, logPath))
            .ContinueWith(
                task =>
                {
                    _result = task.IsFaulted
                        ? new SetupRunResult(-1, logPath)
                        : task.Result;
                    GoToStep(WizardStep.Done);
                },
                CancellationToken.None,
                TaskContinuationOptions.None,
                TaskScheduler.FromCurrentSynchronizationContext());
    }

    // --- Page 4: Done ----------------------------------------------------

    private Control BuildDonePage()
    {
        var layout = new TableLayoutPanel { ColumnCount = 1, RowCount = 3, Dock = DockStyle.Fill };
        var succeeded = _result is { ExitCode: 0 };

        layout.Controls.Add(new Label
        {
            Text = succeeded ? Strings.DoneHeadingSuccess : Strings.DoneHeadingFailure,
            Font = new Font(Font.FontFamily, 13f, FontStyle.Bold),
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 8),
        });

        var bodyText = succeeded
            ? Strings.DoneBodySuccess(_productName)
            : Strings.DoneBodyFailure(_productName, _result?.ExitCode ?? -1, _result?.LogPath ?? SetupRunner.DefaultLogPath);
        layout.Controls.Add(new Label
        {
            Text = bodyText,
            AutoSize = true,
            MaximumSize = new Size(460, 0),
            Margin = new Padding(0, 0, 0, 16),
        });

        if (succeeded)
        {
            var checkBox = new CheckBox
            {
                Text = Strings.DoneLaunchCheckbox(_productName),
                Checked = true,
                AutoSize = true,
            };
            _launchCheckBox = checkBox;
            layout.Controls.Add(checkBox);
        }

        return layout;
    }

    private void OnFinishClicked(object? sender, EventArgs e)
    {
        if (_result is { ExitCode: 0 } && _launchCheckBox is { Checked: true })
        {
            LaunchInstalledApp();
        }

        Close();
    }

    /// <summary>Velopack's per-user layout places the runnable app at
    /// "&lt;installDir&gt;\current\&lt;AssemblyName&gt;.App.exe".
    ///
    /// <para>Checked against a real install rather than inferred: Velopack's
    /// own Start Menu and Desktop shortcuts both target
    /// <c>...\Nyanako.Syrtis\current\Syrtis.App.exe</c>. There is also a
    /// 518 KB stub at the install root, and pointing here rather than at that
    /// stub is deliberate — it is what Velopack itself points at.</para>
    ///
    /// <para>Best-effort: if the app cannot be found or started, Setup already
    /// reported success, so this silently does nothing rather than surfacing a
    /// second dialog for what is a launch convenience.</para></summary>
    private void LaunchInstalledApp()
    {
        var exePath = Path.Combine(_installDir, "current", $"{_productName}.App.exe");
        if (!File.Exists(exePath))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo { FileName = exePath, UseShellExecute = true });
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Best-effort launch; installation itself already succeeded.
        }
    }

    // --- Navigation ----------------------------------------------------------

    private void OnBackClicked(object? sender, EventArgs e)
    {
        if (_step == WizardStep.Location)
        {
            GoToStep(WizardStep.Welcome);
        }
    }

    private void OnNextClicked(object? sender, EventArgs e)
    {
        switch (_step)
        {
            case WizardStep.Welcome:
                GoToStep(WizardStep.Location);
                break;
            case WizardStep.Location:
                if (IsValidInstallPath(_installDir))
                {
                    GoToStep(WizardStep.Installing);
                    StartInstall();
                }
                break;
        }
    }

    private void OnCancelClicked(object? sender, EventArgs e) => Close();
}

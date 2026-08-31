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

        // The Welcome page states what will be installed, so both halves of
        // that sentence are claims about the payload, not about this wrapper.
        // They were read from this assembly, which is only correct by accident:
        // in slice 1a the setup file is whatever path was passed in, and the
        // repo version stamped on this exe has nothing to do with it. Velopack's
        // Setup.exe does carry the app's own identity — measured on a packed
        // 0.2.1 build, FileVersion 0.2.1 and ProductName Syrtis, not Velopack's
        // own — so ask the file that is about to be run.
        var payload = ReadPayloadIdentity(setupExePath);
        var self = Assembly.GetExecutingAssembly();
        _productName = PayloadIdentity.Choose(
            payload.Product,
            self.GetCustomAttribute<AssemblyProductAttribute>()?.Product,
            "Syrtis");
        _versionText = PayloadIdentity.Choose(
            payload.Version,
            self.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                ?? self.GetName().Version?.ToString(),
            "?");

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

    /// <summary>Product name and version as declared by the setup file itself.
    /// Either may come back null: a file with no version resource, or an
    /// unreadable one. Never throws — a Welcome page is not worth failing an
    /// install over.</summary>
    private static (string? Product, string? Version) ReadPayloadIdentity(string setupExePath)
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(setupExePath);
            return (info.ProductName, info.ProductVersion ?? info.FileVersion);
        }
        catch (Exception)
        {
            return (null, null);
        }
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
        // AcceptButton has to follow the page, not be set once at construction:
        // Enter fires the assigned button even when it is hidden, so leaving
        // Next as the accept button left the Done page with no keyboard route
        // to the only control on it. Nothing visible would have happened.
        AcceptButton = showFinish ? _finishButton : _nextButton;

        switch (_step)
        {
            case WizardStep.Welcome:
                _backButton.Enabled = false;
                _nextButton.Enabled = true;
                _cancelButton.Enabled = true;
                break;
            case WizardStep.Location:
                _backButton.Enabled = true;
                _nextButton.Enabled = InstallPath.IsValidInstallPath(_locationTextBox?.Text ?? string.Empty);
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
            Visible = !InstallPath.IsValidInstallPath(_installDir),
        };
        textBox.TextChanged += (_, _) => invalidHint.Visible = !InstallPath.IsValidInstallPath(textBox.Text);
        layout.Controls.Add(invalidHint);

        return layout;
    }

    private void BrowseForFolder(TextBox textBox)
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = Strings.BrowseDialogDescription,
            SelectedPath = InstallPath.IsValidInstallPath(textBox.Text) ? textBox.Text : SetupRunner.DefaultInstallDirectory,
            ShowNewFolderButton = true,
        };
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            textBox.Text = dialog.SelectedPath;
        }
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
        // Normalise here rather than while the user types — rewriting the box
        // under a caret fights whoever is editing it. What Setup receives is
        // therefore the resolved path rather than the keystrokes, and the
        // directory the Launch button composes against below is the same one.
        //
        // GetFullPath folds "C:\a\..\b" to "C:\b" but it does NOT drop a
        // trailing separator — measured: GetFullPath("C:\foo\") is "C:\foo\".
        // An earlier version of this comment claimed both, having checked only
        // the first, and that unchecked half is what hid a P1: a value ending
        // in a backslash used to break out of its own quotes on the command
        // line. SetupRunner.Quote handles it now; the point here is that the
        // string arriving there can still legitimately end in one.
        _installDir = Path.GetFullPath(_installDir);
        var installDir = _installDir;
        var logPath = SetupRunner.NewLogPath();

        Task.Run(() => SetupRunner.Run(setupExePath, installDir, logPath))
            .ContinueWith(
                task =>
                {
                    // Carry the reason, not just the code. Discarding
                    // task.Exception left the Done page able to say only
                    // "exit code -1" and then send the user to a log Setup
                    // never opened — the silent path already reports the
                    // message and the two surfaces have to agree.
                    _result = task.IsFaulted
                        ? new SetupRunResult(
                            ExitCodes.LaunchFailed,
                            // No log: Setup never ran, so it wrote nothing.
                            // Carrying the path here would put the same stale
                            // file back into the result by another door.
                            logPath: null,
                            Describe(task.Exception))
                        : task.Result;
                    GoToStep(WizardStep.Done);
                },
                CancellationToken.None,
                TaskContinuationOptions.None,
                TaskScheduler.FromCurrentSynchronizationContext());
    }

    /// <summary>The innermost message of a faulted install task. Task wraps
    /// everything in AggregateException, whose own message is boilerplate
    /// about one or more errors — useless on a dialog.</summary>
    private static string Describe(AggregateException? fault) =>
        fault?.GetBaseException().Message ?? string.Empty;

    // --- Page 4: Done ----------------------------------------------------

    /// <summary>What the Done page says when the install did not succeed.
    /// Three distinct cases, because they need three different things from the
    /// user: Setup never started (show why, and do not mention a log), Setup
    /// ran and failed with a log to read, and Setup ran and failed without one.
    ///
    /// <para>Whether there is a log is not decided here. This asks the result,
    /// and SetupRunner answers by whether the file was written during the run
    /// it just performed. An earlier version tested File.Exists at this point
    /// and was wrong in the way that matters: the log path used to be a fixed name in
    /// %TEMP%, so a leftover from a previous install passes an existence check
    /// and the page would offer an unrelated run's record — possibly a
    /// successful one — as the explanation for this failure.</para></summary>
    private string FailureBody()
    {
        if (_result is not { } result)
        {
            // Unreachable: Done is only entered from the install continuation,
            // which always assigns _result first.
            return Strings.DoneBodyFailureNoLog(_productName, ExitCodes.LaunchFailed);
        }

        if (!string.IsNullOrEmpty(result.LaunchError))
        {
            return Strings.DoneBodyLaunchFailed(_productName, result.LaunchError!);
        }

        return result.LogPath is { } log
            ? Strings.DoneBodyFailure(_productName, result.ExitCode, log)
            : Strings.DoneBodyFailureNoLog(_productName, result.ExitCode);
    }

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
            : FailureBody();
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
                if (InstallPath.IsValidInstallPath(_installDir))
                {
                    GoToStep(WizardStep.Installing);
                    StartInstall();
                }
                break;
        }
    }

    private void OnCancelClicked(object? sender, EventArgs e) => Close();
}

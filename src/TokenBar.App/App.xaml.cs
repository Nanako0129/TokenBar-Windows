using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.UI.Xaml;

namespace TokenBar.App;

public partial class App : Application
{
    private static Mutex? _singleInstance;

    private TrayService? _tray;
    private FlyoutWindow? _flyout;
    private bool _started;
    private bool _startupSmokeRequested;
    private string? _startupSmokePath;
    private string? _startupSmokeError;
    private int _updateCheckStarted;

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

        var startupSmoke = ParseStartupSmokeArguments();
        _startupSmokeRequested = startupSmoke.Requested;
        _startupSmokePath = startupSmoke.Path;
        _startupSmokeError = startupSmoke.Error;

        // Tray-resident app: no window shows at launch; the tray icon owns
        // the flyout's lifetime (mirrors the macOS NSStatusItem shell).
        // Single instance: a second launch (autostart + manual, say) exits
        // quietly; the mutex lives for the process lifetime.
        _singleInstance = new Mutex(true, @"Local\TokenBar.App.SingleInstance", out var isFirst);
        if (!isFirst)
        {
            if (_startupSmokeRequested)
            {
                Environment.ExitCode = 1;
            }
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

            // The opt-in startup probe deliberately runs through one
            // DispatcherQueue turn after both windows/services are constructed.
            // The normal launch path is untouched when the flag is absent.
            if (_startupSmokeRequested)
            {
                QueueStartupSmoke();
            }

            StartUpdateCheckOnce();

            // Dev-only 3D panel (Phase 8 Gate 0). --graph3d mounts and shows
            // it for manual inspection; --soak3d runs the device-lifecycle
            // soak (50 cycles, or --soak3d-minutes duration mode) and exits
            // with a pass/fail code.
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

    private void StartUpdateCheckOnce()
    {
        if (Interlocked.Exchange(ref _updateCheckStarted, 1) != 0)
        {
            return;
        }

        try
        {
            _ = Task.Run(CheckForUpdatesOnceAsync);
        }
        catch (Exception ex)
        {
            LogUpdateFailure("update-check", ex);
        }
    }

    private async Task CheckForUpdatesOnceAsync()
    {
        try
        {
            var flow = new UpdateFlow();
            var candidate = await flow.CheckForUpdatesAsync().ConfigureAwait(false);
            if (candidate is null)
            {
                DevLog.Write("update-check: none");
                return;
            }

            if (!QueueUpdateUi(() => PublishUpdate(flow, candidate)))
            {
                throw new InvalidOperationException();
            }
        }
        catch (Exception ex)
        {
            LogUpdateFailure("update-check", ex);
        }
    }

    private void PublishUpdate(UpdateFlow flow, UpdateCandidate candidate)
    {
        try
        {
            var tray = _tray;
            if (tray is null || !tray.CanHandoff)
            {
                return;
            }

            if (tray.PublishUpdate(
                candidate.Version,
                () => StartUpdateDownload(flow, candidate)))
            {
                DevLog.Write($"update-available: v{candidate.Version}");
            }
        }
        catch (Exception ex)
        {
            LogUpdateFailure("update-check", ex);
        }
    }

    private void StartUpdateDownload(UpdateFlow flow, UpdateCandidate candidate)
    {
        try
        {
            _ = Task.Run(() => DownloadUpdateAsync(flow, candidate));
        }
        catch (Exception ex)
        {
            RestoreUpdateAction("update-download");
            LogUpdateFailure("update-download", ex);
        }
    }

    private async Task DownloadUpdateAsync(
        UpdateFlow flow,
        UpdateCandidate candidate)
    {
        DevLog.Write($"update-download: start v{candidate.Version}");
        try
        {
            await flow.DownloadAndVerifyAsync(candidate).ConfigureAwait(false);
            DevLog.Write($"update-download: verified v{candidate.Version}");
            if (!QueueUpdateUi(() => HandoffUpdate(flow, candidate)))
            {
                throw new InvalidOperationException();
            }
        }
        catch (Exception ex)
        {
            LogUpdateFailure("update-download", ex);
            _ = QueueUpdateUi(() => RestoreUpdateAction("update-download"));
        }
    }

    private void HandoffUpdate(UpdateFlow flow, UpdateCandidate candidate)
    {
        var tray = _tray;
        if (tray is null || !tray.CanHandoff)
        {
            return;
        }

        try
        {
            var quit = TrayService.QuitApp
                ?? throw new InvalidOperationException();
            DevLog.Write($"update-handoff: start v{candidate.Version}");
            if (flow.TryHandoff(
                candidate,
                () => tray.CanHandoff,
                () =>
                {
                    tray.CompleteUpdateAction();
                    quit();
                }))
            {
                DevLog.Write($"update-handoff: started v{candidate.Version}");
            }
        }
        catch (Exception ex)
        {
            RestoreUpdateAction("update-handoff");
            LogUpdateFailure("update-handoff", ex);
        }
    }

    private void RestoreUpdateAction(string stage)
    {
        try
        {
            _tray?.RestoreUpdateAction();
        }
        catch (Exception ex)
        {
            LogUpdateFailure(stage, ex);
        }
    }

    private bool QueueUpdateUi(Action action) =>
        _flyout?.DispatcherQueue.TryEnqueue(() => action()) == true;

    private static void LogUpdateFailure(string stage, Exception exception)
    {
        var type = exception.GetType().Name;
        if (type.Length > 64)
        {
            type = type[..64];
        }

        DevLog.Write($"{stage}: failed {type}");
    }

    private static (bool Requested, string? Path, string? Error)
        ParseStartupSmokeArguments()
    {
        var args = Environment.GetCommandLineArgs();
        var indexes = args
            .Select((value, index) => (value, index))
            .Where(item => string.Equals(
                item.value, "--startup-smoke", StringComparison.Ordinal))
            .Select(item => item.index)
            .ToList();
        if (indexes.Count == 0)
        {
            return (false, null, null);
        }

        if (indexes.Count != 1)
        {
            return (true, null, "--startup-smoke must appear exactly once");
        }

        var index = indexes[0];
        if (index + 1 >= args.Length)
        {
            return (true, null, "--startup-smoke requires a sentinel path");
        }

        var path = args[index + 1];
        if (string.IsNullOrWhiteSpace(path)
            || path.StartsWith("--", StringComparison.Ordinal))
        {
            return (true, null, "--startup-smoke requires a non-empty sentinel path");
        }

        return (true, path, null);
    }

    private void QueueStartupSmoke()
    {
        if (_flyout is null || _tray is null)
        {
            FailStartupSmoke("startup services were not constructed");
            return;
        }

        if (!_flyout.DispatcherQueue.TryEnqueue(() =>
            CompleteStartupSmoke(_startupSmokePath, _startupSmokeError)))
        {
            FailStartupSmoke("DispatcherQueue rejected the startup probe");
        }
    }

    private void CompleteStartupSmoke(string? path, string? parseError)
    {
        try
        {
            if (parseError is not null)
            {
                throw new InvalidOperationException(parseError);
            }

            if (path is null)
            {
                throw new InvalidOperationException(
                    "--startup-smoke sentinel path is missing");
            }

            WriteStartupSmokeSentinel(path);
            Environment.ExitCode = 0;
            DevLog.Write("startup-smoke: success stage=tray-ready");
        }
        catch (Exception ex)
        {
            Environment.ExitCode = 1;
            DevLog.Write($"startup-smoke: FAILED {ex.Message}");
        }
        finally
        {
            // Route shutdown through the existing tray owner so feed/timers,
            // tray resources, and owned GDI handles are released first.
            TrayService.QuitApp?.Invoke();
        }
    }

    private void FailStartupSmoke(string message)
    {
        Environment.ExitCode = 1;
        DevLog.Write($"startup-smoke: FAILED {message}");
        TrayService.QuitApp?.Invoke();
    }

    private static void WriteStartupSmokeSentinel(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (File.Exists(fullPath) || Directory.Exists(fullPath))
        {
            throw new IOException($"sentinel path already exists: {path}");
        }

        var parent = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrEmpty(parent) || !Directory.Exists(parent))
        {
            throw new DirectoryNotFoundException(
                $"sentinel parent directory does not exist: {path}");
        }

        var version = typeof(App).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;
        if (string.IsNullOrEmpty(version))
        {
            throw new InvalidOperationException(
                "assembly informational version is missing");
        }

        var sentinel = new StartupSmokeSentinel(
            Environment.ProcessId,
            version,
            RuntimeInformation.ProcessArchitecture.ToString(),
            "tray-ready");
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
        };

        // Serialize and flush beside the destination, then atomically rename
        // without overwrite. A verifier can therefore treat appearance of the
        // final path as a complete record, while a racing existing destination
        // remains a hard failure.
        var tempPath = Path.Combine(
            parent,
            $".{Path.GetFileName(fullPath)}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(
                tempPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                options: FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, sentinel, options);
                stream.Flush(flushToDisk: true);
            }

            File.Move(tempPath, fullPath, overwrite: false);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    private sealed record StartupSmokeSentinel(
        int Pid,
        string InformationalVersion,
        string ProcessArchitecture,
        string Stage);

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

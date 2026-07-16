namespace TokenBar.App;

/// <summary>
/// Phase 8 Gate 0 soak: force the 3D panel active, then toggle the flyout
/// shown/hidden so the SwapChainPanel device/swapchain is created and released
/// on every cycle. Writes ONE summary line via DevLog and exits with code 0
/// (clean) / 1 (leak, device-removed, or exception) — assertable over SSH from
/// the log + exit code, no window inspection needed.
/// </summary>
internal static class Soak3D
{
    private const int Cycles = 50;

    public static void Run(FlyoutWindow flyout)
    {
        var minutesArg = Environment.GetCommandLineArgs()
            .FirstOrDefault(a => a.StartsWith("--soak3d-minutes=", StringComparison.Ordinal));
        if (minutesArg is not null)
        {
            var value = minutesArg["--soak3d-minutes=".Length..];
            if (!int.TryParse(
                    value,
                    System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var minutes)
                || minutes <= 0)
            {
                DevLog.Write(
                    $"soak3d: invalid --soak3d-minutes value '{value}', " +
                    "expected a positive integer");
                Environment.Exit(2);
                return;
            }

            // Fire-and-forget on the UI dispatcher; the loop awaits real delays
            // so layout + present + release complete between cycles.
            _ = RunAsync(flyout, minutes);
            return;
        }

        // Fire-and-forget on the UI dispatcher; the loop awaits real delays so
        // layout + present + release complete between cycles.
        _ = RunAsync(flyout, null);
    }

    private static async Task RunAsync(FlyoutWindow flyout, int? durationMinutes)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var durationMode = durationMinutes is not null;
        var target = durationMinutes is { } minutes
            ? TimeSpan.FromMinutes(minutes)
            : TimeSpan.Zero;
        var mode = durationMode ? "duration" : "fixed";
        var requested = durationMinutes is { } requestedMinutes
            ? $"{requestedMinutes}m"
            : $"{Cycles} cycles";
        var cycles = 0;
        var nextSample = TimeSpan.FromMinutes(10);
        long? baselinePrivateBytes = null;
        int? baselineHandleCount = null;
        long? finalPrivateBytes = null;
        int? finalHandleCount = null;

        void SampleDue()
        {
            if (!durationMode)
            {
                return;
            }

            while (stopwatch.Elapsed >= nextSample)
            {
                using var process = System.Diagnostics.Process.GetCurrentProcess();
                var privateBytes = process.PrivateMemorySize64;
                var handles = process.HandleCount;
                var label = baselinePrivateBytes is null
                    ? "baseline"
                    : $"interval={nextSample.TotalMinutes:F0}m";
                DevLog.Write(
                    $"soak3d: sample {label} elapsed={stopwatch.Elapsed.TotalMinutes:F1}m " +
                    $"privateMemorySize64={privateBytes} handleCount={handles}");
                baselinePrivateBytes ??= privateBytes;
                baselineHandleCount ??= handles;
                nextSample += TimeSpan.FromMinutes(10);
            }
        }

        DevLog.Write($"soak3d: start mode={mode} requested={requested}");
        try
        {
            flyout.EnableGraph3D();
            do
            {
                flyout.ShowFlyout();
                await Task.Delay(180);
                flyout.HideFlyout();
                await Task.Delay(180);
                cycles++;
                SampleDue();
            }
            while (durationMode ? stopwatch.Elapsed < target : cycles < Cycles);
        }
        catch (Exception ex)
        {
            DevLog.Write($"soak3d: EXCEPTION {ex}");
            Interlocked.Increment(ref Graph3DPanel.ErrorCount);
        }

        // Let the final hide's release settle before reading counters.
        await Task.Delay(400);

        if (durationMode)
        {
            try
            {
                using var process = System.Diagnostics.Process.GetCurrentProcess();
                finalPrivateBytes = process.PrivateMemorySize64;
                finalHandleCount = process.HandleCount;
                DevLog.Write(
                    $"soak3d: sample final elapsed={stopwatch.Elapsed.TotalMinutes:F1}m " +
                    $"privateMemorySize64={finalPrivateBytes} handleCount={finalHandleCount}");
            }
            catch (Exception ex)
            {
                DevLog.Write($"soak3d: final sample FAILED {ex}");
                Interlocked.Increment(ref Graph3DPanel.ErrorCount);
            }
        }

        var created = Graph3DPanel.CreatedCount;
        var released = Graph3DPanel.ReleasedCount;
        var removed = Graph3DPanel.DeviceRemovedCount;
        var errors = Graph3DPanel.ErrorCount;
        var lifecyclePass = durationMode
            ? cycles > 0 && created == released && created == cycles
            : cycles == Cycles && created == Cycles && released == Cycles;
        var resourcePass = true;
        if (durationMinutes == 60)
        {
            var privateLimit = baselinePrivateBytes is { } baselinePrivate
                ? baselinePrivate + Math.Max(64L * 1024 * 1024, baselinePrivate / 5)
                : -1;
            var handleLimit = baselineHandleCount is { } baselineHandles
                ? baselineHandles + 8
                : -1;
            resourcePass = privateLimit >= 0 && handleLimit >= 0
                && finalPrivateBytes is { } finalPrivate
                && finalHandleCount is { } finalHandles
                && finalPrivate <= privateLimit
                && finalHandles <= handleLimit;
            if (!resourcePass)
            {
                DevLog.Write(
                    $"soak3d: resource threshold FAILED " +
                    $"baselinePrivateMemorySize64={baselinePrivateBytes?.ToString() ?? "n/a"} " +
                    $"finalPrivateMemorySize64={finalPrivateBytes?.ToString() ?? "n/a"} " +
                    $"privateLimit={privateLimit} " +
                    $"baselineHandleCount={baselineHandleCount?.ToString() ?? "n/a"} " +
                    $"finalHandleCount={finalHandleCount?.ToString() ?? "n/a"} " +
                    $"handleLimit={handleLimit}");
            }
        }

        var clean = lifecyclePass && errors == 0 && removed == 0 && resourcePass;
        DevLog.Write(
            $"soak3d: cycles={cycles} created={created} released={released} " +
            $"removed={removed} errors={errors} mode={mode} requested={requested} " +
            $"actual={stopwatch.Elapsed.TotalSeconds:F1}s");
        Environment.Exit(clean ? 0 : 1);
    }
}

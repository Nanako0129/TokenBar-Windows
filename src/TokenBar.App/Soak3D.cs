namespace TokenBar.App;

/// <summary>
/// Phase 8 Gate 0 soak: force the 3D panel active, then toggle the flyout
/// shown/hidden 50 times so the SwapChainPanel device/swapchain is created and
/// released on every cycle. Writes ONE summary line via DevLog and exits with
/// code 0 (clean) / 1 (leak, device-removed, or exception) — assertable over
/// SSH from the log + exit code, no window inspection needed.
/// </summary>
internal static class Soak3D
{
    private const int Cycles = 50;

    public static void Run(FlyoutWindow flyout)
    {
        // Fire-and-forget on the UI dispatcher; the loop awaits real delays so
        // layout + present + release complete between cycles.
        _ = RunAsync(flyout);
    }

    private static async Task RunAsync(FlyoutWindow flyout)
    {
        DevLog.Write("soak3d: start");
        flyout.EnableGraph3D();

        try
        {
            for (var i = 0; i < Cycles; i++)
            {
                flyout.ShowFlyout();
                await Task.Delay(180);
                flyout.HideFlyout();
                await Task.Delay(180);
            }
        }
        catch (Exception ex)
        {
            DevLog.Write($"soak3d: EXCEPTION {ex}");
            Interlocked.Increment(ref Graph3DPanel.ErrorCount);
        }

        // Let the final hide's release settle before reading counters.
        await Task.Delay(400);

        var created = Graph3DPanel.CreatedCount;
        var released = Graph3DPanel.ReleasedCount;
        var removed = Graph3DPanel.DeviceRemovedCount;
        var errors = Graph3DPanel.ErrorCount;
        var clean = errors == 0 && removed == 0
            && created == Cycles && released == Cycles;

        DevLog.Write(
            $"soak3d: cycles={Cycles} created={created} released={released} " +
            $"removed={removed} errors={errors}");
        Environment.Exit(clean ? 0 : 1);
    }
}

using System.Runtime.InteropServices;

namespace TokenBar.App;

/// <summary>
/// Windows 11 tags tray-resident processes as Efficiency Mode (EcoQoS) and
/// throttles their execution speed, which is why the cold parse crawled even
/// after the rayon pool grew. The boost is scoped: heavy FFI work opts the
/// process out of throttling, and the last scope to close hands the decision
/// back to Windows so the idle tray process stays power-friendly.
/// </summary>
internal static class ProcessPower
{
    private static int _boostDepth;

    /// <summary>Startup fixes, once per process: log the inherited power
    /// state, and lift a BELOW_NORMAL/IDLE priority class back to NORMAL —
    /// schtasks-launched processes can inherit a background priority that
    /// starves the parse behind every foreground app.</summary>
    public static void EnsureNormalPriority()
    {
        try
        {
            var throttle = new PROCESS_POWER_THROTTLING_STATE
            {
                Version = PROCESS_POWER_THROTTLING_CURRENT_VERSION,
            };
            var queried = GetProcessInformation(GetCurrentProcess(), ProcessPowerThrottling,
                ref throttle, (uint)Marshal.SizeOf<PROCESS_POWER_THROTTLING_STATE>());

            var priority = GetPriorityClass(GetCurrentProcess());
            DevLog.Write($"power: priority=0x{priority:X} throttling " +
                (queried ? $"control=0x{throttle.ControlMask:X} state=0x{throttle.StateMask:X}" : "query failed"));

            if (priority is BELOW_NORMAL_PRIORITY_CLASS or IDLE_PRIORITY_CLASS)
            {
                var ok = SetPriorityClass(GetCurrentProcess(), NORMAL_PRIORITY_CLASS);
                DevLog.Write($"power: raised priority to normal, ok={ok}");
            }
        }
        catch (Exception ex)
        {
            DevLog.Write($"power: startup check failed: {ex.Message}");
        }
    }

    /// <summary>Opt out of EcoQoS for the duration of the returned scope.
    /// Scopes nest (the slow lane and a lazy lens fetch can overlap); the
    /// last one out restores system-managed throttling.</summary>
    public static IDisposable Boost()
    {
        if (Interlocked.Increment(ref _boostDepth) == 1)
        {
            SetThrottling(optOut: true);
        }

        return new BoostScope();
    }

    private sealed class BoostScope : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0
                && Interlocked.Decrement(ref _boostDepth) == 0)
            {
                SetThrottling(optOut: false);
            }
        }
    }

    private static void SetThrottling(bool optOut)
    {
        var state = new PROCESS_POWER_THROTTLING_STATE
        {
            Version = PROCESS_POWER_THROTTLING_CURRENT_VERSION,
            // EXECUTION_SPEED in ControlMask with StateMask 0 = never throttle;
            // ControlMask 0 = system-managed again (Windows may re-apply EcoQoS).
            ControlMask = optOut ? PROCESS_POWER_THROTTLING_EXECUTION_SPEED : 0,
            StateMask = 0,
        };
        if (!SetProcessInformation(GetCurrentProcess(), ProcessPowerThrottling,
                ref state, (uint)Marshal.SizeOf<PROCESS_POWER_THROTTLING_STATE>()))
        {
            DevLog.Write($"power: SetProcessInformation optOut={optOut} " +
                $"failed, err={Marshal.GetLastWin32Error()}");
        }
    }

    private const uint PROCESS_POWER_THROTTLING_CURRENT_VERSION = 1;
    private const uint PROCESS_POWER_THROTTLING_EXECUTION_SPEED = 0x1;
    private const int ProcessPowerThrottling = 4; // PROCESS_INFORMATION_CLASS

    private const uint NORMAL_PRIORITY_CLASS = 0x0020;
    private const uint IDLE_PRIORITY_CLASS = 0x0040;
    private const uint BELOW_NORMAL_PRIORITY_CLASS = 0x4000;

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_POWER_THROTTLING_STATE
    {
        public uint Version;
        public uint ControlMask;
        public uint StateMask;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern nint GetCurrentProcess();

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessInformation(
        nint process, int informationClass,
        ref PROCESS_POWER_THROTTLING_STATE information, uint size);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessInformation(
        nint process, int informationClass,
        ref PROCESS_POWER_THROTTLING_STATE information, uint size);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetPriorityClass(nint process, uint priorityClass);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetPriorityClass(nint process);
}

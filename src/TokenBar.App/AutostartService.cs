using Microsoft.Win32;

namespace TokenBar.App;

/// <summary>
/// Launch-at-login via HKCU\...\Run (the macOS side uses SMAppService).
/// Like there, the OS is the only source of truth — no settings key — and
/// callers re-read state every time the panel shows so external changes
/// (Task Manager's Startup tab) are reflected. Task Manager "disables" by
/// writing a StartupApproved record rather than deleting the Run value, so
/// both must agree before we call it enabled.
/// </summary>
internal static class AutostartService
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovedKey =
        @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private static readonly string ValueName = ProductIdentity.Name;

    public static bool IsEnabled
    {
        get
        {
            try
            {
                using var run = Registry.CurrentUser.OpenSubKey(RunKey);
                if (run?.GetValue(ValueName) is not string)
                {
                    return false;
                }

                using var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey);
                // Missing record = enabled; an even first byte means enabled
                // (0x02), odd means Task Manager disabled it (0x03).
                return approved?.GetValue(ValueName) is not byte[] { Length: > 0 } record
                    || (record[0] & 1) == 0;
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>Returns false when the registry write fails, so the toggle
    /// can stay put instead of lying (macOS AutostartService.setEnabled).</summary>
    public static bool SetEnabled(bool enabled)
    {
        try
        {
            using var run = Registry.CurrentUser.CreateSubKey(RunKey);
            if (enabled)
            {
                run.SetValue(ValueName, $"\"{Environment.ProcessPath}\"");
                // Clear a Task Manager "disabled" verdict, or the Run value
                // we just wrote stays inert.
                using var approved = Registry.CurrentUser.CreateSubKey(ApprovedKey);
                approved.DeleteValue(ValueName, throwOnMissingValue: false);
            }
            else
            {
                run.DeleteValue(ValueName, throwOnMissingValue: false);
            }

            return true;
        }
        catch (Exception ex)
        {
            DevLog.Write($"autostart set failed: {ex.Message}");
            return false;
        }
    }
}

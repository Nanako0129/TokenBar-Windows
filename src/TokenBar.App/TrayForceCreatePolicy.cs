namespace TokenBar.App;

/// <summary>
/// Pure policy for H.NotifyIcon ForceCreate retries. Kept free of WinUI types
/// so unit tests can lock the bounds without constructing a tray.
/// </summary>
internal static class TrayForceCreatePolicy
{
    public const int MaxAttempts = 5;

    /// <summary>
    /// Only the shell-not-ready InvalidOperationException path is soft-retried.
    /// Everything else fails closed immediately.
    /// </summary>
    public static bool IsSoftRetryable(Exception exception) =>
        exception is InvalidOperationException;

    public static int BackoffMilliseconds(int attemptNumber)
    {
        if (attemptNumber < 1)
        {
            return 0;
        }

        return 50 * attemptNumber;
    }
}

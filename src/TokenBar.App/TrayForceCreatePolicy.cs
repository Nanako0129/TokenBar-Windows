namespace TokenBar.App;

/// <summary>
/// Pure policy + injectable per-episode ForceCreate state machine.
/// Free of WinUI types so unit tests can inject attempt functions without a tray.
/// </summary>
internal static class TrayForceCreatePolicy
{
    /// <summary>Max ForceCreate attempts in a single creation episode.</summary>
    public const int MaxAttemptsPerEpisode = 5;

    /// <summary>Startup-smoke overall wait budget for tray readiness.</summary>
    public const int StartupSmokeTimeoutMilliseconds = 10_000;

    /// <summary>Delay before the next timer tick after a soft failure (ms).</summary>
    public static int DelayMilliseconds(int attemptNumberAfterFailure)
    {
        if (attemptNumberAfterFailure < 1)
        {
            return 0;
        }

        return Math.Min(50 * attemptNumberAfterFailure, 500);
    }

    /// <summary>
    /// Only InvalidOperationException is soft-retryable (shell not ready).
    /// Everything else fails closed immediately.
    /// </summary>
    public static bool IsSoftRetryable(Exception exception) =>
        exception is InvalidOperationException;
}

/// <summary>Result of one ForceCreate tick inside an episode.</summary>
internal enum TrayForceCreateTickResult
{
    /// <summary>Icon reported created; stop retrying.</summary>
    Success,

    /// <summary>Soft failure; more attempts remain in this episode.</summary>
    Continue,

    /// <summary>Episode budget exhausted without success.</summary>
    Exhausted,
}

/// <summary>
/// Per-episode attempt accounting. Call <see cref="Tick"/> once per timer/async
/// delay. Does not sleep — the host schedules the next tick.
/// </summary>
internal sealed class TrayForceCreateEpisode
{
    private readonly int _maxAttempts;

    public TrayForceCreateEpisode(
        int maxAttempts = TrayForceCreatePolicy.MaxAttemptsPerEpisode)
    {
        if (maxAttempts < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxAttempts));
        }

        _maxAttempts = maxAttempts;
    }

    public int Attempts { get; private set; }

    public bool IsExhausted => Attempts >= _maxAttempts;

    /// <summary>
    /// Runs one ForceCreate attempt. Propagates non-retryable exceptions.
    /// Soft-retryable failures return Continue or Exhausted.
    /// </summary>
    /// <param name="attempt">
    /// Returns true when the icon is created after the attempt.
    /// </param>
    public TrayForceCreateTickResult Tick(Func<bool> attempt)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        if (IsExhausted)
        {
            return TrayForceCreateTickResult.Exhausted;
        }

        Attempts++;
        try
        {
            if (attempt())
            {
                return TrayForceCreateTickResult.Success;
            }
        }
        catch (Exception ex) when (TrayForceCreatePolicy.IsSoftRetryable(ex))
        {
            // Soft miss — budget continues.
        }

        return Attempts >= _maxAttempts
            ? TrayForceCreateTickResult.Exhausted
            : TrayForceCreateTickResult.Continue;
    }
}

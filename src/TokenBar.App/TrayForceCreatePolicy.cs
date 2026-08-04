namespace TokenBar.App;

/// <summary>
/// Pure policy + injectable per-episode ForceCreate state machine.
/// Free of WinUI types so unit tests can inject attempt and delay functions
/// without constructing a tray.
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
/// Per-episode attempt accounting. Call <see cref="Tick"/> once per timer tick.
/// The attempt and delay policy are injected; this type never sleeps or owns a
/// timer, so production can schedule with DispatcherQueueTimer and tests can
/// drive the exact state machine deterministically.
/// </summary>
internal sealed class TrayForceCreateEpisode
{
    private readonly Func<bool> _attempt;
    private readonly Func<int, int> _delayMilliseconds;
    private readonly int _maxAttempts;

    public TrayForceCreateEpisode(
        Func<bool> attempt,
        Func<int, int>? delayMilliseconds = null,
        int maxAttempts = TrayForceCreatePolicy.MaxAttemptsPerEpisode)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        if (maxAttempts < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxAttempts));
        }

        _attempt = attempt;
        _delayMilliseconds = delayMilliseconds
            ?? TrayForceCreatePolicy.DelayMilliseconds;
        _maxAttempts = maxAttempts;
    }

    public int Attempts { get; private set; }

    public bool IsExhausted => Attempts >= _maxAttempts;

    /// <summary>
    /// Delay for the next host-scheduled tick after <see cref="Tick"/> returns
    /// <see cref="TrayForceCreateTickResult.Continue"/>. Zero in all terminal
    /// states.
    /// </summary>
    public int NextDelayMilliseconds { get; private set; }

    /// <summary>
    /// Runs exactly one ForceCreate attempt. Propagates non-retryable attempt
    /// exceptions and invalid delay-policy output. Soft failures return
    /// Continue or Exhausted.
    /// </summary>
    public TrayForceCreateTickResult Tick()
    {
        if (IsExhausted)
        {
            NextDelayMilliseconds = 0;
            return TrayForceCreateTickResult.Exhausted;
        }

        Attempts++;
        try
        {
            if (_attempt())
            {
                NextDelayMilliseconds = 0;
                return TrayForceCreateTickResult.Success;
            }
        }
        catch (Exception ex) when (TrayForceCreatePolicy.IsSoftRetryable(ex))
        {
            // Soft miss — budget continues.
        }

        if (Attempts >= _maxAttempts)
        {
            NextDelayMilliseconds = 0;
            return TrayForceCreateTickResult.Exhausted;
        }

        var delay = _delayMilliseconds(Attempts);
        if (delay < 1)
        {
            throw new InvalidOperationException(
                $"ForceCreate retry delay must be positive after attempt {Attempts}.");
        }

        NextDelayMilliseconds = delay;
        return TrayForceCreateTickResult.Continue;
    }
}

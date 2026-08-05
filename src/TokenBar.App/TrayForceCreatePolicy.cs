namespace TokenBar.App;

/// <summary>
/// Pure policy + injectable per-episode ForceCreate state machine.
/// Free of WinUI types so unit tests can inject attempt, delay, and clock
/// functions without constructing a tray.
/// </summary>
internal static class TrayForceCreatePolicy
{
    /// <summary>
    /// Wall-clock budget for one tray-creation episode (normal launch and smoke).
    /// Logon autostart is the primary path; the shell can take several seconds
    /// to accept a tray icon. Exhaustion is time-based, not attempt-count based.
    /// </summary>
    public const int EpisodeDeadlineMilliseconds = 10_000;

    /// <summary>
    /// Minimum total wall-clock window the production soft-fail schedule must
    /// cover. Tests fail if a schedule change shrinks the recovery window.
    /// Matches <see cref="EpisodeDeadlineMilliseconds"/> so the episode actually
    /// runs for the intended logon-tolerant startup window.
    /// </summary>
    public const int MinProductionRetryWindowMilliseconds = EpisodeDeadlineMilliseconds;

    /// <summary>
    /// Startup-smoke outer wait for tray readiness (same window as the episode).
    /// </summary>
    public const int StartupSmokeTimeoutMilliseconds = EpisodeDeadlineMilliseconds;

    /// <summary>Cap on a single inter-attempt delay (ms).</summary>
    public const int MaxDelayMilliseconds = 500;

    /// <summary>Base step for the linear backoff before the cap (ms).</summary>
    public const int DelayStepMilliseconds = 50;

    /// <summary>Delay before the next timer tick after a soft failure (ms).</summary>
    public static int DelayMilliseconds(int attemptNumberAfterFailure)
    {
        if (attemptNumberAfterFailure < 1)
        {
            return 0;
        }

        return Math.Min(DelayStepMilliseconds * attemptNumberAfterFailure, MaxDelayMilliseconds);
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

    /// <summary>Soft failure; schedule another tick before the deadline.</summary>
    Continue,

    /// <summary>Episode wall-clock deadline passed without success.</summary>
    Exhausted,
}

/// <summary>
/// Per-episode ForceCreate accounting driven by an elapsed-time deadline.
/// Call <see cref="Tick(Func{bool})"/> once per timer tick. This type never
/// sleeps or owns a timer; production schedules the next tick with
/// DispatcherQueueTimer and tests inject attempt, delay, and clock functions.
/// </summary>
internal sealed class TrayForceCreateEpisode
{
    private readonly TimeSpan _deadline;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly DateTimeOffset _startedUtc;

    public TrayForceCreateEpisode(
        TimeSpan? deadline = null,
        Func<DateTimeOffset>? utcNow = null)
    {
        _deadline = deadline
            ?? TimeSpan.FromMilliseconds(TrayForceCreatePolicy.EpisodeDeadlineMilliseconds);
        if (_deadline <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(deadline));
        }

        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        _startedUtc = _utcNow();
    }

    public int Attempts { get; private set; }

    /// <summary>Elapsed wall time since the episode started (injected clock).</summary>
    public TimeSpan Elapsed => _utcNow() - _startedUtc;

    /// <summary>True when the wall-clock deadline has been reached or passed.</summary>
    public bool IsExhausted => Elapsed >= _deadline;

    /// <summary>
    /// Delay computed for the next host-scheduled tick after a Continue result.
    /// Zero in terminal states. Never exceeds the delay cap or remaining budget.
    /// </summary>
    public int NextDelayMilliseconds { get; private set; }

    /// <summary>
    /// Runs one attempt using the production delay policy.
    /// </summary>
    public TrayForceCreateTickResult Tick(Func<bool> attempt) =>
        Tick(attempt, TrayForceCreatePolicy.DelayMilliseconds);

    /// <summary>
    /// Runs exactly one ForceCreate attempt unless the deadline has already
    /// passed. Propagates non-retryable attempt exceptions and invalid delay
    /// output. Soft failures return Continue while time remains, otherwise
    /// Exhausted. The injected delay function is called only when another tick
    /// is actually required.
    /// </summary>
    public TrayForceCreateTickResult Tick(
        Func<bool> attempt,
        Func<int, int> delayMilliseconds)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        ArgumentNullException.ThrowIfNull(delayMilliseconds);

        if (IsExhausted)
        {
            NextDelayMilliseconds = 0;
            return TrayForceCreateTickResult.Exhausted;
        }

        Attempts++;
        try
        {
            if (attempt())
            {
                NextDelayMilliseconds = 0;
                return TrayForceCreateTickResult.Success;
            }
        }
        catch (Exception ex) when (TrayForceCreatePolicy.IsSoftRetryable(ex))
        {
            // Soft miss — keep retrying until the wall-clock deadline.
        }

        var remainingMs = (int)Math.Ceiling((_deadline - Elapsed).TotalMilliseconds);
        if (remainingMs <= 0)
        {
            NextDelayMilliseconds = 0;
            return TrayForceCreateTickResult.Exhausted;
        }

        var delay = delayMilliseconds(Attempts);
        if (delay < 1)
        {
            throw new InvalidOperationException(
                $"ForceCreate retry delay must be positive after attempt {Attempts}.");
        }

        // Cap single-step delay, then never schedule past the episode deadline.
        delay = Math.Min(delay, TrayForceCreatePolicy.MaxDelayMilliseconds);
        delay = Math.Min(delay, remainingMs);
        if (delay < 1)
        {
            NextDelayMilliseconds = 0;
            return TrayForceCreateTickResult.Exhausted;
        }

        NextDelayMilliseconds = delay;
        return TrayForceCreateTickResult.Continue;
    }
}

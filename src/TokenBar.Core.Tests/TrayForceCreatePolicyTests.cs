using TokenBar.App;

namespace TokenBar.Core.Tests;

public sealed class TrayForceCreatePolicyTests
{
    [Fact]
    public void Episode_deadline_covers_logon_scale_window()
    {
        Assert.True(
            TrayForceCreatePolicy.EpisodeDeadlineMilliseconds >= 10_000,
            "Episode deadline must cover a multi-second logon shell window.");
        Assert.Equal(
            TrayForceCreatePolicy.EpisodeDeadlineMilliseconds,
            TrayForceCreatePolicy.StartupSmokeTimeoutMilliseconds);
        Assert.Equal(
            TrayForceCreatePolicy.EpisodeDeadlineMilliseconds,
            TrayForceCreatePolicy.MinProductionRetryWindowMilliseconds);
        Assert.True(TrayForceCreatePolicy.MaxDelayMilliseconds >= 50);
        Assert.True(
            TrayForceCreatePolicy.MaxDelayMilliseconds
            <= TrayForceCreatePolicy.EpisodeDeadlineMilliseconds);
    }

    [Fact]
    public void Only_InvalidOperationException_is_soft_retryable()
    {
        Assert.True(TrayForceCreatePolicy.IsSoftRetryable(new InvalidOperationException("shell")));
        Assert.False(TrayForceCreatePolicy.IsSoftRetryable(new ArgumentException("bad")));
        Assert.False(TrayForceCreatePolicy.IsSoftRetryable(new Exception("generic")));
        Assert.False(TrayForceCreatePolicy.IsSoftRetryable(new NotImplementedException("platform")));
    }

    [Fact]
    public void Delay_grows_with_attempt_number_and_is_capped()
    {
        Assert.Equal(0, TrayForceCreatePolicy.DelayMilliseconds(0));
        Assert.Equal(50, TrayForceCreatePolicy.DelayMilliseconds(1));
        Assert.Equal(100, TrayForceCreatePolicy.DelayMilliseconds(2));
        Assert.Equal(
            TrayForceCreatePolicy.MaxDelayMilliseconds,
            TrayForceCreatePolicy.DelayMilliseconds(100));
    }

    [Fact]
    public void Episode_stops_immediately_on_success()
    {
        var calls = 0;
        var delayCalls = 0;
        var episode = new TrayForceCreateEpisode();
        var result = episode.Tick(
            () =>
            {
                calls++;
                return true;
            },
            attempt =>
            {
                delayCalls++;
                return attempt * 10;
            });

        Assert.Equal(TrayForceCreateTickResult.Success, result);
        Assert.Equal(1, calls);
        Assert.Equal(0, delayCalls);
        Assert.Equal(0, episode.NextDelayMilliseconds);
        Assert.Equal(1, episode.Attempts);
    }

    [Fact]
    public void Episode_soft_fail_then_success_same_episode()
    {
        var now = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var calls = 0;
        var episode = new TrayForceCreateEpisode(
            deadline: TimeSpan.FromSeconds(10),
            utcNow: () => now);

        var soft = episode.Tick(
            () =>
            {
                calls++;
                throw new InvalidOperationException("shell not ready");
            },
            attempt => attempt * 10);
        Assert.Equal(TrayForceCreateTickResult.Continue, soft);
        Assert.Equal(10, episode.NextDelayMilliseconds);
        Assert.Equal(1, episode.Attempts);

        now = now.AddMilliseconds(episode.NextDelayMilliseconds);

        var ok = episode.Tick(
            () =>
            {
                calls++;
                return true;
            },
            _ => 99);
        Assert.Equal(TrayForceCreateTickResult.Success, ok);
        Assert.Equal(0, episode.NextDelayMilliseconds);
        Assert.Equal(2, calls);
        Assert.Equal(2, episode.Attempts);
        Assert.False(episode.IsExhausted);
    }

    [Fact]
    public void Host_simulation_cancellation_stops_before_deadline()
    {
        using var cts = new CancellationTokenSource();
        var now = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var episode = new TrayForceCreateEpisode(
            deadline: TimeSpan.FromSeconds(10),
            utcNow: () => now);
        var calls = 0;

        while (!episode.IsExhausted && !cts.IsCancellationRequested)
        {
            var result = episode.Tick(
                () =>
                {
                    calls++;
                    if (calls >= 2)
                    {
                        cts.Cancel();
                    }

                    throw new InvalidOperationException("shell not ready");
                },
                attempt => attempt * 10);

            if (cts.IsCancellationRequested || result != TrayForceCreateTickResult.Continue)
            {
                break;
            }

            now = now.AddMilliseconds(episode.NextDelayMilliseconds);
        }

        Assert.True(cts.IsCancellationRequested);
        Assert.Equal(2, calls);
        Assert.False(episode.IsExhausted);
        Assert.Equal(2, episode.Attempts);
    }

    [Fact]
    public void Production_soft_fail_schedule_covers_minimum_retry_window()
    {
        // Advance a fake clock by each production delay until Exhausted.
        // The wall-clock span must not shrink below MinProductionRetryWindowMilliseconds.
        var now = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var start = now;
        var episode = new TrayForceCreateEpisode(
            deadline: TimeSpan.FromMilliseconds(
                TrayForceCreatePolicy.EpisodeDeadlineMilliseconds),
            utcNow: () => now);

        var safety = 0;
        TrayForceCreateTickResult result;
        do
        {
            result = episode.Tick(
                () => throw new InvalidOperationException("shell not ready"));
            if (result == TrayForceCreateTickResult.Continue)
            {
                Assert.True(episode.NextDelayMilliseconds >= 1);
                Assert.True(
                    episode.NextDelayMilliseconds
                    <= TrayForceCreatePolicy.MaxDelayMilliseconds);
                now = now.AddMilliseconds(episode.NextDelayMilliseconds);
            }

            safety++;
            Assert.True(safety < 10_000, "episode failed to exhaust under fake clock");
        }
        while (result == TrayForceCreateTickResult.Continue);

        Assert.Equal(TrayForceCreateTickResult.Exhausted, result);
        var windowMs = (now - start).TotalMilliseconds;
        Assert.True(
            windowMs >= TrayForceCreatePolicy.MinProductionRetryWindowMilliseconds,
            $"production soft-fail window {windowMs}ms is below minimum "
            + $"{TrayForceCreatePolicy.MinProductionRetryWindowMilliseconds}ms");
        // Deadline is the effective bound; clock should land at or past it.
        Assert.True(episode.IsExhausted);
        Assert.True(episode.Attempts > 5, "time-based budget must allow more than five attempts");
    }

    [Fact]
    public void Episode_soft_failures_continue_until_deadline_then_exhaust()
    {
        var now = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var delayAttempts = new List<int>();
        var episode = new TrayForceCreateEpisode(
            deadline: TimeSpan.FromMilliseconds(250),
            utcNow: () => now);

        TrayForceCreateTickResult Tick() => episode.Tick(
            () => throw new InvalidOperationException("shell not ready"),
            attempt =>
            {
                delayAttempts.Add(attempt);
                return 100;
            });

        Assert.Equal(TrayForceCreateTickResult.Continue, Tick());
        Assert.Equal(100, episode.NextDelayMilliseconds);
        now = now.AddMilliseconds(100);

        Assert.Equal(TrayForceCreateTickResult.Continue, Tick());
        Assert.Equal(100, episode.NextDelayMilliseconds);
        now = now.AddMilliseconds(100);

        // Elapsed 200ms, remaining 50ms → delay clamped to remaining.
        Assert.Equal(TrayForceCreateTickResult.Continue, Tick());
        Assert.Equal(50, episode.NextDelayMilliseconds);
        now = now.AddMilliseconds(50);

        // Elapsed == deadline → Exhausted on entry without another attempt.
        Assert.Equal(TrayForceCreateTickResult.Exhausted, Tick());
        Assert.Equal(0, episode.NextDelayMilliseconds);
        Assert.True(episode.IsExhausted);
        Assert.Equal(3, episode.Attempts);

        // Further ticks stay exhausted without invoking attempt or delay.
        var calls = 0;
        var after = episode.Tick(
            () =>
            {
                calls++;
                return true;
            },
            _ => 1);
        Assert.Equal(TrayForceCreateTickResult.Exhausted, after);
        Assert.Equal(0, calls);
    }

    [Fact]
    public void Episode_rejects_non_positive_injected_delay()
    {
        var episode = new TrayForceCreateEpisode(deadline: TimeSpan.FromSeconds(5));

        var error = Assert.Throws<InvalidOperationException>(() =>
            episode.Tick(() => false, _ => 0));

        Assert.Contains("delay must be positive", error.Message, StringComparison.Ordinal);
        Assert.Equal(1, episode.Attempts);
    }

    [Fact]
    public void Episode_propagates_non_retryable_exceptions()
    {
        var episode = new TrayForceCreateEpisode(deadline: TimeSpan.FromSeconds(5));
        Assert.Throws<ArgumentException>(() =>
            episode.Tick(() => throw new ArgumentException("boom")));
        Assert.Equal(1, episode.Attempts);
    }

    [Fact]
    public void Separate_episodes_have_independent_deadlines()
    {
        var now = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var first = new TrayForceCreateEpisode(
            deadline: TimeSpan.FromMilliseconds(50),
            utcNow: () => now);
        _ = first.Tick(() => throw new InvalidOperationException("a"));
        now = now.AddMilliseconds(first.NextDelayMilliseconds);
        Assert.Equal(
            TrayForceCreateTickResult.Exhausted,
            first.Tick(() => throw new InvalidOperationException("b")));
        Assert.True(first.IsExhausted);

        var second = new TrayForceCreateEpisode(
            deadline: TimeSpan.FromSeconds(10),
            utcNow: () => now);
        var ok = second.Tick(() => true);
        Assert.Equal(TrayForceCreateTickResult.Success, ok);
        Assert.Equal(1, second.Attempts);
        Assert.False(second.IsExhausted);
    }

    [Fact]
    public void No_ThreadSleep_in_TrayService_source()
    {
        // Production path must not block the UI thread with Sleep.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        string? path = null;
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "src", "TokenBar.App", "TrayService.cs");
            if (File.Exists(candidate))
            {
                path = candidate;
                break;
            }

            dir = dir.Parent;
        }

        Assert.False(string.IsNullOrEmpty(path), "TrayService.cs not found from test base directory");
        var text = File.ReadAllText(path!);
        Assert.DoesNotContain("Thread.Sleep", text, StringComparison.Ordinal);
    }
}

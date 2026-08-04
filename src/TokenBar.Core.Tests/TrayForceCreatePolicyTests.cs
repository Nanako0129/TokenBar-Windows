using TokenBar.App;

namespace TokenBar.Core.Tests;

public sealed class TrayForceCreatePolicyTests
{
    [Fact]
    public void MaxAttemptsPerEpisode_is_bounded_and_positive()
    {
        Assert.True(TrayForceCreatePolicy.MaxAttemptsPerEpisode >= 2);
        Assert.True(TrayForceCreatePolicy.MaxAttemptsPerEpisode <= 10);
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
        Assert.Equal(500, TrayForceCreatePolicy.DelayMilliseconds(100));
    }

    [Fact]
    public void Episode_stops_immediately_on_success()
    {
        var calls = 0;
        var delayCalls = 0;
        var episode = new TrayForceCreateEpisode(maxAttempts: 5);
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
    public void Episode_soft_failures_use_injected_delay_then_exhaust()
    {
        var calls = 0;
        var delayAttempts = new List<int>();
        var episode = new TrayForceCreateEpisode(maxAttempts: 3);

        TrayForceCreateTickResult Tick() => episode.Tick(
            () =>
            {
                calls++;
                throw new InvalidOperationException("shell not ready");
            },
            attempt =>
            {
                delayAttempts.Add(attempt);
                return attempt * 17;
            });

        Assert.Equal(TrayForceCreateTickResult.Continue, Tick());
        Assert.Equal(17, episode.NextDelayMilliseconds);
        Assert.Equal(TrayForceCreateTickResult.Continue, Tick());
        Assert.Equal(34, episode.NextDelayMilliseconds);
        Assert.Equal(TrayForceCreateTickResult.Exhausted, Tick());
        Assert.Equal(0, episode.NextDelayMilliseconds);

        Assert.Equal(3, calls);
        Assert.Equal([1, 2], delayAttempts);
        Assert.True(episode.IsExhausted);

        // Further ticks stay exhausted without invoking attempt or delay.
        var after = episode.Tick(
            () =>
            {
                calls++;
                return true;
            },
            attempt =>
            {
                delayAttempts.Add(attempt);
                return 1;
            });
        Assert.Equal(TrayForceCreateTickResult.Exhausted, after);
        Assert.Equal(3, calls);
        Assert.Equal([1, 2], delayAttempts);
    }

    [Fact]
    public void Episode_rejects_non_positive_injected_delay()
    {
        var episode = new TrayForceCreateEpisode(maxAttempts: 2);

        var error = Assert.Throws<InvalidOperationException>(() =>
            episode.Tick(() => false, _ => 0));

        Assert.Contains("delay must be positive", error.Message, StringComparison.Ordinal);
        Assert.Equal(1, episode.Attempts);
    }

    [Fact]
    public void Episode_propagates_non_retryable_exceptions()
    {
        var episode = new TrayForceCreateEpisode(maxAttempts: 5);
        Assert.Throws<ArgumentException>(() =>
            episode.Tick(() => throw new ArgumentException("boom")));
        Assert.Equal(1, episode.Attempts);
    }

    [Fact]
    public void Separate_episodes_have_independent_budgets()
    {
        var first = new TrayForceCreateEpisode(maxAttempts: 2);
        _ = first.Tick(() => throw new InvalidOperationException("a"));
        _ = first.Tick(() => throw new InvalidOperationException("b"));
        Assert.True(first.IsExhausted);

        var second = new TrayForceCreateEpisode(maxAttempts: 2);
        var ok = second.Tick(() => true);
        Assert.Equal(TrayForceCreateTickResult.Success, ok);
        Assert.Equal(1, second.Attempts);
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

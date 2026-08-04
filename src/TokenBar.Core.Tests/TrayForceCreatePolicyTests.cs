using TokenBar.App;

namespace TokenBar.Core.Tests;

public sealed class TrayForceCreatePolicyTests
{
    [Fact]
    public void MaxAttempts_is_bounded_and_positive()
    {
        Assert.True(TrayForceCreatePolicy.MaxAttempts >= 2);
        Assert.True(TrayForceCreatePolicy.MaxAttempts <= 10);
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
    public void Backoff_grows_with_attempt_number()
    {
        Assert.Equal(0, TrayForceCreatePolicy.BackoffMilliseconds(0));
        Assert.Equal(50, TrayForceCreatePolicy.BackoffMilliseconds(1));
        Assert.Equal(100, TrayForceCreatePolicy.BackoffMilliseconds(2));
        Assert.True(
            TrayForceCreatePolicy.BackoffMilliseconds(TrayForceCreatePolicy.MaxAttempts) >
            TrayForceCreatePolicy.BackoffMilliseconds(1));
    }
}

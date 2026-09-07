using TokenBar.Interop;
using Xunit;

namespace TokenBar.Core.Tests;

// Exercises the real native library (copied to the test output by
// src/Directory.Build.targets), the way ProbeTests does — round-tripping
// tb_window_usage through the real TbCore decoder rather than reasoning
// about the wire shape. Row content is environment-dependent (empty on a
// machine with no agent sessions), so only the decode contract is asserted.
public class WindowUsageTests
{
    [Fact]
    public void WindowUsageLoadsNativeLibraryAndDecodes()
    {
        var usage = TbCore.WindowUsage(0, long.MaxValue);

        Assert.NotNull(usage.Messages);
        Assert.True(usage.UndatedCount >= 0);
        Assert.True(usage.ProcessingTimeMs >= 0);
        foreach (var message in usage.Messages)
        {
            Assert.False(string.IsNullOrEmpty(message.Client));
        }
    }

    [Fact]
    public void EmptyRangeDecodesToNoMessages()
    {
        // from > until: no message's timestamp can ever satisfy the window,
        // so this must decode as an empty list rather than an error.
        var usage = TbCore.WindowUsage(1_700_000_060_000, 1_700_000_000_000);

        Assert.Empty(usage.Messages);
    }
}
